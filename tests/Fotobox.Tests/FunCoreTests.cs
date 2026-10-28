using System.IO;
using System.Text;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Fotobox.Services.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Normalisierung der Fun-Einstellungen, Overlay-Abbildung, Zielgeometrie, Tracker, Text und Töne.</summary>
public sealed class FunCoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FotoboxTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private JsonSettingsStore StoreWithFile(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);
        return new JsonSettingsStore(_dir);
    }

    private static AppSettings Normalized(Action<AppSettings> configure)
    {
        var s = new AppSettings();
        configure(s);
        return SettingsNormalizer.Normalize(s);
    }

    private static void AssertRect(NormRect expected, NormRect actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }

    // ---- Normalizer

    [Fact]
    public void Fun_Defaults_AreValid_AndUnchangedByNormalizer()
    {
        var f = Normalized(_ => { }).Fun;
        var d = new FunSettings();

        Assert.True(f.Enabled);
        Assert.Equal(TargetScope.FullBody, f.Scope);
        Assert.Equal(d.TextSeconds, f.TextSeconds);
        Assert.Equal(d.CooldownSeconds, f.CooldownSeconds);
        Assert.Equal(d.MinSizePercent, f.MinSizePercent);
        Assert.Equal(d.MaxTargets, f.MaxTargets);
        Assert.Equal(d.SensitivityPercent, f.SensitivityPercent);
        Assert.Equal(d.DwellMilliseconds, f.DwellMilliseconds);
        Assert.Equal(d.AnalysesPerSecond, f.AnalysesPerSecond);
    }

    [Fact]
    public void Fun_NumbersAboveLimits_AreClamped()
    {
        var f = Normalized(s =>
        {
            s.Fun.TextSeconds = 9999;
            s.Fun.CooldownSeconds = 99_999;
            s.Fun.MinSizePercent = 999;
            s.Fun.MaxTargets = 99;
            s.Fun.SensitivityPercent = 999;
            s.Fun.DwellMilliseconds = 99_999;
            s.Fun.AnalysesPerSecond = 99;
        }).Fun;

        Assert.Equal(SettingsLimits.FunTextSecondsMax, f.TextSeconds);
        Assert.Equal(SettingsLimits.FunCooldownSecondsMax, f.CooldownSeconds);
        Assert.Equal(SettingsLimits.FunMinSizePercentMax, f.MinSizePercent);
        Assert.Equal(SettingsLimits.FunMaxTargetsMax, f.MaxTargets);
        Assert.Equal(SettingsLimits.FunSensitivityMax, f.SensitivityPercent);
        Assert.Equal(SettingsLimits.FunDwellMillisecondsMax, f.DwellMilliseconds);
        Assert.Equal(SettingsLimits.FunAnalysesPerSecondMax, f.AnalysesPerSecond);
    }

    [Fact]
    public void Fun_NumbersBelowLimits_AreClamped()
    {
        var f = Normalized(s =>
        {
            s.Fun.TextSeconds = -1;
            s.Fun.CooldownSeconds = -1;
            s.Fun.MinSizePercent = -1;
            s.Fun.MaxTargets = 0;
            s.Fun.SensitivityPercent = 0;
            s.Fun.DwellMilliseconds = -5;
            s.Fun.AnalysesPerSecond = 0;
        }).Fun;

        Assert.Equal(SettingsLimits.FunTextSecondsMin, f.TextSeconds);
        Assert.Equal(SettingsLimits.FunCooldownSecondsMin, f.CooldownSeconds);
        Assert.Equal(SettingsLimits.FunMinSizePercentMin, f.MinSizePercent);
        Assert.Equal(SettingsLimits.FunMaxTargetsMin, f.MaxTargets);
        Assert.Equal(SettingsLimits.FunSensitivityMin, f.SensitivityPercent);
        Assert.Equal(SettingsLimits.FunDwellMillisecondsMin, f.DwellMilliseconds);
        Assert.Equal(SettingsLimits.FunAnalysesPerSecondMin, f.AnalysesPerSecond);
    }

    [Fact]
    public void Fun_UndefinedEnums_FallBackToDefaults()
    {
        var f = Normalized(s =>
        {
            s.Fun.Method = (DetectionMethod)77;
            s.Fun.Scope = (TargetScope)77;
            s.Fun.BoxStyle = (TargetBoxStyle)77;
            s.Fun.Color = (TargetColor)77;
            s.Fun.Animation = (LockOnAnimation)77;
            s.Fun.TextPosition = (FunTextPosition)77;
            s.Fun.TextStyle = (FunTextStyle)77;
            s.Fun.TextHintStyle = (HintStyle)77;
        }).Fun;
        var d = new FunSettings();

        Assert.Equal(d.Method, f.Method);
        Assert.Equal(d.Scope, f.Scope);
        Assert.Equal(d.BoxStyle, f.BoxStyle);
        Assert.Equal(d.Color, f.Color);
        Assert.Equal(d.Animation, f.Animation);
        Assert.Equal(d.TextPosition, f.TextPosition);
        Assert.Equal(d.TextStyle, f.TextStyle);
        Assert.Equal(d.TextHintStyle, f.TextHintStyle);
    }

    [Fact]
    public void Fun_UnknownEnumNamesInFile_DoNotInvalidateTheFile()
    {
        var store = StoreWithFile(
            "{ \"Version\": 2, \"Flow\": { \"CountdownSeconds\": 7 }, \"Fun\": { \"Enabled\": true, \"Scope\": \"Hologramm\", " +
            "\"Color\": \"Lila\", \"TextStyle\": \"Neonröhre\", \"TextHintStyle\": \"Zukunft\", \"MaxTargets\": 3 } }");

        Assert.False(store.LoadFailed);
        var s = store.Current;
        Assert.Equal(7, s.Flow.CountdownSeconds);
        Assert.True(s.Fun.Enabled);
        Assert.Equal(3, s.Fun.MaxTargets);
        Assert.Equal(TargetScope.FullBody, s.Fun.Scope);
        Assert.Equal(TargetColor.Red, s.Fun.Color);
        Assert.Equal(FunTextStyle.Hud, s.Fun.TextStyle);
        Assert.Equal(HintStyle.Pill, s.Fun.TextHintStyle);
    }

    [Fact]
    public void Fun_MissingOrNullObject_IsReplacedWithDefaults()
    {
        foreach (var json in new[] { "{ \"Version\": 2 }", "{ \"Version\": 2, \"Fun\": null }" })
        {
            var s = StoreWithFile(json).Current;
            Assert.NotNull(s.Fun);
            Assert.True(s.Fun.Enabled);
            Assert.Equal(TargetScope.FullBody, s.Fun.Scope);
            Assert.Equal(6, s.Fun.TextSeconds);
        }

        var direct = new AppSettings { Fun = null! };
        Assert.NotNull(SettingsNormalizer.Normalize(direct).Fun);
    }

    [Fact]
    public void Fun_Texts_BlankBecomesNull_AndLongTextIsTrimmed()
    {
        Assert.Null(Normalized(s => s.Fun.SingularText = "   ").Fun.SingularText);
        Assert.Null(Normalized(s => s.Fun.PluralText = "").Fun.PluralText);
        Assert.Null(Normalized(s => s.Fun.PluralText = null).Fun.PluralText);
        Assert.Equal("Hey  du", Normalized(s => s.Fun.SingularText = "  Hey\r\ndu ").Fun.SingularText);

        var f = Normalized(s =>
        {
            s.Fun.SingularText = new string('a', SettingsLimits.HintTextMaxLength + 40);
            s.Fun.PluralText = new string('b', SettingsLimits.HintTextMaxLength - 1) + "📸📸"; // Surrogatpaar an der Grenze
        }).Fun;
        Assert.Equal(SettingsLimits.HintTextMaxLength, f.SingularText!.Length);
        Assert.True(f.PluralText!.Length <= SettingsLimits.HintTextMaxLength);
        Assert.False(char.IsHighSurrogate(f.PluralText[^1]));
    }

    [Fact]
    public void Fun_Roundtrip_KeepsValues_AndWritesEnumsAsNames()
    {
        Directory.CreateDirectory(_dir);
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings();
        s.Fun.Enabled = true;
        s.Fun.Scope = TargetScope.FullBody;
        s.Fun.Color = TargetColor.Cyan;
        s.Fun.PluralText = "Ihr {Anzahl}!";
        store.Save(s);

        var json = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.Contains("\"FullBody\"", json);

        var reloaded = new JsonSettingsStore(_dir).Current.Fun;
        Assert.True(reloaded.Enabled);
        Assert.Equal(TargetScope.FullBody, reloaded.Scope);
        Assert.Equal(TargetColor.Cyan, reloaded.Color);
        Assert.Equal("Ihr {Anzahl}!", reloaded.PluralText);
    }

    // ---- OverlayMapper

    [Fact]
    public void Map_Identity_WhenImageAndViewMatch()
    {
        var r = OverlayMapper.Map(new NormRect(0.25, 0.5, 0.5, 0.25), 1920, 1080, 1920, 1080, OverlayFit.Uniform, mirror: false);
        Assert.Equal(480, r.X, 6);
        Assert.Equal(540, r.Y, 6);
        Assert.Equal(960, r.Width, 6);
        Assert.Equal(270, r.Height, 6);
    }

    [Fact]
    public void Map_Uniform_CentersImage_WithBars()
    {
        // 4:3 in 16:9 → links/rechts je 200 px Rand.
        var r = OverlayMapper.Map(NormRect.Full, 640, 480, 1600, 900, OverlayFit.Uniform, mirror: false);
        Assert.Equal(new ViewRect(200, 0, 1200, 900), r);
    }

    [Fact]
    public void Map_UniformToFill_CropsImage()
    {
        var r = OverlayMapper.Map(NormRect.Full, 640, 480, 1600, 900, OverlayFit.UniformToFill, mirror: false);
        Assert.Equal(0, r.X, 6);
        Assert.Equal(-150, r.Y, 6);
        Assert.Equal(1600, r.Width, 6);
        Assert.Equal(1200, r.Height, 6);
    }

    [Theory]
    [InlineData(OverlayFit.Uniform)]
    [InlineData(OverlayFit.UniformToFill)]
    public void Map_Mirror_FlipsHorizontallyWithinImage(OverlayFit fit)
    {
        var box = new NormRect(0.1, 0.2, 0.25, 0.3);
        var plain = OverlayMapper.Map(box, 640, 480, 1600, 900, fit, mirror: false);
        var mirrored = OverlayMapper.Map(box, 640, 480, 1600, 900, fit, mirror: true);
        var full = OverlayMapper.Map(NormRect.Full, 640, 480, 1600, 900, fit, mirror: false);

        Assert.Equal(plain.Y, mirrored.Y, 6);
        Assert.Equal(plain.Width, mirrored.Width, 6);
        Assert.Equal(plain.Height, mirrored.Height, 6);
        // Abstand zum linken Bildrand wird zum Abstand zum rechten.
        Assert.Equal(plain.X - full.X, full.Right - mirrored.Right, 6);
    }

    [Fact]
    public void Map_UnknownImageSize_AssumesSixteenByNine()
    {
        var unknown = OverlayMapper.Map(new NormRect(0.1, 0.1, 0.5, 0.5), 0, 0, 800, 600, OverlayFit.Uniform, mirror: false);
        var known = OverlayMapper.Map(new NormRect(0.1, 0.1, 0.5, 0.5), 1920, 1080, 800, 600, OverlayFit.Uniform, mirror: false);
        var shortcut = OverlayMapper.Map(new NormRect(0.1, 0.1, 0.5, 0.5), 800, 600, OverlayFit.Uniform, mirror: false);

        Assert.Equal(known.X, unknown.X, 6);
        Assert.Equal(known.Y, unknown.Y, 6);
        Assert.Equal(known.Width, unknown.Width, 6);
        Assert.Equal(known.Height, unknown.Height, 6);
        Assert.Equal(unknown, shortcut);
        Assert.Equal(16.0 / 9, OverlayMapper.DefaultAspect, 9);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, -5)]
    [InlineData(double.NaN, 100)]
    public void Map_InvalidView_GivesEmptyRect(double w, double h)
    {
        var r = OverlayMapper.Map(NormRect.Full, 1920, 1080, w, h, OverlayFit.Uniform, mirror: true);
        Assert.True(r.IsEmpty);
        Assert.Equal(default, r);
    }

    [Fact]
    public void VisibleRegion_Uniform_IsFullImage()
    {
        Assert.Equal(NormRect.Full, OverlayMapper.VisibleRegion(640, 480, 1600, 900, OverlayFit.Uniform));
    }

    [Fact]
    public void VisibleRegion_FourByThreeInSixteenByNine_CropsTopAndBottom()
    {
        var r = OverlayMapper.VisibleRegion(640, 480, 1600, 900, OverlayFit.UniformToFill);
        AssertRect(new NormRect(0, 0.125, 1, 0.75), r);
        Assert.Equal(0.125, 1 - r.Bottom, 6); // oben und unten je 12,5 %
    }

    [Fact]
    public void VisibleRegion_SixteenByNineInFourByThree_CropsLeftAndRight()
    {
        var r = OverlayMapper.VisibleRegion(1600, 900, 800, 600, OverlayFit.UniformToFill);
        AssertRect(new NormRect(0.125, 0, 0.75, 1), r);
    }

    [Fact]
    public void VisibleRegion_SameAspect_IsFull_AndInvalidViewIsFull()
    {
        AssertRect(NormRect.Full, OverlayMapper.VisibleRegion(1920, 1080, 800, 450, OverlayFit.UniformToFill));
        Assert.Equal(NormRect.Full, OverlayMapper.VisibleRegion(1920, 1080, 0, 450, OverlayFit.UniformToFill));
    }

    [Fact]
    public void VisibleRegion_UnknownImage_Uses16To9()
    {
        // 16:9 im 4:3-Fenster: links/rechts je 12,5 % beschnitten.
        AssertRect(new NormRect(0.125, 0, 0.75, 1), OverlayMapper.VisibleRegion(800, 600, OverlayFit.UniformToFill));
    }

    // ---- TargetGeometry

    private static Detection Face(double x, double y, double w, double h) => new(new NormRect(x, y, w, h), 1, DetectionKind.Face);
    private static Detection Body(double x, double y, double w, double h) => new(new NormRect(x, y, w, h), 0.9, DetectionKind.Body);

    [Fact]
    public void Geometry_Face_AllScopes()
    {
        var face = Face(0.4, 0.3, 0.1, 0.1);
        AssertRect(new NormRect(0.38, 0.27, 0.14, 0.14), TargetGeometry.ToTargetBox(face, TargetScope.Head));
        AssertRect(new NormRect(0.30, 0.27, 0.30, 0.40), TargetGeometry.ToTargetBox(face, TargetScope.UpperBody));
        AssertRect(new NormRect(0.275, 0.27, 0.35, 0.73), TargetGeometry.ToTargetBox(face, TargetScope.FullBody)); // unten gekappt
    }

    [Fact]
    public void Geometry_Face_HeadIsLargerThanFace_AndStartsAboveIt()
    {
        var face = Face(0.4, 0.4, 0.1, 0.12);
        var head = TargetGeometry.ToTargetBox(face, TargetScope.Head);
        Assert.True(head.Width > face.Box.Width);
        Assert.True(head.Height > face.Box.Height);
        Assert.True(head.Y < face.Box.Y);
        Assert.Equal(face.Box.CenterX, head.CenterX, 6);
    }

    [Fact]
    public void Geometry_Body_AllScopes()
    {
        var body = Body(0.2, 0.1, 0.2, 0.8);
        AssertRect(new NormRect(0.26, 0.1, 0.08, 0.144), TargetGeometry.ToTargetBox(body, TargetScope.Head));
        AssertRect(new NormRect(0.2, 0.1, 0.2, 0.44), TargetGeometry.ToTargetBox(body, TargetScope.UpperBody));
        AssertRect(new NormRect(0.2, 0.1, 0.2, 0.8), TargetGeometry.ToTargetBox(body, TargetScope.FullBody));
    }

    [Theory]
    [InlineData(TargetScope.Head)]
    [InlineData(TargetScope.UpperBody)]
    [InlineData(TargetScope.FullBody)]
    public void Geometry_AtImageEdge_IsClampedToImage(TargetScope scope)
    {
        foreach (var d in new[] { Face(0.0, 0.0, 0.1, 0.1), Face(0.92, 0.9, 0.1, 0.1), Body(-0.1, -0.1, 0.5, 1.4), Body(0.8, 0.5, 0.5, 0.8) })
        {
            var r = TargetGeometry.ToTargetBox(d, scope);
            Assert.InRange(r.X, 0, 1);
            Assert.InRange(r.Y, 0, 1);
            Assert.InRange(r.Right, 0, 1);
            Assert.InRange(r.Bottom, 0, 1);
        }
    }

    [Fact]
    public void Geometry_FullyOutsideImage_IsEmpty()
    {
        Assert.True(TargetGeometry.ToTargetBox(Body(1.5, 0.1, 0.2, 0.5), TargetScope.FullBody).IsEmpty);
    }

    // ---- TargetTracker

    private static (TargetTracker Tracker, FakeTimeProvider Time) NewTracker(int dwellMs = 0)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var tracker = new TargetTracker(time);
        tracker.Configure(TimeSpan.FromMilliseconds(dwellMs));
        return (tracker, time);
    }

    [Fact]
    public void Tracker_NewBoxGetsNewId_AndSamePlaceKeepsIt()
    {
        var (tracker, time) = NewTracker();
        var box = new NormRect(0.2, 0.2, 0.2, 0.5);

        var first = Assert.Single(tracker.Update([box]));
        time.Advance(TimeSpan.FromMilliseconds(200));
        var second = Assert.Single(tracker.Update([new NormRect(0.21, 0.2, 0.2, 0.5)]));
        time.Advance(TimeSpan.FromMilliseconds(200));
        var other = tracker.Update([new NormRect(0.21, 0.2, 0.2, 0.5), new NormRect(0.7, 0.2, 0.2, 0.5)]);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, other.Count);
        Assert.Equal(first.Id, other.Single(t => t.Box.X < 0.5).Id);
        Assert.NotEqual(first.Id, other.Single(t => t.Box.X > 0.5).Id);
    }

    [Fact]
    public void Tracker_BoxWithoutOverlap_BecomesNewTarget()
    {
        var (tracker, time) = NewTracker();
        var a = Assert.Single(tracker.Update([new NormRect(0.1, 0.1, 0.2, 0.4)]));
        time.Advance(TimeSpan.FromMilliseconds(100));
        var list = tracker.Update([new NormRect(0.6, 0.1, 0.2, 0.4)]);

        Assert.Equal(2, list.Count); // altes bleibt erhalten, neues kommt dazu
        Assert.NotEqual(a.Id, list.Single(t => t.Box.X > 0.5).Id);
    }

    [Fact]
    public void Tracker_GreedyAssignment_BestPairFirst()
    {
        var (tracker, time) = NewTracker();
        var left = new NormRect(0.1, 0.1, 0.3, 0.5);
        var right = new NormRect(0.35, 0.1, 0.3, 0.5); // überlappt links teilweise
        var ids = tracker.Update([left, right]).ToDictionary(t => t.Box.X, t => t.Id);
        time.Advance(TimeSpan.FromMilliseconds(100));

        // Beide leicht verschoben: jedes Ziel muss seine eigene Box bekommen, nicht die benachbarte.
        var list = tracker.Update([new NormRect(0.36, 0.1, 0.3, 0.5), new NormRect(0.11, 0.1, 0.3, 0.5)]);
        Assert.Equal(2, list.Count);
        Assert.Equal(ids[0.1], list.Single(t => t.Box.X < 0.2).Id);
        Assert.Equal(ids[0.35], list.Single(t => t.Box.X > 0.3).Id);
    }

    [Fact]
    public void Tracker_SmoothsPositionWithEma()
    {
        var (tracker, time) = NewTracker();
        tracker.Update([new NormRect(0.2, 0.2, 0.2, 0.4)]);
        time.Advance(TimeSpan.FromMilliseconds(100));
        var t = Assert.Single(tracker.Update([new NormRect(0.3, 0.2, 0.2, 0.4)]));

        Assert.Equal(0.25, t.Box.X, 6); // Mitte zwischen alt und neu (alpha 0,5)
        time.Advance(TimeSpan.FromMilliseconds(100));
        t = Assert.Single(tracker.Update([new NormRect(0.3, 0.2, 0.2, 0.4)]));
        Assert.Equal(0.275, t.Box.X, 6);
    }

    [Fact]
    public void Tracker_ConfirmsAfterDwell()
    {
        var (tracker, time) = NewTracker(dwellMs: 800);
        var box = new NormRect(0.2, 0.2, 0.2, 0.5);

        Assert.False(Assert.Single(tracker.Update([box])).IsConfirmed);
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(Assert.Single(tracker.Update([box])).IsConfirmed);
        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(Assert.Single(tracker.Update([box])).IsConfirmed);
    }

    [Fact]
    public void Tracker_ZeroDwell_ConfirmsImmediately()
    {
        var (tracker, _) = NewTracker(dwellMs: 0);
        Assert.True(Assert.Single(tracker.Update([new NormRect(0.2, 0.2, 0.2, 0.5)])).IsConfirmed);
    }

    [Fact]
    public void Tracker_LostTarget_StaysFor700ms_ThenDisappears()
    {
        var (tracker, time) = NewTracker();
        var box = new NormRect(0.2, 0.2, 0.2, 0.5);
        var id = Assert.Single(tracker.Update([box])).Id;

        time.Advance(TimeSpan.FromMilliseconds(600));
        var kept = Assert.Single(tracker.Update([]));
        Assert.Equal(id, kept.Id);
        Assert.Equal(box, kept.Box);

        time.Advance(TimeSpan.FromMilliseconds(100)); // 700 ms seit letztem Treffer
        Assert.Empty(tracker.Update([]));
    }

    [Fact]
    public void Tracker_ReappearsWithinTimeout_KeepsId()
    {
        var (tracker, time) = NewTracker();
        var box = new NormRect(0.2, 0.2, 0.2, 0.5);
        var id = Assert.Single(tracker.Update([box])).Id;
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(id, Assert.Single(tracker.Update([box])).Id);
    }

    [Fact]
    public void Tracker_ReappearsAfterTimeout_GetsNewId()
    {
        var (tracker, time) = NewTracker();
        var box = new NormRect(0.2, 0.2, 0.2, 0.5);
        var id = Assert.Single(tracker.Update([box])).Id;
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(id, Assert.Single(tracker.Update([box])).Id);
    }

    [Fact]
    public void Tracker_PruneDropsExpired_AndReportsConfirmationOverTime()
    {
        var (tracker, time) = NewTracker(dwellMs: 500);
        tracker.Update([new NormRect(0.2, 0.2, 0.2, 0.5)]);

        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(Assert.Single(tracker.Prune()).IsConfirmed); // Bestätigung auch ohne neue Analyse

        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Empty(tracker.Prune());
    }

    [Fact]
    public void Tracker_Reset_ClearsTargets_IdsStayUnique()
    {
        var (tracker, _) = NewTracker();
        var id = Assert.Single(tracker.Update([new NormRect(0.2, 0.2, 0.2, 0.5)])).Id;
        tracker.Reset();
        Assert.Empty(tracker.Prune());
        Assert.NotEqual(id, Assert.Single(tracker.Update([new NormRect(0.2, 0.2, 0.2, 0.5)])).Id);
    }

    // ---- FunTextFormatter

    [Fact]
    public void Text_NoPeople_IsEmpty()
    {
        Assert.Equal("", FunTextFormatter.Resolve(new FunSettings(), 0));
        Assert.Equal("", FunTextFormatter.Resolve(new FunSettings(), -2));
    }

    [Fact]
    public void Text_SingularVsPlural_AndDefaults()
    {
        var s = new FunSettings();
        Assert.Equal(FunSettings.DefaultSingularText, FunTextFormatter.Resolve(s, 1));
        Assert.Equal(FunSettings.DefaultPluralText, FunTextFormatter.Resolve(s, 2));
        Assert.Equal(FunSettings.DefaultPluralText, FunTextFormatter.Resolve(s, 7));

        s.SingularText = "Allein";
        s.PluralText = "Gruppe";
        Assert.Equal("Allein", FunTextFormatter.Resolve(s, 1));
        Assert.Equal("Gruppe", FunTextFormatter.Resolve(s, 3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Text_BlankFallsBackToDefault(string? text)
    {
        var s = new FunSettings { SingularText = text, PluralText = text };
        Assert.Equal(FunSettings.DefaultSingularText, FunTextFormatter.Resolve(s, 1));
        Assert.Equal(FunSettings.DefaultPluralText, FunTextFormatter.Resolve(s, 2));
    }

    [Fact]
    public void Text_CountPlaceholder_IsCaseInsensitive_AndUnknownStaysLiteral()
    {
        var s = new FunSettings { PluralText = "{Anzahl} von {ANZAHL} oder {anzahl}, aber {Klick} und {x" };
        Assert.Equal("4 von 4 oder 4, aber {Klick} und {x", FunTextFormatter.Resolve(s, 4));
    }

    // ---- ToneSynth

    [Theory]
    [InlineData(FunSoundCue.Acquire, 80)]
    [InlineData(FunSoundCue.Lock, 190)]
    [InlineData(FunSoundCue.Text, 150)]
    public void ToneSynth_CreatesValidWav(FunSoundCue cue, int expectedMilliseconds)
    {
        var wav = ToneSynth.Create(cue);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));       // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));       // mono
        Assert.Equal(44_100, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));

        var dataBytes = BitConverter.ToInt32(wav, 40);
        Assert.Equal(wav.Length - 44, dataBytes);
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));

        var milliseconds = dataBytes / 2 * 1000.0 / 44_100;
        Assert.InRange(milliseconds, expectedMilliseconds - 2, expectedMilliseconds + 2);
    }

    [Theory]
    [InlineData(FunSoundCue.Acquire)]
    [InlineData(FunSoundCue.Lock)]
    [InlineData(FunSoundCue.Text)]
    public void ToneSynth_IsModerateAndFadesAtTheEdges(FunSoundCue cue)
    {
        var wav = ToneSynth.Create(cue);
        var samples = new short[(wav.Length - 44) / 2];
        Buffer.BlockCopy(wav, 44, samples, 0, wav.Length - 44);

        var peak = samples.Max(s => Math.Abs((int)s));
        Assert.InRange(peak, short.MaxValue * 0.2, short.MaxValue * 0.4); // moderate Lautstärke
        Assert.True(Math.Abs((int)samples[0]) < short.MaxValue * 0.1);
        Assert.True(Math.Abs((int)samples[^1]) < short.MaxValue * 0.1);
    }

    [Fact]
    public void ToneSynth_LockHasPauseBetweenTwoTones()
    {
        var wav = ToneSynth.Create(FunSoundCue.Lock);
        var samples = new short[(wav.Length - 44) / 2];
        Buffer.BlockCopy(wav, 44, samples, 0, wav.Length - 44);

        var middle = samples.Length / 2;
        Assert.All(samples.Skip(middle - 100).Take(200), s => Assert.Equal(0, s));
    }

    [Fact]
    public void ToneSynth_IsCached()
    {
        Assert.Same(ToneSynth.Create(FunSoundCue.Acquire), ToneSynth.Create(FunSoundCue.Acquire));
        Assert.NotSame(ToneSynth.Create(FunSoundCue.Acquire), ToneSynth.Create(FunSoundCue.Text));
    }
}
