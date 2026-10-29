using Fotobox.Core.Music;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Zufallswahl des Songs (<see cref="KisscamSongPicker"/>) und die Berechnung der Startzeit und Lautstärke.</summary>
public class KisscamSongPickerTests
{
    private static KisscamSong Song(string name, bool enabled = true, int start = 0, int volume = 100, TimeSpan? duration = null,
        bool playable = true) => FakeMusicLibrary.Song(name, enabled, start, volume, duration, playable);

    private static KisscamLibrarySnapshot Snapshot(params KisscamSong[] songs) =>
        new(@"C:\Musik", KisscamLibraryStatus.Ready, songs, false);

    [Fact]
    public void NoSongs_OrNoSnapshot_PicksNothing()
    {
        Assert.Null(KisscamSongPicker.Pick(null, 80, new Random(1)));
        Assert.Null(KisscamSongPicker.Pick(Snapshot(), 80, new Random(1)));
        Assert.Null(KisscamSongPicker.Pick(KisscamLibrarySnapshot.Empty, 80, new Random(1)));
    }

    [Fact]
    public void OnlyEnabledPlayableSongs_AreCandidates()
    {
        var snapshot = Snapshot(Song("aus.mp3", enabled: false), Song("kaputt.mp3", playable: false), Song("gut.mp3"));

        for (var i = 0; i < 20; i++)
            Assert.Equal(@"C:\Musik\gut.mp3", KisscamSongPicker.Pick(snapshot, 80, new Random(i))!.Path);
    }

    [Fact]
    public void NoCandidate_PicksNothing()
    {
        var snapshot = Snapshot(Song("aus.mp3", enabled: false), Song("kaputt.mp3", playable: false));

        Assert.Null(KisscamSongPicker.Pick(snapshot, 80, new Random(1)));
    }

    [Fact]
    public void ChoiceIsPurelyRandom_RepetitionIsAllowed()
    {
        var snapshot = Snapshot(Song("a.mp3"), Song("b.mp3"), Song("c.mp3"));

        // Dieselbe Zufallsquelle ergibt dieselbe Wahl – immer wieder (kein Merken des letzten Songs).
        for (var i = 0; i < 5; i++)
            Assert.Equal(@"C:\Musik\b.mp3", KisscamSongPicker.Pick(snapshot, 80, new FixedRandom(1))!.Path);
        Assert.Equal(@"C:\Musik\a.mp3", KisscamSongPicker.Pick(snapshot, 80, new FixedRandom(0))!.Path);
        Assert.Equal(@"C:\Musik\c.mp3", KisscamSongPicker.Pick(snapshot, 80, new FixedRandom(2))!.Path);
    }

    [Fact]
    public void AllCandidates_AreReachable_WithARealRandom()
    {
        var snapshot = Snapshot(Song("a.mp3"), Song("b.mp3"), Song("c.mp3"));
        var random = new Random(42);

        var seen = Enumerable.Range(0, 200).Select(_ => KisscamSongPicker.Pick(snapshot, 80, random)!.Path).ToHashSet();

        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void StartTime_IsTheSavedOne_OrZeroWhenBeyondTheEnd()
    {
        var inside = KisscamSongPicker.Pick(Snapshot(Song("a.mp3", start: 40, duration: TimeSpan.FromSeconds(180))), 80, new Random(1))!;
        var beyond = KisscamSongPicker.Pick(Snapshot(Song("a.mp3", start: 200, duration: TimeSpan.FromSeconds(180))), 80, new Random(1))!;
        var unknown = KisscamSongPicker.Pick(Snapshot(Song("a.mp3", start: 200)), 80, new Random(1))!;

        Assert.Equal(TimeSpan.FromSeconds(40), inside.StartAt);
        Assert.Equal(TimeSpan.Zero, beyond.StartAt);
        Assert.Equal(TimeSpan.FromSeconds(200), unknown.StartAt); // ohne bekannte Länge gilt die Startzeit
    }

    [Theory]
    [InlineData(80, 100, 80)]
    [InlineData(80, 50, 40)]
    [InlineData(55, 50, 28)] // 27,5 rundet kaufmännisch auf
    [InlineData(100, 10, 10)]
    [InlineData(0, 100, 0)]
    [InlineData(100, 100, 100)]
    [InlineData(150, 100, 100)]
    [InlineData(-10, 100, 0)]
    public void EffectiveVolume_IsTotalTimesSongPercent(int total, int song, int expected) =>
        Assert.Equal(expected, KisscamSongPicker.EffectiveVolume(total, song));

    [Fact]
    public void PickCarriesTheEffectiveVolume()
    {
        var pick = KisscamSongPicker.Pick(Snapshot(Song("a.mp3", volume: 60)), 75, new Random(1))!;

        Assert.Equal(45, pick.VolumePercent);
    }

    [Fact]
    public void Song_StartBeyondEnd_AndEffectiveStart()
    {
        var song = Song("a.mp3", start: 100, duration: TimeSpan.FromSeconds(100));
        Assert.True(song.StartBeyondEnd); // gleich der Länge zählt als hinter dem Ende
        Assert.Equal(TimeSpan.Zero, song.EffectiveStart);

        var before = song with { StartSeconds = 99 };
        Assert.False(before.StartBeyondEnd);
        Assert.Equal(TimeSpan.FromSeconds(99), before.EffectiveStart);

        var unknown = song with { Duration = null };
        Assert.False(unknown.StartBeyondEnd);
        Assert.Equal(TimeSpan.FromSeconds(100), unknown.EffectiveStart);
    }

    [Fact]
    public void Snapshot_HasEnabledSong()
    {
        Assert.False(Snapshot(Song("a.mp3", enabled: false)).HasEnabledSong);
        Assert.True(Snapshot(Song("a.mp3", enabled: false), Song("b.mp3")).HasEnabledSong);
        Assert.False(KisscamLibrarySnapshot.Empty.HasEnabledSong);
    }
}
