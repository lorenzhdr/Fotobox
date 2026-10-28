using System.IO;
using Fotobox.Core;
using Xunit;

namespace Fotobox.Tests;

/// <summary>Aufräumen alter Tagesdateien (eigener Temp-Ordner, ohne <c>AppLog.Initialize</c>, damit der statische Zustand unberührt bleibt).</summary>
public sealed class AppLogRetentionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 3, 31, 14, 5, 9);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fotobox-logtest-" + Guid.NewGuid().ToString("N"));

    public AppLogRetentionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Aufräumen im Temp-Ordner ist best effort.
        }
    }

    private string Create(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private string CreateDayLog(int daysAgo) => Create(AppLog.LogFileName(Now.AddDays(-daysAgo)));

    [Fact]
    public void RetentionDays_Is30() => Assert.Equal(30, AppLog.RetentionDays);

    [Fact]
    public void DeletesFilesOlderThanKeepDays()
    {
        var old = CreateDayLog(31);

        var deleted = AppLog.DeleteOldLogs(_dir, Now, 30);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void KeepsFileExactlyKeepDaysOld()
    {
        var edge = CreateDayLog(30);

        var deleted = AppLog.DeleteOldLogs(_dir, Now, 30);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(edge));
    }

    [Fact]
    public void KeepsTodaysFile()
    {
        var today = CreateDayLog(0);

        var deleted = AppLog.DeleteOldLogs(_dir, Now, 30);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(today));
    }

    [Fact]
    public void KeepsForeignFiles()
    {
        var foreign = new[]
        {
            Create("notes.txt"),
            Create("fotobox-kaputt.log"),
            Create("fotobox-20250101.log.bak"),
            Create("fotobox-2025.log"),
        };

        var deleted = AppLog.DeleteOldLogs(_dir, Now, 30);

        Assert.Equal(0, deleted);
        Assert.All(foreign, f => Assert.True(File.Exists(f), f));
    }

    [Fact]
    public void DeletesOnlyOldAmongMixedFiles()
    {
        var old1 = CreateDayLog(31);
        var old2 = CreateDayLog(400);
        var edge = CreateDayLog(30);
        var recent = CreateDayLog(1);
        var foreign = Create("notes.txt");

        var deleted = AppLog.DeleteOldLogs(_dir, Now, 30);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(old1));
        Assert.False(File.Exists(old2));
        Assert.True(File.Exists(edge));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void DoesNotTouchSubdirectories()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        var nested = Path.Combine(sub, AppLog.LogFileName(Now.AddDays(-100)));
        File.WriteAllText(nested, "x");

        var deleted = AppLog.DeleteOldLogs(_dir, Now, 30);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void MissingDirectory_ReturnsZeroWithoutException()
    {
        var missing = Path.Combine(_dir, "gibt-es-nicht");

        var deleted = AppLog.DeleteOldLogs(missing, Now, 30);

        Assert.Equal(0, deleted);
    }
}
