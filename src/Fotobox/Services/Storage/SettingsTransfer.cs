using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fotobox.Core;

namespace Fotobox.Services.Storage;

/// <summary>Fehler beim Lesen einer Einstellungsdatei; <see cref="Exception.Message"/> ist ein deutscher Text für den Admin.</summary>
public sealed class SettingsImportException : Exception
{
    public SettingsImportException(string message) : base(message)
    {
    }
}

/// <summary>
/// Ergebnis von <see cref="SettingsTransfer.Parse"/>.
/// </summary>
/// <param name="Sections">
/// Vollständiges <see cref="AppSettings"/>, dessen übertragbare Abschnitte (<see cref="SettingsSections.PortableSections"/>) das
/// Importergebnis sind (fehlende Werte = Standard, normalisiert, gerätespezifische Felder leer). <see cref="AppSettings.Version"/>
/// und <see cref="AppSettings.Security"/> dieses Objekts sind ohne Bedeutung; der Aufrufer kopiert die Abschnitte per
/// <see cref="SettingsSections.CopyPortableSections"/> und ergänzt die Geräte des Zielrechners per
/// <see cref="DeviceSpecificSettings.CopyFrom"/>.
/// </param>
/// <param name="Pin">Die in der Datei enthaltene, plausible PIN (null = keine oder ungültig); der Aufrufer fragt den Admin, ob er sie übernehmen will.</param>
/// <param name="FromNewerVersion">Die Datei stammt offenbar aus einer neueren App-Version (Version, unbekannte Einstellungen oder Werte).</param>
/// <param name="FileAppVersion">App-Version laut Datei (nur bei einer Umhüllung mit Kopfdaten).</param>
/// <param name="FileSettingsVersion">Einstellungs-Format laut Datei (<see cref="AppSettings.Version"/>), sonst null.</param>
/// <param name="Notes">Hinweise für den Admin (deutsch, Sie-Form).</param>
/// <param name="ResetSections">Namen der Abschnitte, die in der Datei ungültig waren und auf die Standardwerte zurückgesetzt wurden.</param>
public sealed record SettingsImportResult(
    AppSettings Sections,
    SecuritySettings? Pin,
    bool FromNewerVersion,
    string? FileAppVersion,
    int? FileSettingsVersion,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> ResetSections);

/// <summary>
/// Export und Import der Einstellungen als Datei („Einstellungen teilen“), UI-frei. Die Datei ist eine Umhüllung
/// (<c>Format</c>, <c>FormatVersion</c>, <c>AppVersion</c>, <c>SettingsVersion</c>, <c>ExportedAt</c>, <c>Settings</c>); der Import
/// nimmt zusätzlich eine rohe settings.json an. Geräte- und pfadabhängige Felder (<see cref="DeviceSpecificSettings"/>) stehen
/// nie in der Datei und werden nie importiert; die PIN nur auf ausdrücklichen Wunsch. Fehlende Einstellungen gelten als
/// Standardwerte (nicht als die des Zielrechners), unbekannte werden ignoriert.
/// </summary>
public static class SettingsTransfer
{
    /// <summary>Wert von <c>Format</c> in der Datei.</summary>
    public const string FormatName = "FotoboxPlus.Settings";

    /// <summary>Version der Umhüllung (nicht der Einstellungen).</summary>
    public const int FormatVersion = 1;

    /// <summary>Größere Dateien werden beim Lesen abgelehnt (echte Einstellungsdateien sind wenige KB groß).</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>Frist für Lesen und Schreiben der Datei (Netzlaufwerk, Wechseldatenträger).</summary>
    public static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Filter für Datei-Dialoge.</summary>
    public const string FileFilter = "Fotobox+-Einstellungen (*.json)|*.json";

    /// <summary>Hinweis für den Admin, wenn <see cref="ApplyOverlayFallback"/> etwas geändert hat.</summary>
    public const string OverlayMissingNote = "Overlay-Bild fehlt – bitte im Tab Layout neu wählen.";

    private const string InvalidFileMessage = "Die Datei ist keine gültige Einstellungsdatei.";
    private const int MaxAppVersionLength = 64;

    private static readonly Regex NumericVersion = new(@"^\s*v?(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant);

    /// <summary>Vorgeschlagener Dateiname: <c>FotoboxPlus-Einstellungen-yyyyMMdd-HHmm.json</c> (invariante Kultur).</summary>
    public static string SuggestedFileName(DateTime now) =>
        $"FotoboxPlus-Einstellungen-{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.json";

    /// <summary>
    /// Erzeugt den Dateiinhalt: Der Entwurf wird kopiert und normalisiert; gerätespezifische Felder fehlen im JSON ganz
    /// (nicht nur als <c>null</c>), die PIN steht nur drin, wenn <paramref name="pin"/> angegeben ist. <paramref name="settings"/>
    /// wird nicht verändert.
    /// </summary>
    public static string Serialize(AppSettings settings, SecuritySettings? pin, string appVersion, DateTimeOffset exportedAt)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var copy = SettingsNormalizer.Normalize(settings.Clone());
        DeviceSpecificSettings.Clear(copy);
        copy.Security = pin is null
            ? new SecuritySettings()
            : new SecuritySettings { PinHash = pin.PinHash, PinSalt = pin.PinSalt, PinIterations = pin.PinIterations };

        var node = JsonSerializer.SerializeToNode(copy, AppSettings.JsonOptions)!.AsObject();
        // Die Einstellungs-Version steht im Kopf (SettingsVersion).
        node.Remove(nameof(AppSettings.Version));
        if (pin is null)
            node.Remove(nameof(AppSettings.Security));

        foreach (var path in DeviceSpecificSettings.Paths)
        {
            var dot = path.IndexOf('.');
            if (node[path[..dot]] is JsonObject section)
                section.Remove(path[(dot + 1)..]);
        }

        var wrapper = new JsonObject
        {
            ["Format"] = FormatName,
            ["FormatVersion"] = FormatVersion,
            ["AppVersion"] = appVersion,
            ["SettingsVersion"] = copy.Version,
            ["ExportedAt"] = exportedAt.ToString("o", CultureInfo.InvariantCulture),
            ["Settings"] = node,
        };
        return wrapper.ToJsonString(AppSettings.JsonOptions);
    }

    /// <summary>
    /// Liest den Dateiinhalt (Umhüllung oder rohe settings.json; Namen ohne Beachtung der Groß-/Kleinschreibung, Kommentare
    /// und abschließende Kommas erlaubt). Jeder übertragbare Abschnitt wird einzeln gelesen: Ein ungültiger Abschnitt wird
    /// auf die Standardwerte zurückgesetzt (<see cref="SettingsImportResult.ResetSections"/>), ein fehlender ist Standard.
    /// Gerätespezifische Felder bleiben immer leer. Eine Datei mit Einstellungs-Version unter 2 wird migriert
    /// (Präfix „Fotobox“ → „Fotobox+“); fehlt die Version, gilt wie im Store die aktuelle.
    /// </summary>
    /// <exception cref="SettingsImportException">Kein gültiges JSON, kein Objekt, fremdes Format oder keine bekannten Abschnitte.</exception>
    public static SettingsImportResult Parse(string json)
    {
        if (json is null)
            throw new SettingsImportException(InvalidFileMessage);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json.TrimStart('﻿'), SettingsJson.DocumentOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new SettingsImportException(InvalidFileMessage);
        }

        using (doc)
            return ParseRoot(doc.RootElement);
    }

    private static SettingsImportResult ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new SettingsImportException(InvalidFileMessage);

        var hasFormat = SettingsJson.TryGetProperty(root, "Format", out var format);
        if (hasFormat && (format.ValueKind != JsonValueKind.String || !string.Equals(format.GetString(), FormatName, StringComparison.Ordinal)))
            throw new SettingsImportException("Die Datei ist keine Fotobox+-Einstellungsdatei.");

        JsonElement settingsElement;
        string? fileAppVersion = null;
        int? fileSettingsVersion = null;
        if (SettingsJson.TryGetProperty(root, "Settings", out var wrapped) && (wrapped.ValueKind == JsonValueKind.Object || hasFormat))
        {
            if (wrapped.ValueKind != JsonValueKind.Object)
                throw new SettingsImportException(InvalidFileMessage);

            settingsElement = wrapped;
            if (SettingsJson.TryGetProperty(root, "AppVersion", out var app) && app.ValueKind == JsonValueKind.String)
            {
                var text = app.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text))
                    fileAppVersion = text.Length > MaxAppVersionLength ? text[..MaxAppVersionLength] : text;
            }
            fileSettingsVersion = ReadInt(root, "SettingsVersion") ?? ReadInt(settingsElement, nameof(AppSettings.Version));
        }
        else
        {
            // Rohe settings.json: die Abschnitte stehen direkt in der Wurzel.
            var known = SettingsSections.PortableSections.Select(p => p.Name).Append(nameof(AppSettings.Security));
            if (!known.Any(name => SettingsJson.TryGetProperty(root, name, out _)))
                throw new SettingsImportException(InvalidFileMessage);

            settingsElement = root;
            fileSettingsVersion = ReadInt(root, nameof(AppSettings.Version));
        }

        var notes = new List<string>();
        var resets = new List<string>();
        var newer = false;

        // Fehlt die Version, gilt wie beim Laden der settings.json die aktuelle (keine Migration).
        var temp = new AppSettings { Version = fileSettingsVersion ?? AppSettings.CurrentVersion };

        foreach (var section in SettingsSections.PortableSections)
        {
            object? value = null;
            if (SettingsJson.TryGetProperty(settingsElement, section.Name, out var element) && element.ValueKind != JsonValueKind.Null)
            {
                try
                {
                    value = JsonSerializer.Deserialize(element, section.PropertyType, AppSettings.JsonOptions);
                    if (value is not null)
                    {
                        if (HasUnknownProperties(element, section.PropertyType) || HasUndefinedEnum(value))
                            newer = true;
                    }
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    value = null;
                    resets.Add(section.Name);
                    notes.Add($"Der Abschnitt „{section.Name}“ in der Datei war ungültig und wurde auf die Standardwerte zurückgesetzt.");
                }
            }

            section.SetValue(temp, value ?? Activator.CreateInstance(section.PropertyType));
        }

        // Unbekannte Abschnitte (nur in neueren Versionen denkbar).
        var knownNames = PropertyNames(typeof(AppSettings));
        foreach (var property in settingsElement.EnumerateObject())
        {
            if (!knownNames.Contains(property.Name))
                newer = true;
        }

        if (fileSettingsVersion > AppSettings.CurrentVersion)
            newer = true;
        if (fileAppVersion is not null && IsNewerAppVersion(fileAppVersion, AppInfo.Version))
            newer = true;

        var pin = ReadPin(settingsElement, notes);

        // Geräte gehören dem Zielrechner; vor dem Normalisieren leeren, damit auch eine rohe settings.json keine einschleppt.
        DeviceSpecificSettings.Clear(temp);
        SettingsNormalizer.Normalize(temp);
        temp.Security = new SecuritySettings();

        if (newer)
        {
            var detail = fileAppVersion is not null ? $"Fotobox+ {fileAppVersion}"
                : fileSettingsVersion is { } v ? $"Einstellungsformat {v}"
                : "unbekannte Version";
            notes.Insert(0, $"Die Datei stammt aus einer neueren Version ({detail}). " +
                            "Einstellungen, die es in dieser Version nicht gibt, wurden ignoriert.");
        }

        return new SettingsImportResult(temp, pin, newer, fileAppVersion, fileSettingsVersion, notes, resets);
    }

    /// <summary>Liest die PIN der Datei; unplausible Werte ergeben null und einen Hinweis, eine leere/fehlende PIN nichts.</summary>
    private static SecuritySettings? ReadPin(JsonElement settingsElement, List<string> notes)
    {
        if (!SettingsJson.TryGetProperty(settingsElement, nameof(AppSettings.Security), out var sec) || sec.ValueKind == JsonValueKind.Null)
            return null;

        if (SettingsJson.TryReadSecurity(sec, out var security) && IsPlausible(security))
            return security;

        if (sec.ValueKind != JsonValueKind.Object || HasPinData(sec))
            notes.Add("Die in der Datei enthaltene PIN ist ungültig und wird ignoriert.");
        return null;
    }

    private static bool HasPinData(JsonElement sec)
    {
        foreach (var name in new[] { nameof(SecuritySettings.PinHash), nameof(SecuritySettings.PinSalt) })
        {
            if (SettingsJson.TryGetProperty(sec, name, out var value) &&
                value.ValueKind != JsonValueKind.Null &&
                !(value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString())))
                return true;
        }
        return false;
    }

    /// <summary>Wie <see cref="PinService.Verify"/>: Hash 32 Byte (Base64), Salt nicht leer (Base64), Iterationen 1 … <see cref="SettingsNormalizer.MaxPlausiblePinIterations"/>.</summary>
    private static bool IsPlausible(SecuritySettings s)
    {
        if (string.IsNullOrEmpty(s.PinHash) || string.IsNullOrEmpty(s.PinSalt))
            return false;
        if (s.PinIterations < 1 || s.PinIterations > SettingsNormalizer.MaxPlausiblePinIterations)
            return false;

        var hash = new byte[s.PinHash.Length];
        if (!Convert.TryFromBase64String(s.PinHash, hash, out var hashLength) || hashLength != 32)
            return false;
        var salt = new byte[s.PinSalt.Length];
        return Convert.TryFromBase64String(s.PinSalt, salt, out var saltLength) && saltLength > 0;
    }

    private static int? ReadInt(JsonElement obj, string name) =>
        SettingsJson.TryGetProperty(obj, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>Ist <paramref name="fileVersion"/> (Major.Minor[.Patch], Zusätze wie „-beta“ bleiben unbeachtet) größer als <paramref name="currentVersion"/>?</summary>
    private static bool IsNewerAppVersion(string fileVersion, string currentVersion) =>
        TryParseNumeric(fileVersion, out var file) && TryParseNumeric(currentVersion, out var current) && file > current;

    private static bool TryParseNumeric(string text, out Version version)
    {
        version = new Version(0, 0, 0);
        var match = NumericVersion.Match(text);
        if (!match.Success)
            return false;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
            return false;
        var patch = 0;
        if (match.Groups[3].Success && !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch))
            return false;
        version = new Version(major, minor, patch);
        return true;
    }

    /// <summary>
    /// Die Eigenschaften, die der Serializer schreibt (öffentlich lesbar, nicht <c>[JsonIgnore]</c>). Auch abgeleitete, nur lesbare
    /// Eigenschaften wie <c>VideoSettings.UsesAudio</c> stehen in einer vom Store geschriebenen Datei und sind daher bekannt.
    /// </summary>
    private static IEnumerable<PropertyInfo> SettingProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0
                        && p.GetCustomAttribute<JsonIgnoreAttribute>() is null);

    private static HashSet<string> PropertyNames(Type type) =>
        SettingProperties(type)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kennt <paramref name="type"/> alle direkten Eigenschaften des JSON-Objekts <paramref name="element"/>? (Nur die oberste Ebene des Abschnitts.)</summary>
    private static bool HasUnknownProperties(JsonElement element, Type type)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var names = PropertyNames(type);
        return element.EnumerateObject().Any(p => !names.Contains(p.Name));
    }

    /// <summary>
    /// Enthält <paramref name="section"/> einen Enum-Wert, den es in dieser Version nicht gibt (der tolerante Konverter liefert
    /// für unbekannte Namen einen undefinierten Wert, den der Normalizer erst danach auf den Standard setzt)? Nur eine neuere
    /// Version schreibt so etwas; Flag-Enums bleiben unbeachtet.
    /// </summary>
    private static bool HasUndefinedEnum(object section)
    {
        foreach (var property in SettingProperties(section.GetType()))
        {
            var value = property.GetValue(section);
            if (value is null)
                continue;

            if (IsUndefinedEnum(value))
                return true;
            if (value is IEnumerable items && value is not string && items.Cast<object?>().Any(i => i is not null && IsUndefinedEnum(i)))
                return true;
        }
        return false;
    }

    private static bool IsUndefinedEnum(object value)
    {
        var type = value.GetType();
        return type.IsEnum && type.GetCustomAttribute<FlagsAttribute>() is null && !Enum.IsDefined(type, value);
    }

    /// <summary>
    /// Ersetzt ein nicht vorhandenes oder unzulässiges Overlay-Bild: Der Dateiname gilt nur als reiner Dateiname ohne Pfad
    /// (sonst könnte eine fremde Datei einen Pfad einschleusen) und nur, wenn <paramref name="overlayExists"/> ihn kennt. Sonst
    /// werden <see cref="LayoutSettings.OverlayFileName"/> und das Overlay in <see cref="LayoutSettings.GuestChoices"/>
    /// entfernt; war Overlay das Standard-Layout, gilt die erste verbleibende Gastwahl bzw. <see cref="GuestLayout.Single"/>.
    /// Danach wird die Gastwahl wie im Normalizer bereinigt.
    /// </summary>
    /// <returns>true, wenn etwas geändert wurde (der Aufrufer zeigt dann <see cref="OverlayMissingNote"/>); false, wenn kein Overlay verwendet wird oder die Datei vorhanden ist.</returns>
    public static bool ApplyOverlayFallback(AppSettings s, Func<string, bool> overlayExists)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(overlayExists);

        var layout = s.Layout ??= new LayoutSettings();
        layout.GuestChoices ??= [];
        var name = layout.OverlayFileName;
        var referenced = !string.IsNullOrEmpty(name)
                         || layout.Kind == LayoutKind.Overlay
                         || layout.GuestChoices.Contains(GuestLayout.Overlay);
        if (!referenced)
            return false;

        if (IsValidOverlayName(name) && Exists(overlayExists, name!))
            return false;

        layout.OverlayFileName = null;
        layout.GuestChoices.RemoveAll(g => g == GuestLayout.Overlay);
        if (layout.Kind == LayoutKind.Overlay)
        {
            var replacement = layout.GuestChoices.Count > 0 ? layout.GuestChoices[0] : GuestLayout.Single;
            GuestLayouts.ApplyTo(layout, replacement);
        }
        layout.GuestChoices = SettingsNormalizer.NormalizeGuestChoices(layout);
        return true;
    }

    private static bool Exists(Func<string, bool> overlayExists, string name)
    {
        try
        {
            return overlayExists(name);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Prüfung des Overlay-Bilds fehlgeschlagen.", ex);
            return false;
        }
    }

    /// <summary>Nicht leer, nur ein Dateiname (kein Pfad, kein „.“/„..“), keine ungültigen Zeichen.</summary>
    private static bool IsValidOverlayName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name != "." && name != ".."
        && Path.GetFileName(name) == name
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>
    /// Liest die Datei als Text (UTF-8, Byte-Order-Mark erlaubt) auf dem Threadpool mit <see cref="IoTimeout"/>.
    /// </summary>
    /// <exception cref="SettingsImportException">Die Datei ist größer als <see cref="MaxFileBytes"/>.</exception>
    /// <exception cref="TimeoutException">Lesen dauert länger als <see cref="IoTimeout"/>.</exception>
    public static Task<string> ReadFileAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => ReadFile(path), ct).WaitAsync(IoTimeout, ct);

    /// <summary>
    /// Schreibt <paramref name="content"/> als UTF-8 ohne BOM: erst in <c>path.tmp</c>, dann ersetzt es die Zieldatei (bei
    /// einem Fehler wird die temporäre Datei gelöscht). Auf dem Threadpool mit <see cref="IoTimeout"/>.
    /// </summary>
    /// <exception cref="TimeoutException">Schreiben dauert länger als <see cref="IoTimeout"/> (der Vorgang kann im Hintergrund noch auslaufen).</exception>
    public static Task WriteFileAsync(string path, string content, CancellationToken ct = default) =>
        Task.Run(() => WriteFile(path, content), ct).WaitAsync(IoTimeout, ct);

    private static string ReadFile(string path)
    {
        const string tooLarge = "Die Datei ist zu groß.";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > MaxFileBytes)
            throw new SettingsImportException(tooLarge);

        // Auch bei einer wachsenden Datei höchstens MaxFileBytes + 1 Byte lesen.
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxFileBytes)
                throw new SettingsImportException(tooLarge);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void WriteFile(string path, string content)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                    writer.Write(content);
                stream.Flush(true);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); }
            catch { /* best effort */ }
            throw;
        }
    }
}
