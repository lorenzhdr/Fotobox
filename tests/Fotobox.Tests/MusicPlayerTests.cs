using System.Diagnostics;
using System.IO;
using System.Text;
using Fotobox.Core;
using Fotobox.Services.Audio;
using Xunit;

namespace Fotobox.Tests;

// Bewusst ohne [Collection(WpfCollection.Name)]: Der MusicPlayer nutzt nur MediaPlayer auf einem eigenen STA-Thread mit eigenem
// Dispatcher und lädt weder BAML noch Pack-URIs, kann also nicht mit den WPF-Tests im Hauptthread verklemmen.
public class MusicPlayerTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Aufräumen darf nie fehlschlagen.
            }
        }
    }

    private string TempFile(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fotobox-music-{Guid.NewGuid():N}{extension}");
        _tempFiles.Add(path);
        return path;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    // Kurzer Sinuston als 16-bit-Mono-WAV.
    private static void WriteSineWav(string path, double seconds)
    {
        const int rate = 22050;
        var samples = (int)(rate * seconds);
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream, Encoding.ASCII);
        w.Write("RIFF"u8);
        w.Write(36 + samples * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples * 2);
        for (var i = 0; i < samples; i++)
            w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000));
    }

    [Fact]
    public async Task MissingFile_DoesNotThrow_AndStaysSilent()
    {
        using var player = new MusicPlayer();
        player.Play(TempFile(".mp3"), 50, loop: true);

        await Task.Delay(300);
        Assert.False(player.IsPlaying);
        await Task.Delay(MusicPlayer.OpenTimeout + TimeSpan.FromMilliseconds(500));
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task GarbageFile_DoesNotThrow_AndGivesUpWithinTimeout()
    {
        var path = TempFile(".mp3");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("Das ist keine Musik, nur Text."));

        using var player = new MusicPlayer();
        player.Play(path, 50, loop: false);

        await Task.Delay(MusicPlayer.OpenTimeout + TimeSpan.FromSeconds(1));
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task MissingFile_RaisesPlaybackFailed_WithoutPlaybackChanged()
    {
        using var player = new MusicPlayer();
        var failed = 0;
        var changed = 0;
        player.PlaybackFailed += (_, _) => Interlocked.Increment(ref failed);
        player.PlaybackChanged += (_, _) => Interlocked.Increment(ref changed);

        player.Play(TempFile(".mp3"), 50, loop: true);

        Assert.True(await WaitUntil(() => Volatile.Read(ref failed) > 0, TimeSpan.FromSeconds(5)), "Kein PlaybackFailed.");
        Assert.Equal(1, Volatile.Read(ref failed));
        Assert.Equal(0, Volatile.Read(ref changed));
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task GarbageFile_RaisesPlaybackFailed_WithinTheOpenTimeout()
    {
        var path = TempFile(".mp3");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("Das ist keine Musik, nur Text."));
        using var player = new MusicPlayer();
        var failed = 0;
        player.PlaybackFailed += (_, _) => Interlocked.Increment(ref failed);

        player.Play(path, 50, loop: false);

        // MediaFailed oder spätestens die Öffnungsfrist melden den Fehlschlag.
        Assert.True(await WaitUntil(() => Volatile.Read(ref failed) > 0, MusicPlayer.OpenTimeout + TimeSpan.FromSeconds(3)),
            "Kein PlaybackFailed.");
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task PlaybackFailed_CarriesThePathOfTheFailedFile()
    {
        using var player = new MusicPlayer();
        string? failedPath = null;
        player.PlaybackFailed += (_, e) => failedPath = e.Path;
        var path = TempFile(".mp3");

        player.Play(path, 50, loop: false, TimeSpan.FromSeconds(12));

        Assert.True(await WaitUntil(() => failedPath is not null, TimeSpan.FromSeconds(5)), "Kein PlaybackFailed.");
        Assert.Equal(path, failedPath);
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(0.0)]
    [InlineData(3599.0)]
    public async Task StartAt_OnAMissingFile_IsSilent_WhateverTheValue(double seconds)
    {
        using var player = new MusicPlayer();

        player.Play(TempFile(".wav"), 50, loop: true, TimeSpan.FromSeconds(seconds));

        await Task.Delay(300);
        Assert.False(player.IsPlaying);
    }

    [Theory]
    [InlineData(0.0, null, 0.0)]
    [InlineData(-3.0, null, 0.0)]
    [InlineData(12.0, null, 12.0)] // Länge unbekannt: Startzeit unverändert
    [InlineData(12.0, 100.0, 12.0)]
    [InlineData(99.9, 100.0, 99.9)]
    [InlineData(100.0, 100.0, 0.0)] // gleich der Länge: ab Anfang
    [InlineData(250.0, 100.0, 0.0)] // hinter dem Songende: ab Anfang
    [InlineData(5.0, 0.0, 5.0)] // Länge 0 gilt als unbekannt
    public void ResolveStart_ClampsAgainstTheNaturalDuration(double start, double? duration, double expected)
    {
        var result = MusicPlayer.ResolveStart(TimeSpan.FromSeconds(start), duration is null ? null : TimeSpan.FromSeconds(duration.Value));

        Assert.Equal(TimeSpan.FromSeconds(expected), result);
    }

    [Fact]
    public void PositionInterval_Is250Milliseconds() =>
        Assert.Equal(TimeSpan.FromMilliseconds(250), MusicPlayer.PositionInterval);

    [Fact]
    public async Task SupersededPlay_DoesNotRaiseAStalePlaybackFailed()
    {
        using var player = new MusicPlayer();
        var failed = 0;
        player.PlaybackFailed += (_, _) => Interlocked.Increment(ref failed);

        player.Play(TempFile(".mp3"), 50, loop: true);
        player.Stop(); // überholt den Start, bevor er auf dem Musik-Thread läuft

        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref failed));
    }

    [Fact]
    public async Task ThrowingPlaybackFailedHandler_DoesNotBreakThePlayer()
    {
        using var player = new MusicPlayer();
        var failed = 0;
        player.PlaybackFailed += (_, _) =>
        {
            Interlocked.Increment(ref failed);
            throw new InvalidOperationException("Testfehler");
        };

        player.Play(TempFile(".wav"), 50, loop: false);

        Assert.True(await WaitUntil(() => Volatile.Read(ref failed) > 0, TimeSpan.FromSeconds(5)));
        player.Play(TempFile(".wav"), 50, loop: false); // der Player lebt weiter
        Assert.True(await WaitUntil(() => Volatile.Read(ref failed) > 1, TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NullOrEmptyPath_IsSilent(string? path)
    {
        using var player = new MusicPlayer();
        player.Play(path, 80, loop: false);

        await Task.Delay(200);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public void StopAndFadeOut_WithoutPlay_AndRepeated_DoNotThrow()
    {
        using var player = new MusicPlayer();
        player.Stop();
        player.FadeOut(TimeSpan.FromSeconds(1));
        player.FadeOut(TimeSpan.Zero);
        player.Stop();
        player.Stop();
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task StopAndFadeOut_AfterFailedPlay_AndRepeated_DoNotThrow()
    {
        using var player = new MusicPlayer();
        player.Play(TempFile(".wav"), 50, loop: false);
        player.FadeOut(TimeSpan.FromMilliseconds(200));
        player.FadeOut(TimeSpan.FromMilliseconds(200));
        player.Stop();
        player.Stop();
        player.Play("", 50, loop: false);

        await Task.Delay(300);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task Dispose_IsFast_AndLaterCallsAreNoOps()
    {
        var player = new MusicPlayer();
        player.Play(TempFile(".mp3"), 50, loop: true);
        await Task.Delay(100);

        var sw = Stopwatch.StartNew();
        player.Dispose();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Dispose dauerte {sw.Elapsed}.");

        player.Play(TempFile(".wav"), 50, loop: true);
        player.FadeOut(TimeSpan.FromSeconds(1));
        player.Stop();
        player.Dispose();
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public void Dispose_WithoutAnyPlay_DoesNotThrow()
    {
        var player = new MusicPlayer();
        player.Dispose();
        player.Play("x.mp3", 50, loop: false);
        player.Stop();
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task ThrowingPlaybackChangedHandler_DoesNotBreakThePlayer()
    {
        using var player = new MusicPlayer();
        player.PlaybackChanged += (_, _) => throw new InvalidOperationException("Testfehler");
        player.Play(TempFile(".wav"), 50, loop: false);
        player.Stop();

        await Task.Delay(200);
        Assert.False(player.IsPlaying);
    }

    // Braucht ein Audiogerät und Media Foundation: nicht in der CI.
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task RealWav_StartsAtTheStartTime_ReportsThePosition_AndLoopsBackToIt()
    {
        var path = TempFile(".wav");
        WriteSineWav(path, 4.0);
        using var player = new MusicPlayer();
        var positions = new List<MusicPositionEventArgs>();
        player.PositionChanged += (_, e) =>
        {
            lock (positions)
                positions.Add(e);
        };

        player.Play(path, 5, loop: true, TimeSpan.FromSeconds(2.5));
        Assert.True(await WaitUntil(() => player.IsPlaying, TimeSpan.FromSeconds(5)), "Wiedergabe startete nicht.");
        Assert.True(await WaitUntil(() => { lock (positions) return positions.Count >= 2; }, TimeSpan.FromSeconds(5)), "Keine Positionsmeldung.");

        lock (positions)
        {
            Assert.Equal(path, positions[0].Path);
            Assert.True(positions[0].Position >= TimeSpan.FromSeconds(2.4), $"Start bei {positions[0].Position}.");
            Assert.True(positions[0].Position < TimeSpan.FromSeconds(3.5));
        }

        // Nach dem Dateiende springt die Schleife auf die Startzeit zurück (nicht auf 0:00): Die Position wird kleiner und bleibt
        // danach ab der Startzeit (eine einzelne Meldung genau am Dateiende kann noch 0:00 zeigen).
        Assert.True(await WaitUntil(() =>
        {
            lock (positions)
                return positions.Zip(positions.Skip(1), (a, b) => b.Position < a.Position).Any(wrapped => wrapped);
        }, TimeSpan.FromSeconds(5)), "Die Schleife lief nicht über das Dateiende hinaus.");
        await Task.Delay(700);
        lock (positions)
            Assert.True(positions[^1].Position >= TimeSpan.FromSeconds(2.4), $"Nach der Schleife bei {positions[^1].Position}.");
        Assert.True(player.IsPlaying);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task RealWav_StartBeyondTheEnd_PlaysFromZero()
    {
        var path = TempFile(".wav");
        WriteSineWav(path, 2.0);
        using var player = new MusicPlayer();
        MusicPositionEventArgs? first = null;
        player.PositionChanged += (_, e) => first ??= e;

        player.Play(path, 5, loop: true, TimeSpan.FromSeconds(30));

        Assert.True(await WaitUntil(() => first is not null, TimeSpan.FromSeconds(5)), "Keine Positionsmeldung.");
        Assert.True(first!.Position < TimeSpan.FromSeconds(1), $"Start bei {first.Position}.");
    }

    // Braucht ein Audiogerät und Media Foundation: nicht in der CI.
    [Fact]
    [Trait("Category", "Hardware")]
    public async Task RealWav_PlaysAndFadesOut()
    {
        var path = TempFile(".wav");
        WriteSineWav(path, 3.0);

        using var player = new MusicPlayer();
        var changes = 0;
        player.PlaybackChanged += (_, _) => Interlocked.Increment(ref changes);

        player.Play(path, 30, loop: true);
        Assert.True(await WaitUntil(() => player.IsPlaying, TimeSpan.FromSeconds(5)), "Wiedergabe startete nicht.");

        // Mit Wiederholung läuft sie über das Dateiende hinaus weiter.
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.True(player.IsPlaying);

        player.FadeOut(TimeSpan.FromMilliseconds(300));
        Assert.True(await WaitUntil(() => !player.IsPlaying, TimeSpan.FromSeconds(3)), "Ausblendung endete nicht.");
        Assert.True(Volatile.Read(ref changes) >= 2);
    }
}
