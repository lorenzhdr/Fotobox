using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Export/Import der Einstellungen (<see cref="SettingsTransfer"/>): Serialize, Parse, Overlay-Ersatz und Dateizugriff.</summary>
public sealed class SettingsTransferTests : IDisposable
{
    private static readonly DateTimeOffset ExportedAt = new(2026, 5, 17, 14, 30, 0, TimeSpan.FromHours(2));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FotoboxTransferTests_" + Guid.NewGuid().ToString("N"));

    public SettingsTransferTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    // ---------- Hilfen ----------

    /// <summary>Einstellungen mit Nicht-Standardwerten in jedem übertragbaren Abschnitt und gefüllten Gerätefeldern.</summary>
    private static AppSettings NonDefaultSettings()
    {
        var s = new AppSettings();
        s.Camera.MirrorPreview = false;
        s.Camera.MirrorPhoto = true;
        s.Camera.Powerline = CameraPowerline.Hz60;
        s.Flow.CountdownSeconds = 7;
        s.Flow.PreviewSeconds = 6;
        s.Flow.PauseBetweenShotsSeconds = 3;
        s.Flow.ErrorDisplaySeconds = 9;
        s.Flow.PrintingEnabled = false;
        s.Flow.ExpectedPrintSeconds = 200;
        s.Layout.Kind = LayoutKind.Strip;
        s.Layout.StripStyle = StripStyle.Single;
        s.Layout.StripPhotoCount = 4;
        s.Layout.CaptionText = "Hochzeit";
        s.Layout.ShowDate = true;
        s.Layout.CaptionFontFamily = "Georgia";
        s.Layout.GuestChoices = [GuestLayout.StripSingle, GuestLayout.Single];
        s.Printer.Orientation = PageOrientation.Landscape;
        s.Printer.Copies = 2;
        s.Printer.TimeoutSeconds = 400;
        s.Storage.DateSubfolder = false;
        s.Storage.SaveOriginals = false;
        s.Storage.JpegQuality = 80;
        s.Storage.FilePrefix = "Party";
        s.Input.TriggerKeys = ["F9", "F10"];
        s.Input.AdminKeys = ["Ctrl+Alt+B"];
        s.Input.CancelCountdownKeys = ["F11"];
        s.Input.VideoStartKeys = ["F6"];
        s.Input.VideoStopKeys = ["F7"];
        s.Input.TriggerOnLeftClick = false;
        s.Input.TriggerOnRightClick = false;
        s.Input.InvertMouseWheel = true;
        s.GuestScreen.HintText = "Bitte lächeln";
        s.GuestScreen.HintStyle = HintStyle.Neon;
        s.GuestScreen.VideoHintText = "Video läuft";
        s.GuestScreen.VideoHintStyle = HintStyle.Glass;
        s.GuestScreen.ShowWheelHint = false;
        s.GuestScreen.WheelHintText = "Rad drehen";
        s.GuestScreen.WheelHintStyle = WheelHintStyle.Chip;
        s.Appearance.AdminTheme = AdminThemeMode.Dark;
        s.Video.Enabled = false;
        s.Video.MaxDurationSeconds = 120;
        s.Video.CountdownSeconds = 5;
        s.Video.MinDurationSeconds = 4;
        s.Video.WarningSeconds = 20;
        s.Video.ShowRecDot = false;
        s.Video.ShowElapsed = false;
        s.Video.ShowRemaining = false;
        s.Video.SuccessDisplaySeconds = 6;
        s.Video.SuccessText = "Gespeichert!";
        s.Video.Quality = VideoQuality.Low;
        s.Video.RecordAudio = false;
        s.Video.DateSubfolder = false;
        s.Video.FilePrefix = "Clip";
        s.Fun.Enabled = false;
        s.Fun.Method = DetectionMethod.Face;
        s.Fun.Scope = TargetScope.Head;
        s.Fun.BoxStyle = TargetBoxStyle.Rectangle;
        s.Fun.Color = TargetColor.Cyan;
        s.Fun.Animation = LockOnAnimation.ScanPulse;
        s.Fun.SingularText = "Ziel erfasst";
        s.Fun.PluralText = "{Anzahl} Ziele";
        s.Fun.TextPosition = FunTextPosition.Top;
        s.Fun.TextStyle = FunTextStyle.Hint;
        s.Fun.TextHintStyle = HintStyle.Banner;
        s.Fun.TextSeconds = 9;
        s.Fun.CooldownSeconds = 30;
        s.Fun.SwapTriggers = false;
        s.Fun.RepeatEnabled = true;
        s.Fun.RepeatSeconds = 90;
        s.Fun.MinSizePercent = 25;
        s.Fun.MaxTargets = 3;
        s.Fun.SensitivityPercent = 70;
        s.Fun.DwellMilliseconds = 1000;
        s.Fun.LockOnMilliseconds = 2000;
        s.Fun.LockOnIntensityPercent = 80;
        s.Fun.AnalysesPerSecond = 3;
        s.Fun.SoundEnabled = true;
        s.Peace.Enabled = false;
        s.Peace.ActionSelfDestruct = false;
        s.Peace.ActionPhoto = true;
        s.Peace.ActionMoustache = true;
        s.Peace.HoldMilliseconds = 1500;
        s.Peace.ShowHoldProgress = true;
        s.Peace.CooldownSeconds = 30;
        s.Peace.SensitivityPercent = 65;
        s.Peace.AnalysesPerSecond = 4;
        s.Peace.SelfDestructSeconds = 15;
        s.Peace.SelfDestructSoundEnabled = true;
        s.Peace.PhotoCountdownSeconds = 5;
        s.Peace.MoustacheTargets = MoustacheTargets.PeaceShower;
        s.Peace.MoustacheStyle = MoustacheStyle.Walrus;
        s.Peace.RandomStyles = [MoustacheStyle.Twirl, MoustacheStyle.Brush];
        s.Peace.MoustacheVisibility = MoustacheVisibility.FixedDuration;
        s.Peace.MoustacheDurationSeconds = 40;
        s.Peace.MoustacheAutoPhoto = true;
        s.Peace.MoustacheAutoPhotoCountdownSeconds = 6;
        s.MiddleFinger.Enabled = false;
        s.MiddleFinger.HoldMilliseconds = 900;
        s.MiddleFinger.SensitivityPercent = 75;
        s.MiddleFinger.ActionCancelKisscam = false;
        s.Heart.Enabled = false;
        s.Heart.ShapeInverted = true;
        s.Heart.HoldMilliseconds = 1400;
        s.Heart.KisscamSeconds = 20;
        s.Heart.KisscamColor = KisscamColor.Gold;
        s.Heart.KisscamFilePrefix = "Kuss";

        FillDevices(s);
        return s;
    }

    private static void FillDevices(AppSettings s)
    {
        s.Camera.DeviceId = "kamera-geraet-4711";
        s.Camera.ResolutionWidth = 1920;
        s.Camera.ResolutionHeight = 1080;
        s.Camera.ControlsDeviceId = "kamera-controls-4711";
        s.Camera.ControlValues = [new CameraControlValue { Kind = CameraControlKind.Zoom, Value = 777.5 }];
        s.Camera.ControlProfiles =
        [
            new CameraControlProfile { Name = "Profil-Name-4711", DeviceId = "kamera-controls-4711", Values = [new CameraControlValue { Kind = CameraControlKind.Hue, Auto = true }] },
        ];
        s.Printer.PrinterName = "Drucker-Wohnzimmer";
        s.Printer.PaperSizeName = "Papier-Spezial";
        s.Printer.PaperWidthMm = 101.6;
        s.Printer.PaperHeightMm = 152.4;
        s.Storage.PhotoFolder = @"D:\Eigene\Fotos-Pfad";
        s.Video.MicrophoneId = "mikrofon-id-4711";
        s.Video.MicrophoneName = "Mikrofon-Name-4711";
        s.Video.Folder = @"D:\Eigene\Videos-Pfad";
        s.Appearance.GuestMonitorId = @"\\?\DISPLAY#MONITOR-4711";
        s.Appearance.GuestMonitorName = "Monitor-Name-4711";
        s.Appearance.AdminZoomPercent = 65;
        s.Heart.KisscamMusicFolder = @"D:\Eigene\Musik-Pfad";
    }

    /// <summary>Gerätefeld nach Export/Import: leer (null/leere Liste) bzw. beim Admin-Zoom der Standard (eine Zahl, nie null).</summary>
    private static bool IsClearedDeviceValue(string path, object? value) =>
        value is null
        || value is System.Collections.IList { Count: 0 }
        || (path == "Appearance.AdminZoomPercent" && value is int zoom && zoom == SettingsLimits.AdminZoomDefault);

    [Fact]
    public void AdminZoom_IsNotExported_AndNotOverwrittenByImport()
    {
        var original = NonDefaultSettings();
        Assert.Equal(65, original.Appearance.AdminZoomPercent);

        var json = SettingsTransfer.Serialize(original, null, "2.3.0", ExportedAt);
        Assert.DoesNotContain("AdminZoomPercent", json);
        // Ein Zoom in einer fremden Datei (z. B. rohe settings.json) wird beim Einlesen ebenfalls ignoriert.
        var raw = SettingsTransfer.Parse(JsonSerializer.Serialize(original, AppSettings.JsonOptions));
        Assert.Equal(SettingsLimits.AdminZoomDefault, raw.Sections.Appearance.AdminZoomPercent);

        var result = SettingsTransfer.Parse(json);
        var target = new AppSettings();
        target.Appearance.AdminZoomPercent = 90;
        var targetDevices = target.Clone();
        SettingsSections.CopyPortableSections(result.Sections, target);
        DeviceSpecificSettings.CopyFrom(targetDevices, target);

        Assert.Equal(90, target.Appearance.AdminZoomPercent);
    }

    private static readonly string[] DeviceValues =
    [
        "kamera-geraet-4711", "kamera-controls-4711", "Profil-Name-4711", "777.5", "Drucker-Wohnzimmer", "Papier-Spezial", "Fotos-Pfad", "mikrofon-id-4711", "Mikrofon-Name-4711",
        "Videos-Pfad", "Musik-Pfad", "MONITOR-4711", "Monitor-Name-4711", "101.6", "152.4", "1920", "1080",
    ];

    /// <summary>JSON der übertragbaren Abschnitte ohne Gerätefelder, Version und PIN (normalisiert) – zum Vergleichen.</summary>
    private static string PortableJson(AppSettings s)
    {
        var copy = SettingsNormalizer.Normalize(s.Clone());
        DeviceSpecificSettings.Clear(copy);
        var node = JsonSerializer.SerializeToNode(copy, AppSettings.JsonOptions)!.AsObject();
        node.Remove(nameof(AppSettings.Version));
        node.Remove(nameof(AppSettings.Security));
        return node.ToJsonString();
    }

    private static string SectionJson(AppSettings s, string section)
    {
        var copy = SettingsNormalizer.Normalize(s.Clone());
        DeviceSpecificSettings.Clear(copy);
        return JsonSerializer.SerializeToNode(copy, AppSettings.JsonOptions)![section]!.ToJsonString();
    }

    private static string Wrapper(string settings, string? appVersion = "2.3.0", int? settingsVersion = 2, string format = SettingsTransfer.FormatName)
    {
        var sb = new StringBuilder("{ \"Format\": \"" + format + "\", \"FormatVersion\": 1");
        if (appVersion is not null)
            sb.Append(", \"AppVersion\": \"" + appVersion + "\"");
        if (settingsVersion is not null)
            sb.Append(", \"SettingsVersion\": " + settingsVersion);
        sb.Append(", \"Settings\": " + settings + " }");
        return sb.ToString();
    }

    private static SecuritySettings ValidPin() => new()
    {
        PinHash = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
        PinSalt = Convert.ToBase64String(Enumerable.Range(50, 16).Select(i => (byte)i).ToArray()),
        PinIterations = 123_456,
    };

    private static string SecurityJson(string hash, string salt, string iterations) =>
        $"\"Security\": {{ \"PinHash\": {hash}, \"PinSalt\": {salt}, \"PinIterations\": {iterations} }}";

    // ---------- Namen, Konstanten ----------

    [Fact]
    public void SuggestedFileName_UsesInvariantTimestamp()
    {
        Assert.Equal("FotoboxPlus-Einstellungen-20260517-1430.json", SettingsTransfer.SuggestedFileName(new DateTime(2026, 5, 17, 14, 30, 59)));
    }

    [Fact]
    public void Constants_HaveAgreedValues()
    {
        Assert.Equal("FotoboxPlus.Settings", SettingsTransfer.FormatName);
        Assert.Equal(1, SettingsTransfer.FormatVersion);
        Assert.Equal(1024 * 1024, SettingsTransfer.MaxFileBytes);
        Assert.Equal(TimeSpan.FromSeconds(10), SettingsTransfer.IoTimeout);
        Assert.Equal("Fotobox+-Einstellungen (*.json)|*.json", SettingsTransfer.FileFilter);
    }

    [Fact]
    public void AppInfoVersion_IsNotEmpty_AndHasNoSourceSuffix()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppInfo.Version));
        Assert.DoesNotContain('+', AppInfo.Version);
        Assert.Matches(@"^\d+\.\d+", AppInfo.Version);
    }

    // ---------- Serialize ----------

    [Fact]
    public void Serialize_WritesWrapperHeader()
    {
        var json = SettingsTransfer.Serialize(new AppSettings(), null, "2.3.0-beta", ExportedAt);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(SettingsTransfer.FormatName, root.GetProperty("Format").GetString());
        Assert.Equal(SettingsTransfer.FormatVersion, root.GetProperty("FormatVersion").GetInt32());
        Assert.Equal("2.3.0-beta", root.GetProperty("AppVersion").GetString());
        Assert.Equal(AppSettings.CurrentVersion, root.GetProperty("SettingsVersion").GetInt32());
        Assert.Equal(ExportedAt, root.GetProperty("ExportedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("Settings").ValueKind);
        Assert.Contains("\n", json); // eingerückt
    }

    [Fact]
    public void Serialize_DoesNotContainDeviceFieldsOrSecurity()
    {
        var settings = NonDefaultSettings();
        settings.Security.PinHash = "geheim-hash";
        settings.Security.PinSalt = "geheim-salt";

        var json = SettingsTransfer.Serialize(settings, null, "2.3.0", ExportedAt);

        using var doc = JsonDocument.Parse(json);
        var inner = doc.RootElement.GetProperty("Settings");
        foreach (var path in DeviceSpecificSettings.Paths)
        {
            var parts = path.Split('.');
            Assert.True(inner.TryGetProperty(parts[0], out var section), $"Abschnitt {parts[0]} fehlt.");
            Assert.False(section.TryGetProperty(parts[1], out _), $"{path} darf nicht in der Datei stehen.");
        }
        foreach (var value in DeviceValues)
            Assert.DoesNotContain(value, json);
        Assert.False(inner.TryGetProperty("Security", out _));
        Assert.DoesNotContain("geheim", json);
        Assert.DoesNotContain("PinHash", json);
        Assert.False(inner.TryGetProperty("Version", out _));
    }

    [Fact]
    public void Serialize_WithPin_ContainsOnlyThatPin_AndParseReturnsIt()
    {
        var settings = NonDefaultSettings();
        settings.Security.PinHash = "anderer-hash";
        var pin = ValidPin();

        var json = SettingsTransfer.Serialize(settings, pin, "2.3.0", ExportedAt);
        var result = SettingsTransfer.Parse(json);

        Assert.DoesNotContain("anderer-hash", json);
        Assert.NotNull(result.Pin);
        Assert.Equal(pin.PinHash, result.Pin!.PinHash);
        Assert.Equal(pin.PinSalt, result.Pin.PinSalt);
        Assert.Equal(pin.PinIterations, result.Pin.PinIterations);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Serialize_DoesNotChangeTheInput()
    {
        var settings = NonDefaultSettings();
        var before = JsonSerializer.Serialize(settings, AppSettings.JsonOptions);

        SettingsTransfer.Serialize(settings, ValidPin(), "2.3.0", ExportedAt);

        Assert.Equal(before, JsonSerializer.Serialize(settings, AppSettings.JsonOptions));
    }

    // ---------- Round trip ----------

    [Fact]
    public void NonDefaultFixture_DiffersFromDefaults_InEveryPortableSection()
    {
        var fixture = NonDefaultSettings();
        var defaults = new AppSettings();

        foreach (var section in SettingsSections.PortableSections)
            Assert.NotEqual(SectionJson(defaults, section.Name), SectionJson(fixture, section.Name));
    }

    [Fact]
    public void RoundTrip_KeepsAllPortableSections_AndDropsDevices()
    {
        var original = NonDefaultSettings();

        var json = SettingsTransfer.Serialize(original, null, "2.3.0", ExportedAt);
        var result = SettingsTransfer.Parse(json);

        Assert.Equal(PortableJson(original), PortableJson(result.Sections));
        Assert.Null(result.Pin);
        Assert.False(result.FromNewerVersion);
        Assert.Empty(result.Notes);
        Assert.Empty(result.ResetSections);
        Assert.Equal("2.3.0", result.FileAppVersion);
        Assert.Equal(AppSettings.CurrentVersion, result.FileSettingsVersion);
        foreach (var path in DeviceSpecificSettings.Paths)
        {
            var parts = path.Split('.');
            var section = typeof(AppSettings).GetProperty(parts[0])!.GetValue(result.Sections)!;
            var value = section.GetType().GetProperty(parts[1])!.GetValue(section);
            Assert.True(IsClearedDeviceValue(path, value), $"{path} muss leer sein.");
        }
        // Der Flimmerschutz ist übertragbar.
        Assert.Equal(CameraPowerline.Hz60, result.Sections.Camera.Powerline);
        Assert.Contains("\"Powerline\"", json);
        Assert.DoesNotContain("ControlValues", json);
        Assert.DoesNotContain("ControlProfiles", json);
        Assert.DoesNotContain("ControlsDeviceId", json);
    }

    [Fact]
    public void Import_KeepsTargetControlValuesAndProfiles_WhenDevicesAreCopiedBack()
    {
        var original = NonDefaultSettings();
        var result = SettingsTransfer.Parse(SettingsTransfer.Serialize(original, null, "2.3.0", ExportedAt));

        var target = new AppSettings();
        target.Camera.ControlsDeviceId = "ziel-kamera";
        target.Camera.ControlValues = [new CameraControlValue { Kind = CameraControlKind.Brightness, Value = 3 }];
        target.Camera.ControlProfiles = [new CameraControlProfile { Name = "Ziel", DeviceId = "ziel-kamera" }];
        var targetDevices = target.Clone();
        SettingsSections.CopyPortableSections(result.Sections, target);
        DeviceSpecificSettings.CopyFrom(targetDevices, target);

        Assert.Equal("ziel-kamera", target.Camera.ControlsDeviceId);
        Assert.Equal(CameraControlKind.Brightness, Assert.Single(target.Camera.ControlValues).Kind);
        Assert.Equal("Ziel", Assert.Single(target.Camera.ControlProfiles).Name);
        Assert.Equal(CameraPowerline.Hz60, target.Camera.Powerline);
    }

    [Fact]
    public void RoundTrip_WorksWithCopyPortableSections_AndKeepsTargetDevicesWhenCopied()
    {
        var original = NonDefaultSettings();
        var result = SettingsTransfer.Parse(SettingsTransfer.Serialize(original, null, "2.3.0", ExportedAt));

        var target = new AppSettings();
        target.Camera.DeviceId = "ziel-kamera";
        target.Storage.PhotoFolder = @"E:\Ziel";
        var targetDevices = target.Clone();
        SettingsSections.CopyPortableSections(result.Sections, target);
        DeviceSpecificSettings.CopyFrom(targetDevices, target);

        Assert.Equal("ziel-kamera", target.Camera.DeviceId);
        Assert.Equal(@"E:\Ziel", target.Storage.PhotoFolder);
        Assert.Equal(PortableJson(original), PortableJson(target));
    }

    [Fact]
    public void Parse_AcceptsRawSettingsJson_AsWrittenByTheStore()
    {
        var store = new JsonSettingsStore(_dir);
        var settings = NonDefaultSettings();
        store.Save(settings);
        var raw = File.ReadAllText(Path.Combine(_dir, "settings.json"));

        var result = SettingsTransfer.Parse(raw);

        Assert.Equal(PortableJson(settings), PortableJson(result.Sections));
        Assert.Null(result.Sections.Camera.DeviceId);
        Assert.Null(result.Sections.Storage.PhotoFolder);
        Assert.Null(result.FileAppVersion);
        Assert.Equal(AppSettings.CurrentVersion, result.FileSettingsVersion);
        Assert.False(result.FromNewerVersion);
        Assert.Null(result.Pin);
    }

    [Fact]
    public void Parse_RawSettingsJson_WithPin_ReturnsPin()
    {
        var settings = NonDefaultSettings();
        settings.Security = ValidPin();
        var raw = JsonSerializer.Serialize(settings, AppSettings.JsonOptions);

        var result = SettingsTransfer.Parse(raw);

        Assert.NotNull(result.Pin);
        Assert.Equal(settings.Security.PinHash, result.Pin!.PinHash);
        Assert.Equal(PortableJson(settings), PortableJson(result.Sections));
    }

    [Fact]
    public void Parse_IgnoresDeviceFieldsEvenIfTheFileContainsThem()
    {
        var raw = JsonSerializer.Serialize(NonDefaultSettings(), AppSettings.JsonOptions);

        var result = SettingsTransfer.Parse(raw);

        foreach (var path in DeviceSpecificSettings.Paths)
        {
            var parts = path.Split('.');
            var section = typeof(AppSettings).GetProperty(parts[0])!.GetValue(result.Sections)!;
            var value = section.GetType().GetProperty(parts[1])!.GetValue(section);
            Assert.True(IsClearedDeviceValue(path, value), $"{path} muss leer sein.");
        }
        Assert.False(result.FromNewerVersion);
    }

    [Fact]
    public void Parse_IsCaseInsensitive_AndAllowsCommentsAndTrailingCommas()
    {
        const string json = """
            {
              // Kommentar
              "format": "FotoboxPlus.Settings",
              "settings": {
                "flow": { "countdownseconds": 8, },
                "PEACE": { "holdmilliseconds": 1500 },
              },
            }
            """;

        var result = SettingsTransfer.Parse(json);

        Assert.Equal(8, result.Sections.Flow.CountdownSeconds);
        Assert.Equal(1500, result.Sections.Peace.HoldMilliseconds);
        Assert.False(result.FromNewerVersion);
    }

    [Fact]
    public void Parse_AcceptsLeadingByteOrderMarkCharacter()
    {
        var json = "\uFEFF" + Wrapper("{ \"Flow\": { \"CountdownSeconds\": 9 } }");

        Assert.Equal(9, SettingsTransfer.Parse(json).Sections.Flow.CountdownSeconds);
    }

    // ---------- Fehlende / unbekannte Werte ----------

    [Fact]
    public void Parse_MissingSection_GivesDefaults()
    {
        var json = Wrapper("{ \"Flow\": { \"CountdownSeconds\": 9 } }");

        var result = SettingsTransfer.Parse(json);

        Assert.Equal(9, result.Sections.Flow.CountdownSeconds);
        var defaults = new AppSettings();
        foreach (var name in new[] { "Camera", "Layout", "Printer", "Storage", "Input", "GuestScreen", "Appearance", "Video", "Fun", "Peace", "MiddleFinger", "Heart" })
            Assert.Equal(SectionJson(defaults, name), SectionJson(result.Sections, name));
        Assert.False(result.FromNewerVersion);
        Assert.Empty(result.Notes);
        Assert.Empty(result.ResetSections);
    }

    [Fact]
    public void Parse_MissingProperty_GivesDefault_NotTheTargetValue()
    {
        var json = Wrapper("{ \"Flow\": { \"CountdownSeconds\": 9 }, \"Peace\": { \"Enabled\": false } }");

        var result = SettingsTransfer.Parse(json);

        Assert.Equal(new FlowSettings().PreviewSeconds, result.Sections.Flow.PreviewSeconds);
        Assert.Equal(new PeaceSettings().HoldMilliseconds, result.Sections.Peace.HoldMilliseconds);
        Assert.False(result.Sections.Peace.Enabled);
    }

    [Fact]
    public void Parse_NullSection_GivesDefaults()
    {
        var result = SettingsTransfer.Parse(Wrapper("{ \"Flow\": null, \"Fun\": { \"MaxTargets\": 2 } }"));

        Assert.Equal(new FlowSettings().CountdownSeconds, result.Sections.Flow.CountdownSeconds);
        Assert.Equal(2, result.Sections.Fun.MaxTargets);
        Assert.Empty(result.ResetSections);
    }

    [Fact]
    public void Parse_UnknownProperty_IsIgnored_AndFlagsNewerVersion()
    {
        var json = Wrapper("{ \"Flow\": { \"CountdownSeconds\": 9, \"HologrammModus\": true } }", appVersion: null, settingsVersion: null);

        var result = SettingsTransfer.Parse(json);

        Assert.Equal(9, result.Sections.Flow.CountdownSeconds);
        Assert.True(result.FromNewerVersion);
        Assert.Contains(result.Notes, n => n.Contains("neueren Version") && n.Contains("ignoriert"));
    }

    [Fact]
    public void Parse_UnknownSection_IsIgnored_AndFlagsNewerVersion()
    {
        var json = Wrapper("{ \"Flow\": { \"CountdownSeconds\": 9 }, \"Zukunft\": { \"X\": 1 } }", appVersion: null, settingsVersion: null);

        var result = SettingsTransfer.Parse(json);

        Assert.Equal(9, result.Sections.Flow.CountdownSeconds);
        Assert.True(result.FromNewerVersion);
        Assert.Contains(result.Notes, n => n.Contains("neueren Version"));
    }

    [Fact]
    public void Parse_UnknownEnumName_GivesDefault_AndFlagsNewerVersion()
    {
        var json = Wrapper("{ \"Layout\": { \"Kind\": \"Hologramm\", \"CaptionText\": \"Hallo\" }, \"Fun\": { \"Color\": \"Magenta\" } }",
            appVersion: null, settingsVersion: null);

        var result = SettingsTransfer.Parse(json);

        Assert.Equal(LayoutKind.SingleBorderless, result.Sections.Layout.Kind);
        Assert.Equal("Hallo", result.Sections.Layout.CaptionText);
        Assert.Equal(new FunSettings().Color, result.Sections.Fun.Color);
        Assert.True(result.FromNewerVersion);
        Assert.Contains(result.Notes, n => n.Contains("neueren Version"));
        Assert.Empty(result.ResetSections);
    }

    [Fact]
    public void Parse_UnknownEnumNameInList_IsDropped_AndFlagsNewerVersion()
    {
        var json = Wrapper("{ \"Peace\": { \"RandomStyles\": [\"Twirl\", \"Zukunftsbart\"] } }", appVersion: null, settingsVersion: null);

        var result = SettingsTransfer.Parse(json);

        Assert.Equal([MoustacheStyle.Twirl], result.Sections.Peace.RandomStyles);
        Assert.True(result.FromNewerVersion);
    }

    [Fact]
    public void Parse_NewerSettingsVersion_FlagsNewerVersion()
    {
        var json = Wrapper("{ \"Flow\": { \"CountdownSeconds\": 9 } }", appVersion: null, settingsVersion: AppSettings.CurrentVersion + 1);

        var result = SettingsTransfer.Parse(json);

        Assert.True(result.FromNewerVersion);
        Assert.Equal(AppSettings.CurrentVersion + 1, result.FileSettingsVersion);
        Assert.Contains(result.Notes, n => n.Contains("neueren Version") && n.Contains("Einstellungsformat"));
        Assert.Equal(9, result.Sections.Flow.CountdownSeconds);
    }

    [Fact]
    public void Parse_NewerAppVersion_FlagsNewerVersion_ButOlderOrEqualDoesNot()
    {
        const string settings = "{ \"Flow\": { \"CountdownSeconds\": 9 } }";

        var newer = SettingsTransfer.Parse(Wrapper(settings, appVersion: "999.0.0-beta"));
        var same = SettingsTransfer.Parse(Wrapper(settings, appVersion: AppInfo.Version));
        var older = SettingsTransfer.Parse(Wrapper(settings, appVersion: "0.0.1"));
        var garbage = SettingsTransfer.Parse(Wrapper(settings, appVersion: "irgendwas"));

        Assert.True(newer.FromNewerVersion);
        Assert.Contains(newer.Notes, n => n.Contains("999.0.0-beta"));
        Assert.False(same.FromNewerVersion);
        Assert.False(older.FromNewerVersion);
        Assert.False(garbage.FromNewerVersion);
        Assert.Equal("999.0.0-beta", newer.FileAppVersion);
    }

    [Fact]
    public void Parse_ValuesOutsideTheLimits_AreClamped()
    {
        var result = SettingsTransfer.Parse(Wrapper("{ \"Flow\": { \"CountdownSeconds\": 99999 }, \"Peace\": { \"HoldMilliseconds\": 5 } }"));

        Assert.Equal(SettingsLimits.CountdownMax, result.Sections.Flow.CountdownSeconds);
        Assert.Equal(SettingsLimits.PeaceHoldMillisecondsMin, result.Sections.Peace.HoldMilliseconds);
    }

    // ---------- Defekte Abschnitte ----------

    [Fact]
    public void Parse_WrongTypeInOneSection_ResetsOnlyThatSection()
    {
        var json = Wrapper("""
            {
              "Flow": "kaputt",
              "Peace": { "HoldMilliseconds": "viel" },
              "Layout": { "CaptionText": "Hallo" },
              "Fun": { "MaxTargets": 2 }
            }
            """);

        var result = SettingsTransfer.Parse(json);

        Assert.Equal(["Flow", "Peace"], result.ResetSections.OrderBy(n => n, StringComparer.Ordinal));
        var defaults = new AppSettings();
        Assert.Equal(SectionJson(defaults, "Flow"), SectionJson(result.Sections, "Flow"));
        Assert.Equal(SectionJson(defaults, "Peace"), SectionJson(result.Sections, "Peace"));
        Assert.Equal("Hallo", result.Sections.Layout.CaptionText);
        Assert.Equal(2, result.Sections.Fun.MaxTargets);
        Assert.Contains(result.Notes, n => n.Contains("Flow"));
        Assert.Contains(result.Notes, n => n.Contains("Peace"));
        Assert.False(result.FromNewerVersion);
    }

    // ---------- Version / Migration ----------

    [Fact]
    public void Parse_SettingsVersion1_MigratesLegacyPrefix()
    {
        var wrapped = Wrapper("{ \"Storage\": { \"FilePrefix\": \"Fotobox\" } }", settingsVersion: 1);
        var raw = "{ \"Version\": 1, \"Storage\": { \"FilePrefix\": \"Fotobox\" } }";
        var custom = Wrapper("{ \"Storage\": { \"FilePrefix\": \"Party\" } }", settingsVersion: 1);

        Assert.Equal("Fotobox+", SettingsTransfer.Parse(wrapped).Sections.Storage.FilePrefix);
        Assert.Equal("Fotobox+", SettingsTransfer.Parse(raw).Sections.Storage.FilePrefix);
        Assert.Equal("Party", SettingsTransfer.Parse(custom).Sections.Storage.FilePrefix);
        Assert.Equal(1, SettingsTransfer.Parse(wrapped).FileSettingsVersion);
    }

    [Fact]
    public void Parse_MissingVersion_MeansCurrent_AsInTheStore()
    {
        var result = SettingsTransfer.Parse("{ \"Storage\": { \"FilePrefix\": \"Fotobox\" } }");

        Assert.Equal("Fotobox", result.Sections.Storage.FilePrefix);
        Assert.Null(result.FileSettingsVersion);
    }

    // ---------- Fehler ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ nicht json")]
    [InlineData("{\"Flow\": }")]
    [InlineData("[1, 2")]
    public void Parse_InvalidJson_Throws(string json)
    {
        var ex = Assert.Throws<SettingsImportException>(() => SettingsTransfer.Parse(json));
        Assert.Equal("Die Datei ist keine gültige Einstellungsdatei.", ex.Message);
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Assert.Throws<SettingsImportException>(() => SettingsTransfer.Parse(null!));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"Flow\": {}}]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public void Parse_RootNotAnObject_Throws(string json)
    {
        var ex = Assert.Throws<SettingsImportException>(() => SettingsTransfer.Parse(json));
        Assert.Equal("Die Datei ist keine gültige Einstellungsdatei.", ex.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"Foo\": 1 }")]
    [InlineData("{ \"Format\": \"FotoboxPlus.Settings\" }")]
    [InlineData("{ \"Format\": \"FotoboxPlus.Settings\", \"Settings\": \"text\" }")]
    [InlineData("{ \"Settings\": 5 }")]
    public void Parse_NeitherWrapperNorKnownSections_Throws(string json)
    {
        var ex = Assert.Throws<SettingsImportException>(() => SettingsTransfer.Parse(json));
        Assert.Equal("Die Datei ist keine gültige Einstellungsdatei.", ex.Message);
    }

    [Theory]
    [InlineData("\"Andere.Anwendung\"")]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("\"fotoboxplus.settings\"")]
    public void Parse_ForeignFormat_Throws(string format)
    {
        var json = "{ \"Format\": " + format + ", \"Settings\": { \"Flow\": { \"CountdownSeconds\": 9 } } }";

        var ex = Assert.Throws<SettingsImportException>(() => SettingsTransfer.Parse(json));

        Assert.Equal("Die Datei ist keine Fotobox+-Einstellungsdatei.", ex.Message);
    }

    // ---------- PIN ----------

    [Theory]
    [InlineData("\"AAAA\"", "\"AQID\"", "100000")] // Hash nur 3 Byte
    [InlineData("\"%%%\"", "\"AQID\"", "100000")] // Hash kein Base64
    [InlineData("1234", "\"AQID\"", "100000")] // Hash keine Zeichenkette
    [InlineData("VALIDHASH", "\"%%%\"", "100000")] // Salt kein Base64
    [InlineData("VALIDHASH", "\"\"", "100000")] // Salt leer
    [InlineData("VALIDHASH", "\"AQID\"", "0")] // Iterationen zu klein
    [InlineData("VALIDHASH", "\"AQID\"", "1")] // schwach: unter SettingsNormalizer.MinPinIterations
    [InlineData("VALIDHASH", "\"AQID\"", "9999")]
    [InlineData("VALIDHASH", "\"AQID\"", "-5")]
    [InlineData("VALIDHASH", "\"AQID\"", "10000001")] // über der Obergrenze
    [InlineData("VALIDHASH", "\"AQID\"", "\"viele\"")] // keine Zahl
    public void Parse_ImplausiblePin_IsIgnored_WithNote(string hash, string salt, string iterations)
    {
        if (hash == "VALIDHASH")
            hash = "\"" + ValidPin().PinHash + "\"";
        var json = Wrapper("{ \"Flow\": { \"CountdownSeconds\": 9 }, " + SecurityJson(hash, salt, iterations) + " }");

        var result = SettingsTransfer.Parse(json);

        Assert.Null(result.Pin);
        Assert.Contains("Die in der Datei enthaltene PIN ist ungültig und wird ignoriert.", result.Notes);
        Assert.Equal(9, result.Sections.Flow.CountdownSeconds);
    }

    [Fact]
    public void Parse_SecurityNotAnObject_IsIgnored_WithNote()
    {
        var result = SettingsTransfer.Parse(Wrapper("{ \"Flow\": { \"CountdownSeconds\": 9 }, \"Security\": \"x\" }"));

        Assert.Null(result.Pin);
        Assert.Contains(result.Notes, n => n.Contains("PIN"));
    }

    [Fact]
    public void Parse_EmptySecurity_MeansNoPin_WithoutNote()
    {
        var result = SettingsTransfer.Parse(Wrapper("{ \"Flow\": { \"CountdownSeconds\": 9 }, \"Security\": { \"PinIterations\": 100000 } }"));

        Assert.Null(result.Pin);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Parse_PinAtLimits_IsAccepted()
    {
        var pin = ValidPin();
        pin.PinIterations = SettingsNormalizer.MaxPlausiblePinIterations;
        var json = Wrapper("{ \"Flow\": { \"CountdownSeconds\": 9 }, " + SecurityJson($"\"{pin.PinHash}\"", $"\"{pin.PinSalt}\"", pin.PinIterations.ToString()) + " }");

        var result = SettingsTransfer.Parse(json);

        Assert.NotNull(result.Pin);
        Assert.Equal(SettingsNormalizer.MaxPlausiblePinIterations, result.Pin!.PinIterations);
    }

    [Fact]
    public void Parse_PinWithExactlyMinIterations_IsAccepted()
    {
        var pin = ValidPin();
        pin.PinIterations = SettingsNormalizer.MinPinIterations;
        var json = Wrapper("{ \"Flow\": { \"CountdownSeconds\": 9 }, " + SecurityJson($"\"{pin.PinHash}\"", $"\"{pin.PinSalt}\"", pin.PinIterations.ToString()) + " }");

        var result = SettingsTransfer.Parse(json);

        Assert.NotNull(result.Pin);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Serialize_WritesTheCurrentSettingsVersion_NotTheDraftsVersion()
    {
        var draft = new AppSettings { Version = 5 };
        draft.Flow.CountdownSeconds = 9;

        var json = SettingsTransfer.Serialize(draft, null, "2.3.0", ExportedAt);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(AppSettings.CurrentVersion, doc.RootElement.GetProperty("SettingsVersion").GetInt32());
        var result = SettingsTransfer.Parse(json);
        Assert.False(result.FromNewerVersion);
        Assert.Equal(AppSettings.CurrentVersion, result.FileSettingsVersion);
        Assert.Equal(9, result.Sections.Flow.CountdownSeconds);
        Assert.Equal(5, draft.Version); // der Entwurf bleibt unverändert
    }

    // ---------- Overlay-Ersatz ----------

    [Theory]
    [InlineData(@"..\x.png")]
    [InlineData("../x.png")]
    [InlineData("a/b.png")]
    [InlineData(@"a\b.png")]
    [InlineData(@"C:\x.png")]
    [InlineData("/abs.png")]
    [InlineData(@"\\server\share\x.png")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("bad|name.png")]
    [InlineData("  ")]
    public void ApplyOverlayFallback_RejectsUnsafeNames_EvenIfTheFileWouldExist(string name)
    {
        var s = new AppSettings();
        s.Layout.Kind = LayoutKind.Overlay;
        s.Layout.OverlayFileName = name;
        s.Layout.GuestChoices = [GuestLayout.Overlay, GuestLayout.StripDouble];
        var asked = false;

        var changed = SettingsTransfer.ApplyOverlayFallback(s, _ => { asked = true; return true; });

        Assert.True(changed);
        Assert.False(asked, "Ein unzulässiger Name darf nicht einmal an die Existenzprüfung gehen.");
        Assert.Null(s.Layout.OverlayFileName);
        Assert.NotEqual(LayoutKind.Overlay, s.Layout.Kind);
        Assert.DoesNotContain(GuestLayout.Overlay, s.Layout.GuestChoices);
    }

    [Fact]
    public void ApplyOverlayFallback_MissingFile_ReplacesOverlay_WithFirstRemainingGuestChoice()
    {
        var s = new AppSettings();
        s.Layout.Kind = LayoutKind.Overlay;
        s.Layout.OverlayFileName = "rahmen.png";
        s.Layout.GuestChoices = [GuestLayout.Overlay, GuestLayout.StripSingle, GuestLayout.Single];

        var changed = SettingsTransfer.ApplyOverlayFallback(s, _ => false);

        Assert.True(changed);
        Assert.Null(s.Layout.OverlayFileName);
        Assert.Equal(LayoutKind.Strip, s.Layout.Kind);
        Assert.Equal(StripStyle.Single, s.Layout.StripStyle);
        Assert.Equal([GuestLayout.StripSingle, GuestLayout.Single], s.Layout.GuestChoices);
    }

    [Fact]
    public void ApplyOverlayFallback_MissingFile_WithoutOtherChoices_FallsBackToSingle()
    {
        var s = new AppSettings();
        s.Layout.Kind = LayoutKind.Overlay;
        s.Layout.OverlayFileName = "rahmen.png";
        s.Layout.GuestChoices = [GuestLayout.Overlay];

        var changed = SettingsTransfer.ApplyOverlayFallback(s, _ => false);

        Assert.True(changed);
        Assert.Equal(LayoutKind.SingleBorderless, s.Layout.Kind);
        Assert.Empty(s.Layout.GuestChoices);
    }

    [Fact]
    public void ApplyOverlayFallback_OverlayOnlyInGuestChoices_IsRemoved_AndStandardStays()
    {
        var s = new AppSettings();
        s.Layout.Kind = LayoutKind.Strip;
        s.Layout.StripStyle = StripStyle.DoubleCut;
        s.Layout.OverlayFileName = "rahmen.png";
        s.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.Overlay];

        var changed = SettingsTransfer.ApplyOverlayFallback(s, _ => false);

        Assert.True(changed);
        Assert.Equal(LayoutKind.Strip, s.Layout.Kind);
        Assert.Null(s.Layout.OverlayFileName);
        // Der Standard (2 Streifen) gehört nach der Bereinigung wieder in die Liste (Normalizer), Overlay nicht.
        Assert.Equal([GuestLayout.Single, GuestLayout.StripDouble], s.Layout.GuestChoices);
    }

    [Fact]
    public void ApplyOverlayFallback_OverlayKindWithoutAnyFileName_IsReplaced()
    {
        var s = new AppSettings();
        s.Layout.Kind = LayoutKind.Overlay;
        s.Layout.OverlayFileName = null;

        var changed = SettingsTransfer.ApplyOverlayFallback(s, _ => true);

        Assert.True(changed);
        Assert.Equal(LayoutKind.SingleBorderless, s.Layout.Kind);
    }

    [Fact]
    public void ApplyOverlayFallback_NameOnly_IsCleared_WhenFileIsMissing()
    {
        var s = new AppSettings();
        s.Layout.OverlayFileName = "alt.png";

        var changed = SettingsTransfer.ApplyOverlayFallback(s, _ => false);

        Assert.True(changed);
        Assert.Null(s.Layout.OverlayFileName);
        Assert.Equal(LayoutKind.SingleBorderless, s.Layout.Kind);
    }

    [Fact]
    public void ApplyOverlayFallback_ExistingFile_IsLeftUntouched()
    {
        var s = new AppSettings();
        s.Layout.Kind = LayoutKind.Overlay;
        s.Layout.OverlayFileName = "rahmen.png";
        s.Layout.GuestChoices = [GuestLayout.Overlay, GuestLayout.Single];
        var before = JsonSerializer.Serialize(s, AppSettings.JsonOptions);
        string? asked = null;

        var changed = SettingsTransfer.ApplyOverlayFallback(s, name => { asked = name; return true; });

        Assert.False(changed);
        Assert.Equal("rahmen.png", asked);
        Assert.Equal(before, JsonSerializer.Serialize(s, AppSettings.JsonOptions));
    }

    [Fact]
    public void ApplyOverlayFallback_NoOverlayInUse_ChangesNothing_AndNeverAsks()
    {
        var s = new AppSettings();
        s.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.StripDouble];
        var before = JsonSerializer.Serialize(s, AppSettings.JsonOptions);

        var changed = SettingsTransfer.ApplyOverlayFallback(s, _ => throw new InvalidOperationException("darf nicht gefragt werden"));

        Assert.False(changed);
        Assert.Equal(before, JsonSerializer.Serialize(s, AppSettings.JsonOptions));
    }

    [Fact]
    public void ApplyOverlayFallback_ThrowingExistenceCheck_CountsAsMissing()
    {
        var s = new AppSettings();
        s.Layout.Kind = LayoutKind.Overlay;
        s.Layout.OverlayFileName = "rahmen.png";

        var changed = SettingsTransfer.ApplyOverlayFallback(s, _ => throw new IOException("Laufwerk weg"));

        Assert.True(changed);
        Assert.Null(s.Layout.OverlayFileName);
    }

    [Fact]
    public void OverlayMissingNote_HasTheAgreedText()
    {
        Assert.Equal("Overlay-Bild fehlt – bitte im Tab Layout neu wählen.", SettingsTransfer.OverlayMissingNote);
    }

    // ---------- Dateizugriff ----------

    [Fact]
    public async Task WriteThenRead_RoundTripsUtf8WithoutBom_AndLeavesNoTempFile()
    {
        var path = Path.Combine(_dir, "export.json");
        var json = SettingsTransfer.Serialize(NonDefaultSettings(), null, "2.3.0", ExportedAt).Replace("Hochzeit", "Hochzeit äöü €");

        await SettingsTransfer.WriteFileAsync(path, json);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "Kein BOM erwartet.");
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(json, await SettingsTransfer.ReadFileAsync(path));
        Assert.Equal(["export.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task WriteFileAsync_OverwritesAnExistingFile()
    {
        var path = Path.Combine(_dir, "export.json");
        await File.WriteAllTextAsync(path, "alt");

        await SettingsTransfer.WriteFileAsync(path, "neu");

        Assert.Equal("neu", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task WriteFileAsync_ReplacesAStaleTempFile()
    {
        var path = Path.Combine(_dir, "export.json");
        await File.WriteAllTextAsync(path + ".tmp", "Rest einer alten Sicherung");

        await SettingsTransfer.WriteFileAsync(path, "neu");

        Assert.Equal("neu", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task WriteFileAsync_Failure_LeavesNoTempFile_AndKeepsTheOldFile()
    {
        // Ziel ist ein Ordner: Verschieben scheitert, nachdem die temporäre Datei geschrieben wurde.
        var path = Path.Combine(_dir, "ziel");
        Directory.CreateDirectory(path);

        await Assert.ThrowsAnyAsync<Exception>(() => SettingsTransfer.WriteFileAsync(path, "inhalt"));

        Assert.False(File.Exists(path + ".tmp"));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task WriteFileAsync_MissingDirectory_Throws_WithoutTempFile()
    {
        var path = Path.Combine(_dir, "gibt-es-nicht", "export.json");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => SettingsTransfer.WriteFileAsync(path, "x"));

        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task ReadFileAsync_RejectsFilesOverTheSizeLimit()
    {
        var path = Path.Combine(_dir, "gross.json");
        await File.WriteAllBytesAsync(path, new byte[SettingsTransfer.MaxFileBytes + 1]);

        var ex = await Assert.ThrowsAsync<SettingsImportException>(() => SettingsTransfer.ReadFileAsync(path));

        Assert.Equal("Die Datei ist zu groß.", ex.Message);
    }

    [Fact]
    public async Task ReadFileAsync_AcceptsAFileExactlyAtTheLimit()
    {
        var path = Path.Combine(_dir, "grenze.json");
        await File.WriteAllTextAsync(path, new string(' ', SettingsTransfer.MaxFileBytes));

        var text = await SettingsTransfer.ReadFileAsync(path);

        Assert.Equal(SettingsTransfer.MaxFileBytes, text.Length);
    }

    [Fact]
    public async Task ReadFileAsync_HandlesAByteOrderMark()
    {
        var path = Path.Combine(_dir, "bom.json");
        await File.WriteAllTextAsync(path, Wrapper("{ \"Flow\": { \"CountdownSeconds\": 9 } }"), new UTF8Encoding(true));

        var text = await SettingsTransfer.ReadFileAsync(path);

        Assert.StartsWith("{", text);
        Assert.Equal(9, SettingsTransfer.Parse(text).Sections.Flow.CountdownSeconds);
    }

    [Fact]
    public async Task ReadFileAsync_MissingFile_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => SettingsTransfer.ReadFileAsync(Path.Combine(_dir, "fehlt.json")));
    }

    [Fact]
    public async Task ReadFileAsync_Cancelled_Throws()
    {
        var path = Path.Combine(_dir, "x.json");
        await File.WriteAllTextAsync(path, "{}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SettingsTransfer.ReadFileAsync(path, cts.Token));
    }
}
