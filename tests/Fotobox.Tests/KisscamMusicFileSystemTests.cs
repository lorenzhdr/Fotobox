using System.Diagnostics;
using System.IO;
using Fotobox.Core.Music;
using Fotobox.Services.Audio;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Das echte Dateisystem der Musikbibliothek in einem temporären Ordner (Endungen, keine Rekursion, atomares Schreiben, Beobachtung).</summary>
public sealed class KisscamMusicFileSystemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-kisscam-fs-tests-" + Guid.NewGuid().ToString("N"));
    private readonly KisscamMusicFileSystem _fs = new();

    public KisscamMusicFileSystemTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Touch(string relative, string content = "x")
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, int seconds = 10)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (condition())
                return true;
            await Task.Delay(10);
        }
        return condition();
    }

    [Fact]
    public void EnsureDirectory_CreatesNestedFolders_AndUsesExistingOnes()
    {
        var folder = Path.Combine(_dir, "Musik", "Fotobox+", "Kisscam");

        _fs.EnsureDirectory(folder);
        _fs.EnsureDirectory(folder);

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void ListSongs_ReturnsOnlyTheFourExtensions_AnyCase_AndNotRecursive()
    {
        Touch("a.mp3");
        Touch("B.WAV");
        Touch("c.m4a");
        Touch("d.wma");
        Touch("e.ogg");
        Touch("f.flac");
        Touch("kisscam-musik.json");
        Touch("kisscam-musik.json.tmp");
        Touch("ohne-endung");
        Touch(Path.Combine("unter", "tief.mp3"));

        var names = _fs.ListSongs(_dir).Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(["a.mp3", "B.WAV", "c.m4a", "d.wma"], names);
    }

    [Fact]
    public void ListSongs_ReportsFullPathLengthAndStamp()
    {
        var path = Touch("a.mp3", "12345");

        var entry = Assert.Single(_fs.ListSongs(_dir));

        Assert.Equal(path, entry.FullPath);
        Assert.Equal(5, entry.Length);
        Assert.Equal(File.GetLastWriteTimeUtc(path), entry.LastWriteUtc);
    }

    [Fact]
    public void ListSongs_EmptyFolder_IsEmpty_AndMissingFolderThrows()
    {
        Assert.Empty(_fs.ListSongs(_dir));
        Assert.ThrowsAny<IOException>(() => _fs.ListSongs(Path.Combine(_dir, "gibt-es-nicht")));
    }

    [Fact]
    public void ReadConfig_MissingFile_IsNull_ExistingFileIsReturned()
    {
        var path = Path.Combine(_dir, KisscamMusicFiles.ConfigFileName);
        Assert.Null(_fs.ReadConfig(path));
        Assert.Null(_fs.ReadConfig(Path.Combine(_dir, "gibt-es-nicht", "x.json")));

        File.WriteAllText(path, "{ }");

        Assert.Equal("{ }", _fs.ReadConfig(path));
    }

    [Fact]
    public void WriteConfigAtomic_CreatesTheFile_ReplacesIt_AndLeavesNoTemporaryFile()
    {
        var path = Path.Combine(_dir, KisscamMusicFiles.ConfigFileName);

        _fs.WriteConfigAtomic(path, "{ \"a\": 1 }");
        Assert.Equal("{ \"a\": 1 }", File.ReadAllText(path));

        _fs.WriteConfigAtomic(path, "{ \"b\": \"Küssen\" }");
        Assert.Equal("{ \"b\": \"Küssen\" }", File.ReadAllText(path));

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal([KisscamMusicFiles.ConfigFileName], Directory.GetFiles(_dir).Select(Path.GetFileName));
        Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })); // ohne BOM
    }

    [Fact]
    public void WriteConfigAtomic_IntoAMissingFolder_Throws_AndLeavesNothingBehind()
    {
        var path = Path.Combine(_dir, "gibt-es-nicht", KisscamMusicFiles.ConfigFileName);

        Assert.ThrowsAny<IOException>(() => _fs.WriteConfigAtomic(path, "{}"));
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void MoveBroken_MovesTheFileAside_AndReplacesAnOlderCopy()
    {
        var path = Touch(KisscamMusicFiles.ConfigFileName, "neu defekt");
        var broken = Path.Combine(_dir, KisscamMusicFiles.BrokenConfigFileName);
        File.WriteAllText(broken, "alt defekt");

        _fs.MoveBroken(path, broken);

        Assert.False(File.Exists(path));
        Assert.Equal("neu defekt", File.ReadAllText(broken));
    }

    [Fact]
    public async Task Watch_ReportsMusicFiles_ButNotTheConfigOrTemporaryFiles()
    {
        var changes = 0;
        using var watcher = _fs.Watch(_dir, () => Interlocked.Increment(ref changes), _ => { });

        // Die Config und ihre temporäre Datei lösen nichts aus (sonst gäbe es eine Schleife beim Speichern).
        var config = Path.Combine(_dir, KisscamMusicFiles.ConfigFileName);
        _fs.WriteConfigAtomic(config, "{}");
        _fs.WriteConfigAtomic(config, "{ }");
        File.WriteAllText(Path.Combine(_dir, "notiz.txt"), "x");
        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref changes));

        Touch("neu.mp3");

        Assert.True(await WaitUntil(() => Volatile.Read(ref changes) > 0), "Keine Änderung gemeldet.");
    }

    [Fact]
    public async Task Watch_ReportsRenameAndDelete()
    {
        var path = Touch("a.mp3");
        var changes = 0;
        using var watcher = _fs.Watch(_dir, () => Interlocked.Increment(ref changes), _ => { });

        File.Move(path, Path.Combine(_dir, "b.wav"));
        Assert.True(await WaitUntil(() => Volatile.Read(ref changes) > 0), "Umbenennen nicht gemeldet.");

        var before = Volatile.Read(ref changes);
        File.Delete(Path.Combine(_dir, "b.wav"));
        Assert.True(await WaitUntil(() => Volatile.Read(ref changes) > before), "Löschen nicht gemeldet.");
    }

    [Fact]
    public void Watch_OnAMissingFolder_Throws()
    {
        Assert.ThrowsAny<Exception>(() => _fs.Watch(Path.Combine(_dir, "gibt-es-nicht"), () => { }, _ => { }));
    }

    [Fact]
    public async Task ReadDuration_OfGarbage_ReturnsNullOrThrows_ButNeverHangs()
    {
        var path = Touch("kaputt.mp3", "kein Audio");
        var task = Task.Run(() =>
        {
            try
            {
                return _fs.ReadDuration(path);
            }
            catch (Exception)
            {
                return null; // Die Bibliothek fängt Ausnahmen ab; hier zählt nur, dass es zurückkehrt.
            }
        });

        Assert.True(await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20))) == task, "ReadDuration kehrte nicht zurück.");
    }
}
