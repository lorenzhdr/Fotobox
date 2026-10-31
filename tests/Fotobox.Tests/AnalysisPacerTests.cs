using Fotobox.Services.Detection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Fotobox.Tests;

public sealed class AnalysisPacerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private static TimeSpan Interval(int perSecond) => TimeSpan.FromTicks(TimeSpan.TicksPerSecond / perSecond);

    /// <summary>Spielt Kamerabilder mit <paramref name="fps"/> über <paramref name="seconds"/> ab und zählt die Starts (wie der Kamera-Handler: IsDue, dann MarkStarted).</summary>
    private int CountStarts(int perSecond, int fps, int seconds)
    {
        var pacer = new AnalysisPacer(_time);
        var interval = Interval(perSecond);
        var frameGap = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / fps);
        var frames = fps * seconds;
        var starts = 0;
        for (var i = 0; i < frames; i++)
        {
            if (pacer.IsDue(interval))
            {
                pacer.MarkStarted(interval);
                starts++;
            }
            _time.Advance(frameGap);
        }
        return starts;
    }

    [Theory]
    [InlineData(10, 25)]
    [InlineData(10, 30)]
    [InlineData(20, 25)]
    [InlineData(20, 30)]
    [InlineData(5, 25)]
    [InlineData(5, 30)]
    [InlineData(7, 30)]
    public void Rate_IsKeptOnAverage_DespiteFramesThatDoNotMatchTheInterval(int perSecond, int fps)
    {
        const int seconds = 10;
        var starts = CountStarts(perSecond, fps, seconds);
        var expected = perSecond * seconds;
        Assert.InRange(starts, expected * 0.99, expected * 1.01);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(60)]
    public void RateAtOrAboveTheFrameRate_AnalysesEveryFrame(int fps)
    {
        const int seconds = 10;
        var starts = CountStarts(50, fps, seconds);
        Assert.Equal(Math.Min(50, fps) * seconds, starts);
    }

    [Fact]
    public void FirstFrame_IsAlwaysDue()
    {
        var pacer = new AnalysisPacer(_time);
        Assert.True(pacer.IsDue(TimeSpan.FromSeconds(1)));
        Assert.True(pacer.IsDue(TimeSpan.FromSeconds(1))); // ohne MarkStarted bleibt es fällig
    }

    [Fact]
    public void NotDue_BeforeTheInterval_DueAfterwards()
    {
        var pacer = new AnalysisPacer(_time);
        var interval = Interval(10);
        pacer.MarkStarted(interval);

        _time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.False(pacer.IsDue(interval));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(pacer.IsDue(interval));
    }

    [Fact]
    public void LongPause_DoesNotCauseABurst()
    {
        var pacer = new AnalysisPacer(_time);
        var interval = Interval(10);
        pacer.MarkStarted(interval);

        _time.Advance(TimeSpan.FromSeconds(10)); // z. B. Kamera-Neustart oder Gate-Pause
        Assert.True(pacer.IsDue(interval));
        pacer.MarkStarted(interval);

        // Die verpassten Takte werden nicht nachgeholt: Die nächsten Bilder sind noch nicht fällig.
        var starts = 0;
        for (var i = 0; i < 2; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(40));
            if (pacer.IsDue(interval))
            {
                pacer.MarkStarted(interval);
                starts++;
            }
        }
        Assert.Equal(0, starts);
    }

    [Fact]
    public void LateAnalysis_ShiftsTheNextDueTimeByAFullIntervalFromNow_NotFromThePlan()
    {
        var pacer = new AnalysisPacer(_time);
        var interval = Interval(10);
        pacer.MarkStarted(interval); // Soll 100 ms

        _time.Advance(TimeSpan.FromMilliseconds(120)); // Verspätung 20 ms
        Assert.True(pacer.IsDue(interval));
        pacer.MarkStarted(interval); // nächster Soll 200 ms, nicht 220 ms

        _time.Advance(TimeSpan.FromMilliseconds(80));
        Assert.True(pacer.IsDue(interval));
    }

    [Fact]
    public void ShorterRate_TakesEffectAtOnce_MeasuredFromTheLastStart()
    {
        var pacer = new AnalysisPacer(_time);
        pacer.MarkStarted(TimeSpan.FromSeconds(1)); // 1 Analyse/s: nächster Soll bei 1 s

        _time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.False(pacer.IsDue(TimeSpan.FromSeconds(1)));
        Assert.False(pacer.IsDue(TimeSpan.FromMilliseconds(100))); // jetzt 10/s: Soll rückt auf 100 ms nach dem letzten Start
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(pacer.IsDue(TimeSpan.FromMilliseconds(100)));

        pacer.MarkStarted(TimeSpan.FromMilliseconds(100));
        _time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.False(pacer.IsDue(TimeSpan.FromMilliseconds(100)));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(pacer.IsDue(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void ShorterRate_AfterALongWait_IsDueImmediately()
    {
        var pacer = new AnalysisPacer(_time);
        pacer.MarkStarted(TimeSpan.FromSeconds(1));

        _time.Advance(TimeSpan.FromMilliseconds(250)); // bei 1/s noch zu früh
        Assert.False(pacer.IsDue(TimeSpan.FromSeconds(1)));
        Assert.True(pacer.IsDue(TimeSpan.FromMilliseconds(100))); // 250 ms ≥ 100 ms seit dem letzten Start
    }

    [Fact]
    public void LongerRate_DoesNotShortenTheCurrentWait()
    {
        var pacer = new AnalysisPacer(_time);
        pacer.MarkStarted(TimeSpan.FromMilliseconds(100));

        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(pacer.IsDue(TimeSpan.FromSeconds(1))); // der bereits fällige Takt bleibt fällig
    }
}
