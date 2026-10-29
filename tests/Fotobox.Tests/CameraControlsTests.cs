using System.IO;
using System.Text.Json;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Kamerasteuerung: Rechenhilfen, Anzeige, Normalisierung, tiefes Kopieren und Laden aus settings.json.</summary>
public sealed class CameraControlsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FotoboxTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private static CameraControlInfo Info(
        CameraControlKind kind, double min = 0, double max = 100, double step = 1, double def = 50,
        bool auto = false, double? value = null, bool? isAuto = null) =>
        new(kind, true, true, min, max, step, def, auto, value, isAuto);

    private static CameraControlValue V(CameraControlKind kind, double? value = null, bool? auto = null) =>
        new() { Kind = kind, Value = value, Auto = auto };

    private static AppSettings Normalized(Action<CameraSettings> configure)
    {
        var s = new AppSettings();
        configure(s.Camera);
        return SettingsNormalizer.Normalize(s);
    }

    // ---------- Defaults ----------

    [Fact]
    public void Defaults_OfCameraSettings()
    {
        var c = new CameraSettings();

        Assert.Null(c.ControlsDeviceId);
        Assert.Empty(c.ControlValues);
        Assert.Empty(c.ControlProfiles);
        Assert.Equal(CameraPowerline.Hz50, c.Powerline);
        Assert.Equal(10, SettingsLimits.CameraProfilesMax);
        Assert.Equal(40, SettingsLimits.CameraProfileNameMaxLength);
        Assert.Same(CameraControlsSnapshot.Empty, CameraControlsSnapshot.Empty);
        Assert.Empty(CameraControlsSnapshot.Empty.Controls);
        Assert.Null(CameraControlsSnapshot.Empty.DeviceId);
    }

    [Fact]
    public void NewControlFields_DoNotRequireACameraRestart()
    {
        var a = new CameraSettings { DeviceId = "x" };
        var b = a.Clone();
        b.ControlsDeviceId = "x";
        b.ControlValues = [V(CameraControlKind.Zoom, 3)];
        b.ControlProfiles = [new CameraControlProfile { Name = "p", DeviceId = "x" }];
        b.Powerline = CameraPowerline.Hz60;

        Assert.False(a.RequiresRestartComparedTo(b));
    }

    // ---------- Clone ----------

    [Fact]
    public void Clone_CopiesNewFieldsDeeply_AndIsIndependent()
    {
        var original = new CameraSettings
        {
            ControlsDeviceId = "cam",
            ControlValues = [V(CameraControlKind.Zoom, 2.5), V(CameraControlKind.Focus, null, true)],
            ControlProfiles = [new CameraControlProfile { Name = "P", DeviceId = "cam", Values = [V(CameraControlKind.Hue, 4)] }],
            Powerline = CameraPowerline.Auto,
        };

        var copy = original.Clone();
        original.ControlValues[0].Value = 99;
        original.ControlValues.Add(V(CameraControlKind.Tilt, 1));
        original.ControlProfiles[0].Name = "Q";
        original.ControlProfiles[0].Values[0].Value = 77;
        original.ControlProfiles.Clear();
        original.ControlsDeviceId = "andere";
        original.Powerline = CameraPowerline.Off;

        Assert.Equal("cam", copy.ControlsDeviceId);
        Assert.Equal(2, copy.ControlValues.Count);
        Assert.Equal(2.5, copy.ControlValues[0].Value);
        Assert.True(copy.ControlValues[1].Auto);
        var profile = Assert.Single(copy.ControlProfiles);
        Assert.Equal("P", profile.Name);
        Assert.Equal("cam", profile.DeviceId);
        Assert.Equal(4, Assert.Single(profile.Values).Value);
        Assert.Equal(CameraPowerline.Auto, copy.Powerline);
    }

    [Fact]
    public void AppSettingsClone_KeepsControlFields()
    {
        var s = new AppSettings();
        s.Camera.ControlValues = [V(CameraControlKind.Contrast, 12)];
        s.Camera.Powerline = CameraPowerline.Hz60;

        var copy = s.Clone();

        Assert.Equal(CameraControlKind.Contrast, Assert.Single(copy.Camera.ControlValues).Kind);
        Assert.Equal(CameraPowerline.Hz60, copy.Camera.Powerline);
    }

    // ---------- Math ----------

    [Theory]
    [InlineData(5, 0, 10, 1, 5)]
    [InlineData(-3, 0, 10, 1, 0)]
    [InlineData(99, 0, 10, 1, 10)]
    [InlineData(4.4, 0, 10, 1, 4)]
    [InlineData(4.6, 0, 10, 1, 5)]
    [InlineData(7, 2, 12, 4, 6)]      // relativ zu min: 2, 6, 10
    [InlineData(11.9, 2, 12, 4, 10)]  // 14 würde über max liegen → geklemmt auf 12? nächster Schritt ist 10 (11.9-2)/4 = 2.475 → 2 → 10
    [InlineData(5.5, 10, 0, 1, 6)]    // vertauschte Grenzen
    [InlineData(5.3, 0, 10, 0, 5.3)]  // Schritt 0: nur klemmen
    [InlineData(5.3, 0, 10, -1, 5.3)]
    [InlineData(5.3, 0, 10, double.NaN, 5.3)]
    [InlineData(5.3, 0, 10, double.PositiveInfinity, 5.3)]
    [InlineData(double.NaN, 3, 10, 1, 3)]
    [InlineData(double.PositiveInfinity, 0, 10, 1, 10)]
    [InlineData(double.NegativeInfinity, 2, 10, 1, 2)]
    public void ClampAndSnap_Cases(double value, double min, double max, double step, double expected)
    {
        Assert.Equal(expected, CameraControlMath.ClampAndSnap(value, min, max, step), 9);
    }

    [Fact]
    public void ClampAndSnap_NeverLeavesTheRange_EvenWithAnOddStep()
    {
        var result = CameraControlMath.ClampAndSnap(9.9, 0, 10, 3); // Schritte 0, 3, 6, 9, 12 → 9
        Assert.InRange(result, 0, 10);
        Assert.Equal(9, result, 9);
        Assert.Equal(10, CameraControlMath.ClampAndSnap(10, 0, 10, 4)); // 12 würde überragen → 10 (geklemmt)
    }

    [Fact]
    public void Merge_OverridesWin_ResultIsNewObjectsSortedByKind()
    {
        var overrides = new[] { V(CameraControlKind.Focus, 5), V(CameraControlKind.Zoom, null, false) };
        var baseline = new[] { V(CameraControlKind.Zoom, 1, true), V(CameraControlKind.Brightness, 7), V(CameraControlKind.Focus, 9) };

        var merged = CameraControlMath.Merge(overrides, baseline);

        Assert.Equal([CameraControlKind.Zoom, CameraControlKind.Brightness, CameraControlKind.Focus], merged.Select(m => m.Kind));
        Assert.Null(merged[0].Value);          // override ersetzt den ganzen Eintrag
        Assert.False(merged[0].Auto);
        Assert.Equal(7, merged[1].Value);
        Assert.Equal(5, merged[2].Value);
        Assert.DoesNotContain(merged, m => overrides.Contains(m) || baseline.Contains(m));
    }

    [Fact]
    public void Merge_HandlesEmptyInputs()
    {
        Assert.Empty(CameraControlMath.Merge([], []));
        Assert.Single(CameraControlMath.Merge([V(CameraControlKind.Hue, 1)], []));
        Assert.Single(CameraControlMath.Merge([], [V(CameraControlKind.Hue, 1)]));
    }

    private static CameraControlsSnapshot Snapshot() => new("cam", true,
    [
        Info(CameraControlKind.Zoom, 100, 400, 10, 100, auto: false, value: 150),
        Info(CameraControlKind.Exposure, -10, 0, 1, -5, auto: true, value: -4, isAuto: false),
        new CameraControlInfo(CameraControlKind.Pan, false, false, 0, 0, 0, 0, false, null, null),
        Info(CameraControlKind.Focus, 0, 255, 5, 0, auto: true, value: 30, isAuto: true),
    ], true, CameraPowerline.Hz50);

    [Fact]
    public void CurrentValues_OnlySupported_AutoOnlyIfSupported_SortedByKind()
    {
        var values = CameraControlMath.CurrentValues(Snapshot());

        Assert.Equal([CameraControlKind.Zoom, CameraControlKind.Exposure, CameraControlKind.Focus], values.Select(v => v.Kind));
        Assert.Equal(150, values[0].Value);
        Assert.Null(values[0].Auto);
        Assert.Equal(-4, values[1].Value);
        Assert.False(values[1].Auto);
        Assert.True(values[2].Auto);
    }

    [Fact]
    public void Defaults_UseDriverDefault_AndAutoWhereSupported()
    {
        var values = CameraControlMath.Defaults(Snapshot());

        Assert.Equal(3, values.Count);
        Assert.Equal(100, values[0].Value);
        Assert.Null(values[0].Auto);
        Assert.Equal(-5, values[1].Value);
        Assert.True(values[1].Auto);
        Assert.True(values[2].Auto);
        Assert.Empty(CameraControlMath.Defaults(CameraControlsSnapshot.Empty));
        Assert.Empty(CameraControlMath.CurrentValues(CameraControlsSnapshot.Empty));
    }

    [Fact]
    public void SameValues_ComparesKindsAutoAndValuesWithTolerance()
    {
        var a = new[] { V(CameraControlKind.Zoom, 150), V(CameraControlKind.Focus, null, true) };

        Assert.True(CameraControlMath.SameValues(a, [V(CameraControlKind.Focus, null, true), V(CameraControlKind.Zoom, 150.0000001)]));
        Assert.False(CameraControlMath.SameValues(a, [V(CameraControlKind.Focus, null, true), V(CameraControlKind.Zoom, 150.01)]));
        Assert.False(CameraControlMath.SameValues(a, [V(CameraControlKind.Zoom, 150)]));
        Assert.False(CameraControlMath.SameValues(a, [V(CameraControlKind.Zoom, 150), V(CameraControlKind.Focus, null, false)]));
        Assert.False(CameraControlMath.SameValues(a, [V(CameraControlKind.Zoom, 150), V(CameraControlKind.Hue, null, true)]));
        Assert.False(CameraControlMath.SameValues([V(CameraControlKind.Zoom, 1)], [V(CameraControlKind.Zoom, null)]));
        Assert.True(CameraControlMath.SameValues([], []));
    }

    [Fact]
    public void SameValues_UsesHalfTheStepOfTheControl()
    {
        var ranges = Snapshot(); // Zoom: Step 10 → Toleranz 5
        Assert.True(CameraControlMath.SameValues([V(CameraControlKind.Zoom, 150)], [V(CameraControlKind.Zoom, 154)], ranges));
        Assert.False(CameraControlMath.SameValues([V(CameraControlKind.Zoom, 150)], [V(CameraControlKind.Zoom, 156)], ranges));
        // Regler ohne Eintrag in ranges: 1e-6
        Assert.False(CameraControlMath.SameValues([V(CameraControlKind.Hue, 1)], [V(CameraControlKind.Hue, 1.1)], ranges));
    }

    // ---------- Formatter ----------

    [Fact]
    public void DisplayNames_AndGroups_CoverEveryKind()
    {
        var expected = new Dictionary<CameraControlKind, (string Name, CameraControlGroup Group)>
        {
            [CameraControlKind.Zoom] = ("Zoom", CameraControlGroup.ZoomAndFraming),
            [CameraControlKind.Pan] = ("Schwenken", CameraControlGroup.ZoomAndFraming),
            [CameraControlKind.Tilt] = ("Neigen", CameraControlGroup.ZoomAndFraming),
            [CameraControlKind.Brightness] = ("Helligkeit", CameraControlGroup.Picture),
            [CameraControlKind.Contrast] = ("Kontrast", CameraControlGroup.Picture),
            [CameraControlKind.Hue] = ("Farbton", CameraControlGroup.Picture),
            [CameraControlKind.Exposure] = ("Belichtung", CameraControlGroup.Exposure),
            [CameraControlKind.BacklightCompensation] = ("Gegenlichtausgleich", CameraControlGroup.Exposure),
            [CameraControlKind.Focus] = ("Fokus", CameraControlGroup.FocusAndWhiteBalance),
            [CameraControlKind.WhiteBalance] = ("Weißabgleich", CameraControlGroup.FocusAndWhiteBalance),
        };

        foreach (var kind in Enum.GetValues<CameraControlKind>())
        {
            Assert.Equal(expected[kind].Name, CameraControlFormatter.DisplayName(kind));
            Assert.Equal(expected[kind].Group, CameraControlFormatter.GroupOf(kind));
        }
        Assert.Equal(expected.Count, Enum.GetValues<CameraControlKind>().Length);
    }

    [Fact]
    public void GroupTitles_AndPowerlineLabels()
    {
        Assert.Equal("Zoom & Ausschnitt", CameraControlFormatter.GroupTitle(CameraControlGroup.ZoomAndFraming));
        Assert.Equal("Bild", CameraControlFormatter.GroupTitle(CameraControlGroup.Picture));
        Assert.Equal("Belichtung", CameraControlFormatter.GroupTitle(CameraControlGroup.Exposure));
        Assert.Equal("Fokus & Weißabgleich", CameraControlFormatter.GroupTitle(CameraControlGroup.FocusAndWhiteBalance));

        Assert.Equal("Nicht verändern", CameraControlFormatter.PowerlineLabel(CameraPowerline.Unchanged));
        Assert.Equal("Aus", CameraControlFormatter.PowerlineLabel(CameraPowerline.Off));
        Assert.Equal("50 Hz", CameraControlFormatter.PowerlineLabel(CameraPowerline.Hz50));
        Assert.Equal("60 Hz", CameraControlFormatter.PowerlineLabel(CameraPowerline.Hz60));
        Assert.Equal("Automatisch", CameraControlFormatter.PowerlineLabel(CameraPowerline.Auto));
    }

    [Fact]
    public void FormatValue_DefaultIsPercentOfRange()
    {
        var info = Info(CameraControlKind.Brightness, 0, 255);
        Assert.Equal("62 %", CameraControlFormatter.FormatValue(info, 158));   // 61,96
        Assert.Equal("0 %", CameraControlFormatter.FormatValue(info, 0));
        Assert.Equal("100 %", CameraControlFormatter.FormatValue(info, 255));
        Assert.Equal("100 %", CameraControlFormatter.FormatValue(info, 999));  // außerhalb: geklemmt
        Assert.Equal("50 %", CameraControlFormatter.FormatValue(Info(CameraControlKind.Hue, -10, 10), 0));
        Assert.Equal("–", CameraControlFormatter.FormatValue(Info(CameraControlKind.Hue, 5, 5), 5));
        Assert.Equal("–", CameraControlFormatter.FormatValue(info, double.NaN));
    }

    [Fact]
    public void FormatValue_ZoomWithPositiveMin_IsAFactor_UsingGermanDecimalComma()
    {
        var info = Info(CameraControlKind.Zoom, 100, 500, 1, 100);
        Assert.Equal("1,5×", CameraControlFormatter.FormatValue(info, 150));
        Assert.Equal("1,0×", CameraControlFormatter.FormatValue(info, 100));
        // Min 0 → Prozent
        Assert.Equal("50 %", CameraControlFormatter.FormatValue(Info(CameraControlKind.Zoom, 0, 10), 5));
    }

    [Fact]
    public void FormatValue_WhiteBalanceInKelvin_OnlyForKelvinRanges()
    {
        var kelvin = Info(CameraControlKind.WhiteBalance, 2800, 6500, 100, 4500);
        Assert.Equal("4500 K", CameraControlFormatter.FormatValue(kelvin, 4500));
        Assert.Equal("4500 K", CameraControlFormatter.FormatValue(kelvin, 4499.6));
        Assert.Equal("25 %", CameraControlFormatter.FormatValue(Info(CameraControlKind.WhiteBalance, 0, 100), 25));
        Assert.Equal("50 %", CameraControlFormatter.FormatValue(Info(CameraControlKind.WhiteBalance, 1000, 20000), 10500));
    }

    [Fact]
    public void FormatValue_BacklightCompensation_ZeroToOneIsOffOn()
    {
        var info = Info(CameraControlKind.BacklightCompensation, 0, 1, 1, 0);
        Assert.Equal("Aus", CameraControlFormatter.FormatValue(info, 0));
        Assert.Equal("An", CameraControlFormatter.FormatValue(info, 1));
        Assert.Equal("50 %", CameraControlFormatter.FormatValue(Info(CameraControlKind.BacklightCompensation, 0, 4), 2));
    }

    [Fact]
    public void FormatValue_IsCultureIndependent()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("1,5×", CameraControlFormatter.FormatValue(Info(CameraControlKind.Zoom, 100, 500), 150));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = old;
        }
    }

    // ---------- Normalizer ----------

    [Fact]
    public void Normalizer_NullListsBecomeEmpty_UnknownPowerlineBecomesHz50()
    {
        var s = Normalized(c =>
        {
            c.ControlValues = null!;
            c.ControlProfiles = null!;
            c.Powerline = (CameraPowerline)99;
        });

        Assert.Empty(s.Camera.ControlValues);
        Assert.Empty(s.Camera.ControlProfiles);
        Assert.Equal(CameraPowerline.Hz50, s.Camera.Powerline);
    }

    [Fact]
    public void Normalizer_KnownPowerlineValues_AreKept()
    {
        foreach (var p in Enum.GetValues<CameraPowerline>())
            Assert.Equal(p, Normalized(c => c.Powerline = p).Camera.Powerline);
    }

    [Fact]
    public void Normalizer_ControlValues_DropsInvalid_Dedupes_AndSorts()
    {
        var s = Normalized(c =>
        {
            c.ControlsDeviceId = "cam";
            c.ControlValues =
            [
                V(CameraControlKind.Focus, 5),
                null!,
                V((CameraControlKind)123, 1),
                V(CameraControlKind.Zoom, 2),
                V(CameraControlKind.Zoom, 9),                    // doppelt: der erste gewinnt
                V(CameraControlKind.Hue),                        // weder Wert noch Auto
                V(CameraControlKind.Tilt, double.NaN),           // nur NaN → verworfen
                V(CameraControlKind.Pan, double.PositiveInfinity, true), // ∞ → Value null, Auto bleibt
                V(CameraControlKind.Brightness, null, false),
            ];
        });

        Assert.Equal(
            [CameraControlKind.Zoom, CameraControlKind.Pan, CameraControlKind.Brightness, CameraControlKind.Focus],
            s.Camera.ControlValues.Select(v => v.Kind));
        Assert.Equal(2, s.Camera.ControlValues[0].Value);
        Assert.Null(s.Camera.ControlValues[1].Value);
        Assert.True(s.Camera.ControlValues[1].Auto);
        Assert.False(s.Camera.ControlValues[2].Auto);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalizer_EmptyControlsDeviceId_BecomesNull_AndClearsValues(string? id)
    {
        var s = Normalized(c =>
        {
            c.ControlsDeviceId = id;
            c.ControlValues = [V(CameraControlKind.Zoom, 2)];
        });

        Assert.Null(s.Camera.ControlsDeviceId);
        Assert.Empty(s.Camera.ControlValues);
    }

    [Fact]
    public void Normalizer_TooLongControlsDeviceId_IsDiscarded_ExactLengthIsKept()
    {
        var tooLong = new string('x', SettingsLimits.CameraDeviceIdMaxLength + 1);
        var exact = new string('y', SettingsLimits.CameraDeviceIdMaxLength);

        var a = Normalized(c => { c.ControlsDeviceId = tooLong; c.ControlValues = [V(CameraControlKind.Zoom, 2)]; });
        var b = Normalized(c => { c.ControlsDeviceId = exact; c.ControlValues = [V(CameraControlKind.Zoom, 2)]; });

        Assert.Null(a.Camera.ControlsDeviceId);
        Assert.Empty(a.Camera.ControlValues);
        Assert.Equal(exact, b.Camera.ControlsDeviceId);
        Assert.Single(b.Camera.ControlValues);
    }

    private static CameraControlProfile Profile(string? name, string? device, params CameraControlValue[] values) =>
        new() { Name = name!, DeviceId = device!, Values = [.. values] };

    [Fact]
    public void Normalizer_Profiles_DropInvalid_TrimAndShortenNames()
    {
        var longName = new string('n', 60);
        var s = Normalized(c => c.ControlProfiles =
        [
            null!,
            Profile("  Hell  ", "cam1"),
            Profile("", "cam1"),
            Profile("   ", "cam1"),
            Profile(null, "cam1"),
            Profile("Ohne Gerät", ""),
            Profile("Ohne Gerät 2", null),
            Profile("Ohne Gerät 3", "  "),
            Profile(longName, "cam1"),
            Profile("Zu lange Id", new string('z', SettingsLimits.CameraDeviceIdMaxLength + 1)),
        ]);

        Assert.Equal(2, s.Camera.ControlProfiles.Count);
        Assert.Equal("Hell", s.Camera.ControlProfiles[0].Name);
        Assert.Equal(new string('n', SettingsLimits.CameraProfileNameMaxLength), s.Camera.ControlProfiles[1].Name);
    }

    [Fact]
    public void Normalizer_Profiles_NamesAreUniquePerDevice_IgnoringCase_FirstWins()
    {
        var s = Normalized(c => c.ControlProfiles =
        [
            Profile("Hell", "cam1", V(CameraControlKind.Zoom, 1)),
            Profile("HELL", "cam1", V(CameraControlKind.Zoom, 2)),
            Profile("hell ", "cam1"),
            Profile("Hell", "cam2", V(CameraControlKind.Zoom, 3)),
        ]);

        Assert.Equal(2, s.Camera.ControlProfiles.Count);
        Assert.Equal(1, s.Camera.ControlProfiles[0].Values[0].Value);
        Assert.Equal("cam2", s.Camera.ControlProfiles[1].DeviceId);
    }

    [Fact]
    public void Normalizer_Profiles_AtMostTenPerDevice_OrderKept()
    {
        var profiles = new List<CameraControlProfile>();
        for (var i = 0; i < 12; i++)
        {
            profiles.Add(Profile("A" + i, "camA"));
            if (i < 3)
                profiles.Add(Profile("B" + i, "camB"));
        }

        var s = Normalized(c => c.ControlProfiles = profiles);

        Assert.Equal(10, s.Camera.ControlProfiles.Count(p => p.DeviceId == "camA"));
        Assert.Equal(3, s.Camera.ControlProfiles.Count(p => p.DeviceId == "camB"));
        Assert.Equal(
            profiles.Where(p => p.DeviceId == "camA").Take(10).Select(p => p.Name),
            s.Camera.ControlProfiles.Where(p => p.DeviceId == "camA").Select(p => p.Name));
        // Reihenfolge wie in der Eingabe (nicht sortiert): A0, B0, A1, B1, …
        Assert.Equal(["A0", "B0", "A1", "B1"], s.Camera.ControlProfiles.Take(4).Select(p => p.Name));
    }

    [Fact]
    public void Normalizer_ProfileValues_AreNormalizedLikeControlValues()
    {
        var s = Normalized(c => c.ControlProfiles =
        [
            Profile("P", "cam",
                V(CameraControlKind.Focus, 5), null!, V((CameraControlKind)77, 1), V(CameraControlKind.Zoom, 2),
                V(CameraControlKind.Zoom, 3), V(CameraControlKind.Hue)),
        ]);

        var values = s.Camera.ControlProfiles[0].Values;
        Assert.Equal([CameraControlKind.Zoom, CameraControlKind.Focus], values.Select(v => v.Kind));
        Assert.Equal(2, values[0].Value);
    }

    [Fact]
    public void Normalizer_IsIdempotent_ForControlFields()
    {
        var s = Normalized(c =>
        {
            c.ControlsDeviceId = "cam";
            c.ControlValues = [V(CameraControlKind.Focus, 5), V(CameraControlKind.Zoom, 2)];
            c.ControlProfiles = [Profile(" P ", "cam", V(CameraControlKind.Hue, 1))];
        });
        var first = JsonSerializer.Serialize(s, AppSettings.JsonOptions);

        var second = JsonSerializer.Serialize(SettingsNormalizer.Normalize(s.Clone()), AppSettings.JsonOptions);

        Assert.Equal(first, second);
    }

    // ---------- Settings-Store / JSON ----------

    [Fact]
    public void Store_Roundtrip_PersistsControlFields_WithEnumNames()
    {
        var store = new JsonSettingsStore(_dir);
        var s = store.Current.Clone();
        s.Camera.ControlsDeviceId = "cam-1";
        s.Camera.ControlValues = [V(CameraControlKind.Zoom, 2.5), V(CameraControlKind.Focus, null, true)];
        s.Camera.ControlProfiles = [Profile("Abend", "cam-1", V(CameraControlKind.Exposure, -3))];
        s.Camera.Powerline = CameraPowerline.Hz60;
        store.Save(s);

        var reloaded = new JsonSettingsStore(_dir).Current.Camera;

        Assert.Equal("cam-1", reloaded.ControlsDeviceId);
        Assert.Equal(2.5, reloaded.ControlValues[0].Value);
        Assert.True(reloaded.ControlValues[1].Auto);
        Assert.Equal("Abend", Assert.Single(reloaded.ControlProfiles).Name);
        Assert.Equal(CameraPowerline.Hz60, reloaded.Powerline);
        var text = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.Contains("\"Hz60\"", text);
        Assert.Contains("\"Zoom\"", text);
    }

    [Fact]
    public void Store_Load_UnknownKindAndPowerlineNames_DoNotInvalidateTheFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """
        {
          "Flow": { "CountdownSeconds": 9 },
          "Camera": {
            "ControlsDeviceId": "cam-1",
            "Powerline": "Hz400",
            "ControlValues": [
              { "Kind": "Zoom", "Value": 3 },
              { "Kind": "Teleportation", "Value": 1 },
              { "Kind": "Focus", "Auto": true }
            ],
            "ControlProfiles": [
              { "Name": "A", "DeviceId": "cam-1", "Values": [ { "Kind": "Warp", "Value": 1 }, { "Kind": "hue", "Value": 2 } ] }
            ]
          }
        }
        """);

        var store = new JsonSettingsStore(_dir);

        Assert.False(store.LoadFailed);
        Assert.Equal(9, store.Current.Flow.CountdownSeconds);   // Datei gilt, kein Zurücksetzen auf Defaults
        var camera = store.Current.Camera;
        Assert.Equal(CameraPowerline.Hz50, camera.Powerline);
        Assert.Equal([CameraControlKind.Zoom, CameraControlKind.Focus], camera.ControlValues.Select(v => v.Kind));
        var profile = Assert.Single(camera.ControlProfiles);
        Assert.Equal(CameraControlKind.Hue, Assert.Single(profile.Values).Kind);
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.bak-*"));
    }

    [Fact]
    public void Store_Load_FileWithoutControlFields_UsesDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{ "Camera": { "DeviceId": "cam-1" } }""");

        var camera = new JsonSettingsStore(_dir).Current.Camera;

        Assert.Equal("cam-1", camera.DeviceId);
        Assert.Null(camera.ControlsDeviceId);
        Assert.Empty(camera.ControlValues);
        Assert.Empty(camera.ControlProfiles);
        Assert.Equal(CameraPowerline.Hz50, camera.Powerline);
    }

    // ---------- Interface-Defaults ----------

    [Fact]
    public async Task CameraService_DefaultInterfaceMembers_AreHarmless()
    {
        ICameraService camera = new MinimalCamera();

        Assert.Null(camera.ActiveDeviceId);
        Assert.Same(CameraControlsSnapshot.Empty, await camera.GetControlsAsync());
        camera.ConfigureControls(new CameraSettings());
        await camera.ApplyControlsLiveAsync([V(CameraControlKind.Zoom, 1)], CameraPowerline.Auto);
    }

    private sealed class MinimalCamera : ICameraService
    {
        public CameraStatus Status => CameraStatus.Stopped;
        public string? StatusMessage => null;
        public CameraResolution? ActiveResolution => null;
        public event EventHandler? StatusChanged { add { } remove { } }
        public event EventHandler<CameraFrame>? FrameArrived { add { } remove { } }
        public Task<IReadOnlyList<CameraDevice>> GetDevicesAsync() => Task.FromResult<IReadOnlyList<CameraDevice>>([]);
        public Task<IReadOnlyList<CameraResolution>> GetResolutionsAsync(string deviceId) => Task.FromResult<IReadOnlyList<CameraResolution>>([]);
        public Task StartAsync(CameraSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task<System.Windows.Media.Imaging.BitmapSource> CapturePhotoAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
