using Fotobox.Core;
using Fotobox.Services.Camera;
using Windows.Media.Capture;
using Xunit;

namespace Fotobox.Tests;

public sealed class LatestOnlyWorkerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Submit_RunsWork_AndCompletesTask()
    {
        var seen = new List<int>();
        var worker = new LatestOnlyWorker<int>(i => { lock (seen) seen.Add(i); return Task.CompletedTask; });

        await worker.SubmitAsync(1).WaitAsync(Wait);

        Assert.Equal([1], seen);
    }

    [Fact]
    public async Task Burst_WhileBusy_OnlyLatestRunsNext_AndAllTasksEnd()
    {
        var seen = new List<int>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new LatestOnlyWorker<int>(async i =>
        {
            lock (seen) seen.Add(i);
            if (i == 1)
            {
                firstStarted.SetResult();
                await release.Task;
            }
        });

        var t1 = worker.SubmitAsync(1);
        await firstStarted.Task.WaitAsync(Wait);
        var t2 = worker.SubmitAsync(2);
        var t3 = worker.SubmitAsync(3);
        var t4 = worker.SubmitAsync(4);

        Assert.False(t4.IsCompleted);
        release.SetResult();

        await Task.WhenAll(t1, t2, t3, t4).WaitAsync(Wait);
        lock (seen)
            Assert.Equal([1, 4], seen);
    }

    [Fact]
    public async Task SupersededTask_EndsOnlyAfterNewerStateIsApplied()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<int>();
        var worker = new LatestOnlyWorker<int>(async i =>
        {
            if (i == 1)
            {
                firstStarted.SetResult();
                await release.Task;
            }
            lock (applied) applied.Add(i);
        });

        var t1 = worker.SubmitAsync(1);
        await firstStarted.Task.WaitAsync(Wait);
        var t2 = worker.SubmitAsync(2);
        var t3 = worker.SubmitAsync(3);
        release.SetResult();

        await t1.WaitAsync(Wait);
        await Task.WhenAll(t2, t3).WaitAsync(Wait);
        lock (applied)
            Assert.Equal([1, 3], applied);
    }

    [Fact]
    public async Task NeverRunsTwoJobsAtOnce()
    {
        var running = 0;
        var maxRunning = 0;
        var worker = new LatestOnlyWorker<int>(async _ =>
        {
            var now = Interlocked.Increment(ref running);
            lock (MaxLock) maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(5);
            Interlocked.Decrement(ref running);
        });

        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() => worker.SubmitAsync(i))).ToList();
        await Task.WhenAll(tasks).WaitAsync(Wait);

        Assert.Equal(1, maxRunning);
    }

    private static readonly object MaxLock = new();

    [Fact]
    public async Task FailingWork_IsReported_DoesNotThrow_AndWorkerContinues()
    {
        var errors = new List<Exception>();
        var seen = new List<int>();
        var worker = new LatestOnlyWorker<int>(
            i =>
            {
                if (i == 1)
                    throw new InvalidOperationException("kaputt");
                lock (seen) seen.Add(i);
                return Task.CompletedTask;
            },
            ex => { lock (errors) errors.Add(ex); });

        await worker.SubmitAsync(1).WaitAsync(Wait);
        await worker.SubmitAsync(2).WaitAsync(Wait);

        lock (errors)
            Assert.IsType<InvalidOperationException>(Assert.Single(errors));
        lock (seen)
            Assert.Equal([2], seen);
    }

    [Fact]
    public async Task Close_DiscardsPending_CompletesTasks_AndIgnoresLaterSubmits()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<int>();
        var worker = new LatestOnlyWorker<int>(async i =>
        {
            lock (seen) seen.Add(i);
            if (i == 1)
            {
                firstStarted.SetResult();
                await release.Task;
            }
        });

        var t1 = worker.SubmitAsync(1);
        await firstStarted.Task.WaitAsync(Wait);
        var t2 = worker.SubmitAsync(2);

        worker.Close(); // blockiert nicht, obwohl Auftrag 1 noch läuft
        await Task.WhenAll(t1, t2).WaitAsync(Wait);
        release.SetResult();

        await worker.SubmitAsync(3).WaitAsync(Wait);
        await Task.Delay(50);
        lock (seen)
            Assert.Equal([1], seen);
    }
}

public sealed class CameraControlAccessTests
{
    private sealed class FakeTarget : ICameraControlTarget
    {
        public Dictionary<CameraControlKind, ControlCaps> Caps { get; } = [];
        public Dictionary<CameraControlKind, double> Values { get; } = [];
        public Dictionary<CameraControlKind, bool> Autos { get; } = [];
        public HashSet<CameraControlKind> Throwing { get; } = [];
        public HashSet<CameraControlKind> RejectValue { get; } = [];
        public List<string> Calls { get; } = [];
        public bool PowerlineSupported { get; set; } = true;
        public bool PowerlineRejects { get; set; }
        public CameraPowerline PowerlineValue { get; set; } = CameraPowerline.Hz60;

        public ControlCaps GetCaps(CameraControlKind kind)
        {
            if (Throwing.Contains(kind))
                throw new InvalidOperationException("Treiber");
            return Caps.TryGetValue(kind, out var c) ? c : default;
        }

        public bool TryGetValue(CameraControlKind kind, out double value) => Values.TryGetValue(kind, out value);
        public bool TryGetAuto(CameraControlKind kind, out bool value) => Autos.TryGetValue(kind, out value);

        public bool TrySetValue(CameraControlKind kind, double value)
        {
            Calls.Add($"value:{kind}={value}");
            if (RejectValue.Contains(kind))
                return false;
            Values[kind] = value;
            return true;
        }

        public bool TrySetAuto(CameraControlKind kind, bool value)
        {
            Calls.Add($"auto:{kind}={value}");
            Autos[kind] = value;
            return true;
        }

        public bool TryGetPowerline(out CameraPowerline value)
        {
            value = PowerlineValue;
            return PowerlineSupported;
        }

        public bool TrySetPowerline(CameraPowerline value)
        {
            Calls.Add($"powerline={value}");
            if (PowerlineRejects)
                return false;
            PowerlineValue = value;
            return true;
        }
    }

    private static FakeTarget Target()
    {
        var t = new FakeTarget();
        t.Caps[CameraControlKind.Brightness] = new ControlCaps(true, 0, 100, 5, 50, false);
        t.Caps[CameraControlKind.Exposure] = new ControlCaps(true, -10, 0, 1, -5, true);
        t.Values[CameraControlKind.Brightness] = 50;
        t.Values[CameraControlKind.Exposure] = -5;
        t.Autos[CameraControlKind.Exposure] = true;
        return t;
    }

    [Fact]
    public void Apply_ClampsAndSnapsValue()
    {
        var t = Target();
        CameraControlAccess.Apply(t, [new CameraControlValue { Kind = CameraControlKind.Brightness, Value = 52.4 }], CameraPowerline.Unchanged, live: false);

        Assert.Equal(["value:Brightness=50"], t.Calls);

        t.Calls.Clear();
        CameraControlAccess.Apply(t, [new CameraControlValue { Kind = CameraControlKind.Brightness, Value = 500 }], CameraPowerline.Unchanged, live: false);
        Assert.Equal(["value:Brightness=100"], t.Calls);
    }

    [Fact]
    public void Apply_AutoTrue_SetsAutoOnly()
    {
        var t = Target();
        CameraControlAccess.Apply(t, [new CameraControlValue { Kind = CameraControlKind.Exposure, Value = -3, Auto = true }], CameraPowerline.Unchanged, live: false);

        Assert.Equal(["auto:Exposure=True"], t.Calls);
    }

    [Fact]
    public void Apply_AutoFalse_SwitchesOffThenSetsValue()
    {
        var t = Target();
        CameraControlAccess.Apply(t, [new CameraControlValue { Kind = CameraControlKind.Exposure, Value = -3, Auto = false }], CameraPowerline.Unchanged, live: false);

        Assert.Equal(["auto:Exposure=False", "value:Exposure=-3"], t.Calls);
    }

    [Fact]
    public void Apply_AutoTrueWithoutAutoSupport_FallsBackToValue()
    {
        var t = Target();
        CameraControlAccess.Apply(t, [new CameraControlValue { Kind = CameraControlKind.Brightness, Value = 20, Auto = true }], CameraPowerline.Unchanged, live: false);

        Assert.Equal(["value:Brightness=20"], t.Calls);
    }

    [Fact]
    public void Apply_SkipsUnsupported_AndContinuesAfterRejectionOrException()
    {
        var t = Target();
        t.Caps[CameraControlKind.Zoom] = new ControlCaps(false, 0, 0, 0, 0, false);
        t.Caps[CameraControlKind.Contrast] = new ControlCaps(true, 0, 10, 1, 5, false);
        t.Throwing.Add(CameraControlKind.Hue);
        t.RejectValue.Add(CameraControlKind.Contrast);

        CameraControlAccess.Apply(t,
        [
            new CameraControlValue { Kind = CameraControlKind.Zoom, Value = 3 },
            new CameraControlValue { Kind = CameraControlKind.Hue, Value = 1 },
            new CameraControlValue { Kind = CameraControlKind.Contrast, Value = 7 },
            new CameraControlValue { Kind = CameraControlKind.Brightness, Value = 10 },
        ], CameraPowerline.Unchanged, live: true);

        Assert.Equal(["value:Contrast=7", "value:Brightness=10"], t.Calls);
    }

    [Fact]
    public void Apply_NullValueAndNullAuto_DoNothing()
    {
        var t = Target();
        CameraControlAccess.Apply(t, [new CameraControlValue { Kind = CameraControlKind.Brightness }], CameraPowerline.Unchanged, live: false);
        Assert.Empty(t.Calls);
    }

    [Fact]
    public void Apply_Powerline_SetOnlyWhenSupportedAndNotUnchanged()
    {
        var t = Target();
        CameraControlAccess.Apply(t, [], CameraPowerline.Unchanged, live: false);
        Assert.Empty(t.Calls);

        CameraControlAccess.Apply(t, [], CameraPowerline.Hz50, live: false);
        Assert.Equal(["powerline=Hz50"], t.Calls);

        t.Calls.Clear();
        t.PowerlineSupported = false;
        CameraControlAccess.Apply(t, [], CameraPowerline.Hz50, live: false);
        Assert.Empty(t.Calls);
    }

    [Fact]
    public void Apply_PowerlineRejected_DoesNotThrow()
    {
        var t = Target();
        t.PowerlineRejects = true;
        CameraControlAccess.Apply(t, [], CameraPowerline.Auto, live: true);
        Assert.Equal(["powerline=Auto"], t.Calls);
    }

    [Fact]
    public void Read_ReportsCapabilitiesAndState_AndMarksUnsupported()
    {
        var t = Target();
        t.Throwing.Add(CameraControlKind.Hue);

        var snap = CameraControlAccess.Read(t, "dev", writable: true);

        Assert.Equal("dev", snap.DeviceId);
        Assert.True(snap.Writable);
        Assert.Equal(Enum.GetValues<CameraControlKind>().Length, snap.Controls.Count);
        var b = snap.Controls.Single(c => c.Kind == CameraControlKind.Brightness);
        Assert.True(b.Supported);
        Assert.True(b.Writable);
        Assert.Equal((0d, 100d, 5d, 50d), (b.Min, b.Max, b.Step, b.Default));
        Assert.Equal(50, b.Value);
        Assert.Null(b.Auto); // Regler ohne Automatik
        var e = snap.Controls.Single(c => c.Kind == CameraControlKind.Exposure);
        Assert.True(e.AutoSupported);
        Assert.True(e.Auto);
        Assert.False(snap.Controls.Single(c => c.Kind == CameraControlKind.Zoom).Supported);
        Assert.False(snap.Controls.Single(c => c.Kind == CameraControlKind.Hue).Supported);
        Assert.True(snap.PowerlineSupported);
        Assert.Equal(CameraPowerline.Hz60, snap.Powerline);
    }

    [Fact]
    public void Read_NotWritable_MarksControlsReadOnly_AndPowerlineMissing()
    {
        var t = Target();
        t.PowerlineSupported = false;

        var snap = CameraControlAccess.Read(t, null, writable: false);

        Assert.False(snap.Writable);
        Assert.All(snap.Controls.Where(c => c.Supported), c => Assert.False(c.Writable));
        Assert.False(snap.PowerlineSupported);
        Assert.Null(snap.Powerline);
    }

    [Fact]
    public void ValuesFor_OnlyForTheSameDevice()
    {
        IReadOnlyList<CameraControlValue> saved = [new CameraControlValue { Kind = CameraControlKind.Zoom, Value = 2 }, null!];

        Assert.Single(CameraControlAccess.ValuesFor("A", saved, "A"));
        Assert.Single(CameraControlAccess.ValuesFor("a", saved, "A")); // Groß-/Kleinschreibung egal
        Assert.Empty(CameraControlAccess.ValuesFor("A", saved, "B"));
        Assert.Empty(CameraControlAccess.ValuesFor(null, saved, "A"));
        Assert.Empty(CameraControlAccess.ValuesFor("", saved, ""));
        Assert.Empty(CameraControlAccess.ValuesFor("A", saved, null));
        Assert.Empty(CameraControlAccess.ValuesFor("A", null, "A"));
    }

    [Theory]
    [InlineData(CameraPowerline.Off, PowerlineFrequency.Disabled)]
    [InlineData(CameraPowerline.Hz50, PowerlineFrequency.FiftyHertz)]
    [InlineData(CameraPowerline.Hz60, PowerlineFrequency.SixtyHertz)]
    [InlineData(CameraPowerline.Auto, PowerlineFrequency.Auto)]
    public void PowerlineMap_RoundTrips(CameraPowerline ours, PowerlineFrequency theirs)
    {
        Assert.Equal(theirs, CameraPowerlineMap.ToWinRt(ours));
        Assert.Equal(ours, CameraPowerlineMap.FromWinRt(theirs));
    }

    [Fact]
    public void PowerlineMap_UnchangedAndUnknown_MapToNull()
    {
        Assert.Null(CameraPowerlineMap.ToWinRt(CameraPowerline.Unchanged));
        Assert.Null(CameraPowerlineMap.ToWinRt((CameraPowerline)99));
        Assert.Null(CameraPowerlineMap.FromWinRt((PowerlineFrequency)99));
    }

    [Fact]
    public async Task WinRtService_WithoutSession_ControlsAreEmptyAndLiveIsNoOp()
    {
        await using var camera = new WinRtCameraService();

        Assert.Null(camera.ActiveDeviceId);
        Assert.Same(CameraControlsSnapshot.Empty, await camera.GetControlsAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await camera.ApplyControlsLiveAsync([new CameraControlValue { Kind = CameraControlKind.Zoom, Value = 1 }], CameraPowerline.Hz50)
            .WaitAsync(TimeSpan.FromSeconds(10));
        camera.ConfigureControls(new CameraSettings { ControlsDeviceId = "x", Powerline = CameraPowerline.Off });
    }
}
