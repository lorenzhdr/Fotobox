using System.Text.Json;
using Fotobox.Core;
using Xunit;

namespace Fotobox.Tests;

public class PageSpecTests
{
    [Fact]
    public void Default_IsTenByFifteenPortrait_At300Dpi()
    {
        var spec = PageSpec.Default;

        Assert.Equal(1200, spec.PixelWidth);
        Assert.Equal(1800, spec.PixelHeight);
        Assert.False(spec.IsLandscape);
        Assert.Equal(300, spec.Dpi);
    }

    [Theory]
    [InlineData(210.0, 297.0, 2480, 3508)]
    [InlineData(148.0, 100.0, 1748, 1181)]
    public void PixelSize_IsComputedFromMillimetersAndDpi(double w, double h, int pw, int ph)
    {
        var spec = new PageSpec(w, h);

        Assert.Equal(pw, spec.PixelWidth);
        Assert.Equal(ph, spec.PixelHeight);
    }

    [Fact]
    public void PixelSize_RespectsCustomDpi()
    {
        var spec = new PageSpec(25.4, 50.8, 600);

        Assert.Equal(600, spec.PixelWidth);
        Assert.Equal(1200, spec.PixelHeight);
    }

    [Fact]
    public void ToOrientation_Landscape_SwapsPortraitSpec()
    {
        var landscape = PageSpec.Default.ToOrientation(true);

        Assert.True(landscape.IsLandscape);
        Assert.Equal(PageSpec.Default.HeightMm, landscape.WidthMm);
        Assert.Equal(PageSpec.Default.WidthMm, landscape.HeightMm);
        Assert.Equal(1800, landscape.PixelWidth);
        Assert.Equal(1200, landscape.PixelHeight);
    }

    [Fact]
    public void ToOrientation_SameOrientation_ReturnsSameInstance()
    {
        Assert.Same(PageSpec.Default, PageSpec.Default.ToOrientation(false));

        var landscape = PageSpec.Default.ToOrientation(true);
        Assert.Same(landscape, landscape.ToOrientation(true));
    }

    [Fact]
    public void ToOrientation_RoundTrip_RestoresOriginalValues()
    {
        var back = PageSpec.Default.ToOrientation(true).ToOrientation(false);

        Assert.Equal(PageSpec.Default, back);
    }
}

public class AppSettingsTests
{
    [Fact]
    public void Defaults_MatchSpecification()
    {
        var s = new AppSettings();

        Assert.Equal(2, s.Version);
        Assert.Equal(AppSettings.CurrentVersion, s.Version);
        Assert.Null(s.Camera.DeviceId);
        Assert.Null(s.Camera.ResolutionWidth);
        Assert.Null(s.Camera.ResolutionHeight);
        Assert.True(s.Camera.MirrorPreview);
        Assert.False(s.Camera.MirrorPhoto);

        Assert.Equal(5, s.Flow.CountdownSeconds);
        Assert.Equal(4, s.Flow.PreviewSeconds);
        Assert.Equal(2, s.Flow.PauseBetweenShotsSeconds);
        Assert.Equal(6, s.Flow.ErrorDisplaySeconds);
        Assert.True(s.Flow.PrintingEnabled);
        Assert.Equal(240, s.Flow.ExpectedPrintSeconds);

        Assert.Equal(LayoutKind.SingleBorderless, s.Layout.Kind);
        Assert.Equal(3, s.Layout.StripPhotoCount);
        Assert.Null(s.Layout.OverlayFileName);
        Assert.Null(s.Layout.CaptionText);
        Assert.False(s.Layout.ShowDate);

        Assert.Null(s.Printer.PrinterName);
        Assert.Null(s.Printer.PaperSizeName);
        Assert.Null(s.Printer.PaperWidthMm);
        Assert.Null(s.Printer.PaperHeightMm);
        Assert.Equal(PageOrientation.Auto, s.Printer.Orientation);
        Assert.Equal(1, s.Printer.Copies);
        Assert.Equal(300, s.Printer.TimeoutSeconds);

        Assert.Null(s.Storage.PhotoFolder);
        Assert.True(s.Storage.DateSubfolder);
        Assert.True(s.Storage.SaveOriginals);
        Assert.Equal(92, s.Storage.JpegQuality);
        Assert.Equal("Fotobox+", s.Storage.FilePrefix);

        Assert.Null(s.Security.PinHash);
        Assert.Null(s.Security.PinSalt);
        Assert.Equal(100_000, s.Security.PinIterations);
    }

    [Fact]
    public void VideoDefaults_MatchSpecification()
    {
        var v = new AppSettings().Video;

        Assert.True(v.Enabled);
        Assert.Equal(300, v.MaxDurationSeconds);
        Assert.Equal(3, v.CountdownSeconds);
        Assert.Equal(2, v.MinDurationSeconds);
        Assert.Equal(10, v.WarningSeconds);
        Assert.True(v.ShowRecDot);
        Assert.True(v.ShowElapsed);
        Assert.True(v.ShowRemaining);
        Assert.Equal(3, v.SuccessDisplaySeconds);
        Assert.Null(v.SuccessText);
        Assert.Equal(VideoQuality.High, v.Quality);
        Assert.True(v.RecordAudio);
        Assert.Null(v.MicrophoneId);
        Assert.Null(v.MicrophoneName);
        Assert.Null(v.Folder);
        Assert.True(v.DateSubfolder);
        Assert.Equal("Fotobox+_Video", v.FilePrefix);
    }

    [Fact]
    public void VideoInputDefaults_BothUseEnter_AndGuestHintDefaultsToAutomaticPill()
    {
        var s = new AppSettings();

        Assert.Equal(["Enter"], s.Input.VideoStartKeys);
        Assert.Equal(["Enter"], s.Input.VideoStopKeys);
        Assert.Null(s.GuestScreen.VideoHintText);
        Assert.Equal(HintStyle.Pill, s.GuestScreen.VideoHintStyle);
    }

    [Fact]
    public void Clone_IsDeepCopy()
    {
        var original = new AppSettings();
        original.Camera.DeviceId = "cam-1";
        original.Layout.Kind = LayoutKind.Strip;
        original.Printer.PrinterName = "Drucker A";

        var clone = original.Clone();

        Assert.NotSame(original, clone);
        Assert.NotSame(original.Camera, clone.Camera);
        Assert.NotSame(original.Flow, clone.Flow);
        Assert.NotSame(original.Layout, clone.Layout);
        Assert.NotSame(original.Printer, clone.Printer);
        Assert.NotSame(original.Storage, clone.Storage);
        Assert.NotSame(original.Security, clone.Security);
        Assert.Equal("cam-1", clone.Camera.DeviceId);
        Assert.Equal(LayoutKind.Strip, clone.Layout.Kind);

        clone.Camera.DeviceId = "cam-2";
        clone.Layout.Kind = LayoutKind.Overlay;
        clone.Printer.PrinterName = "Drucker B";

        Assert.Equal("cam-1", original.Camera.DeviceId);
        Assert.Equal(LayoutKind.Strip, original.Layout.Kind);
        Assert.Equal("Drucker A", original.Printer.PrinterName);
    }

    [Fact]
    public void Json_Serializes_EnumsAsStrings()
    {
        var s = new AppSettings();
        s.Layout.Kind = LayoutKind.Overlay;
        s.Printer.Orientation = PageOrientation.Landscape;

        var json = JsonSerializer.Serialize(s, AppSettings.JsonOptions);

        Assert.Contains("\"Kind\": \"Overlay\"", json);
        Assert.Contains("\"Orientation\": \"Landscape\"", json);
    }

    [Fact]
    public void Json_Roundtrip_PreservesAllValues()
    {
        var s = new AppSettings();
        s.Camera.DeviceId = "device\\id";
        s.Camera.ResolutionWidth = 1920;
        s.Camera.ResolutionHeight = 1080;
        s.Camera.MirrorPhoto = true;
        s.Flow.CountdownSeconds = 3;
        s.Flow.PrintingEnabled = false;
        s.Layout.Kind = LayoutKind.Strip;
        s.Layout.StripPhotoCount = 4;
        s.Layout.CaptionText = "Hochzeit Müller & Söhne – äöüß";
        s.Layout.ShowDate = true;
        s.Printer.PaperWidthMm = 101.6;
        s.Printer.PaperHeightMm = 152.4;
        s.Printer.Orientation = PageOrientation.Portrait;
        s.Storage.PhotoFolder = @"D:\Fotos";
        s.Storage.JpegQuality = 80;
        s.Security.PinHash = "hash";
        s.Security.PinSalt = "salt";

        var json = JsonSerializer.Serialize(s, AppSettings.JsonOptions);
        var back = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions);

        Assert.NotNull(back);
        Assert.Equal(json, JsonSerializer.Serialize(back, AppSettings.JsonOptions));
        Assert.Equal("device\\id", back.Camera.DeviceId);
        Assert.Equal(1920, back.Camera.ResolutionWidth);
        Assert.Equal(LayoutKind.Strip, back.Layout.Kind);
        Assert.Equal("Hochzeit Müller & Söhne – äöüß", back.Layout.CaptionText);
        Assert.Equal(PageOrientation.Portrait, back.Printer.Orientation);
        Assert.Equal(101.6, back.Printer.PaperWidthMm);
        Assert.Equal(@"D:\Fotos", back.Storage.PhotoFolder);
        Assert.Equal("hash", back.Security.PinHash);
    }

    [Fact]
    public void Json_MissingProperties_FallBackToDefaults()
    {
        var back = JsonSerializer.Deserialize<AppSettings>("{ \"Flow\": { \"CountdownSeconds\": 9 } }", AppSettings.JsonOptions);

        Assert.NotNull(back);
        Assert.Equal(9, back.Flow.CountdownSeconds);
        Assert.Equal(4, back.Flow.PreviewSeconds);
        Assert.Equal(LayoutKind.SingleBorderless, back.Layout.Kind);
        Assert.NotNull(back.Camera);
    }
}

public class AppLogFormatTests
{
    [Fact]
    public void Formatting_IsCultureInvariant()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            var now = new DateTime(2026, 3, 7, 14, 5, 9, 123);

            Assert.Equal("2026-03-07 14:05:09.123", AppLog.FormatTimestamp(now));
            Assert.Equal("fotobox-20260307.log", AppLog.LogFileName(now));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = old;
        }
    }
}
