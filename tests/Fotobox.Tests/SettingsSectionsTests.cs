using Fotobox.Core;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Übertragbare Abschnitte von <see cref="AppSettings"/> und <see cref="SettingsSections.CopyPortableSections"/>.</summary>
public sealed class SettingsSectionsTests
{
    [Fact]
    public void EveryPublicProperty_IsEitherPortableOrExcluded()
    {
        var portable = SettingsSections.PortableSections.Select(p => p.Name).ToHashSet();

        foreach (var property in typeof(AppSettings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            var isPortable = portable.Contains(property.Name);
            var isExcluded = SettingsSections.ExcludedProperties.Contains(property.Name);
            Assert.True(isPortable || isExcluded,
                $"{property.Name} ist weder übertragbarer Abschnitt noch ausdrücklich ausgeschlossen (SettingsSections.ExcludedProperties).");
            Assert.False(isPortable && isExcluded, $"{property.Name} ist zugleich übertragbar und ausgeschlossen.");
        }
    }

    [Fact]
    public void ExcludedProperties_AreVersionAndSecurity_AndExist()
    {
        Assert.Equal(new[] { nameof(AppSettings.Security), nameof(AppSettings.Version) },
            SettingsSections.ExcludedProperties.OrderBy(n => n, StringComparer.Ordinal));
        foreach (var name in SettingsSections.ExcludedProperties)
            Assert.NotNull(typeof(AppSettings).GetProperty(name));
    }

    [Fact]
    public void PortableSections_ContainAllKnownSections_ButNeitherVersionNorSecurity()
    {
        var names = SettingsSections.PortableSections.Select(p => p.Name).ToList();

        Assert.DoesNotContain(nameof(AppSettings.Version), names);
        Assert.DoesNotContain(nameof(AppSettings.Security), names);
        foreach (var expected in new[]
                 {
                     nameof(AppSettings.Camera), nameof(AppSettings.Flow), nameof(AppSettings.Layout), nameof(AppSettings.Printer),
                     nameof(AppSettings.Storage), nameof(AppSettings.Input), nameof(AppSettings.GuestScreen), nameof(AppSettings.Appearance),
                     nameof(AppSettings.Video), nameof(AppSettings.Fun), nameof(AppSettings.Peace), nameof(AppSettings.MiddleFinger),
                     nameof(AppSettings.Heart), nameof(AppSettings.Performance), nameof(AppSettings.PrintArea),
                     nameof(AppSettings.PerformanceOverlay),
                 })
            Assert.Contains(expected, names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void CopyPortableSections_CopiesValues_AndLeavesVersionAndSecurityUntouched()
    {
        var from = new AppSettings { Version = 99 };
        from.Camera.DeviceId = "cam-x";
        from.Flow.CountdownSeconds = 17;
        from.Peace.HoldMilliseconds = 2200;
        from.MiddleFinger.HoldMilliseconds = 1700;
        from.MiddleFinger.Enabled = false;
        from.Security.PinHash = "fremd";

        var to = new AppSettings { Version = 5 };
        to.Security.PinHash = "hash";
        to.Security.PinSalt = "salt";
        to.Security.PinIterations = 123_456;
        var securityBefore = to.Security;

        SettingsSections.CopyPortableSections(from, to);

        Assert.Equal("cam-x", to.Camera.DeviceId);
        Assert.Equal(17, to.Flow.CountdownSeconds);
        Assert.Equal(2200, to.Peace.HoldMilliseconds);
        Assert.Equal(1700, to.MiddleFinger.HoldMilliseconds);
        Assert.False(to.MiddleFinger.Enabled);
        Assert.Equal(5, to.Version);
        Assert.Same(securityBefore, to.Security);
        Assert.Equal("hash", to.Security.PinHash);
        Assert.Equal("salt", to.Security.PinSalt);
        Assert.Equal(123_456, to.Security.PinIterations);
        Assert.Equal(99, from.Version);
    }

    [Fact]
    public void CopyPortableSections_IsDeep_MutatingSourceAfterwardsDoesNotAffectTarget()
    {
        var from = new AppSettings();
        from.Peace.HoldMilliseconds = 2200;
        from.Peace.RandomStyles = [MoustacheStyle.Walrus];
        from.Layout.GuestChoices = [GuestLayout.Single, GuestLayout.StripDouble];
        from.Input.TriggerKeys = ["F9"];
        var to = new AppSettings();

        SettingsSections.CopyPortableSections(from, to);
        from.Peace.HoldMilliseconds = 400;
        from.Peace.RandomStyles.Add(MoustacheStyle.Twirl);
        from.Layout.GuestChoices.Add(GuestLayout.StripSingle);
        from.Input.TriggerKeys.Add("F10");
        from.MiddleFinger.HoldMilliseconds = 2500;

        Assert.Equal(2200, to.Peace.HoldMilliseconds);
        Assert.Equal([MoustacheStyle.Walrus], to.Peace.RandomStyles);
        Assert.Equal([GuestLayout.Single, GuestLayout.StripDouble], to.Layout.GuestChoices);
        Assert.Equal(["F9"], to.Input.TriggerKeys);
        Assert.Equal(new MiddleFingerSettings().HoldMilliseconds, to.MiddleFinger.HoldMilliseconds);
        foreach (var section in SettingsSections.PortableSections)
            Assert.NotSame(section.GetValue(from), section.GetValue(to));
    }

    [Fact]
    public void CopyPortableSections_FromDefaults_ResetsEverythingExceptVersionAndSecurity()
    {
        var to = new AppSettings();
        to.Camera.MirrorPhoto = true;
        to.MiddleFinger.SensitivityPercent = 90;
        to.Security.PinHash = "hash";

        SettingsSections.CopyPortableSections(new AppSettings(), to);

        var expected = new AppSettings();
        expected.Security.PinHash = "hash";
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(expected, AppSettings.JsonOptions),
            System.Text.Json.JsonSerializer.Serialize(to, AppSettings.JsonOptions));
    }
}
