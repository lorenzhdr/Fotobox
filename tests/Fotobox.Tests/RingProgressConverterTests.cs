using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Fotobox.Views.Converters;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Ladekreis-Bogen: Fortschritt 0..1 → Geometrie im Uhrzeigersinn ab 12 Uhr.</summary>
[Collection(WpfCollection.Name)]
public class RingProgressConverterTests
{
    // Durchmesser 100, Strichstärke 10 → Bogenmitte auf Radius 45 um (50, 50).
    private static readonly RingProgressConverter Converter = new() { Diameter = 100, Thickness = 10 };

    private static Geometry Convert(object? value) =>
        (Geometry)Converter.Convert(value, typeof(Geometry), null!, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    public void NoProgress_GivesEmptyGeometry(double progress)
    {
        Assert.True(Convert(progress).Bounds.IsEmpty);
    }

    [Fact]
    public void NonNumericValue_GivesEmptyGeometry()
    {
        Assert.True(Convert(null).Bounds.IsEmpty);
        Assert.True(Convert("kein Wert").Bounds.IsEmpty);
    }

    [Fact]
    public void Quarter_RunsFromTwelveToThreeOClock()
    {
        var bounds = Convert(0.25).Bounds;

        Assert.Equal(50, bounds.Left, 1);
        Assert.Equal(95, bounds.Right, 1);
        Assert.Equal(5, bounds.Top, 1);
        Assert.Equal(50, bounds.Bottom, 1);
    }

    [Fact]
    public void Half_RunsClockwiseThroughThreeOClockToSixOClock()
    {
        var bounds = Convert(0.5).Bounds;

        // Rechte Hälfte des Rings: von oben (12 Uhr) über rechts nach unten (6 Uhr).
        Assert.Equal(50, bounds.Left, 1);
        Assert.Equal(95, bounds.Right, 1);
        Assert.Equal(5, bounds.Top, 1);
        Assert.Equal(95, bounds.Bottom, 1);
    }

    [Fact]
    public void ThreeQuarters_UsesTheLargeArc_AndEndsAtNineOClock()
    {
        var bounds = Convert(0.75).Bounds;

        Assert.Equal(5, bounds.Left, 1);
        Assert.Equal(95, bounds.Right, 1);
        Assert.Equal(5, bounds.Top, 1);
        Assert.Equal(95, bounds.Bottom, 1);
    }

    [Fact]
    public void Full_IsAClosedCircle_OnTheRingCenterLine()
    {
        var geometry = Convert(1.0);

        Assert.IsType<EllipseGeometry>(geometry);
        Assert.Equal(new Rect(5, 5, 90, 90), geometry.Bounds);
    }

    [Theory]
    [InlineData(1.0001)]
    [InlineData(7.0)]
    public void ProgressAboveOne_IsClampedToTheFullCircle(double progress)
    {
        Assert.Equal(new Rect(5, 5, 90, 90), Convert(progress).Bounds);
    }

    [Fact]
    public void Geometry_IsFrozen()
    {
        Assert.True(Convert(0.4).IsFrozen);
        Assert.True(Convert(1.0).IsFrozen);
    }

    [Fact]
    public void ConvertBack_DoesNothing()
    {
        Assert.Same(Binding.DoNothing, Converter.ConvertBack(Geometry.Empty, typeof(double), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void DefaultSize_MatchesTheRingInTheGuestView()
    {
        var converter = new RingProgressConverter();

        Assert.Equal(90, converter.Diameter);
        Assert.Equal(9, converter.Thickness);
    }
}
