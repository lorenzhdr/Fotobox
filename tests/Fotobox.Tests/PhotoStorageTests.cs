using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Services.Storage;
using Xunit;

namespace Fotobox.Tests;

public sealed class PhotoStorageTests : IDisposable
{
    private static readonly DateTime Stamp = new(2025, 6, 14, 15, 30, 45);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FotoboxTests_" + Guid.NewGuid().ToString("N"));
    private readonly PhotoStorage _storage = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    private StorageSettings Settings(bool dateSub = false, bool originals = true) => new()
    {
        PhotoFolder = _root,
        DateSubfolder = dateSub,
        SaveOriginals = originals,
        FilePrefix = "Party",
        JpegQuality = 90
    };

    private static BitmapSource MakeImage(int w, int h, byte seed = 100)
    {
        var stride = w * 4;
        var pixels = new byte[stride * h];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = seed;
            pixels[i + 1] = (byte)(i / 4 % 256);
            pixels[i + 2] = 200;
            pixels[i + 3] = 255;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    private static (int W, int H) ReadSize(string path)
    {
        using var fs = File.OpenRead(path);
        var decoder = new JpegBitmapDecoder(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var f = decoder.Frames[0];
        return (f.PixelWidth, f.PixelHeight);
    }

    [Fact]
    public void ResolveTargetFolder_Defaults_ToPicturesFotobox()
    {
        var folder = _storage.ResolveTargetFolder(new StorageSettings { DateSubfolder = false }, Stamp);

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Fotobox"), folder);
    }

    [Fact]
    public void ResolveTargetFolder_AddsDateSubfolder()
    {
        var folder = _storage.ResolveTargetFolder(Settings(dateSub: true), Stamp);

        Assert.Equal(Path.Combine(_root, "2025-06-14"), folder);
    }

    [Fact]
    public async Task SaveAsync_WritesValidJpegs_WithExpectedNames()
    {
        var layout = MakeImage(120, 80);
        var originals = new[] { MakeImage(64, 48), MakeImage(32, 24) };

        var result = await _storage.SaveAsync(layout, originals, Settings(dateSub: true), Stamp);

        var folder = Path.Combine(_root, "2025-06-14");
        Assert.Equal(Path.Combine(folder, "Party_20250614_153045.jpg"), result.LayoutPath);
        Assert.Equal(
            [Path.Combine(folder, "Party_20250614_153045_orig1.jpg"), Path.Combine(folder, "Party_20250614_153045_orig2.jpg")],
            result.OriginalPaths);
        Assert.Equal((120, 80), ReadSize(result.LayoutPath));
        Assert.Equal((64, 48), ReadSize(result.OriginalPaths[0]));
        Assert.Equal((32, 24), ReadSize(result.OriginalPaths[1]));
        Assert.Equal(3, Directory.GetFiles(folder).Length); // keine Temp-Reste
    }

    [Fact]
    public async Task SaveAsync_SaveOriginalsFalse_WritesOnlyLayout()
    {
        var result = await _storage.SaveAsync(MakeImage(50, 50), [MakeImage(10, 10)], Settings(originals: false), Stamp);

        Assert.Empty(result.OriginalPaths);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task SaveAsync_Collision_AppendsNumericSuffix()
    {
        var s = Settings();
        var first = await _storage.SaveAsync(MakeImage(20, 20), [MakeImage(10, 10)], s, Stamp);
        var second = await _storage.SaveAsync(MakeImage(20, 20), [MakeImage(10, 10)], s, Stamp);
        var third = await _storage.SaveAsync(MakeImage(20, 20), [MakeImage(10, 10)], s, Stamp);

        Assert.Equal("Party_20250614_153045.jpg", Path.GetFileName(first.LayoutPath));
        Assert.Equal("Party_20250614_153045_2.jpg", Path.GetFileName(second.LayoutPath));
        Assert.Equal("Party_20250614_153045_2_orig1.jpg", Path.GetFileName(second.OriginalPaths[0]));
        Assert.Equal("Party_20250614_153045_3.jpg", Path.GetFileName(third.LayoutPath));
        Assert.Equal(6, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public async Task SaveAsync_ParallelSaves_GetDistinctNames()
    {
        var s = Settings();
        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => _storage.SaveAsync(MakeImage(20, 20), [], s, Stamp)));

        Assert.Equal(6, results.Select(r => r.LayoutPath).Distinct().Count());
        Assert.Equal(6, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public async Task SaveAsync_SanitizesPrefix()
    {
        var s = Settings();
        s.FilePrefix = "a/b";

        var result = await _storage.SaveAsync(MakeImage(20, 20), [], s, Stamp);

        Assert.Equal("ab_20250614_153045.jpg", Path.GetFileName(result.LayoutPath));
    }

    [Fact]
    public async Task SaveAsync_HigherQuality_ProducesLargerFile()
    {
        var img = MakeImage(200, 200);
        var low = Settings();
        low.JpegQuality = 50;
        var high = Settings();
        high.JpegQuality = 100;

        var a = await _storage.SaveAsync(img, [], low, Stamp);
        var b = await _storage.SaveAsync(img, [], high, Stamp.AddSeconds(1));

        Assert.True(new FileInfo(b.LayoutPath).Length > new FileInfo(a.LayoutPath).Length);
    }

    [Fact]
    public async Task SaveAsync_UnwritableFolder_ThrowsGermanIOException()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blockiert");
        File.WriteAllText(blocker, "ich bin eine Datei, kein Ordner");
        var s = Settings();
        s.PhotoFolder = Path.Combine(blocker, "unterordner");

        var ex = await Assert.ThrowsAsync<IOException>(() => _storage.SaveAsync(MakeImage(20, 20), [], s, Stamp));

        Assert.StartsWith("Fotos können nicht gespeichert werden:", ex.Message);
    }

    [Fact]
    public async Task SaveAsync_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _storage.SaveAsync(MakeImage(20, 20), [], Settings(), Stamp, cts.Token));
    }
}
