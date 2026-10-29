using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fotobox.Core;

public enum LayoutKind { SingleBorderless, Strip, Overlay }
/// <summary>DoubleCut: zwei gleiche Streifen nebeneinander (zum Teilen); Single: ein Streifen über die ganze Seite.</summary>
public enum StripStyle { DoubleCut, Single }
/// <summary>Per Mausrad wählbare Layoutvariante der Gastansicht (fasst <see cref="LayoutKind"/> und <see cref="StripStyle"/> zusammen).</summary>
public enum GuestLayout { Single, StripDouble, StripSingle, Overlay }
/// <summary>Auto: Strip → Hochformat, sonst Querformat.</summary>
public enum PageOrientation { Auto, Portrait, Landscape }
/// <summary>
/// Darstellung des Hinweistexts in der Gastansicht: Pille mit Rahmen, schlichter Text mit Schatten, Band über die volle Breite,
/// Neon-Schrift, Sprechblase, Laufschrift, Glaskarte, Zettel oder Kino-Filmstreifen. Neue Stile nur hinten anhängen (Reihenfolge = Reihenfolge der Bereiche in <c>GuestHint</c>).
/// </summary>
public enum HintStyle { Pill, Plain, Banner, Neon, Bubble, Ticker, Glass, Note, Cinema }
/// <summary>
/// Darstellung der Mausradzeile („Mausrad: Layout wechseln · …“): passend zum Hinweis-Stil (wie bisher), kleine Pille, schlichter Text,
/// Chip oder Pfeile.
/// </summary>
public enum WheelHintStyle { Matching, SmallPill, Plain, Chip, Arrows }

/// <summary>Qualitätsstufe der Videoaufnahme (MP4/H.264); die Auflösung ist immer die der laufenden Kamera.</summary>
public enum VideoQuality { High, Medium, Low }

/// <summary>Farbschema der Admin-Ansicht und der PIN-Dialoge; <see cref="System"/> folgt dem Windows-App-Modus.</summary>
public enum AdminThemeMode { System, Light, Dark }

/// <summary>Fun „Zielerfassung“: Ganzkörper per KI-Modell (ONNX) oder Gesichter per Windows-<c>FaceDetector</c>.</summary>
public enum DetectionMethod { Body, Face }
/// <summary>Was die Zielbox umrahmt. Bei Gesichtserkennung wird aus dem Gesicht hochgerechnet.</summary>
public enum TargetScope { Head, UpperBody, FullBody }
/// <summary>Aussehen der Zielbox. Neue Stile nur hinten anhängen.</summary>
public enum TargetBoxStyle { Corners, Rectangle, CornersCrosshair }
/// <summary>Feste Farbpalette für Zielbox, Beschriftung und HUD-Text. Neue Farben nur hinten anhängen.</summary>
public enum TargetColor { Red, Green, Cyan, Yellow, White }
/// <summary>Einrasten einer neuen Zielbox: Zusammenziehen + Pulsieren, nur Pulsieren, Scan-Linie + Pulsieren.</summary>
public enum LockOnAnimation { ShrinkPulse, Pulse, ScanPulse }
/// <summary>Position des Fun-Texts in der Gastansicht.</summary>
public enum FunTextPosition { Center, Bottom, Top }
/// <summary>Fun-Text im eigenen HUD-Stil (Tipp-Effekt) oder in einem der Hinweis-Stile (<see cref="FunSettings.TextHintStyle"/>).</summary>
public enum FunTextStyle { Hud, Hint }
/// <summary>
/// Peaceerkennung, Aktion Schnurrbart: Bartform (<see cref="Random"/> = je Person zufällig, aus <see cref="PeaceSettings.RandomStyles"/>).
/// Nur hinten erweitern. Handlebar = Dalí (steil hochgezwirbelte Spitzen), FuManchu = lange, dünne Hängeenden, Horseshoe = Hufeisen
/// (seitlich bis zum Kinn herunter).
/// </summary>
public enum MoustacheStyle { Random, Twirl, Walrus, Chevron, Brush, Handlebar, FuManchu, Horseshoe }
/// <summary>Peaceerkennung, Aktion Schnurrbart: wer einen Bart bekommt. Nur hinten erweitern.</summary>
public enum MoustacheTargets { Everyone, PeaceShower }
/// <summary>Peaceerkennung, Aktion Schnurrbart: wie lange der Bart sichtbar bleibt. Nur hinten erweitern.</summary>
public enum MoustacheVisibility { UntilPhotoOrGone, FixedDuration }
/// <summary>Herzerkennung: erkennbare Herzformen (Classic = Daumen und Zeigefinger beider Hände, Roof = Fingerspitzen oben zusammen, Inverted = auf den Kopf gestellt, FingerHeart = Fingerherz einer Hand). Nur hinten erweitern.</summary>
public enum HeartShape { Classic, Roof, Inverted, FingerHeart }
/// <summary>Herzerkennung: Aktion, die eine Herzgeste auslöst. Nur hinten erweitern.</summary>
public enum HeartAction { Kisscam }
/// <summary>Kisscam: Farbschema des Jumbotron-Rahmens. Nur hinten erweitern.</summary>
public enum KisscamColor { PinkRed, Red, Gold, Purple, BlackWhite }

/// <summary>Gemeinsame Wertebereiche für Admin-UI, Validierung und Normalisierung.</summary>
public static class SettingsLimits
{
    public const int CountdownMin = 1, CountdownMax = 30;
    public const int PreviewMin = 1, PreviewMax = 60;
    public const int PauseMin = 0, PauseMax = 30;
    public const int ErrorDisplayMin = 2, ErrorDisplayMax = 60;
    public const int StripPhotosMin = 2, StripPhotosMax = 4;
    public const int CopiesMin = 1, CopiesMax = 20;
    public const int PrintTimeoutMin = 10, PrintTimeoutMax = 900;
    /// <summary>Erwartete Druckdauer (Fortschrittsbalken in der Gastansicht).</summary>
    public const int ExpectedPrintMin = 30, ExpectedPrintMax = 600;
    /// <summary>Das Druck-Timeout liegt immer mindestens so weit über der erwarteten Druckdauer.</summary>
    public const int PrintTimeoutReserveSeconds = 60;
    public const int KeysPerActionMax = 8;
    public const int FontNameMaxLength = 100;
    public const int JpegQualityMin = 50, JpegQualityMax = 100;
    /// <summary>Längster Dateinamen-Präfix; längere Werte aus der settings.json werden beim Laden gekürzt.</summary>
    public const int FilePrefixMaxLength = 40;
    /// <summary>Längste Bildunterschrift; längere Werte aus der settings.json werden beim Laden gekürzt.</summary>
    public const int CaptionMaxLength = 80;
    /// <summary>Längster eigener Hinweistext der Gastansicht (mit Platzhaltern, vor dem Ersetzen); längere Werte aus der settings.json werden beim Laden gekürzt.</summary>
    public const int HintTextMaxLength = 100;
    /// <summary>Längste gespeicherte Monitor-Id (Geräte-Pfad); längere Werte aus der settings.json werden verworfen (gekürzt passte die Id nie).</summary>
    public const int MonitorIdMaxLength = 512;
    /// <summary>Längster gespeicherter Monitorname (nur Anzeige); längere Werte aus der settings.json werden gekürzt.</summary>
    public const int MonitorNameMaxLength = 200;
    /// <summary>Längste gespeicherte Kamera-Id (Geräte-Id der Reglerwerte und Profile); längere Werte werden verworfen (gekürzt passte die Id nie).</summary>
    public const int CameraDeviceIdMaxLength = 512;
    /// <summary>
    /// Kamerasteuerung: Höchstzahl benannter Profile je Kamera und Länge des Profilnamens (längere Namen kürzt der Normalizer).
    /// Die Wertebereiche der Regler selbst kommen zur Laufzeit vom Treiber und werden beim Anwenden im Kameradienst geklemmt
    /// (<see cref="CameraControlMath"/>), nicht hier.
    /// </summary>
    public const int CameraProfilesMax = 10, CameraProfileNameMaxLength = 40;
    /// <summary>Videoaufnahme: Höchstdauer eines Clips in Sekunden (Standard 300).</summary>
    public const int VideoMaxDurationMin = 10, VideoMaxDurationMax = 1800;
    /// <summary>Countdown vor dem Video; 0 = sofort.</summary>
    public const int VideoCountdownMin = 0, VideoCountdownMax = 30;
    /// <summary>Mindestlänge: vorher wird ein Stopp ignoriert. Liegt immer unter der Höchstdauer.</summary>
    public const int VideoMinDurationMin = 0, VideoMinDurationMax = 30;
    /// <summary>Warnung vor dem automatischen Ende ab so vielen Sekunden Restzeit; 0 = aus.</summary>
    public const int VideoWarningMin = 0, VideoWarningMax = 120;
    /// <summary>Anzeigedauer der Erfolgsmeldung nach dem Speichern.</summary>
    public const int VideoSuccessDisplayMin = 1, VideoSuccessDisplayMax = 60;
    /// <summary>Längster eigener Erfolgstext; längere Werte aus der settings.json werden beim Laden gekürzt.</summary>
    public const int VideoSuccessTextMaxLength = 80;
    /// <summary>Längste gespeicherte Mikrofon-Id; längere Werte werden verworfen (gekürzt passte die Id nie).</summary>
    public const int MicrophoneIdMaxLength = 512;
    /// <summary>Längster gespeicherter Mikrofonname (nur Anzeige); längere Werte werden gekürzt.</summary>
    public const int MicrophoneNameMaxLength = 200;
    /// <summary>Länge der PIN (Ziffern).</summary>
    public const int PinMinLength = 4, PinMaxLength = 12;
    /// <summary>Fun: Anzeigedauer des Texts nach dem Einrasten.</summary>
    public const int FunTextSecondsMin = 2, FunTextSecondsMax = 30;
    /// <summary>Fun: Pause nach dem Text, bevor eine neu auftauchende Gruppe wieder auslöst (Zuwachs löst trotzdem aus).</summary>
    public const int FunCooldownSecondsMin = 0, FunCooldownSecondsMax = 600;
    /// <summary>Fun: Abstand der Wiederholung bei gleichbleibender Gruppe, gerechnet ab dem Ende des Texts.</summary>
    public const int FunRepeatSecondsMin = 10, FunRepeatSecondsMax = 600;
    /// <summary>Fun: Mindesthöhe einer Zielbox in Prozent der Bildhöhe (kleinere = Hintergrund, ignoriert).</summary>
    public const int FunMinSizePercentMin = 5, FunMinSizePercentMax = 80;
    /// <summary>Fun: Höchstzahl gleichzeitiger Zielboxen (die größten gewinnen).</summary>
    public const int FunMaxTargetsMin = 1, FunMaxTargetsMax = 10;
    /// <summary>Fun: Empfindlichkeit in Prozent; Mindestkonfidenz = (100 − Wert) / 100.</summary>
    public const int FunSensitivityMin = 10, FunSensitivityMax = 95;
    /// <summary>Fun: So lange muss eine Person erkannt sein, bevor sie anvisiert wird.</summary>
    public const int FunDwellMillisecondsMin = 0, FunDwellMillisecondsMax = 3000;
    /// <summary>Fun: Dauer der Einrast-Animation (Millisekunden, in 100er-Schritten einstellbar), danach erscheint der Text.</summary>
    public const int FunLockOnMillisecondsMin = 500, FunLockOnMillisecondsMax = 5000, FunLockOnMillisecondsStep = 100;
    /// <summary>Fun: Intensität der Einrast-Animation in Prozent (Bewegung, Leuchten, Blinken, Lautstärke); 50 = bisheriges Aussehen.</summary>
    public const int FunLockOnIntensityMin = 0, FunLockOnIntensityMax = 100, FunLockOnIntensityStep = 5;
    /// <summary>Fun: Analysen pro Sekunde (Rechenlast); das Livebild hat immer Vorrang.</summary>
    public const int FunAnalysesPerSecondMin = 1, FunAnalysesPerSecondMax = 10;
    /// <summary>Peace: Haltedauer des Zeichens in Millisekunden (100er-Schritte).</summary>
    public const int PeaceHoldMillisecondsMin = 300, PeaceHoldMillisecondsMax = 3000, PeaceHoldMillisecondsStep = 100;
    /// <summary>Peace: Sperrzeit nach einer Auslösung in Sekunden.</summary>
    public const int PeaceCooldownSecondsMin = 0, PeaceCooldownSecondsMax = 600;
    /// <summary>Peace: Empfindlichkeit in Prozent.</summary>
    public const int PeaceSensitivityMin = 10, PeaceSensitivityMax = 95;
    /// <summary>Peace: Analysen pro Sekunde (Rechenlast).</summary>
    public const int PeaceAnalysesPerSecondMin = 1, PeaceAnalysesPerSecondMax = 10;
    /// <summary>Peace: Dauer der Selbstzerstörung in Sekunden.</summary>
    public const int PeaceSelfDestructSecondsMin = 5, PeaceSelfDestructSecondsMax = 30;
    /// <summary>Peace: Countdown der Aktion Foto in Sekunden (0 = sofort).</summary>
    public const int PeacePhotoCountdownMin = 0, PeacePhotoCountdownMax = 30;
    /// <summary>Peace: feste Sichtbarkeitsdauer des Schnurrbarts in Sekunden.</summary>
    public const int PeaceMoustacheDurationMin = 3, PeaceMoustacheDurationMax = 120;
    /// <summary>Peace: Countdown des Auto-Fotos nach dem Schnurrbart in Sekunden (0 = sofort).</summary>
    public const int PeaceMoustacheAutoPhotoCountdownMin = 0, PeaceMoustacheAutoPhotoCountdownMax = 30;
    /// <summary>Mittelfinger: Haltedauer des Zeichens in Millisekunden (100er-Schritte).</summary>
    public const int MiddleFingerHoldMillisecondsMin = 200, MiddleFingerHoldMillisecondsMax = 3000, MiddleFingerHoldMillisecondsStep = 100;
    /// <summary>Mittelfinger: Empfindlichkeit in Prozent.</summary>
    public const int MiddleFingerSensitivityMin = 10, MiddleFingerSensitivityMax = 95;
    /// <summary>Herz: Haltedauer der Geste in Millisekunden (100er-Schritte).</summary>
    public const int HeartHoldMillisecondsMin = 300, HeartHoldMillisecondsMax = 3000, HeartHoldMillisecondsStep = 100;
    /// <summary>Herz: Empfindlichkeit in Prozent.</summary>
    public const int HeartSensitivityMin = 10, HeartSensitivityMax = 95;
    /// <summary>Kisscam: Dauer des Countdowns in Sekunden.</summary>
    public const int KisscamSecondsMin = 3, KisscamSecondsMax = 60;
    /// <summary>Kisscam: Digitalzoom in Prozent (10er-Schritte, 100 = kein Zoom).</summary>
    public const int KisscamZoomPercentMin = 110, KisscamZoomPercentMax = 200, KisscamZoomPercentStep = 10;
    /// <summary>Kisscam: Lautstärke der Musik in Prozent (5er-Schritte).</summary>
    public const int KisscamMusicVolumeMin = 0, KisscamMusicVolumeMax = 100, KisscamMusicVolumeStep = 5;
    /// <summary>Kisscam: längster Banner-Text (z. B. „KISS CAM“); längere Werte aus der settings.json werden beim Laden gekürzt.</summary>
    public const int KisscamBannerMaxLength = 30;
    /// <summary>Kisscam: In den letzten so vielen Sekunden des Countdowns erscheinen große Ziffern.</summary>
    public const int KisscamFinalDigitsSeconds = 3;
    /// <summary>Kisscam: längster gespeicherter Pfad des Musikordners; längere Werte aus der settings.json werden verworfen (gekürzt passte der Pfad nie).</summary>
    public const int KisscamMusicFolderMaxLength = 1024;
    /// <summary>Kisscam: Startzeit eines Songs in Sekunden (1-s-Schritte; 3599 = 59:59).</summary>
    public const int KisscamSongStartSecondsMin = 0, KisscamSongStartSecondsMax = 3599, KisscamSongStartSecondsStep = 1;
    /// <summary>Kisscam: Lautstärke eines Songs in Prozent der Gesamtlautstärke (5er-Schritte).</summary>
    public const int KisscamSongVolumeMin = 10, KisscamSongVolumeMax = 100, KisscamSongVolumeStep = 5, KisscamSongVolumeDefault = 100;
    /// <summary>Kisscam: höchste Zahl berücksichtigter Songs im Musikordner (weitere Dateien werden ignoriert).</summary>
    public const int KisscamMusicMaxSongs = 500;
    /// <summary>Admin-Ansicht: Zoom des Inhaltsbereichs in Prozent (5er-Schritte, Standard 80); Tab-Leiste und Fußleiste bleiben bei 100 %.</summary>
    public const int AdminZoomMin = 60, AdminZoomMax = 100, AdminZoomStep = 5, AdminZoomDefault = 80;
}

/// <summary>
/// Liest Enums tolerant: Namen (ohne Beachtung der Groß-/Kleinschreibung) und Zahlen. Ein unbekannter Name führt nicht zu
/// einer <see cref="JsonException"/> (sonst gälte die ganze Datei als defekt, auch die PIN ginge verloren), sondern ergibt
/// einen undefinierten Wert, den der Normalizer per <see cref="Enum.IsDefined{TEnum}(TEnum)"/> auf den Standard zurücksetzt
/// bzw. aus Listen einzeln verwirft. Geschrieben wird wie bei <see cref="JsonStringEnumConverter"/> der Name.
/// </summary>
public sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        // Ein Wert, der in TEnum nicht definiert ist (für unbekannte Namen und Zahlen außerhalb des Wertebereichs).
        private static readonly TEnum Undefined = FindUndefined();

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var named) ? named : Undefined;
                case JsonTokenType.Number:
                    return reader.TryGetInt64(out var number) ? FromLong(number) : Undefined;
                default:
                    throw new JsonException($"Ungültiger Wert für {typeof(TEnum).Name}.");
            }
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            if (Enum.IsDefined(value))
                writer.WriteStringValue(value.ToString());
            else
                writer.WriteNumberValue(Convert.ToInt64(value));
        }

        private static TEnum FromLong(long number)
        {
            var candidate = (TEnum)Enum.ToObject(typeof(TEnum), number);
            // Zahlen außerhalb des Basistyps würden sonst abgeschnitten und könnten zufällig einen definierten Wert ergeben.
            return Convert.ToInt64(candidate) == number ? candidate : Undefined;
        }

        private static TEnum FindUndefined()
        {
            for (long n = -1; ; n--)
            {
                var candidate = (TEnum)Enum.ToObject(typeof(TEnum), n);
                if (!Enum.IsDefined(candidate))
                    return candidate;
            }
        }
    }
}

public sealed class AppSettings
{
    /// <summary>Gemeinsame Serializer-Optionen für Store und <see cref="Clone"/> (Enums als Strings, beim Lesen tolerant).</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new TolerantEnumConverterFactory() }
    };

    /// <summary>Aktuelles Format der settings.json. 2: Fotobox+ (Video, Präfix-Migration „Fotobox“ → „Fotobox+“).</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public CameraSettings Camera { get; set; } = new();
    public FlowSettings Flow { get; set; } = new();
    public LayoutSettings Layout { get; set; } = new();
    public PrinterSettings Printer { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public InputSettings Input { get; set; } = new();
    public GuestScreenSettings GuestScreen { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public VideoSettings Video { get; set; } = new();
    public FunSettings Fun { get; set; } = new();
    public PeaceSettings Peace { get; set; } = new();
    public MiddleFingerSettings MiddleFinger { get; set; } = new();
    public HeartSettings Heart { get; set; } = new();

    /// <summary>Tiefe Kopie via JSON-Roundtrip.</summary>
    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions) ?? new AppSettings();
}

public sealed class CameraSettings
{
    public string? DeviceId { get; set; }
    public int? ResolutionWidth { get; set; }
    public int? ResolutionHeight { get; set; }
    public bool MirrorPreview { get; set; } = true;
    public bool MirrorPhoto { get; set; } = false;

    /// <summary>
    /// Die tatsächlich geöffnete Kamera (aufgelöste Geräte-Id, nicht <see cref="DeviceId"/>, das null = „erstes Gerät“ sein kann),
    /// zu der <see cref="ControlValues"/> gehört. Gerätespezifisch (wird nicht geteilt).
    /// </summary>
    public string? ControlsDeviceId { get; set; }

    /// <summary>
    /// Nur die im Admin verstellten Regler der Kamera <see cref="ControlsDeviceId"/> (Rohwerte des Treibers). Die Fotobox setzt
    /// beim Start nur diese und lässt den Rest dem Treiber. Gerätespezifisch (wird nicht geteilt).
    /// </summary>
    public List<CameraControlValue> ControlValues { get; set; } = [];

    /// <summary>Benannte Profile mit Reglerwerten, je an eine Kamera gebunden. Gerätespezifisch (wird nicht geteilt).</summary>
    public List<CameraControlProfile> ControlProfiles { get; set; } = [];

    /// <summary>
    /// Flimmerschutz (Netzfrequenz des Raumlichts), Standard 50 Hz. Bewusste Ausnahme von „nur Verstelltes setzen“: wird bei jedem
    /// Kamerastart gesetzt; <see cref="CameraPowerline.Unchanged"/> lässt den Treiber unberührt. Übertragbar (hängt vom Ort ab).
    /// </summary>
    public CameraPowerline Powerline { get; set; } = CameraPowerline.Hz50;

    /// <summary>Tiefe Kopie aller Felder (auch der Listen und ihrer Objekte).</summary>
    public CameraSettings Clone() => new()
    {
        DeviceId = DeviceId,
        ResolutionWidth = ResolutionWidth,
        ResolutionHeight = ResolutionHeight,
        MirrorPreview = MirrorPreview,
        MirrorPhoto = MirrorPhoto,
        ControlsDeviceId = ControlsDeviceId,
        ControlValues = CloneValues(ControlValues),
        ControlProfiles = CloneProfiles(ControlProfiles),
        Powerline = Powerline,
    };

    /// <summary>Tiefe Kopie einer Reglerwerte-Liste (null → leer).</summary>
    internal static List<CameraControlValue> CloneValues(IEnumerable<CameraControlValue>? values) =>
        values is null ? [] : values.Select(v => v?.Clone()!).ToList();

    /// <summary>Tiefe Kopie einer Profilliste (null → leer, null-Einträge entfallen).</summary>
    internal static List<CameraControlProfile> CloneProfiles(IEnumerable<CameraControlProfile?>? profiles) =>
        profiles is null ? [] : profiles.Where(p => p is not null).Select(p => p!.Clone()).ToList();

    /// <summary>
    /// Muss die Kamera neu gestartet werden, damit <paramref name="other"/> gilt? Nur bei anderem Gerät oder anderer Auflösung;
    /// Spiegeln wirkt ohne Neustart (Vorschau live per Bindung, Foto liest der Controller je Durchgang). Reglerwerte, Profile und
    /// Flimmerschutz lösen keinen Neustart aus (sie gelten live bzw. über <c>ICameraService.ConfigureControls</c>; <c>StartAsync</c> ändert den gespeicherten Reglerstand nicht).
    /// </summary>
    public bool RequiresRestartComparedTo(CameraSettings other) =>
        DeviceId != other.DeviceId
        || ResolutionWidth != other.ResolutionWidth
        || ResolutionHeight != other.ResolutionHeight;
}

public sealed class FlowSettings
{
    public int CountdownSeconds { get; set; } = 5;
    public int PreviewSeconds { get; set; } = 4;
    public int PauseBetweenShotsSeconds { get; set; } = 2;
    public int ErrorDisplaySeconds { get; set; } = 6;
    public bool PrintingEnabled { get; set; } = true;
    /// <summary>So lange braucht der Drucker etwa; danach ist der Fortschrittsbalken voll.</summary>
    public int ExpectedPrintSeconds { get; set; } = 240;
}

public sealed class LayoutSettings
{
    public LayoutKind Kind { get; set; } = LayoutKind.SingleBorderless;
    public int StripPhotoCount { get; set; } = 3;
    public StripStyle StripStyle { get; set; } = StripStyle.DoubleCut;
    public string? OverlayFileName { get; set; }
    public string? CaptionText { get; set; }
    public bool ShowDate { get; set; } = false;
    /// <summary>Schriftfamilie der Bildunterschrift (<c>FontFamily.Source</c>); fehlt sie, wird Segoe UI verwendet.</summary>
    public string CaptionFontFamily { get; set; } = DefaultCaptionFont;
    /// <summary>
    /// Layouts, zwischen denen Gäste per Mausrad wählen dürfen. Leer = kein Wechsel, es gilt immer
    /// <see cref="Kind"/>/<see cref="StripStyle"/>. Der Standard ist nach der Normalisierung enthalten, sobald die Liste nicht leer ist.
    /// Die Reihenfolge der Liste ist die Reihenfolge beim Scrollen (im Admin einstellbar).
    /// </summary>
    public List<GuestLayout> GuestChoices { get; set; } = [];

    public const string DefaultCaptionFont = "Segoe UI";
}

/// <summary>Darstellung der Gastansicht im Leerlauf.</summary>
public sealed class GuestScreenSettings
{
    /// <summary>
    /// Eigener Hinweistext mit Platzhaltern (<see cref="HintTextFormatter"/>); null = automatischer Text aus den eingestellten Tasten.
    /// </summary>
    public string? HintText { get; set; }
    public HintStyle HintStyle { get; set; } = HintStyle.Pill;
    /// <summary>
    /// Eigener Text der Video-Hinweiszeile (oben im Bild) mit Platzhaltern; null = automatischer Text aus den Video-Starttasten.
    /// Die Zeile erscheint nur bei aktivem Videomodus.
    /// </summary>
    public string? VideoHintText { get; set; }
    public HintStyle VideoHintStyle { get; set; } = HintStyle.Pill;
    /// <summary>Mausradzeile der Layoutwahl anzeigen (erscheint nur, wenn Gäste zwischen mindestens zwei Layouts wählen können).</summary>
    public bool ShowWheelHint { get; set; } = true;
    /// <summary>Eigener Text der Mausradzeile (wörtlich, ohne Platzhalter); null = automatischer Text mit dem Namen des Layouts.</summary>
    public string? WheelHintText { get; set; }
    public WheelHintStyle WheelHintStyle { get; set; } = WheelHintStyle.Matching;
}

/// <summary>Darstellung: Farbschema der Admin-Ansicht und Monitor der Gastansicht.</summary>
public sealed class AppearanceSettings
{
    /// <summary>Gilt für Admin-Fenster und PIN-Dialoge, nicht für die Gastansicht.</summary>
    public AdminThemeMode AdminTheme { get; set; } = AdminThemeMode.System;
    /// <summary>
    /// Zoom des Inhaltsbereichs der Admin-Ansicht in Prozent (<see cref="SettingsLimits.AdminZoomMin"/> bis
    /// <see cref="SettingsLimits.AdminZoomMax"/> in <see cref="SettingsLimits.AdminZoomStep"/>er-Schritten, Standard 80). Gerätespezifisch
    /// (hängt vom Bildschirm ab): wird nicht exportiert und beim Import nicht überschrieben.
    /// </summary>
    public int AdminZoomPercent { get; set; } = SettingsLimits.AdminZoomDefault;
    /// <summary>Monitor der Gastansicht (<see cref="DisplayMonitor.Id"/>); null = Hauptbildschirm. Fehlt er, läuft die Gastansicht auf dem Hauptbildschirm.</summary>
    public string? GuestMonitorId { get; set; }
    /// <summary>Letzter bekannter Anzeigename des gewählten Monitors, nur für den Hinweis „nicht angeschlossen“.</summary>
    public string? GuestMonitorName { get; set; }
}

public sealed class PrinterSettings
{
    /// <summary>null = Standarddrucker.</summary>
    public string? PrinterName { get; set; }
    public string? PaperSizeName { get; set; }
    public double? PaperWidthMm { get; set; }
    public double? PaperHeightMm { get; set; }
    public PageOrientation Orientation { get; set; } = PageOrientation.Auto;
    public int Copies { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 300;
}

public sealed class StorageSettings
{
    /// <summary>null = <see cref="AppInfo.DefaultPhotoFolder"/> (Bilder\Fotobox+).</summary>
    public string? PhotoFolder { get; set; }
    public bool DateSubfolder { get; set; } = true;
    public bool SaveOriginals { get; set; } = true;
    public int JpegQuality { get; set; } = 92;
    public string FilePrefix { get; set; } = DefaultFilePrefix;

    public const string DefaultFilePrefix = "Fotobox+";
    /// <summary>Standardpräfix bis Version 1; wird beim Laden alter Dateien zu <see cref="DefaultFilePrefix"/>.</summary>
    public const string LegacyFilePrefix = "Fotobox";
}

/// <summary>Videomodus: Clips per eigener Start-/Stopptaste, werden nur gespeichert (nicht angezeigt oder gedruckt).</summary>
public sealed class VideoSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxDurationSeconds { get; set; } = 300;
    public int CountdownSeconds { get; set; } = 3;
    public int MinDurationSeconds { get; set; } = 2;
    /// <summary>Warnung in der Gastansicht ab so vielen Sekunden Restzeit; 0 = keine Warnung.</summary>
    public int WarningSeconds { get; set; } = 10;
    public bool ShowRecDot { get; set; } = true;
    public bool ShowElapsed { get; set; } = true;
    public bool ShowRemaining { get; set; } = true;
    public int SuccessDisplaySeconds { get; set; } = 3;
    /// <summary>Text nach erfolgreichem Speichern; null/leer = <see cref="DefaultSuccessText"/>.</summary>
    public string? SuccessText { get; set; }
    public VideoQuality Quality { get; set; } = VideoQuality.High;
    public bool RecordAudio { get; set; } = true;
    /// <summary>Mikrofon (DeviceInformation.Id); null = Windows-Standardmikrofon.</summary>
    public string? MicrophoneId { get; set; }
    /// <summary>Letzter bekannter Name des gewählten Mikrofons, nur für den Hinweis „nicht angeschlossen“.</summary>
    public string? MicrophoneName { get; set; }
    /// <summary>null = <see cref="AppInfo.DefaultVideoFolder"/> (Videos\Fotobox+).</summary>
    public string? Folder { get; set; }
    public bool DateSubfolder { get; set; } = true;
    public string FilePrefix { get; set; } = DefaultFilePrefix;

    public const string DefaultFilePrefix = "Fotobox+_Video";
    public const string DefaultSuccessText = "Video gespeichert! 🎬";

    /// <summary>
    /// Muss die Kamera neu gestartet werden, damit <paramref name="other"/> gilt? Nur wenn sich ändert, ob bzw. welches
    /// Mikrofon die Kamera-Session öffnet (Ton wird beim Initialisieren der Session festgelegt).
    /// </summary>
    public bool RequiresCameraRestartComparedTo(VideoSettings other) =>
        UsesAudio != other.UsesAudio || (UsesAudio && MicrophoneId != other.MicrophoneId);

    /// <summary>Öffnet die Kamera-Session ein Mikrofon?</summary>
    public bool UsesAudio => Enabled && RecordAudio;
}

/// <summary>
/// Fun „Zielerfassung“: Personen im Livebild der Gastansicht (nur im Leerlauf) mit Zielboxen „TARGET n“ markieren, einrasten lassen
/// und einen Text einblenden. Nur Darstellung in der Gastansicht, nie im Foto oder Video.
/// </summary>
public sealed class FunSettings
{
    public bool Enabled { get; set; } = true;
    public DetectionMethod Method { get; set; } = DetectionMethod.Body;
    public TargetScope Scope { get; set; } = TargetScope.FullBody;
    public TargetBoxStyle BoxStyle { get; set; } = TargetBoxStyle.Corners;
    public TargetColor Color { get; set; } = TargetColor.Red;
    public LockOnAnimation Animation { get; set; } = LockOnAnimation.ShrinkPulse;
    /// <summary>Text bei einer Person (Platzhalter <c>{Anzahl}</c>); null/leer = <see cref="DefaultSingularText"/>.</summary>
    public string? SingularText { get; set; }
    /// <summary>Text bei mehreren Personen (Platzhalter <c>{Anzahl}</c>); null/leer = <see cref="DefaultPluralText"/>.</summary>
    public string? PluralText { get; set; }
    public FunTextPosition TextPosition { get; set; } = FunTextPosition.Center;
    public FunTextStyle TextStyle { get; set; } = FunTextStyle.Hud;
    /// <summary>Hinweis-Stil des Texts, wenn <see cref="TextStyle"/> = <see cref="FunTextStyle.Hint"/>.</summary>
    public HintStyle TextHintStyle { get; set; } = HintStyle.Pill;
    public int TextSeconds { get; set; } = 6;
    public int CooldownSeconds { get; set; } = 20;
    /// <summary>
    /// Ein Personentausch löst aus: Eine neu bestätigte Person (neue Spur), die in dieser Episode noch nicht da war, zählt wie
    /// ein Zuwachs, auch wenn die Anzahl gleich bleibt. Aus: nur ein Anstieg über die bisherige Höchstzahl löst aus.
    /// </summary>
    public bool SwapTriggers { get; set; } = true;
    /// <summary>Bleibt dieselbe Gruppe da, löst die Erfassung nach <see cref="RepeatSeconds"/> erneut aus.</summary>
    public bool RepeatEnabled { get; set; }
    /// <summary>Abstand der Wiederholung ab Textende (frühestens nach der Pause); nur mit <see cref="RepeatEnabled"/>.</summary>
    public int RepeatSeconds { get; set; } = 60;
    public int MinSizePercent { get; set; } = 15;
    public int MaxTargets { get; set; } = 6;
    public int SensitivityPercent { get; set; } = 50;
    public int DwellMilliseconds { get; set; } = 800;
    /// <summary>Dauer des Einrastens (Zielmarke zieht sich zusammen, „LOCKING…“ blinkt) bis zum Erscheinen des Texts.</summary>
    public int LockOnMilliseconds { get; set; } = 1200;
    /// <summary>Wie auffällig das Einrasten ist (Bewegung, Leuchten, Blinken, Lautstärke der Töne); 50 = bisheriges Verhalten.</summary>
    public int LockOnIntensityPercent { get; set; } = 50;
    public int AnalysesPerSecond { get; set; } = 5;
    public bool SoundEnabled { get; set; }

    public const string DefaultSingularText = "Hey du, nimm doch ein Foto oder Video mit mir auf!";
    public const string DefaultPluralText = "Hey ihr, nehmt doch ein Foto oder Video mit mir auf!";

    /// <summary>Mindestkonfidenz einer Erkennung (0,05 … 0,9) aus der Empfindlichkeit.</summary>
    [JsonIgnore]
    public double MinConfidence => Math.Clamp((100 - SensitivityPercent) / 100.0, 0.05, 0.9);

    /// <summary>Tiefe Kopie.</summary>
    public FunSettings Clone() => (FunSettings)MemberwiseClone();
}

/// <summary>
/// Fun „Peaceerkennung“ (verstecktes Extra): Zeigt ein Gast im Leerlauf das Peace-Zeichen und hält es, startet eine der gewählten
/// Aktionen (Selbstzerstörung, Foto, Schnurrbart); bei mehreren gewählten Aktionen entscheidet der Zufall. Bis auf
/// <see cref="RandomStyles"/> nur Werttypen; <see cref="Clone"/> kopiert die Liste mit.
/// </summary>
public sealed class PeaceSettings
{
    public bool Enabled { get; set; } = true;
    public bool ActionSelfDestruct { get; set; } = true;
    public bool ActionPhoto { get; set; }
    public bool ActionMoustache { get; set; }
    /// <summary>So lange muss das Zeichen gehalten werden (Millisekunden).</summary>
    public int HoldMilliseconds { get; set; } = 1000;
    /// <summary>Fortschrittsring an der Hand während des Haltens.</summary>
    public bool ShowHoldProgress { get; set; }
    public int CooldownSeconds { get; set; }
    public int SensitivityPercent { get; set; } = 50;
    public int AnalysesPerSecond { get; set; } = 5;
    public int SelfDestructSeconds { get; set; } = 10;
    public bool SelfDestructSoundEnabled { get; set; }
    public int PhotoCountdownSeconds { get; set; } = 3;
    public MoustacheTargets MoustacheTargets { get; set; } = MoustacheTargets.Everyone;
    public MoustacheStyle MoustacheStyle { get; set; } = MoustacheStyle.Random;
    /// <summary>
    /// Formen, aus denen <see cref="MoustacheStyle.Random"/> je Person würfelt (ohne <see cref="MoustacheStyle.Random"/>, ohne Duplikate,
    /// in Enum-Reihenfolge; der Normalizer ersetzt eine leere Liste durch alle Formen).
    /// </summary>
    public List<MoustacheStyle> RandomStyles { get; set; } = [.. Fotobox.Core.Peace.MoustacheStyles.All];
    public MoustacheVisibility MoustacheVisibility { get; set; } = MoustacheVisibility.UntilPhotoOrGone;
    public int MoustacheDurationSeconds { get; set; } = 15;
    public bool MoustacheAutoPhoto { get; set; }
    public int MoustacheAutoPhotoCountdownSeconds { get; set; } = 3;

    /// <summary>Mindestens eine Aktion gewählt?</summary>
    [JsonIgnore]
    public bool HasAnyAction => ActionSelfDestruct || ActionPhoto || ActionMoustache;

    /// <summary>Mindestscore einer Handerkennung (0,55 … 0,9) linear aus der Empfindlichkeit (10 → 0,9, 95 → 0,55).</summary>
    [JsonIgnore]
    public double MinHandScore
    {
        get
        {
            var t = (SensitivityPercent - SettingsLimits.PeaceSensitivityMin)
                    / (double)(SettingsLimits.PeaceSensitivityMax - SettingsLimits.PeaceSensitivityMin);
            return Math.Clamp(0.9 - t * (0.9 - 0.55), 0.55, 0.9);
        }
    }

    /// <summary>Tiefe Kopie.</summary>
    public PeaceSettings Clone()
    {
        var copy = (PeaceSettings)MemberwiseClone();
        copy.RandomStyles = RandomStyles is null ? null! : [.. RandomStyles];
        return copy;
    }
}

/// <summary>
/// Was der Mittelfinger gerade aufheben könnte (Bitmaske, aktuell sichtbare Peace-Effekte). Künftige Aktionen hängen weitere Flags an.
/// </summary>
[Flags]
public enum MiddleFingerTargets { None = 0, Moustache = 1, SelfDestruct = 2, Kisscam = 4 }

/// <summary>
/// Fun „Mittelfingererkennung“ (verstecktes Extra): Zeigt ein Gast den Mittelfinger und hält ihn, läuft eine der gewählten Aktionen
/// (aktuell: Peace-Effekte aufheben, Kisscam abbrechen). Nur Werttypen.
/// </summary>
public sealed class MiddleFingerSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Aktion „Peace-Effekte aufheben“ (Bart entfernen, Selbstzerstörung abbrechen). Künftige Aktionen als weitere Bools hinten anfügen.</summary>
    public bool ActionCancelPeace { get; set; } = true;
    /// <summary>Aktion „Kisscam abbrechen“ (der Mittelfinger beendet den laufenden Kisscam-Countdown ohne Foto).</summary>
    public bool ActionCancelKisscam { get; set; } = true;
    /// <summary>So lange muss das Zeichen gehalten werden (Millisekunden).</summary>
    public int HoldMilliseconds { get; set; } = 500;
    public int SensitivityPercent { get; set; } = 50;

    /// <summary>Mindestens eine Aktion gewählt?</summary>
    [JsonIgnore]
    public bool HasAnyAction => ActionCancelPeace || ActionCancelKisscam;

    /// <summary>Mindestscore einer Handerkennung (0,55 … 0,9) linear aus der Empfindlichkeit (10 → 0,9, 95 → 0,55).</summary>
    [JsonIgnore]
    public double MinHandScore => MinHandScoreFor(SensitivityPercent);

    /// <summary>
    /// Einzige Quelle der Abbildung Empfindlichkeit → Mindestscore einer Handerkennung (10 → 0,9, 95 → 0,55, linear, außerhalb der
    /// Grenzen geklemmt); auch vom <c>MiddleFingerGestureClassifier</c> genutzt.
    /// </summary>
    public static double MinHandScoreFor(int sensitivityPercent)
    {
        var t = Math.Clamp((sensitivityPercent - SettingsLimits.MiddleFingerSensitivityMin)
                / (double)(SettingsLimits.MiddleFingerSensitivityMax - SettingsLimits.MiddleFingerSensitivityMin), 0, 1);
        return 0.9 - t * (0.9 - 0.55);
    }

    /// <summary>Gibt es etwas, das der Mittelfinger bei den aktuellen Einstellungen aufheben würde?</summary>
    public bool Wants(MiddleFingerTargets present) =>
        Enabled
        && ((ActionCancelPeace && (present & (MiddleFingerTargets.Moustache | MiddleFingerTargets.SelfDestruct)) != 0)
            || (ActionCancelKisscam && present.HasFlag(MiddleFingerTargets.Kisscam)));

    /// <summary>Tiefe Kopie.</summary>
    public MiddleFingerSettings Clone() => (MiddleFingerSettings)MemberwiseClone();
}

/// <summary>
/// Fun „Herzerkennung“ (verstecktes Extra): Formen zwei Gäste (oder einer als Fingerherz) ein Herz und halten es, startet die Aktion
/// <see cref="HeartAction.Kisscam"/> (langer Countdown mit Jumbotron-Rahmen, dann Einzelfoto mit Rahmen). Nur Werttypen und Strings.
/// </summary>
public sealed class HeartSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Klassisches Herz (Daumen und Zeigefinger beider Hände bilden ein Herz).</summary>
    public bool ShapeClassic { get; set; } = true;
    /// <summary>Dach-Herz (Fingerspitzen beider Hände oben zusammen).</summary>
    public bool ShapeRoof { get; set; } = true;
    /// <summary>Auf den Kopf gestelltes Herz (beider Hände).</summary>
    public bool ShapeInverted { get; set; }
    /// <summary>Fingerherz (Daumen und Zeigefinger einer Hand).</summary>
    public bool ShapeFingerHeart { get; set; }
    /// <summary>So lange muss die Geste gehalten werden (Millisekunden). Länger als bei Peace, weil eine Kisscam ein größerer Eingriff ist und kurz ineinandergelegte Hände nicht auslösen sollen.</summary>
    public int HoldMilliseconds { get; set; } = 1800;
    /// <summary>Empfindlichkeit in Prozent (wie bei Peace; Standard 50).</summary>
    public int SensitivityPercent { get; set; } = 50;
    /// <summary>Fortschrittsanzeige am Herz während des Haltens.</summary>
    public bool ShowHoldFeedback { get; set; } = true;
    public HeartAction Action { get; set; } = HeartAction.Kisscam;

    /// <summary>Dauer des Kisscam-Countdowns in Sekunden.</summary>
    public int KisscamSeconds { get; set; } = 10;
    /// <summary>Hinweistext während des Countdowns (Platzhalter <c>{Sekunden}</c>); null = <see cref="DefaultKisscamText"/>.</summary>
    public string? KisscamText { get; set; }
    /// <summary>Banner-Text im Rahmen; null = <see cref="DefaultKisscamBannerText"/>.</summary>
    public string? KisscamBannerText { get; set; }
    public KisscamColor KisscamColor { get; set; } = KisscamColor.PinkRed;
    /// <summary>Digitalzoom auf die Herz-Gäste während des Countdowns.</summary>
    public bool KisscamZoomEnabled { get; set; }
    public int KisscamZoomPercent { get; set; } = 140;
    /// <summary>Dateiname-Präfix des Kisscam-Fotos.</summary>
    public string KisscamFilePrefix { get; set; } = DefaultKisscamFilePrefix;
    /// <summary>
    /// Musikordner der Kisscam (Songs und Config <c>kisscam-musik.json</c> liegen darin); null = <see cref="AppInfo.DefaultKisscamMusicFolder"/>.
    /// Gerätespezifisch (wird nicht geteilt). Ein früheres Feld <c>KisscamMusicPath</c> (Einzeldatei) wird ignoriert.
    /// </summary>
    public string? KisscamMusicFolder { get; set; }
    public int KisscamMusicVolumePercent { get; set; } = 80;

    public const string DefaultKisscamText = "Du hast {Sekunden} Sekunden Zeit, dir jemanden zum Küssen zu suchen!";
    public const string DefaultKisscamBannerText = "KISS CAM";
    public const string DefaultKisscamFilePrefix = "Kisscam";

    /// <summary>Mindestens eine Herzform gewählt?</summary>
    [JsonIgnore]
    public bool HasAnyShape => ShapeClassic || ShapeRoof || ShapeInverted || ShapeFingerHeart;

    /// <summary>Ist die Herzform <paramref name="shape"/> eingeschaltet?</summary>
    public bool IsShapeEnabled(HeartShape shape) => shape switch
    {
        HeartShape.Classic => ShapeClassic,
        HeartShape.Roof => ShapeRoof,
        HeartShape.Inverted => ShapeInverted,
        HeartShape.FingerHeart => ShapeFingerHeart,
        _ => false,
    };

    /// <summary>Mindestscore einer Handerkennung (0,55 … 0,9) aus der Empfindlichkeit; gemeinsame Abbildung <see cref="MiddleFingerSettings.MinHandScoreFor"/>.</summary>
    [JsonIgnore]
    public double MinHandScore => MiddleFingerSettings.MinHandScoreFor(SensitivityPercent);

    /// <summary>Tiefe Kopie.</summary>
    public HeartSettings Clone() => (HeartSettings)MemberwiseClone();
}

public sealed class SecuritySettings
{
    public string? PinHash { get; set; }
    public string? PinSalt { get; set; }
    public int PinIterations { get; set; } = 100_000;
}

/// <summary>Tastenbelegung je Aktion; Einträge im Format von <see cref="KeyChord"/> (z. B. "Space", "Ctrl+Alt+A").</summary>
public sealed class InputSettings
{
    public List<string> TriggerKeys { get; set; } = [DefaultTriggerKey];
    public List<string> AdminKeys { get; set; } = [DefaultAdminKey];
    /// <summary>Tasten der Aktion „Abbrechen“ (Countdown oder Druck). Der JSON-Name bleibt aus Kompatibilitätsgründen.</summary>
    public List<string> CancelCountdownKeys { get; set; } = [DefaultCancelKey];
    /// <summary>Linksklick löst die Aufnahme aus.</summary>
    public bool TriggerOnLeftClick { get; set; } = true;
    /// <summary>Rechtsklick löst die Aufnahme aus.</summary>
    public bool TriggerOnRightClick { get; set; } = true;
    /// <summary>Mausrad-Richtung der Gast-Layoutwahl umkehren (Standard: nach oben = vorheriges Layout, mit Umkehrung = nächstes).</summary>
    public bool InvertMouseWheel { get; set; }
    /// <summary>Tasten „Video starten“. Dieselbe Taste darf auch in <see cref="VideoStopKeys"/> stehen (Umschalten).</summary>
    public List<string> VideoStartKeys { get; set; } = [DefaultVideoKey];
    /// <summary>Tasten „Video beenden“.</summary>
    public List<string> VideoStopKeys { get; set; } = [DefaultVideoKey];

    public const string DefaultTriggerKey = "Space";
    public const string DefaultAdminKey = "Ctrl+Alt+A";
    public const string DefaultCancelKey = "Escape";
    public const string DefaultVideoKey = "Enter";
}
