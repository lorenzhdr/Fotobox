using System.IO;
using System.Text.Json;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FotoboxTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Constructor_CreatesDirectories_AndUsesDefaults()
    {
        var store = new JsonSettingsStore(_dir);

        Assert.Equal(_dir, store.ConfigDirectory);
        Assert.Equal(Path.Combine(_dir, "overlays"), store.OverlayDirectory);
        Assert.True(Directory.Exists(store.OverlayDirectory));
        Assert.NotNull(store.Current);
        Assert.Equal(5, store.Current.Flow.CountdownSeconds);
        Assert.Equal(LayoutKind.SingleBorderless, store.Current.Layout.Kind);
        Assert.Equal("Fotobox+", store.Current.Storage.FilePrefix);
    }

    [Fact]
    public void Save_Roundtrip_PersistsAcrossInstances()
    {
        var store = new JsonSettingsStore(_dir);
        var s = store.Current.Clone();
        s.Flow.CountdownSeconds = 8;
        s.Layout.Kind = LayoutKind.Strip;
        s.Layout.CaptionText = "Hochzeit Änne & Jörg";
        s.Camera.DeviceId = "cam-1";
        s.Camera.ResolutionWidth = 1920;
        s.Printer.Orientation = PageOrientation.Landscape;
        s.Storage.PhotoFolder = @"D:\Fotos";
        store.Save(s);

        var reloaded = new JsonSettingsStore(_dir).Current;

        Assert.Equal(8, reloaded.Flow.CountdownSeconds);
        Assert.Equal(LayoutKind.Strip, reloaded.Layout.Kind);
        Assert.Equal("Hochzeit Änne & Jörg", reloaded.Layout.CaptionText);
        Assert.Equal("cam-1", reloaded.Camera.DeviceId);
        Assert.Equal(1920, reloaded.Camera.ResolutionWidth);
        Assert.Equal(PageOrientation.Landscape, reloaded.Printer.Orientation);
        Assert.Equal(@"D:\Fotos", reloaded.Storage.PhotoFolder);
        Assert.Contains("\"Strip\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Save_ReplacesCurrent_WithCopy_AndRaisesEvent()
    {
        var store = new JsonSettingsStore(_dir);
        var count = 0;
        store.SettingsChanged += (_, _) => count++;
        var s = new AppSettings();
        s.Flow.PreviewSeconds = 7;

        store.Save(s);
        s.Flow.PreviewSeconds = 20; // spätere Änderung darf Current nicht beeinflussen

        Assert.Equal(1, count);
        Assert.Equal(7, store.Current.Flow.PreviewSeconds);
        Assert.NotSame(s, store.Current);
    }

    [Fact]
    public void Save_LeavesNoTempFiles()
    {
        var store = new JsonSettingsStore(_dir);
        store.Save(new AppSettings());
        store.Save(new AppSettings());

        var files = Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).ToArray();
        Assert.Equal(["settings.json"], files);
    }

    [Fact]
    public void BrokenFile_IsBackedUp_AndDefaultsUsed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ das ist kein json");

        var store = new JsonSettingsStore(_dir);

        Assert.Equal(5, store.Current.Flow.CountdownSeconds);
        Assert.False(File.Exists(SettingsPath));
        var backup = Assert.Single(Directory.GetFiles(_dir, "settings.json.bak-*"));
        Assert.Equal("{ das ist kein json", File.ReadAllText(backup));
    }

    [Fact]
    public void MissingFile_IsFirstRun_NotLoadFailed()
    {
        var store = new JsonSettingsStore(_dir, TimeSpan.Zero);

        Assert.False(store.LoadFailed);
        Assert.True(string.IsNullOrEmpty(store.Current.Security.PinHash));
        store.Save(new AppSettings());
        Assert.True(File.Exists(SettingsPath));
    }

    [Fact]
    public void BrokenFile_IsNotLoadFailed_ButBackedUp()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ kaputt");

        var store = new JsonSettingsStore(_dir, TimeSpan.Zero);

        Assert.False(store.LoadFailed);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.bak-*"));
    }

    [Fact]
    public void LockedFile_FailsClosed_SaveDoesNotOverwrite_AndReloadRecovers()
    {
        var withPin = new AppSettings();
        withPin.Security.PinHash = "hash";
        withPin.Security.PinSalt = "salt";
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(withPin, AppSettings.JsonOptions));
        var before = File.ReadAllText(SettingsPath);

        JsonSettingsStore store;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store = new JsonSettingsStore(_dir, TimeSpan.Zero);

            Assert.True(store.LoadFailed);
            Assert.Throws<IOException>(() => store.Save(new AppSettings()));
            Assert.False(store.TryReload()); // weiterhin gesperrt
            Assert.True(store.LoadFailed);
        }

        Assert.Equal(before, File.ReadAllText(SettingsPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        var changed = 0;
        store.SettingsChanged += (_, _) => changed++;
        Assert.True(store.TryReload());
        Assert.False(store.LoadFailed);
        Assert.Equal("hash", store.Current.Security.PinHash);
        Assert.Equal(1, changed);
        store.Save(store.Current.Clone()); // wieder schreibbar
    }

    [Fact]
    public async Task ShortLock_IsRetried_AndLoadsNormally()
    {
        var withPin = new AppSettings();
        withPin.Security.PinHash = "hash";
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(withPin, AppSettings.JsonOptions));

        var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        // Eigener Thread statt Task.Run: Bei ausgelastetem Threadpool (parallele Tests auf CI) startete die Freigabe sonst zu spät.
        var release = new Thread(() =>
        {
            Thread.Sleep(80);
            locked.Dispose();
        }) { IsBackground = true };
        release.Start();

        var store = new JsonSettingsStore(_dir, TimeSpan.FromSeconds(1)); // großzügig: Freigabe nach 80 ms, Puffer für langsame CI-Runner
        await Task.Run(release.Join);

        Assert.False(store.LoadFailed);
        Assert.Equal("hash", store.Current.Security.PinHash);
    }

    [Fact]
    public void UnknownEnumName_KeepsFile_UsesDefault_AndKeepsPin()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
        {
          "Flow": { "CountdownSeconds": 7 },
          "Layout": { "Kind": "Neu", "StripStyle": "Quatsch" },
          "Printer": { "Orientation": "Diagonal" },
          "Security": { "PinHash": "hash", "PinSalt": "salt", "PinIterations": 123456 }
        }
        """);
        var before = File.ReadAllText(SettingsPath);

        var store = new JsonSettingsStore(_dir);

        Assert.False(store.LoadFailed);
        Assert.Equal(LayoutKind.SingleBorderless, store.Current.Layout.Kind);
        Assert.Equal(StripStyle.DoubleCut, store.Current.Layout.StripStyle);
        Assert.Equal(PageOrientation.Auto, store.Current.Printer.Orientation);
        Assert.Equal(7, store.Current.Flow.CountdownSeconds);
        Assert.Equal("hash", store.Current.Security.PinHash);
        Assert.Equal("salt", store.Current.Security.PinSalt);
        Assert.Equal(123456, store.Current.Security.PinIterations);
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.bak-*"));
        Assert.Equal(before, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Enum_IsReadCaseInsensitive_AndByNumber_AndOutOfRangeNumberFallsBack()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
        { "Layout": { "Kind": "strip", "StripStyle": 1 }, "Printer": { "Orientation": 99 } }
        """);

        var s = new JsonSettingsStore(_dir).Current;

        Assert.Equal(LayoutKind.Strip, s.Layout.Kind);
        Assert.Equal(StripStyle.Single, s.Layout.StripStyle);
        Assert.Equal(PageOrientation.Auto, s.Printer.Orientation);
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.bak-*"));
    }

    [Fact]
    public void UnknownGuestChoice_IsDroppedIndividually()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
        { "Layout": { "Kind": "SingleBorderless", "GuestChoices": [ "StripDouble", "Zukunft", "Single", 77 ] },
          "Security": { "PinHash": "hash", "PinSalt": "salt" } }
        """);

        var s = new JsonSettingsStore(_dir).Current;

        Assert.Equal([GuestLayout.StripDouble, GuestLayout.Single], s.Layout.GuestChoices);
        Assert.Equal("hash", s.Security.PinHash);
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.bak-*"));
    }

    [Fact]
    public void WrongType_BacksUpFile_AndRescuesPin()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
        {
          "Printer": { "Copies": "2" },
          "Flow": { "CountdownSeconds": 9 },
          "security": { "pinhash": "hash", "PINSALT": "salt", "PinIterations": 250000 }
        }
        """);

        var store = new JsonSettingsStore(_dir);

        Assert.False(store.LoadFailed);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.bak-*"));
        Assert.False(File.Exists(SettingsPath));
        Assert.Equal("hash", store.Current.Security.PinHash);
        Assert.Equal("salt", store.Current.Security.PinSalt);
        Assert.Equal(250000, store.Current.Security.PinIterations);
        // Alles andere sind Standardwerte.
        Assert.Equal(5, store.Current.Flow.CountdownSeconds);
        Assert.Equal(1, store.Current.Printer.Copies);
    }

    [Fact]
    public void SyntaxError_WithoutParsableJson_StartsFresh_AsBefore()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"Security\": { \"PinHash\": \"hash\", ");

        var store = new JsonSettingsStore(_dir);

        Assert.False(store.LoadFailed);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.bak-*"));
        Assert.True(string.IsNullOrEmpty(store.Current.Security.PinHash));
    }

    [Fact]
    public void WrongType_WithoutRescuablePin_StartsFresh()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "Printer": { "Copies": "2" }, "Security": { "PinHash": "hash" } }""");

        var store = new JsonSettingsStore(_dir);

        Assert.Single(Directory.GetFiles(_dir, "settings.json.bak-*"));
        Assert.True(string.IsNullOrEmpty(store.Current.Security.PinHash));
    }

    [Fact]
    public void WrongType_WithCommentsAndTrailingCommas_StillRescuesPin()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
        {
          // Kommentar
          "Printer": { "Copies": "x", },
          "Security": { "PinHash": "hash", "PinSalt": "salt", },
        }
        """);

        var store = new JsonSettingsStore(_dir);

        Assert.Equal("hash", store.Current.Security.PinHash);
        Assert.Equal(100_000, store.Current.Security.PinIterations);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.bak-*"));
    }

    [Fact]
    public void BackupName_UsesInvariantTimestamp()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ kaputt");

        _ = new JsonSettingsStore(_dir);

        var name = Path.GetFileName(Assert.Single(Directory.GetFiles(_dir, "settings.json.bak-*")));
        Assert.Matches(@"^settings\.json\.bak-\d{14}$", name);
    }

    [Fact]
    public void Enums_AreWrittenAsNames_AndRoundtripUnchanged()
    {
        var s = new AppSettings();
        s.Layout.Kind = LayoutKind.Overlay;
        s.Layout.StripStyle = StripStyle.Single;
        s.Layout.GuestChoices = [GuestLayout.Overlay, GuestLayout.StripSingle];
        s.Printer.Orientation = PageOrientation.Landscape;

        var json = JsonSerializer.Serialize(s, AppSettings.JsonOptions);

        Assert.Contains("\"Kind\": \"Overlay\"", json);
        Assert.Contains("\"StripStyle\": \"Single\"", json);
        Assert.Contains("\"Orientation\": \"Landscape\"", json);
        Assert.Contains("\"StripSingle\"", json);
        var back = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)!;
        Assert.Equal(json, JsonSerializer.Serialize(back, AppSettings.JsonOptions));
        Assert.Equal([GuestLayout.Overlay, GuestLayout.StripSingle], back.Layout.GuestChoices);
    }

    [Fact]
    public void Load_TrimsTooLongPrefixAndCaption_InsteadOfRejecting()
    {
        Directory.CreateDirectory(_dir);
        var prefix = new string('p', SettingsLimits.FilePrefixMaxLength + 15);
        var caption = new string('c', SettingsLimits.CaptionMaxLength + 20);
        File.WriteAllText(SettingsPath,
            $$"""{ "Storage": { "FilePrefix": "{{prefix}}" }, "Layout": { "CaptionText": "{{caption}}" } }""");

        var s = new JsonSettingsStore(_dir).Current;

        Assert.Equal(SettingsLimits.FilePrefixMaxLength, s.Storage.FilePrefix.Length);
        Assert.Equal(SettingsLimits.CaptionMaxLength, s.Layout.CaptionText!.Length);
    }

    [Fact]
    public void Load_ClampsOutOfRangeValues_AndFixesPrefix()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
        {
          "Flow": { "CountdownSeconds": 999, "PreviewSeconds": 0, "PauseBetweenShotsSeconds": -3, "ErrorDisplaySeconds": 1 },
          "Layout": { "StripPhotoCount": 9 },
          "Printer": { "Copies": 0, "TimeoutSeconds": 5 },
          "Storage": { "JpegQuality": 10, "FilePrefix": "a/b:c*d?" },
          "Security": { "PinIterations": 5 }
        }
        """);

        var s = new JsonSettingsStore(_dir).Current;

        Assert.Equal(30, s.Flow.CountdownSeconds);
        Assert.Equal(1, s.Flow.PreviewSeconds);
        Assert.Equal(0, s.Flow.PauseBetweenShotsSeconds);
        Assert.Equal(2, s.Flow.ErrorDisplaySeconds);
        Assert.Equal(4, s.Layout.StripPhotoCount);
        Assert.Equal(1, s.Printer.Copies);
        // Untergrenze ist die erwartete Druckdauer (Standard 240 s) plus Reserve.
        Assert.Equal(240 + SettingsLimits.PrintTimeoutReserveSeconds, s.Printer.TimeoutSeconds);
        Assert.Equal(50, s.Storage.JpegQuality);
        Assert.Equal("abcd", s.Storage.FilePrefix);
        Assert.Equal(5, s.Security.PinIterations);   // gespeicherte Iterationen nie klemmen (sonst passt der Hash nicht mehr)
    }

    [Fact]
    public void Save_ClampsLowerAndUpperBounds()
    {
        var store = new JsonSettingsStore(_dir);
        var low = new AppSettings();
        low.Flow.CountdownSeconds = 0;
        low.Flow.PauseBetweenShotsSeconds = -1;
        low.Flow.ErrorDisplaySeconds = 0;
        low.Layout.StripPhotoCount = 1;
        low.Printer.Copies = 0;
        low.Printer.TimeoutSeconds = 0;
        low.Storage.JpegQuality = 0;
        low.Security.PinIterations = 0;
        store.Save(low);

        Assert.Equal(1, store.Current.Flow.CountdownSeconds);
        Assert.Equal(0, store.Current.Flow.PauseBetweenShotsSeconds);
        Assert.Equal(2, store.Current.Flow.ErrorDisplaySeconds);
        Assert.Equal(2, store.Current.Layout.StripPhotoCount);
        Assert.Equal(1, store.Current.Printer.Copies);
        Assert.Equal(240 + SettingsLimits.PrintTimeoutReserveSeconds, store.Current.Printer.TimeoutSeconds);
        Assert.Equal(50, store.Current.Storage.JpegQuality);
        Assert.Equal(0, store.Current.Security.PinIterations);   // nicht geklemmt; SetPin erzwingt das Minimum für neue PINs

        var high = new AppSettings();
        high.Flow.CountdownSeconds = SettingsLimits.CountdownMax + 1;
        high.Flow.PauseBetweenShotsSeconds = SettingsLimits.PauseMax + 1;
        high.Flow.ErrorDisplaySeconds = SettingsLimits.ErrorDisplayMax + 1;
        high.Printer.Copies = SettingsLimits.CopiesMax + 1;
        high.Printer.TimeoutSeconds = SettingsLimits.PrintTimeoutMax + 1;
        high.Storage.JpegQuality = 101;
        store.Save(high);

        Assert.Equal(SettingsLimits.CountdownMax, store.Current.Flow.CountdownSeconds);
        Assert.Equal(SettingsLimits.PauseMax, store.Current.Flow.PauseBetweenShotsSeconds);
        Assert.Equal(SettingsLimits.ErrorDisplayMax, store.Current.Flow.ErrorDisplaySeconds);
        Assert.Equal(SettingsLimits.CopiesMax, store.Current.Printer.Copies);
        Assert.Equal(SettingsLimits.PrintTimeoutMax, store.Current.Printer.TimeoutSeconds);
        Assert.Equal(100, store.Current.Storage.JpegQuality);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    public void EmptyOrInvalidPrefix_BecomesFotoboxPlus(string prefix)
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Storage.FilePrefix = prefix;
        store.Save(s);

        Assert.Equal("Fotobox+", store.Current.Storage.FilePrefix);
    }

    [Fact]
    public void NullSubObjects_AreReplacedWithDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"Camera\": null, \"Flow\": null, \"Layout\": null, \"Printer\": null, \"Storage\": null, \"Security\": null }");

        var s = new JsonSettingsStore(_dir).Current;

        Assert.NotNull(s.Camera);
        Assert.NotNull(s.Flow);
        Assert.NotNull(s.Layout);
        Assert.NotNull(s.Printer);
        Assert.NotNull(s.Storage);
        Assert.NotNull(s.Security);
        Assert.Equal(5, s.Flow.CountdownSeconds);
    }

    [Fact]
    public void PartialFile_KeepsDefaultsForMissingValues()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"Flow\": { \"CountdownSeconds\": 3 } }");

        var s = new JsonSettingsStore(_dir).Current;

        Assert.Equal(3, s.Flow.CountdownSeconds);
        Assert.Equal(4, s.Flow.PreviewSeconds);
        Assert.True(s.Flow.PrintingEnabled);
    }

    [Fact]
    public void ConcurrentSaves_DoNotCorruptFile()
    {
        var store = new JsonSettingsStore(_dir);
        Parallel.For(0, 40, i =>
        {
            var s = new AppSettings();
            s.Flow.CountdownSeconds = 1 + i % 30;
            store.Save(s);
        });

        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void NewFields_HaveDefaults_WhenMissingInFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "Layout": { "Kind": "Strip" } }""");

        var s = new JsonSettingsStore(_dir).Current;

        Assert.Equal(StripStyle.DoubleCut, s.Layout.StripStyle);
        Assert.Equal("Segoe UI", s.Layout.CaptionFontFamily);
        Assert.Equal(240, s.Flow.ExpectedPrintSeconds);
        Assert.Equal(["Space"], s.Input.TriggerKeys);
        Assert.Equal(["Ctrl+Alt+A"], s.Input.AdminKeys);
        Assert.Equal(["Escape"], s.Input.CancelCountdownKeys);
    }

    [Fact]
    public void NewFields_Roundtrip()
    {
        var store = new JsonSettingsStore(_dir);
        var s = store.Current.Clone();
        s.Layout.StripStyle = StripStyle.Single;
        s.Layout.CaptionFontFamily = "Comic Sans MS";
        s.Flow.ExpectedPrintSeconds = 90;
        s.Input.TriggerKeys = ["Space", "Enter", "PageDown"];
        s.Input.CancelCountdownKeys = ["Escape", "Back"];
        store.Save(s);

        var r = new JsonSettingsStore(_dir).Current;

        Assert.Equal(StripStyle.Single, r.Layout.StripStyle);
        Assert.Equal("Comic Sans MS", r.Layout.CaptionFontFamily);
        Assert.Equal(90, r.Flow.ExpectedPrintSeconds);
        Assert.Equal(3, r.Input.TriggerKeys.Count);
        Assert.Contains("PageDown", r.Input.TriggerKeys);
        Assert.Equal(2, r.Input.CancelCountdownKeys.Count);
    }

    [Fact]
    public void Keys_InvalidEntriesAreDroppedIndividually_NotWholeFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
        {
          "Flow": { "CountdownSeconds": 7 },
          "Input": {
            "TriggerKeys": [ "Space", "Quatsch", "Alt+F4", "LeftCtrl", "space", "", "Ctrl+Ctrl+A", "5" ],
            "AdminKeys": [ "Alt+Tab" ],
            "CancelCountdownKeys": [ "Space", "Escape" ]
          }
        }
        """);

        var s = new JsonSettingsStore(_dir).Current;

        Assert.Equal(7, s.Flow.CountdownSeconds);
        Assert.Equal(["Space"], s.Input.TriggerKeys);
        Assert.Equal(["Ctrl+Alt+A"], s.Input.AdminKeys);
        Assert.Equal(["Escape"], s.Input.CancelCountdownKeys);
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.bak-*"));
    }

    [Fact]
    public void Keys_TriggerAndAdminNeverEmpty_CancelMayBeEmpty()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Input.TriggerKeys = [];
        s.Input.AdminKeys = [];
        s.Input.CancelCountdownKeys = [];
        store.Save(s);

        Assert.Equal(["Space"], store.Current.Input.TriggerKeys);
        Assert.Equal(["Ctrl+Alt+A"], store.Current.Input.AdminKeys);
        Assert.Empty(store.Current.Input.CancelCountdownKeys);
    }

    [Fact]
    public void Keys_DefaultSpaceTakenByAdmin_MovesToTrigger()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Input.AdminKeys = ["Space", "Ctrl+Alt+A"];
        s.Input.TriggerKeys = ["Space"];
        store.Save(s);

        Assert.Equal(["Space"], store.Current.Input.TriggerKeys);
        Assert.Equal(["Ctrl+Alt+A"], store.Current.Input.AdminKeys);
    }

    [Fact]
    public void Keys_LimitedPerAction()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Input.TriggerKeys = Enumerable.Range(0, 12).Select(i => "F" + (i + 1)).ToList();
        store.Save(s);

        Assert.Equal(SettingsLimits.KeysPerActionMax, store.Current.Input.TriggerKeys.Count);
    }

    [Fact]
    public void Font_BlankFallsBackToSegoe_StripStyleInvalidFallsBack()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Layout.CaptionFontFamily = "   ";
        s.Layout.StripStyle = (StripStyle)42;
        store.Save(s);

        Assert.Equal("Segoe UI", store.Current.Layout.CaptionFontFamily);
        Assert.Equal(StripStyle.DoubleCut, store.Current.Layout.StripStyle);
    }

    [Fact]
    public void PrintTimeout_RaisedToExpectedDurationPlusReserve()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Flow.ExpectedPrintSeconds = 300;
        s.Printer.TimeoutSeconds = 120;
        store.Save(s);

        Assert.Equal(300 + SettingsLimits.PrintTimeoutReserveSeconds, store.Current.Printer.TimeoutSeconds);

        s.Flow.ExpectedPrintSeconds = 1;
        s.Printer.TimeoutSeconds = 500;
        store.Save(s);
        Assert.Equal(SettingsLimits.ExpectedPrintMin, store.Current.Flow.ExpectedPrintSeconds);
        Assert.Equal(500, store.Current.Printer.TimeoutSeconds);
    }

    // ---- Gastansicht (Hinweistext und Darstellung)

    [Fact]
    public void GuestScreen_HasDefaults_AndSurvivesMissingObjectInOldFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"Flow\": { \"CountdownSeconds\": 3 } }");

        var s = new JsonSettingsStore(_dir).Current;

        Assert.NotNull(s.GuestScreen);
        Assert.Null(s.GuestScreen.HintText);
        Assert.Equal(HintStyle.Pill, s.GuestScreen.HintStyle);
        Assert.Equal(3, s.Flow.CountdownSeconds);
    }

    [Fact]
    public void GuestScreen_NullObject_IsReplacedWithDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"GuestScreen\": null }");

        var s = new JsonSettingsStore(_dir).Current;

        Assert.NotNull(s.GuestScreen);
        Assert.Equal(HintStyle.Pill, s.GuestScreen.HintStyle);
    }

    [Fact]
    public void GuestScreen_Roundtrip_KeepsTextAndStyle_AndWritesStyleAsName()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.GuestScreen.HintText = "Drücke {Fototaste_1} {Klick}";
        s.GuestScreen.HintStyle = HintStyle.Banner;
        store.Save(s);

        var reloaded = new JsonSettingsStore(_dir).Current;

        Assert.Equal("Drücke {Fototaste_1} {Klick}", reloaded.GuestScreen.HintText);
        Assert.Equal(HintStyle.Banner, reloaded.GuestScreen.HintStyle);
        Assert.Contains("\"HintStyle\": \"Banner\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void GuestScreen_UnknownStyle_FallsBackToPill_WithoutLosingTheFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath,
            "{ \"Flow\": { \"CountdownSeconds\": 3 }, \"GuestScreen\": { \"HintStyle\": \"Neon\", \"HintText\": \"Hallo\" } }");

        var s = new JsonSettingsStore(_dir).Current;

        Assert.Equal(HintStyle.Pill, s.GuestScreen.HintStyle);
        Assert.Equal("Hallo", s.GuestScreen.HintText);
        Assert.Equal(3, s.Flow.CountdownSeconds);
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.bak-*"));
    }

    [Fact]
    public void GuestScreen_HintText_IsTrimmedToLimit_SurrogateSafe()
    {
        Directory.CreateDirectory(_dir);
        var tooLong = new string('h', SettingsLimits.HintTextMaxLength + 25);
        File.WriteAllText(SettingsPath, $$"""{ "GuestScreen": { "HintText": "{{tooLong}}" } }""");
        Assert.Equal(SettingsLimits.HintTextMaxLength, new JsonSettingsStore(_dir).Current.GuestScreen.HintText!.Length);

        // Ein Emoji (Surrogatpaar) an der Grenze wird nicht zerschnitten.
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.GuestScreen.HintText = new string('h', SettingsLimits.HintTextMaxLength - 1) + "📸📸";
        store.Save(s);

        var text = store.Current.GuestScreen.HintText!;
        Assert.Equal(SettingsLimits.HintTextMaxLength - 1, text.Length);
        Assert.False(char.IsHighSurrogate(text[^1]));
    }

    [Fact]
    public void GuestScreen_HintText_ReplacesControlCharactersAndLineBreaksWithSpaces()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.GuestScreen.HintText = "  Drücke\r\n{Fototaste_1}\tfür\u0007ein Foto \n";
        store.Save(s);

        Assert.Equal("Drücke  {Fototaste_1} für ein Foto", store.Current.GuestScreen.HintText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \r\n\t ")]
    public void GuestScreen_BlankHintText_BecomesNull(string text)
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.GuestScreen.HintText = text;
        store.Save(s);

        Assert.Null(store.Current.GuestScreen.HintText);
    }

    [Fact]
    public void GuestScreen_Save_UndefinedStyleFallsBackToPill()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.GuestScreen.HintStyle = (HintStyle)42;
        store.Save(s);

        Assert.Equal(HintStyle.Pill, store.Current.GuestScreen.HintStyle);
    }

    // ---- Darstellung (Farbschema der Admin-Ansicht)

    [Fact]
    public void Appearance_DefaultsToSystem_AndSurvivesMissingOrNullObject()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"Flow\": { \"CountdownSeconds\": 3 } }");
        Assert.Equal(AdminThemeMode.System, new JsonSettingsStore(_dir).Current.Appearance.AdminTheme);

        File.WriteAllText(SettingsPath, "{ \"Appearance\": null }");
        Assert.Equal(AdminThemeMode.System, new JsonSettingsStore(_dir).Current.Appearance.AdminTheme);
    }

    [Fact]
    public void Appearance_Roundtrip_WritesThemeAsName()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Appearance.AdminTheme = AdminThemeMode.Dark;
        store.Save(s);

        Assert.Equal(AdminThemeMode.Dark, new JsonSettingsStore(_dir).Current.Appearance.AdminTheme);
        Assert.Contains("\"AdminTheme\": \"Dark\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Appearance_UnknownTheme_FallsBackToSystem_WithoutLosingTheFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath,
            "{ \"Flow\": { \"CountdownSeconds\": 3 }, \"Appearance\": { \"AdminTheme\": \"Sepia\" } }");

        var s = new JsonSettingsStore(_dir).Current;

        Assert.Equal(AdminThemeMode.System, s.Appearance.AdminTheme);
        Assert.Equal(3, s.Flow.CountdownSeconds);
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.bak-*"));
    }

    [Fact]
    public void Appearance_Save_UndefinedThemeFallsBackToSystem()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Appearance.AdminTheme = (AdminThemeMode)42;
        store.Save(s);

        Assert.Equal(AdminThemeMode.System, store.Current.Appearance.AdminTheme);
    }

    // ---- Darstellung (Monitor der Gastansicht)

    [Fact]
    public void GuestMonitor_DefaultsToNull_AndSurvivesMissingOrNullAppearance()
    {
        var fresh = new AppSettings();
        Assert.Null(fresh.Appearance.GuestMonitorId);
        Assert.Null(fresh.Appearance.GuestMonitorName);

        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"Flow\": { \"CountdownSeconds\": 3 } }");
        var missing = new JsonSettingsStore(_dir).Current;
        Assert.Null(missing.Appearance.GuestMonitorId);
        Assert.Null(missing.Appearance.GuestMonitorName);

        File.WriteAllText(SettingsPath, "{ \"Appearance\": null }");
        var nulled = new JsonSettingsStore(_dir).Current;
        Assert.Null(nulled.Appearance.GuestMonitorId);
        Assert.Null(nulled.Appearance.GuestMonitorName);
    }

    [Fact]
    public void GuestMonitor_Roundtrip_KeepsIdExactly()
    {
        var id = @"\\?\DISPLAY#TST0001#5&1a2b3c&0&UID1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Appearance.GuestMonitorId = id;
        s.Appearance.GuestMonitorName = "Testmonitor";
        store.Save(s);

        var reloaded = new JsonSettingsStore(_dir).Current;
        Assert.Equal(id, reloaded.Appearance.GuestMonitorId);
        Assert.Equal("Testmonitor", reloaded.Appearance.GuestMonitorName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n")]
    public void GuestMonitor_WhitespaceId_BecomesNull_AndTakesNameWithIt(string whitespace)
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Appearance.GuestMonitorId = whitespace;
        s.Appearance.GuestMonitorName = "Testmonitor";
        store.Save(s);

        Assert.Null(store.Current.Appearance.GuestMonitorId);
        Assert.Null(store.Current.Appearance.GuestMonitorName);
    }

    [Fact]
    public void GuestMonitor_WhitespaceName_BecomesNull_ButIdStays()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Appearance.GuestMonitorId = @"\\.\DISPLAY2";
        s.Appearance.GuestMonitorName = "   ";
        store.Save(s);

        Assert.Equal(@"\\.\DISPLAY2", store.Current.Appearance.GuestMonitorId);
        Assert.Null(store.Current.Appearance.GuestMonitorName);
    }

    [Fact]
    public void GuestMonitor_TooLongId_IsDropped_WithItsName()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Appearance.GuestMonitorId = new string('x', SettingsLimits.MonitorIdMaxLength + 1);
        s.Appearance.GuestMonitorName = "Testmonitor";
        store.Save(s);

        Assert.Null(store.Current.Appearance.GuestMonitorId);
        Assert.Null(store.Current.Appearance.GuestMonitorName);
    }

    [Fact]
    public void GuestMonitor_IdAtLimit_IsKept()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Appearance.GuestMonitorId = new string('x', SettingsLimits.MonitorIdMaxLength);
        store.Save(s);

        Assert.Equal(SettingsLimits.MonitorIdMaxLength, store.Current.Appearance.GuestMonitorId!.Length);
    }

    [Fact]
    public void GuestMonitor_TooLongName_IsTruncated()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Appearance.GuestMonitorId = @"\\.\DISPLAY2";
        s.Appearance.GuestMonitorName = new string('n', SettingsLimits.MonitorNameMaxLength + 50);
        store.Save(s);

        Assert.Equal(new string('n', SettingsLimits.MonitorNameMaxLength), store.Current.Appearance.GuestMonitorName);
    }

    [Fact]
    public void GuestMonitor_NameWithoutId_BecomesNull_WhenLoadedFromFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath,
            "{ \"Flow\": { \"CountdownSeconds\": 3 }, \"Appearance\": { \"GuestMonitorName\": \"Testmonitor\" } }");

        var s = new JsonSettingsStore(_dir).Current;

        Assert.Null(s.Appearance.GuestMonitorId);
        Assert.Null(s.Appearance.GuestMonitorName);
        Assert.Equal(3, s.Flow.CountdownSeconds);
    }

    [Fact]
    public void Keys_AdminEmptyAndTriggerHoldsAdminDefault_BothKeepAKey_AndNormalizingIsIdempotent()
    {
        var input = new InputSettings { AdminKeys = [], TriggerKeys = ["Ctrl+Alt+A"], CancelCountdownKeys = [] };

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(["Space"], input.TriggerKeys);
        Assert.Equal(["Ctrl+Alt+A"], input.AdminKeys);

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(["Space"], input.TriggerKeys);
        Assert.Equal(["Ctrl+Alt+A"], input.AdminKeys);
    }

    [Fact]
    public void Keys_OnlySpaceInAdminAndTriggerEmpty_BothKeepAKey_AndCancelStaysClean()
    {
        var input = new InputSettings { AdminKeys = ["Space"], TriggerKeys = [], CancelCountdownKeys = ["Ctrl+Alt+A", "Escape"] };

        SettingsNormalizer.NormalizeKeys(input);

        Assert.Equal(["Space"], input.TriggerKeys);
        Assert.Equal(["Ctrl+Alt+A"], input.AdminKeys);
        Assert.Equal(["Escape"], input.CancelCountdownKeys);
    }

    [Fact]
    public void Normalize_KeepsStoredPinIterations_SoTheHashStillVerifies()
    {
        const string pin = "4711";
        const int iterations = 5000;
        var salt = new byte[16];
        for (var i = 0; i < salt.Length; i++)
            salt[i] = (byte)(i + 1);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            pin, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);

        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Security.PinIterations = iterations;
        s.Security.PinSalt = Convert.ToBase64String(salt);
        s.Security.PinHash = Convert.ToBase64String(hash);
        store.Save(s);

        Assert.Equal(iterations, store.Current.Security.PinIterations);
        Assert.True(new PinService(store).Verify(pin));
        Assert.Equal(iterations, new JsonSettingsStore(_dir).Current.Security.PinIterations);
    }

    [Fact]
    public void Save_ThrowingSettingsChangedHandler_DoesNotFailSave_AndOthersStillRun()
    {
        var store = new JsonSettingsStore(_dir);
        var secondCalled = 0;
        store.SettingsChanged += (_, _) => throw new InvalidOperationException("Handler kaputt");
        store.SettingsChanged += (_, _) => secondCalled++;
        var s = store.Current.Clone();
        s.Flow.CountdownSeconds = 9;

        store.Save(s);

        Assert.Equal(1, secondCalled);
        Assert.Equal(9, store.Current.Flow.CountdownSeconds);
        Assert.Equal(9, new JsonSettingsStore(_dir).Current.Flow.CountdownSeconds);
    }

    [Fact]
    public void Normalize_CaptionControlCharactersBecomeSpaces_AndIsTrimmed()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Layout.CaptionText = "  Hochzeit\r\nAnna\t& Ben \n";
        store.Save(s);
        Assert.Equal("Hochzeit  Anna & Ben", store.Current.Layout.CaptionText);

        s.Layout.CaptionText = " \r\n\t ";
        store.Save(s);
        Assert.True(string.IsNullOrWhiteSpace(store.Current.Layout.CaptionText));
    }

    [Fact]
    public void Normalize_FontNameAndPrefix_AreCutWithoutSplittingSurrogatePairs()
    {
        // Das Paar 😀 (zwei UTF-16-Zeichen) würde genau an der Grenze zerschnitten.
        var font = new string('f', SettingsLimits.FontNameMaxLength - 1) + "😀";
        var prefix = new string('p', SettingsLimits.FilePrefixMaxLength - 1) + "😀";

        var cleanFont = SettingsNormalizer.SanitizeFontName(font);
        var cleanPrefix = SettingsNormalizer.SanitizePrefix(prefix);

        Assert.Equal(new string('f', SettingsLimits.FontNameMaxLength - 1), cleanFont);
        Assert.Equal(new string('p', SettingsLimits.FilePrefixMaxLength - 1), cleanPrefix);
    }
}
