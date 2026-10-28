using System.Globalization;
using System.IO;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

public sealed class VideoStorageTests : IDisposable
{
    private static readonly DateTime Stamp = new(2025, 6, 14, 15, 30, 45);
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FotoboxVideoStorage_" + Guid.NewGuid().ToString("N"));
    private readonly string _fallback = Path.Combine(Path.GetTempPath(), "FotoboxVideoStorage_" + Guid.NewGuid().ToString("N") + "_fallback");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
        try { Directory.Delete(_fallback, recursive: true); }
        catch { /* best effort */ }
    }

    private VideoStorage Storage(Func<string, long?>? free = null) => new(_fallback, free ?? (_ => null));

    private VideoSettings Settings(bool dateSub = false) => new() { Folder = _root, DateSubfolder = dateSub, FilePrefix = "Party_Video" };

    private static void WriteContent(string path, int bytes = 100) => File.WriteAllBytes(path, new byte[bytes]);

    [Fact]
    public void ResolveTargetFolder_Defaults_ToVideosFolder()
    {
        var storage = new VideoStorage();

        var folder = storage.ResolveTargetFolder(new VideoSettings { DateSubfolder = false }, Stamp);

        Assert.Equal(AppInfo.DefaultVideoFolder, folder);
    }

    [Fact]
    public void ResolveTargetFolder_AddsDateSubfolder_Invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH"); // buddhistischer Kalender
        try
        {
            Assert.Equal(Path.Combine(_root, "2025-06-14"), Storage().ResolveTargetFolder(Settings(dateSub: true), Stamp));
            Assert.Equal(_root, Storage().ResolveTargetFolder(Settings(dateSub: false), Stamp));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task PrepareAsync_CreatesDateFolder_AndReturnsTempPathWithoutCreatingFile()
    {
        var target = await Storage().PrepareAsync(Settings(dateSub: true), new CameraResolution(1280, 720), FiveMinutes, Stamp);

        var folder = Path.Combine(_root, "2025-06-14");
        Assert.Equal(folder, target.Folder);
        Assert.True(Directory.Exists(folder));
        Assert.Equal(folder, Path.GetDirectoryName(target.TempPath));
        Assert.EndsWith(".tmp.mp4", target.TempPath);
        Assert.StartsWith(".", Path.GetFileName(target.TempPath));
        Assert.False(File.Exists(target.TempPath));
        Assert.Empty(Directory.GetFileSystemEntries(folder)); // Schreibtest räumt auf
        Assert.Equal("Party_Video", target.FilePrefix);
        Assert.Equal(Stamp, target.Timestamp);
    }

    [Fact]
    public async Task PrepareAsync_TempNamesAreUnique()
    {
        var a = await Storage().PrepareAsync(Settings(), null, FiveMinutes, Stamp);
        var b = await Storage().PrepareAsync(Settings(), null, FiveMinutes, Stamp);

        Assert.NotEqual(a.TempPath, b.TempPath);
    }

    [Fact]
    public async Task PrepareAsync_UnwritableConfiguredFolder_FallsBackToDefaultFolder()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blockiert");
        File.WriteAllText(blocker, "ich bin eine Datei, kein Ordner");
        var video = Settings(dateSub: true);
        video.Folder = Path.Combine(blocker, "unterordner");

        var target = await Storage().PrepareAsync(video, null, FiveMinutes, Stamp);

        Assert.Equal(Path.Combine(_fallback, "2025-06-14"), target.Folder);
        Assert.True(Directory.Exists(target.Folder));
    }

    [Fact]
    public async Task PrepareAsync_NoFolderWritable_ThrowsGermanIOException()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blockiert");
        File.WriteAllText(blocker, "Datei");
        var video = Settings();
        video.Folder = Path.Combine(blocker, "unterordner");
        var storage = new VideoStorage(Path.Combine(blocker, "ausweich"), _ => null);

        var ex = await Assert.ThrowsAsync<IOException>(() => storage.PrepareAsync(video, null, FiveMinutes, Stamp));

        Assert.StartsWith("Videos können nicht gespeichert werden:", ex.Message);
        Assert.IsNotType<InsufficientDiskSpaceException>(ex);
    }

    [Fact]
    public async Task PrepareAsync_TooLittleSpace_ThrowsInsufficientDiskSpaceException_NotFallbackError()
    {
        var storage = Storage(_ => 100L * 1024 * 1024); // nur 100 MB frei

        var ex = await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() =>
            storage.PrepareAsync(Settings(), new CameraResolution(1920, 1080), FiveMinutes, Stamp));

        Assert.Contains("Speicherplatz", ex.Message);
        Assert.False(Directory.Exists(_fallback)); // kein Ausweichen wegen Platzmangel
    }

    [Fact]
    public async Task PrepareAsync_EnoughSpace_Succeeds()
    {
        var res = new CameraResolution(1920, 1080);
        var needed = VideoStorage.RequiredFreeBytes(Settings(), res, FiveMinutes);

        await Storage(_ => needed).PrepareAsync(Settings(), res, FiveMinutes, Stamp);
        await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() =>
            Storage(_ => needed - 1).PrepareAsync(Settings(), res, FiveMinutes, Stamp));
    }

    [Fact]
    public async Task PrepareAsync_UnknownFreeSpace_SkipsCheck()
    {
        var target = await Storage(_ => null).PrepareAsync(Settings(), null, TimeSpan.FromHours(10), Stamp);

        Assert.NotNull(target);
    }

    [Fact]
    public async Task PrepareAsync_RealDriveQuery_DoesNotThrow()
    {
        // Standard-Abfrage über DriveInfo (temporärer Ordner liegt auf einem lokalen Laufwerk); 1 Sekunde braucht fast nichts.
        var target = await new VideoStorage(_fallback).PrepareAsync(Settings(), null, TimeSpan.FromSeconds(1), Stamp);

        Assert.True(Directory.Exists(target.Folder));
    }

    [Fact]
    public void RequiredFreeBytes_UsesQualityResolutionAndReserve()
    {
        var video = new VideoSettings { Quality = VideoQuality.High };
        var hd = VideoStorage.RequiredFreeBytes(video, new CameraResolution(1920, 1080), FiveMinutes);
        var vga = VideoStorage.RequiredFreeBytes(video, new CameraResolution(640, 480), FiveMinutes);
        video.Quality = VideoQuality.Low;
        var low = VideoStorage.RequiredFreeBytes(video, new CameraResolution(1920, 1080), FiveMinutes);

        Assert.True(hd > vga);
        Assert.True(hd > low);
        Assert.True(low > 200L * 1024 * 1024); // Reserve ist immer dabei
        Assert.Equal(
            VideoStorage.RequiredFreeBytes(new VideoSettings { Quality = VideoQuality.High }, new CameraResolution(1920, 1080), FiveMinutes),
            VideoStorage.RequiredFreeBytes(new VideoSettings { Quality = VideoQuality.High }, null, FiveMinutes)); // ohne Auflösung 1080p
    }

    [Theory]
    [InlineData(null, 100, true)]
    [InlineData(99L, 100, false)]
    [InlineData(100L, 100, true)]
    [InlineData(101L, 100, true)]
    public void HasEnoughSpace_Compares(long? free, long required, bool expected) =>
        Assert.Equal(expected, VideoStorage.HasEnoughSpace(free, required));

    [Theory]
    [InlineData(null, "Fotobox+_Video")]
    [InlineData("", "Fotobox+_Video")]
    [InlineData("   ", "Fotobox+_Video")]
    [InlineData("///", "Fotobox+_Video")]
    [InlineData("Hochzeit", "Hochzeit")]
    [InlineData("a/b:c", "abc")]
    public void ResolvePrefix_FallsBackToVideoDefault(string? input, string expected) =>
        Assert.Equal(expected, VideoStorage.ResolvePrefix(input));

    [Fact]
    public async Task CommitAsync_RenamesTempFile_WithInvariantName()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
        try
        {
            var storage = Storage();
            var target = await storage.PrepareAsync(Settings(dateSub: true), null, FiveMinutes, Stamp);
            WriteContent(target.TempPath);

            var path = await storage.CommitAsync(target);

            Assert.Equal(Path.Combine(_root, "2025-06-14", "Party_Video_20250614_153045.mp4"), path);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(target.TempPath));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task CommitAsync_Collision_AppendsNumericSuffix()
    {
        var storage = Storage();
        var paths = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var target = await storage.PrepareAsync(Settings(), null, FiveMinutes, Stamp);
            WriteContent(target.TempPath, 10 + i);
            paths.Add(await storage.CommitAsync(target));
        }

        Assert.Equal(
            [
                Path.Combine(_root, "Party_Video_20250614_153045.mp4"),
                Path.Combine(_root, "Party_Video_20250614_153045_2.mp4"),
                Path.Combine(_root, "Party_Video_20250614_153045_3.mp4"),
            ],
            paths);
        Assert.Equal(10, new FileInfo(paths[0]).Length);
        Assert.Equal(12, new FileInfo(paths[2]).Length);
    }

    [Fact]
    public async Task CommitAsync_ParallelCommits_GetDistinctNames()
    {
        var storage = Storage();
        var targets = new List<VideoTarget>();
        for (var i = 0; i < 8; i++)
        {
            var t = await storage.PrepareAsync(Settings(), null, FiveMinutes, Stamp);
            WriteContent(t.TempPath);
            targets.Add(t);
        }

        var paths = await Task.WhenAll(targets.Select(t => storage.CommitAsync(t)));

        Assert.Equal(8, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public async Task CommitAsync_MissingTempFile_ThrowsFileNotFound()
    {
        var storage = Storage();
        var target = await storage.PrepareAsync(Settings(), null, FiveMinutes, Stamp);

        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.CommitAsync(target));
    }

    [Fact]
    public async Task CommitAsync_EmptyTempFile_ThrowsIOException_AndKeepsFileForDiscard()
    {
        var storage = Storage();
        var target = await storage.PrepareAsync(Settings(), null, FiveMinutes, Stamp);
        File.WriteAllBytes(target.TempPath, []);

        var ex = await Assert.ThrowsAsync<IOException>(() => storage.CommitAsync(target));

        Assert.IsNotType<FileNotFoundException>(ex);
        Assert.True(File.Exists(target.TempPath));
        storage.Discard(target);
        Assert.False(File.Exists(target.TempPath));
    }

    [Fact]
    public async Task Discard_DeletesTempFile_AndNeverThrows()
    {
        var storage = Storage();
        var target = await storage.PrepareAsync(Settings(), null, FiveMinutes, Stamp);
        WriteContent(target.TempPath);

        storage.Discard(target);
        storage.Discard(target); // schon weg
        storage.Discard(new VideoTarget(Path.Combine(_root, "<ungültig>"), "?:\\<>|", "x", Stamp));

        Assert.False(File.Exists(target.TempPath));
    }

    [Fact]
    public async Task PrepareAsync_RelativeFolder_FallsBackToDefaultFolder_AndCreatesNothingRelative()
    {
        var relative = "FotoboxRelativeVideos_" + Guid.NewGuid().ToString("N");

        var target = await Storage().PrepareAsync(new VideoSettings { Folder = relative, DateSubfolder = false }, null, FiveMinutes, Stamp);

        Assert.Equal(_fallback, target.Folder);
        Assert.True(Path.IsPathFullyQualified(target.TempPath));
        Assert.False(Directory.Exists(Path.GetFullPath(relative)));
    }

    [Fact]
    public async Task PrepareAsync_RelativeFolderAndRelativeDefault_ThrowsIOException_WithGermanMessage()
    {
        var relative = "FotoboxRelativeVideos_" + Guid.NewGuid().ToString("N");
        var storage = new VideoStorage(relative, _ => null);

        var ex = await Assert.ThrowsAsync<IOException>(() =>
            storage.PrepareAsync(new VideoSettings { Folder = relative + "_x" }, null, FiveMinutes, Stamp));

        Assert.Contains("vollständiger Pfad", ex.Message);
        Assert.False(Directory.Exists(Path.GetFullPath(relative)));
        Assert.False(Directory.Exists(Path.GetFullPath(relative + "_x")));
    }

    [Fact]
    public async Task PrepareAsync_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Storage().PrepareAsync(Settings(), null, FiveMinutes, Stamp, cts.Token));
    }
}
