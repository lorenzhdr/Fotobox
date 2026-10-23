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
        Assert.Equal(15, s.Printer.TimeoutSeconds);
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
        Assert.Equal(15, store.Current.Printer.TimeoutSeconds);
        Assert.Equal(50, store.Current.Storage.JpegQuality);
        Assert.Equal(10_000, store.Current.Security.PinIterations);

        var high = new AppSettings();
        high.Flow.CountdownSeconds = 31;
        high.Flow.PauseBetweenShotsSeconds = 11;
        high.Flow.ErrorDisplaySeconds = 31;
        high.Printer.Copies = 11;
        high.Printer.TimeoutSeconds = 601;
        high.Storage.JpegQuality = 101;
        store.Save(high);

        Assert.Equal(30, store.Current.Flow.CountdownSeconds);
        Assert.Equal(10, store.Current.Flow.PauseBetweenShotsSeconds);
        Assert.Equal(30, store.Current.Flow.ErrorDisplaySeconds);
        Assert.Equal(10, store.Current.Printer.Copies);
        Assert.Equal(600, store.Current.Printer.TimeoutSeconds);
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
}
