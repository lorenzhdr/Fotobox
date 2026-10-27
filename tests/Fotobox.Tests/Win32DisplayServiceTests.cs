using Fotobox.Core;
using Fotobox.Services.Display;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Zuordnung GDI-Name → Id/Name mit ausgetauschten Win32-Quellen (läuft ohne Hardware).</summary>
public sealed class Win32DisplayServiceMappingTests
{
    private static Win32DisplayService.GdiMonitor Gdi(string name, bool primary) =>
        new(name, new MonitorRect(primary ? 0 : 1920, 0, 1920, 1080), primary);

    private static Dictionary<string, Win32DisplayService.TargetInfo> Targets() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [@"\\.\DISPLAY1"] = new("Erster", "path-1", 0),
            [@"\\.\DISPLAY2"] = new("Zweiter", "path-2", 0),
        };

    [Fact]
    public void DisplayConfigFailure_KeepsLastKnownIdsAndNames()
    {
        var fail = false;
        var service = new Win32DisplayService(
            () => [Gdi(@"\\.\DISPLAY1", true), Gdi(@"\\.\DISPLAY2", false)],
            () => fail ? throw new InvalidOperationException("Test") : Targets());

        var ok = service.GetMonitors();
        Assert.Equal(["path-1", "path-2"], ok.Select(m => m.Id));

        fail = true;
        var degraded = service.GetMonitors();

        Assert.Equal(ok.Select(m => m.Id), degraded.Select(m => m.Id));
        Assert.Equal(ok.Select(m => m.Name), degraded.Select(m => m.Name));
    }

    [Fact]
    public void DisplayConfigFailure_UnknownGdiName_FallsBackToGdiName()
    {
        var fail = false;
        var gdi = new List<Win32DisplayService.GdiMonitor> { Gdi(@"\\.\DISPLAY1", true) };
        var service = new Win32DisplayService(
            () => gdi,
            () => fail ? throw new InvalidOperationException("Test") : Targets());
        service.GetMonitors();

        fail = true;
        gdi.Add(Gdi(@"\\.\DISPLAY3", false));
        var result = service.GetMonitors();

        Assert.Equal(["path-1", @"\\.\DISPLAY3"], result.Select(m => m.Id));
        Assert.Equal("Monitor", result[1].Name);
    }

    [Fact]
    public void DisplayConfigFailure_WithoutEarlierSuccess_UsesGdiNames()
    {
        var service = new Win32DisplayService(
            () => [Gdi(@"\\.\DISPLAY1", true)],
            () => throw new InvalidOperationException("Test"));

        var monitor = Assert.Single(service.GetMonitors());

        Assert.Equal(@"\\.\DISPLAY1", monitor.Id);
    }
}

/// <summary>Liest die echten Monitore dieses Rechners; läuft nicht in der CI.</summary>
[Trait("Category", "Hardware")]
public sealed class Win32DisplayServiceTests
{
    [Fact]
    public void GetMonitors_ReturnsAtLeastOneMonitor_WithExactlyOnePrimary()
    {
        var monitors = new Win32DisplayService().GetMonitors();

        Assert.NotEmpty(monitors);
        Assert.Single(monitors, m => m.IsPrimary);
    }

    [Fact]
    public void GetMonitors_HasUniqueNonEmptyIds_AndPlausibleGeometry()
    {
        var monitors = new Win32DisplayService().GetMonitors();

        Assert.All(monitors, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Id));
            Assert.False(string.IsNullOrWhiteSpace(m.Name));
            Assert.True(m.Number > 0);
            Assert.True(m.PixelWidth > 0);
            Assert.True(m.PixelHeight > 0);
            Assert.True(m.Bounds.Width > 0);
            Assert.True(m.Bounds.Height > 0);
        });
        Assert.Equal(monitors.Count, monitors.Select(m => m.Id).Distinct().Count());
    }

    [Fact]
    public void GetMonitors_IsSortedByNumber_AndStableAcrossCalls()
    {
        var service = new Win32DisplayService();
        var first = service.GetMonitors();
        var second = service.GetMonitors();

        Assert.Equal(first.Select(m => m.Number).OrderBy(n => n), first.Select(m => m.Number));
        Assert.Equal(first.Select(m => m.Id), second.Select(m => m.Id));
    }
}
