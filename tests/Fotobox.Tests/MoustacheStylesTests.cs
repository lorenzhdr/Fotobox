using System.Text.Json;
using Fotobox.Core;
using Fotobox.Core.Peace;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Eine Quelle für die Bartauswahl: <see cref="MoustacheStyles"/>, <see cref="PeaceSettings.RandomStyles"/> und der Normalizer.</summary>
public sealed class MoustacheStylesTests
{
    [Fact]
    public void All_HasSevenConcreteShapes_InEnumOrder()
    {
        Assert.Equal(7, MoustacheStyles.All.Count);
        Assert.DoesNotContain(MoustacheStyle.Random, MoustacheStyles.All);
        Assert.Equal(MoustacheStyles.All.OrderBy(s => (int)s), MoustacheStyles.All);
        Assert.Equal(6, MoustacheStyles.AllColors.Count);
    }

    [Fact]
    public void NewShapes_AreAppendedAtTheEnd_OfTheEnum()
    {
        Assert.Equal(["Random", "Twirl", "Walrus", "Chevron", "Brush", "Handlebar", "FuManchu", "Horseshoe"], Enum.GetNames<MoustacheStyle>());
        Assert.Equal(["Black", "DarkBrown", "Brown", "Ginger", "Blond", "Gray"], Enum.GetNames<MoustacheColor>());
    }

    [Fact]
    public void Defaults_UseAllShapes()
    {
        Assert.Equal(MoustacheStyles.All, new PeaceSettings().RandomStyles);
    }

    [Fact]
    public void Clone_CopiesTheList()
    {
        var original = new PeaceSettings { RandomStyles = [MoustacheStyle.Twirl] };
        var copy = original.Clone();
        copy.RandomStyles.Add(MoustacheStyle.Brush);

        Assert.Equal([MoustacheStyle.Twirl], original.RandomStyles);
    }

    [Theory]
    [InlineData(0, MoustacheStyle.Twirl)]
    [InlineData(1, MoustacheStyle.Walrus)]
    [InlineData(6, MoustacheStyle.Horseshoe)]
    public void PickRandom_UsesAllShapes_ByDefault(int value, MoustacheStyle expected)
    {
        Assert.Equal(expected, MoustacheStyles.PickRandom(new PeaceSettings(), n =>
        {
            Assert.Equal(7, n);
            return value;
        }));
    }

    [Fact]
    public void PickRandom_OnlyReturnsListedShapes()
    {
        var settings = new PeaceSettings { RandomStyles = [MoustacheStyle.Handlebar, MoustacheStyle.Brush] };
        var seen = new HashSet<MoustacheStyle>();
        for (var i = 0; i < 20; i++)
            seen.Add(MoustacheStyles.PickRandom(settings, n => i % n));

        Assert.Equal([MoustacheStyle.Brush, MoustacheStyle.Handlebar], seen.OrderBy(s => (int)s));
    }

    [Fact]
    public void PickRandom_ClampsOutOfRangeDiceValues()
    {
        Assert.Equal(MoustacheStyle.Horseshoe, MoustacheStyles.PickRandom((IEnumerable<MoustacheStyle>?)null, _ => 99));
        Assert.Equal(MoustacheStyle.Twirl, MoustacheStyles.PickRandom((IEnumerable<MoustacheStyle>?)null, _ => -5));
        Assert.Equal(MoustacheColor.Gray, MoustacheStyles.PickColor(_ => 99));
        Assert.Equal(MoustacheColor.Black, MoustacheStyles.PickColor(_ => -1));
    }

    // ---- Normalizer

    [Fact]
    public void Normalizer_DropsUnknownRandomAndDuplicates_KeepsEnumOrder()
    {
        var settings = new AppSettings();
        settings.Peace.RandomStyles =
        [
            MoustacheStyle.Brush, MoustacheStyle.Random, (MoustacheStyle)99, MoustacheStyle.Twirl, MoustacheStyle.Brush, (MoustacheStyle)(-3),
        ];

        SettingsNormalizer.Normalize(settings);

        Assert.Equal([MoustacheStyle.Twirl, MoustacheStyle.Brush], settings.Peace.RandomStyles);
    }

    [Fact]
    public void Normalizer_EmptyOrNullList_BecomesAllShapes()
    {
        var empty = new AppSettings();
        empty.Peace.RandomStyles = [];
        SettingsNormalizer.Normalize(empty);
        Assert.Equal(MoustacheStyles.All, empty.Peace.RandomStyles);

        var nothingValid = new AppSettings();
        nothingValid.Peace.RandomStyles = [MoustacheStyle.Random, (MoustacheStyle)42];
        SettingsNormalizer.Normalize(nothingValid);
        Assert.Equal(MoustacheStyles.All, nothingValid.Peace.RandomStyles);

        var none = new AppSettings();
        none.Peace.RandomStyles = null!;
        SettingsNormalizer.Normalize(none);
        Assert.Equal(MoustacheStyles.All, none.Peace.RandomStyles);
    }

    [Fact]
    public void Json_UnknownNamesAreDropped_AndTheListRoundTripsExactly()
    {
        const string json = """{ "Peace": { "RandomStyles": ["Twirl", "Bogus", "Random", "Twirl", "FuManchu"] } }""";
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)!;
        SettingsNormalizer.Normalize(loaded);
        Assert.Equal([MoustacheStyle.Twirl, MoustacheStyle.FuManchu], loaded.Peace.RandomStyles);

        // Gespeichert und wieder geladen: genau die Auswahl, ohne Auffüllen mit den Standardwerten.
        var single = new AppSettings();
        single.Peace.RandomStyles = [MoustacheStyle.Twirl];
        var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(single, AppSettings.JsonOptions), AppSettings.JsonOptions)!;
        Assert.Equal([MoustacheStyle.Twirl], roundTrip.Peace.RandomStyles);
        Assert.Equal([MoustacheStyle.Twirl], single.Clone().Peace.RandomStyles);
    }

    [Fact]
    public void Json_UnknownMoustacheStyleValue_FallsBackToRandom()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("""{ "Peace": { "MoustacheStyle": "Zukunft" } }""", AppSettings.JsonOptions)!;
        SettingsNormalizer.Normalize(loaded);
        Assert.Equal(MoustacheStyle.Random, loaded.Peace.MoustacheStyle);
    }
}
