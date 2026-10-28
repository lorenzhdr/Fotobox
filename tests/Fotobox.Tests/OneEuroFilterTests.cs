using Fotobox.Core.Peace;
using Xunit;

namespace Fotobox.Tests;

public sealed class OneEuroFilterTests
{
    [Fact]
    public void FirstSample_IsTakenOver()
    {
        var filter = new OneEuroFilter(1, 1);
        Assert.Equal(5, filter.Filter(5, 0.1));
        Assert.Equal(0, filter.Velocity);
    }

    [Fact]
    public void Constant_StaysConstant()
    {
        var filter = new OneEuroFilter(1, 1);
        filter.Filter(3, 0.1);
        for (var i = 0; i < 20; i++)
            Assert.Equal(3, filter.Filter(3, 0.1), 12);
    }

    [Fact]
    public void NonPositiveInterval_IsIgnored()
    {
        var filter = new OneEuroFilter(1, 1);
        filter.Filter(0, 0.1);
        Assert.Equal(0, filter.Filter(100, 0));
        Assert.Equal(0, filter.Filter(100, -1));
    }

    [Fact]
    public void FasterMotion_GivesLessSmoothing()
    {
        // Gleiche Rampe: mit Beta steigt die Grenzfrequenz, der Filter liegt näher am Signal.
        var slow = new OneEuroFilter(0.8, 0);
        var fast = new OneEuroFilter(0.8, 10);
        slow.Filter(0, 0.1);
        fast.Filter(0, 0.1);
        double s = 0, f = 0;
        for (var i = 1; i <= 10; i++)
        {
            s = slow.Filter(i, 0.1);
            f = fast.Filter(i, 0.1);
        }
        Assert.True(f > s, "Mit Beta folgt der Filter schneller.");
    }

    [Fact]
    public void Velocity_ConvergesToTheSlopeOfALinearSignal()
    {
        var filter = new OneEuroFilter(1, 0, 2);
        for (var i = 0; i < 60; i++)
            filter.Filter(i * 0.5, 0.1); // 5 Einheiten/s
        Assert.Equal(5, filter.Velocity, 1);
    }
}
