using System.Collections;
using System.Reflection;
using System.Text.Json;
using Fotobox.Core;
using Xunit;

namespace Fotobox.Tests;

/// <summary>
/// <see cref="DeviceSpecificSettings"/>: Jedes Blattfeld der übertragbaren Abschnitte von <see cref="AppSettings"/> muss entweder
/// gerätespezifisch (<see cref="DeviceSpecificSettings.Paths"/>) oder ausdrücklich übertragbar (<see cref="PortablePaths"/>) sein –
/// ein neues Feld erzwingt so die Entscheidung, ob es beim Teilen von Einstellungen mitwandert.
/// </summary>
public sealed class DeviceSpecificSettingsTests
{
    /// <summary>Bewusst übertragbare Felder (alles, was nicht gerätespezifisch ist). Neue Felder hier oder in <see cref="DeviceSpecificSettings.Paths"/> eintragen.</summary>
    private static readonly string[] PortablePaths =
    [
        // Camera
        "Camera.MirrorPreview",
        "Camera.MirrorPhoto",
        "Camera.Powerline",
        // Flow
        "Flow.CountdownSeconds",
        "Flow.PreviewSeconds",
        "Flow.PauseBetweenShotsSeconds",
        "Flow.ErrorDisplaySeconds",
        "Flow.PrintingEnabled",
        "Flow.ExpectedPrintSeconds",
        // Layout
        "Layout.Kind",
        "Layout.StripPhotoCount",
        "Layout.StripStyle",
        "Layout.OverlayFileName",
        "Layout.CaptionText",
        "Layout.ShowDate",
        "Layout.CaptionFontFamily",
        "Layout.GuestChoices",
        // Printer
        "Printer.Orientation",
        "Printer.Copies",
        "Printer.TimeoutSeconds",
        // Storage
        "Storage.DateSubfolder",
        "Storage.SaveOriginals",
        "Storage.JpegQuality",
        "Storage.FilePrefix",
        // Input
        "Input.TriggerKeys",
        "Input.AdminKeys",
        "Input.CancelCountdownKeys",
        "Input.TriggerOnLeftClick",
        "Input.TriggerOnRightClick",
        "Input.InvertMouseWheel",
        "Input.VideoStartKeys",
        "Input.VideoStopKeys",
        // GuestScreen
        "GuestScreen.HintText",
        "GuestScreen.HintStyle",
        "GuestScreen.VideoHintText",
        "GuestScreen.VideoHintStyle",
        "GuestScreen.ShowWheelHint",
        "GuestScreen.WheelHintText",
        "GuestScreen.WheelHintStyle",
        // Appearance
        "Appearance.AdminTheme",
        // Video
        "Video.Enabled",
        "Video.MaxDurationSeconds",
        "Video.CountdownSeconds",
        "Video.MinDurationSeconds",
        "Video.WarningSeconds",
        "Video.ShowRecDot",
        "Video.ShowElapsed",
        "Video.ShowRemaining",
        "Video.SuccessDisplaySeconds",
        "Video.SuccessText",
        "Video.Quality",
        "Video.RecordAudio",
        "Video.DateSubfolder",
        "Video.FilePrefix",
        // Fun
        "Fun.Enabled",
        "Fun.Method",
        "Fun.Scope",
        "Fun.BoxStyle",
        "Fun.Color",
        "Fun.Animation",
        "Fun.SingularText",
        "Fun.PluralText",
        "Fun.TextPosition",
        "Fun.TextStyle",
        "Fun.TextHintStyle",
        "Fun.TextSeconds",
        "Fun.CooldownSeconds",
        "Fun.SwapTriggers",
        "Fun.RepeatEnabled",
        "Fun.RepeatSeconds",
        "Fun.MinSizePercent",
        "Fun.MaxTargets",
        "Fun.SensitivityPercent",
        "Fun.DwellMilliseconds",
        "Fun.LockOnMilliseconds",
        "Fun.LockOnIntensityPercent",
        "Fun.AnalysesPerSecond",
        "Fun.SoundEnabled",
        // Peace
        "Peace.Enabled",
        "Peace.ActionSelfDestruct",
        "Peace.ActionPhoto",
        "Peace.ActionMoustache",
        "Peace.HoldMilliseconds",
        "Peace.ShowHoldProgress",
        "Peace.CooldownSeconds",
        "Peace.SensitivityPercent",
        "Peace.AnalysesPerSecond",
        "Peace.SelfDestructSeconds",
        "Peace.SelfDestructSoundEnabled",
        "Peace.PhotoCountdownSeconds",
        "Peace.MoustacheTargets",
        "Peace.MoustacheStyle",
        "Peace.RandomStyles",
        "Peace.MoustacheVisibility",
        "Peace.MoustacheDurationSeconds",
        "Peace.MoustacheAutoPhoto",
        "Peace.MoustacheAutoPhotoCountdownSeconds",
        // MiddleFinger
        "MiddleFinger.Enabled",
        "MiddleFinger.ActionCancelPeace",
        "MiddleFinger.HoldMilliseconds",
        "MiddleFinger.SensitivityPercent",
    ];

    [Fact]
    public void EveryLeafProperty_IsEitherDeviceSpecificOrExplicitlyPortable()
    {
        var device = DeviceSpecificSettings.Paths.ToHashSet(StringComparer.Ordinal);
        var portable = PortablePaths.ToHashSet(StringComparer.Ordinal);

        foreach (var path in LeafPaths())
        {
            Assert.True(device.Contains(path) || portable.Contains(path),
                $"{path} ist weder in DeviceSpecificSettings.Paths noch in PortablePaths: bitte entscheiden, ob das Feld beim Teilen der Einstellungen mitwandert.");
            Assert.False(device.Contains(path) && portable.Contains(path), $"{path} steht in beiden Listen.");
        }
    }

    [Fact]
    public void ListedPaths_AllExist_NoStaleEntries()
    {
        var leaves = LeafPaths().ToHashSet(StringComparer.Ordinal);

        foreach (var path in DeviceSpecificSettings.Paths)
            Assert.True(leaves.Contains(path), $"DeviceSpecificSettings.Paths enthält ein unbekanntes Feld: {path}");
        foreach (var path in PortablePaths)
            Assert.True(leaves.Contains(path), $"PortablePaths enthält ein unbekanntes Feld: {path}");
        Assert.Equal(DeviceSpecificSettings.Paths.Count, DeviceSpecificSettings.Paths.Distinct().Count());
        Assert.Equal(PortablePaths.Length, PortablePaths.Distinct().Count());
    }

    [Fact]
    public void Paths_AreTheSixteenAgreedDeviceFields()
    {
        Assert.Equal(
            new[]
            {
                "Appearance.GuestMonitorId", "Appearance.GuestMonitorName",
                "Camera.ControlProfiles", "Camera.ControlValues", "Camera.ControlsDeviceId",
                "Camera.DeviceId", "Camera.ResolutionHeight", "Camera.ResolutionWidth",
                "Printer.PaperHeightMm", "Printer.PaperSizeName", "Printer.PaperWidthMm", "Printer.PrinterName",
                "Storage.PhotoFolder",
                "Video.Folder", "Video.MicrophoneId", "Video.MicrophoneName",
            },
            DeviceSpecificSettings.Paths.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void Clear_ResetsEveryDeviceField_AndOnlyThose()
    {
        var s = new AppSettings();
        FillDeviceFields(s);
        s.Camera.MirrorPhoto = true;
        s.Printer.Copies = 3;
        s.Storage.FilePrefix = "Eigen";
        s.Video.RecordAudio = false;
        s.Appearance.AdminTheme = AdminThemeMode.Dark;

        DeviceSpecificSettings.Clear(s);

        var fresh = new AppSettings();
        foreach (var path in DeviceSpecificSettings.Paths)
            Assert.Equal(Canon(Get(fresh, path)), Canon(Get(s, path)));
        Assert.True(s.Camera.MirrorPhoto);
        Assert.Equal(3, s.Printer.Copies);
        Assert.Equal("Eigen", s.Storage.FilePrefix);
        Assert.False(s.Video.RecordAudio);
        Assert.Equal(AdminThemeMode.Dark, s.Appearance.AdminTheme);
    }

    [Fact]
    public void Clear_SkipsMissingSections()
    {
        var s = new AppSettings { Camera = null!, Printer = null!, Storage = null!, Video = null!, Appearance = null! };

        DeviceSpecificSettings.Clear(s);

        Assert.Null(s.Camera);
    }

    [Fact]
    public void CopyFrom_CopiesEveryDeviceField_AndOnlyThose()
    {
        var source = new AppSettings();
        FillDeviceFields(source);
        source.Camera.MirrorPhoto = true;
        source.Storage.FilePrefix = "Fremd";

        var target = new AppSettings();
        target.Camera.DeviceId = "alt";
        target.Video.Folder = "alt";
        target.Storage.FilePrefix = "Ziel";

        DeviceSpecificSettings.CopyFrom(source, target);

        foreach (var path in DeviceSpecificSettings.Paths)
            Assert.Equal(Canon(Get(source, path)), Canon(Get(target, path)));
        Assert.False(target.Camera.MirrorPhoto);
        Assert.Equal("Ziel", target.Storage.FilePrefix);
        Assert.NotSame(source.Camera, target.Camera);
        // Listen werden tief kopiert, nicht geteilt.
        Assert.NotSame(source.Camera.ControlValues, target.Camera.ControlValues);
        Assert.NotSame(source.Camera.ControlValues[0], target.Camera.ControlValues[0]);
        Assert.NotSame(source.Camera.ControlProfiles[0], target.Camera.ControlProfiles[0]);
        Assert.NotSame(source.Camera.ControlProfiles[0].Values[0], target.Camera.ControlProfiles[0].Values[0]);
    }

    [Fact]
    public void CopyFrom_CopiesNulls_Too()
    {
        var target = new AppSettings();
        FillDeviceFields(target);

        DeviceSpecificSettings.CopyFrom(new AppSettings(), target);

        foreach (var path in DeviceSpecificSettings.Paths)
            Assert.True(IsNullOrEmpty(Get(target, path)), $"{path} ist nicht leer.");
    }

    /// <summary>Alle Blattfelder (öffentlich les-/schreibbar) der übertragbaren Abschnitte; verschachtelte Objekte werden durchlaufen, Texte und Listen sind Blätter.</summary>
    private static IEnumerable<string> LeafPaths()
    {
        foreach (var section in SettingsSections.PortableSections)
            foreach (var path in Leaves(section.PropertyType, section.Name))
                yield return path;
    }

    private static IEnumerable<string> Leaves(Type type, string prefix)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
                continue;

            var path = prefix + "." + property.Name;
            var propertyType = property.PropertyType;
            if (propertyType.IsClass && propertyType != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(propertyType))
            {
                foreach (var nested in Leaves(propertyType, path))
                    yield return nested;
            }
            else
            {
                yield return path;
            }
        }
    }

    private static (object Owner, PropertyInfo Property) Resolve(AppSettings s, string path)
    {
        object owner = s;
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length - 1; i++)
            owner = owner.GetType().GetProperty(parts[i])!.GetValue(owner)!;
        return (owner, owner.GetType().GetProperty(parts[^1])!);
    }

    private static object? Get(AppSettings s, string path)
    {
        var (owner, property) = Resolve(s, path);
        return property.GetValue(owner);
    }

    /// <summary>Vergleichbare Form eines Werts: Listen als JSON (tiefe Kopien sind nie referenzgleich).</summary>
    private static object? Canon(object? value) =>
        value is IEnumerable and not string ? JsonSerializer.Serialize(value, AppSettings.JsonOptions) : value;

    private static bool IsNullOrEmpty(object? value) =>
        value is null || (value is IEnumerable e and not string && !e.GetEnumerator().MoveNext());

    private static void FillDeviceFields(AppSettings s)
    {
        foreach (var path in DeviceSpecificSettings.Paths)
        {
            var (owner, property) = Resolve(s, path);
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object value = type == typeof(List<CameraControlValue>) ? new List<CameraControlValue>
                    { new() { Kind = CameraControlKind.Zoom, Value = 2.5 }, new() { Kind = CameraControlKind.Focus, Auto = true } }
                : type == typeof(List<CameraControlProfile>) ? new List<CameraControlProfile>
                    { new() { Name = "Profil", DeviceId = "wert-" + path, Values = [new() { Kind = CameraControlKind.Hue, Value = 3 }] } }
                : type == typeof(string) ? (object)("wert-" + path)
                : type == typeof(int) ? (object)1234
                : type == typeof(double) ? (object)12.5
                : throw new InvalidOperationException($"Unbekannter Typ für {path}: {type.Name}");
            property.SetValue(owner, value);
        }
    }
}
