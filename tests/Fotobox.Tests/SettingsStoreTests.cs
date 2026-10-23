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
        Assert.Equal("Fotobox", store.Current.Storage.FilePrefix);
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
    public void UnknownEnumValue_IsTreatedAsBrokenFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"Layout\": { \"Kind\": \"Quatsch\" } }");

        var store = new JsonSettingsStore(_dir);

        Assert.Equal(LayoutKind.SingleBorderless, store.Current.Layout.Kind);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.bak-*"));
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
        // Untergrenze ist die erwartete Druckdauer (Standard 120 s) plus Reserve.
        Assert.Equal(120 + SettingsLimits.PrintTimeoutReserveSeconds, s.Printer.TimeoutSeconds);
        Assert.Equal(50, s.Storage.JpegQuality);
        Assert.Equal("abcd", s.Storage.FilePrefix);
        Assert.Equal(10_000, s.Security.PinIterations);
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
        Assert.Equal(120 + SettingsLimits.PrintTimeoutReserveSeconds, store.Current.Printer.TimeoutSeconds);
        Assert.Equal(50, store.Current.Storage.JpegQuality);
        Assert.Equal(10_000, store.Current.Security.PinIterations);

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
    public void EmptyOrInvalidPrefix_BecomesFotobox(string prefix)
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Storage.FilePrefix = prefix;
        store.Save(s);

        Assert.Equal("Fotobox", store.Current.Storage.FilePrefix);
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
        Assert.Equal(120, s.Flow.ExpectedPrintSeconds);
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
}
