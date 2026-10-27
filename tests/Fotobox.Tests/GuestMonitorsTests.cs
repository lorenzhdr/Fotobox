using Fotobox.Core;
using Xunit;

namespace Fotobox.Tests;

public sealed class GuestMonitorsTests
{
    private static DisplayMonitor Monitor(string id, int number, bool primary, string name = "Testmonitor", int w = 1920, int h = 1080) =>
        new(id, name, number, primary, w, h, new MonitorRect(0, 0, w, h));

    [Fact]
    public void Resolve_FindsMonitorById()
    {
        var list = new[] { Monitor("a", 1, true), Monitor("b", 2, false) };

        Assert.Equal("b", GuestMonitors.Resolve(list, "b")!.Id);
    }

    [Fact]
    public void Resolve_UnknownId_FallsBackToPrimary()
    {
        var list = new[] { Monitor("a", 1, false), Monitor("b", 2, true) };

        Assert.Equal("b", GuestMonitors.Resolve(list, "gibt-es-nicht")!.Id);
    }

    [Fact]
    public void Resolve_NullId_ReturnsPrimary()
    {
        var list = new[] { Monitor("a", 1, false), Monitor("b", 2, true) };

        Assert.Equal("b", GuestMonitors.Resolve(list, null)!.Id);
    }

    [Fact]
    public void Resolve_NoPrimary_ReturnsFirst()
    {
        var list = new[] { Monitor("a", 1, false), Monitor("b", 2, false) };

        Assert.Equal("a", GuestMonitors.Resolve(list, null)!.Id);
        Assert.Equal("a", GuestMonitors.Resolve(list, "unbekannt")!.Id);
    }

    [Fact]
    public void Resolve_EmptyList_ReturnsNull()
    {
        Assert.Null(GuestMonitors.Resolve([], null));
        Assert.Null(GuestMonitors.Resolve([], "a"));
    }

    [Fact]
    public void Label_FormatsNumberNameAndResolution()
    {
        Assert.Equal("Bildschirm 2 – Testmonitor (1920 × 1080)", GuestMonitors.Label(Monitor("b", 2, false)));
    }

    [Fact]
    public void Label_MarksPrimary()
    {
        Assert.Equal(
            "Bildschirm 1 – Testmonitor (2560 × 1440) · Hauptbildschirm",
            GuestMonitors.Label(Monitor("a", 1, true, w: 2560, h: 1440)));
    }

    [Fact]
    public void ToString_UsesLabel()
    {
        var m = Monitor("b", 2, false);

        Assert.Equal(GuestMonitors.Label(m), m.ToString());
    }
}
