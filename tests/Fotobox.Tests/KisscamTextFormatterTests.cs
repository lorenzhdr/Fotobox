using Fotobox.Core;
using Fotobox.Core.Heart;
using Xunit;

namespace Fotobox.Tests;

public sealed class KisscamTextFormatterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \r\n")]
    public void EmptyTemplate_UsesDefaultText(string? template)
    {
        Assert.Equal("Du hast 10 Sekunden Zeit, dir jemanden zum Küssen zu suchen!", KisscamTextFormatter.Format(template, 10));
        Assert.Equal(HeartSettings.DefaultKisscamText.Replace("{Sekunden}", "7"), KisscamTextFormatter.Format(template, 7));
    }

    [Theory]
    [InlineData("{Sekunden}", "12")]
    [InlineData("{sekunden}", "12")]
    [InlineData("{SEKUNDEN}", "12")]
    [InlineData("Noch {Sekunden} Sek.", "Noch 12 Sek.")]
    [InlineData("{Sekunden}{Sekunden}", "1212")]
    [InlineData("{Sekunden} und {sekunden}", "12 und 12")]
    public void Placeholder_IsReplaced_CaseInsensitive(string template, string expected)
    {
        Assert.Equal(expected, KisscamTextFormatter.Format(template, 12));
    }

    [Fact]
    public void TextWithoutPlaceholder_IsKeptUnchanged()
    {
        Assert.Equal("Küsst euch!", KisscamTextFormatter.Format("Küsst euch!", 10));
    }

    [Fact]
    public void UnknownPlaceholders_StayInPlace()
    {
        Assert.Equal("Hallo {Name}, noch 5 s {} {Sekunde}", KisscamTextFormatter.Format("Hallo {Name}, noch {Sekunden} s {} {Sekunde}", 5));
    }

    [Fact]
    public void NumberIsCultureIndependent()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal("1000", KisscamTextFormatter.Format("{Sekunden}", 1000));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void FindUnknownPlaceholders_ListsEachOnce_InOrder_WithBraces()
    {
        var unknown = KisscamTextFormatter.FindUnknownPlaceholders("{Sekunden} {Name} {sekunden} {Ort} {name} {Name}");
        Assert.Equal(["{Name}", "{Ort}"], unknown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Kein Platzhalter")]
    [InlineData("{Sekunden}")]
    [InlineData("{sekunden} und {SEKUNDEN}")]
    [InlineData("{} und { }")]
    public void FindUnknownPlaceholders_IsEmpty_WithoutUnknownOnes(string? template)
    {
        Assert.Empty(KisscamTextFormatter.FindUnknownPlaceholders(template));
    }

    [Fact]
    public void DefaultText_HasNoUnknownPlaceholders()
    {
        Assert.Empty(KisscamTextFormatter.FindUnknownPlaceholders(HeartSettings.DefaultKisscamText));
        Assert.Empty(KisscamTextFormatter.FindUnknownPlaceholders(HeartSettings.DefaultKisscamBannerText));
    }
}
