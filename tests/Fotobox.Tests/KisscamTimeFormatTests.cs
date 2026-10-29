using Fotobox.Core.Music;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Anzeige und Eingabe von Startzeiten und Songlängen (<see cref="KisscamTimeFormat"/>).</summary>
public class KisscamTimeFormatTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(62, "1:02")]
    [InlineData(600, "10:00")]
    [InlineData(3599, "59:59")]
    [InlineData(-4, "0:00")]
    public void Format_WritesMinutesAndSeconds(int seconds, string expected) =>
        Assert.Equal(expected, KisscamTimeFormat.Format(seconds));

    [Fact]
    public void FormatDuration_RoundsToSeconds_AndShowsADashWhenUnknown()
    {
        Assert.Equal("3:25", KisscamTimeFormat.FormatDuration(TimeSpan.FromSeconds(204.6)));
        Assert.Equal("0:01", KisscamTimeFormat.FormatDuration(TimeSpan.FromSeconds(0.6)));
        Assert.Equal("–", KisscamTimeFormat.FormatDuration(null));
        Assert.Equal("–", KisscamTimeFormat.FormatDuration(TimeSpan.Zero));
    }

    [Theory]
    [InlineData("0:00", 0)]
    [InlineData("1:30", 90)]
    [InlineData("01:05", 65)]
    [InlineData("59:59", 3599)]
    [InlineData("0:5", 5)]
    [InlineData("90", 90)]
    [InlineData("0", 0)]
    [InlineData("  2:03  ", 123)]
    [InlineData("75:00", 4500)]
    public void TryParse_ReadsMinutesSecondsOrPlainSeconds(string text, int expected)
    {
        Assert.True(KisscamTimeFormat.TryParse(text, out var seconds));
        Assert.Equal(expected, seconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1:60")]
    [InlineData("1:2:3")]
    [InlineData(":30")]
    [InlineData("1:")]
    [InlineData("-5")]
    [InlineData("-1:30")]
    [InlineData("1,5")]
    [InlineData("1.5")]
    [InlineData("1:30,5")]
    [InlineData("9999999999")]
    public void TryParse_RejectsEverythingElse(string? text)
    {
        Assert.False(KisscamTimeFormat.TryParse(text, out var seconds));
        Assert.Equal(0, seconds);
    }

    [Fact]
    public void FormatThenParse_RoundTrips()
    {
        for (var seconds = 0; seconds <= 3599; seconds += 7)
        {
            Assert.True(KisscamTimeFormat.TryParse(KisscamTimeFormat.Format(seconds), out var parsed));
            Assert.Equal(seconds, parsed);
        }
    }
}
