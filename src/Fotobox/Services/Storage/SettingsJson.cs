using System.Text.Json;
using Fotobox.Core;

namespace Fotobox.Services.Storage;

/// <summary>
/// Gemeinsame JSON-Helfer für <see cref="JsonSettingsStore"/> (Wiederherstellung der PIN aus einer defekten Datei) und
/// <see cref="SettingsTransfer"/> (Import): Optionen für <see cref="JsonDocument"/>, Namenssuche ohne Beachtung der
/// Groß-/Kleinschreibung und das Lesen der PIN-Daten.
/// </summary>
internal static class SettingsJson
{
    /// <summary>Optionen zum Parsen von Einstellungs-JSON: Kommentare und abschließende Kommas sind erlaubt (wie <see cref="AppSettings.JsonOptions"/>).</summary>
    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Sucht die Eigenschaft <paramref name="name"/> in einem JSON-Objekt ohne Beachtung der Groß-/Kleinschreibung.
    /// Bei doppelten Namen gewinnt wie beim Deserialisieren der letzte Eintrag.
    /// </summary>
    public static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }
        return found;
    }

    /// <summary>
    /// Zieht die PIN-Daten (PinHash, PinSalt, PinIterations; Namen ohne Beachtung der Groß-/Kleinschreibung) aus einer
    /// ansonsten unbrauchbaren Datei, damit ein Fehler an anderer Stelle nicht zum Erststart ohne PIN führt.
    /// Schlägt fehl, wenn das JSON nicht parsebar ist oder Hash/Salt fehlen bzw. keine Texte sind.
    /// </summary>
    public static bool TryRescueSecurity(string json, out SecuritySettings security)
    {
        security = new SecuritySettings();
        try
        {
            using var doc = JsonDocument.Parse(json, DocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(doc.RootElement, nameof(AppSettings.Security), out var sec))
                return false;

            return TryReadSecurity(sec, out security);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Liest die PIN-Daten aus dem Objekt <paramref name="sec"/> (der <c>Security</c>-Abschnitt). Hash und Salt müssen nicht leere
    /// Texte sein; fehlt die Iterationszahl, gilt der Standard, ein kaputter Wert macht den Hash unbrauchbar (false).
    /// Prüft nicht, ob die Werte plausibel sind (Base64, Längen, Obergrenzen).
    /// </summary>
    public static bool TryReadSecurity(JsonElement sec, out SecuritySettings security)
    {
        security = new SecuritySettings();
        if (sec.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryGetProperty(sec, nameof(SecuritySettings.PinHash), out var hash) || hash.ValueKind != JsonValueKind.String ||
            !TryGetProperty(sec, nameof(SecuritySettings.PinSalt), out var salt) || salt.ValueKind != JsonValueKind.String)
            return false;

        var hashText = hash.GetString();
        var saltText = salt.GetString();
        if (string.IsNullOrEmpty(hashText) || string.IsNullOrEmpty(saltText))
            return false;

        // Fehlt die Iterationszahl, gilt wie beim normalen Laden der Standardwert; ein kaputter Wert macht den Hash unbrauchbar.
        var iterations = security.PinIterations;
        if (TryGetProperty(sec, nameof(SecuritySettings.PinIterations), out var it) &&
            !(it.ValueKind == JsonValueKind.Number && it.TryGetInt32(out iterations)))
            return false;

        security = new SecuritySettings { PinHash = hashText, PinSalt = saltText, PinIterations = iterations };
        return true;
    }
}
