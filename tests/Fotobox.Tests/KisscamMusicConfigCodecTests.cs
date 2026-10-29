using Fotobox.Core;
using Fotobox.Core.Music;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Lesen, Normalisieren und Schreiben von <c>kisscam-musik.json</c> (<see cref="KisscamMusicConfigCodec"/>).</summary>
public class KisscamMusicConfigCodecTests
{
    private static KisscamSongConfig Song(string name, bool enabled = false, int start = 0, int volume = 100) =>
        new() { FileName = name, Enabled = enabled, StartSeconds = start, VolumePercent = volume };

    [Fact]
    public void WriteThenParse_RoundTripsAllValues_SortedByFileName()
    {
        var config = new KisscamMusicConfig { Songs = [Song("b.mp3", true, 75, 60), Song("A.wav", false, 0, 100)] };

        var json = KisscamMusicConfigCodec.Write(config);
        Assert.True(KisscamMusicConfigCodec.TryParse(json, out var parsed));

        Assert.Equal(KisscamMusicConfig.CurrentVersion, parsed.Version);
        Assert.Equal(["A.wav", "b.mp3"], parsed.Songs.Select(s => s.FileName));
        var b = parsed.Songs[1];
        Assert.True(b.Enabled);
        Assert.Equal(75, b.StartSeconds);
        Assert.Equal(60, b.VolumePercent);
        Assert.Contains("\"Version\": 1", json);
    }

    [Fact]
    public void Write_KeepsUmlautsReadable_AndIsIndented()
    {
        var json = KisscamMusicConfigCodec.Write(new KisscamMusicConfig { Songs = [Song("Küssen & Mehr.mp3", true)] });

        Assert.Contains("Küssen & Mehr.mp3", json);
        Assert.Contains(Environment.NewLine, json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("kein json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("{ \"Songs\": 5 }")]
    [InlineData("{ \"Songs\": [ { \"File\": \"a.mp3\", \"StartSeconds\": \"abc\" } ] }")]
    [InlineData("{ \"Songs\": [ { \"File\": \"a.mp3\", \"Enabled\": \"vielleicht\" } ] }")]
    public void TryParse_UnreadableContent_IsReportedAsBroken(string json)
    {
        Assert.False(KisscamMusicConfigCodec.TryParse(json, out var config));
        Assert.Empty(config.Songs);
    }

    [Fact]
    public void TryParse_Null_IsBroken()
    {
        Assert.False(KisscamMusicConfigCodec.TryParse(null, out _));
    }

    [Fact]
    public void TryParse_EmptyObject_IsAnEmptyConfig()
    {
        Assert.True(KisscamMusicConfigCodec.TryParse("{}", out var config));
        Assert.Empty(config.Songs);
        Assert.True(KisscamMusicConfigCodec.TryParse("{ \"Version\": 1, \"Songs\": null }", out config));
        Assert.Empty(config.Songs);
    }

    [Fact]
    public void TryParse_MissingFields_GetDefaults_AndUnknownFieldsAreIgnored()
    {
        const string json = """{ "Zukunft": 1, "Songs": [ { "File": "a.mp3", "Neu": true } ] }""";

        Assert.True(KisscamMusicConfigCodec.TryParse(json, out var config));

        var song = Assert.Single(config.Songs);
        Assert.False(song.Enabled); // neue Songs sind nicht angehakt
        Assert.Equal(0, song.StartSeconds);
        Assert.Equal(SettingsLimits.KisscamSongVolumeDefault, song.VolumePercent);
    }

    [Fact]
    public void TryParse_AllowsCommentsAndTrailingCommas_AndAnyCasing()
    {
        const string json = """
            { // von Hand bearbeitet
              "songs": [ { "file": "a.mp3", "enabled": true, "startSeconds": 3, }, ],
            }
            """;

        Assert.True(KisscamMusicConfigCodec.TryParse(json, out var config));
        Assert.True(Assert.Single(config.Songs).Enabled);
    }

    [Fact]
    public void Normalize_ClampsValues_IntoTheLimits()
    {
        var config = new KisscamMusicConfig
        {
            Songs = [Song("a.mp3", true, -5, 3), Song("b.mp3", true, 99999, 500), Song("c.mp3", true, 10, 47)],
        };

        var normalized = KisscamMusicConfigCodec.Normalize(config);

        Assert.Equal(SettingsLimits.KisscamSongStartSecondsMin, normalized.Songs[0].StartSeconds);
        Assert.Equal(SettingsLimits.KisscamSongVolumeMin, normalized.Songs[0].VolumePercent);
        Assert.Equal(SettingsLimits.KisscamSongStartSecondsMax, normalized.Songs[1].StartSeconds);
        Assert.Equal(SettingsLimits.KisscamSongVolumeMax, normalized.Songs[1].VolumePercent);
        Assert.Equal(45, normalized.Songs[2].VolumePercent); // auf 5er-Schritte gerundet
        Assert.Equal(-5, config.Songs[0].StartSeconds); // das Original bleibt unverändert
    }

    [Fact]
    public void Normalize_RemovesDuplicatesIgnoringCase_FirstWins()
    {
        var config = new KisscamMusicConfig { Songs = [Song("Lied.mp3", true, 10), Song("LIED.MP3", false, 99), Song("lied.mp3")] };

        var normalized = KisscamMusicConfigCodec.Normalize(config);

        var song = Assert.Single(normalized.Songs);
        Assert.Equal("Lied.mp3", song.FileName);
        Assert.True(song.Enabled);
        Assert.Equal(10, song.StartSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("Unter\\Lied.mp3")]
    [InlineData("unter/Lied.mp3")]
    [InlineData("Lied?.mp3")]
    public void Normalize_DropsEntriesWithoutAPlainFileName(string name)
    {
        var normalized = KisscamMusicConfigCodec.Normalize(new KisscamMusicConfig { Songs = [Song(name), Song("ok.mp3")] });

        Assert.Equal("ok.mp3", Assert.Single(normalized.Songs).FileName);
    }

    [Fact]
    public void Normalize_Trims_AndCapsTheNumberOfSongs()
    {
        var songs = Enumerable.Range(0, SettingsLimits.KisscamMusicMaxSongs + 20).Select(i => Song($" s{i:0000}.mp3 ")).ToList();

        var normalized = KisscamMusicConfigCodec.Normalize(new KisscamMusicConfig { Songs = songs });

        Assert.Equal(SettingsLimits.KisscamMusicMaxSongs, normalized.Songs.Count);
        Assert.Equal("s0000.mp3", normalized.Songs[0].FileName);
    }

    [Fact]
    public void Normalize_NullConfig_YieldsAnEmptyOne()
    {
        Assert.Empty(KisscamMusicConfigCodec.Normalize(null).Songs);
        Assert.Equal(KisscamMusicConfig.CurrentVersion, KisscamMusicConfigCodec.Normalize(null).Version);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(1234, 1234)]
    [InlineData(3599, 3599)]
    [InlineData(3600, 3599)]
    public void ClampStart_StaysInTheLimits(int input, int expected) =>
        Assert.Equal(expected, KisscamMusicConfigCodec.ClampStart(input));

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 10)]
    [InlineData(12, 10)]
    [InlineData(13, 15)]
    [InlineData(100, 100)]
    [InlineData(250, 100)]
    public void ClampVolume_RoundsToStepsAndStaysInTheLimits(int input, int expected) =>
        Assert.Equal(expected, KisscamMusicConfigCodec.ClampVolume(input));

    [Theory]
    [InlineData("a.mp3", true)]
    [InlineData("A.MP3", true)]
    [InlineData("b.wav", true)]
    [InlineData("c.m4a", true)]
    [InlineData("d.WMA", true)]
    [InlineData("e.ogg", false)]
    [InlineData("f.flac", false)]
    [InlineData("kisscam-musik.json", false)]
    [InlineData("kisscam-musik.json.tmp", false)]
    [InlineData("mp3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSong_FiltersByTheFourExtensions(string? name, bool expected) =>
        Assert.Equal(expected, KisscamMusicFiles.IsSong(name));
}
