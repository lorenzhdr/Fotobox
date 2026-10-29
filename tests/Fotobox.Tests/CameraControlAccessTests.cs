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

    // ---------- Weg-Auswahl (Haupt- und Ersatzweg) ----------

    [Fact]
    public void FallbackTarget_UsesPrimaryWhenSupported_ElseFallback()
    {
        var primary = new FakeTarget();
        primary.Caps[CameraControlKind.Brightness] = new ControlCaps(true, 0, 100, 1, 50, false);
        primary.Values[CameraControlKind.Brightness] = 10;
        var fallback = new FakeTarget();
        fallback.Caps[CameraControlKind.Brightness] = new ControlCaps(true, 0, 5, 1, 2, false);
        fallback.Caps[CameraControlKind.Zoom] = new ControlCaps(true, 1, 4, 0.125, 1, false);
        fallback.Values[CameraControlKind.Zoom] = 2;
        var target = new FallbackControlTarget(primary, fallback);

        Assert.Equal(100, target.GetCaps(CameraControlKind.Brightness).Max);   // Hauptweg gewinnt
        Assert.Equal(4, target.GetCaps(CameraControlKind.Zoom).Max);           // Ersatzweg
        Assert.False(target.GetCaps(CameraControlKind.Focus).Supported);       // keiner

        Assert.True(target.TryGetValue(CameraControlKind.Zoom, out var zoom));
        Assert.Equal(2, zoom);
        Assert.True(target.TrySetValue(CameraControlKind.Zoom, 3));
        Assert.True(target.TrySetValue(CameraControlKind.Brightness, 20));
        Assert.Equal(["value:Zoom=3"], fallback.Calls);
        Assert.Equal(["value:Brightness=20"], primary.Calls);
    }

    [Fact]
    public void FallbackTarget_PowerlineOnlyFromPrimary()
    {
        var primary = new FakeTarget { PowerlineValue = CameraPowerline.Hz50 };
        var fallback = new FakeTarget { PowerlineValue = CameraPowerline.Hz60 };
        var target = new FallbackControlTarget(primary, fallback);

        Assert.True(target.TryGetPowerline(out var value));
        Assert.Equal(CameraPowerline.Hz50, value);
        Assert.True(target.TrySetPowerline(CameraPowerline.Auto));
        Assert.Empty(fallback.Calls);
    }

    [Fact]
    public void Read_AndApply_WorkThroughFallbackTarget()
    {
        var primary = new FakeTarget();
        var fallback = new FakeTarget();
        fallback.Caps[CameraControlKind.ExposureCompensation] = new ControlCaps(true, -2, 2, 0.25, 0, false);
        fallback.Values[CameraControlKind.ExposureCompensation] = 0.5;
        var target = new FallbackControlTarget(primary, fallback);

        var snap = CameraControlAccess.Read(target, "dev", writable: true);
        var info = snap.Controls.Single(c => c.Kind == CameraControlKind.ExposureCompensation);
        Assert.True(info.Supported);
        Assert.Equal(0.5, info.Value);

        CameraControlAccess.Apply(target, [new CameraControlValue { Kind = CameraControlKind.ExposureCompensation, Value = 1.1 }], CameraPowerline.Unchanged, live: true);
        Assert.Equal(["value:ExposureCompensation=1"], fallback.Calls);
    }

    // ---------- Abbildungen der erweiterten Regler ----------

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.0001)]
    [InlineData(0.033333)]
    [InlineData(2.5)]
    public void ExposureSeconds_RoundTripWithTimeSpan(double seconds)
    {
        var span = CameraControlMapping.ToTimeSpan(seconds);
        Assert.Equal(seconds, CameraControlMapping.ToSeconds(span), 6);
        Assert.Equal(TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)), span);
    }

    [Fact]
    public void ToTimeSpan_HandlesExtremeValues()
    {
        Assert.Equal(TimeSpan.Zero, CameraControlMapping.ToTimeSpan(double.NaN));
        Assert.Equal(TimeSpan.MaxValue, CameraControlMapping.ToTimeSpan(double.PositiveInfinity));
        Assert.Equal(TimeSpan.MinValue, CameraControlMapping.ToTimeSpan(double.NegativeInfinity));
        Assert.Equal(TimeSpan.MaxValue, CameraControlMapping.ToTimeSpan(1e30));
    }

    [Fact]
    public void WhiteBalancePreset_MapsToAuto()
    {
        Assert.True(CameraControlMapping.IsAutoWhiteBalance(Windows.Media.Devices.ColorTemperaturePreset.Auto));
        Assert.False(CameraControlMapping.IsAutoWhiteBalance(Windows.Media.Devices.ColorTemperaturePreset.Manual));
        Assert.False(CameraControlMapping.IsAutoWhiteBalance(Windows.Media.Devices.ColorTemperaturePreset.Fluorescent));
        Assert.Equal(Windows.Media.Devices.ColorTemperaturePreset.Auto, CameraControlMapping.WhiteBalancePresetFor(true));
        Assert.Equal(Windows.Media.Devices.ColorTemperaturePreset.Manual, CameraControlMapping.WhiteBalancePresetFor(false));
    }

    [Fact]
    public void FocusSupport_ReadsContinuousAndManual()
    {
        Assert.Equal((false, false), CameraControlMapping.FocusSupport(null));
        Assert.Equal((false, false), CameraControlMapping.FocusSupport([Windows.Media.Devices.FocusMode.Single]));
        Assert.Equal((true, false), CameraControlMapping.FocusSupport([Windows.Media.Devices.FocusMode.Continuous]));
        Assert.Equal((false, true), CameraControlMapping.FocusSupport([Windows.Media.Devices.FocusMode.Manual]));
        Assert.Equal((true, true), CameraControlMapping.FocusSupport(
            [Windows.Media.Devices.FocusMode.Single, Windows.Media.Devices.FocusMode.Continuous, Windows.Media.Devices.FocusMode.Manual]));
    }

    [Fact]
    public void PickZoomMode_PrefersDirect_ThenAuto_ThenFirst()
    {
        var m = Windows.Media.Devices.ZoomTransitionMode.Auto;
        Assert.Null(CameraControlMapping.PickZoomMode(null));
        Assert.Null(CameraControlMapping.PickZoomMode([]));
        Assert.Equal(Windows.Media.Devices.ZoomTransitionMode.Direct, CameraControlMapping.PickZoomMode(
            [Windows.Media.Devices.ZoomTransitionMode.Smooth, Windows.Media.Devices.ZoomTransitionMode.Direct, m]));
        Assert.Equal(m, CameraControlMapping.PickZoomMode([Windows.Media.Devices.ZoomTransitionMode.Smooth, m]));
        Assert.Equal(Windows.Media.Devices.ZoomTransitionMode.Smooth, CameraControlMapping.PickZoomMode([Windows.Media.Devices.ZoomTransitionMode.Smooth]));
    }

    [Fact]
    public void Defaults_MidAndZeroClamped()
    {
        Assert.Equal(3000, CameraControlMapping.Mid(2000, 4000));
        Assert.Equal(0, CameraControlMapping.ZeroClamped(-2, 2));
        Assert.Equal(1, CameraControlMapping.ZeroClamped(1, 3));
        Assert.Equal(-1, CameraControlMapping.ZeroClamped(-3, -1));
    }

    // ---------- Nur Abweichendes schreiben (ControlStateCache) ----------

    private static CameraControlValue Val(CameraControlKind kind, double? value = null, bool? auto = null) =>
        new() { Kind = kind, Value = value, Auto = auto };

    [Fact]
    public void Cache_SecondApplyOfTheSameState_WritesNothing()
    {
        var t = Target();
        var cache = new ControlStateCache();
        CameraControlValue[] all =
        [
            Val(CameraControlKind.Brightness, 70),
            Val(CameraControlKind.Exposure, -3, auto: false),
        ];

        CameraControlAccess.Apply(t, all, CameraPowerline.Hz50, live: true, cache);
        Assert.Equal(["value:Brightness=70", "auto:Exposure=False", "value:Exposure=-3", "powerline=Hz50"], t.Calls);

        t.Calls.Clear();
        CameraControlAccess.Apply(t, all, CameraPowerline.Hz50, live: true, cache);
        Assert.Empty(t.Calls);
    }

    [Fact]
    public void Cache_OnlyTheChangedControlIsWritten()
    {
        var t = Target();
        var cache = new ControlStateCache();
        CameraControlAccess.Apply(t, [Val(CameraControlKind.Brightness, 70), Val(CameraControlKind.Exposure, -3, auto: false)],
            CameraPowerline.Hz50, live: true, cache);
        t.Calls.Clear();

        CameraControlAccess.Apply(t, [Val(CameraControlKind.Brightness, 75), Val(CameraControlKind.Exposure, -3, auto: false)],
            CameraPowerline.Hz50, live: true, cache);

        Assert.Equal(["value:Brightness=75"], t.Calls);
    }

    [Fact]
    public void Cache_ValueWithinHalfAStep_CountsAsEqual()
    {
        var t = Target(); // Helligkeit: Schritt 5
        var cache = new ControlStateCache();
        cache.Set(CameraControlKind.Brightness, 70, null);

        CameraControlAccess.Apply(t, [Val(CameraControlKind.Brightness, 72.4)], CameraPowerline.Unchanged, live: true, cache);
        Assert.Empty(t.Calls); // rastet auf 70 ein

        CameraControlAccess.Apply(t, [Val(CameraControlKind.Brightness, 73)], CameraPowerline.Unchanged, live: true, cache);
        Assert.Equal(["value:Brightness=75"], t.Calls);
    }

    [Fact]
    public void Cache_AutoOn_IsNotRepeated_AndSwitchingOffWritesAgain()
    {
        var t = Target();
        var cache = new ControlStateCache();
        CameraControlAccess.Apply(t, [Val(CameraControlKind.Exposure, -3, auto: true)], CameraPowerline.Unchanged, live: true, cache);
        CameraControlAccess.Apply(t, [Val(CameraControlKind.Exposure, -3, auto: true)], CameraPowerline.Unchanged, live: true, cache);
        Assert.Equal(["auto:Exposure=True"], t.Calls);

        t.Calls.Clear();
        CameraControlAccess.Apply(t, [Val(CameraControlKind.Exposure, -3, auto: false)], CameraPowerline.Unchanged, live: true, cache);
        Assert.Equal(["auto:Exposure=False", "value:Exposure=-3"], t.Calls);
    }

    [Fact]
    public void Cache_SeedFromRead_SkipsWhatTheDriverAlreadyHas()
    {
        var t = Target();
        var cache = new ControlStateCache();
        cache.Seed(CameraControlAccess.Read(t, "cam", writable: true));

        // Treiberstand: Helligkeit 50, Belichtung -5 in Automatik, Flimmerschutz 60 Hz.
        CameraControlAccess.Apply(t, [Val(CameraControlKind.Brightness, 50), Val(CameraControlKind.Exposure, null, auto: true)],
            CameraPowerline.Hz60, live: true, cache);

        Assert.Empty(t.Calls);
    }

    [Fact]
    public void Cache_AfterARejectedWrite_TheEntryIsForgotten_AndRetried()
    {
        var t = Target();
        var cache = new ControlStateCache();
        t.RejectValue.Add(CameraControlKind.Brightness);
        CameraControlAccess.Apply(t, [Val(CameraControlKind.Brightness, 70)], CameraPowerline.Unchanged, live: true, cache);

        t.RejectValue.Clear();
        CameraControlAccess.Apply(t, [Val(CameraControlKind.Brightness, 70)], CameraPowerline.Unchanged, live: true, cache);

        Assert.Equal(["value:Brightness=70", "value:Brightness=70"], t.Calls);
        Assert.Equal(70, t.Values[CameraControlKind.Brightness]);
    }

    [Fact]
    public void Cache_RejectedPowerline_IsRetried()
    {
        var t = Target();
        var cache = new ControlStateCache();
        t.PowerlineRejects = true;
        CameraControlAccess.Apply(t, [], CameraPowerline.Hz50, live: true, cache);
        t.PowerlineRejects = false;
        CameraControlAccess.Apply(t, [], CameraPowerline.Hz50, live: true, cache);
        CameraControlAccess.Apply(t, [], CameraPowerline.Hz50, live: true, cache);

        Assert.Equal(["powerline=Hz50", "powerline=Hz50"], t.Calls);
    }

    [Fact]
    public void Cache_ValueAfterAuto_IsWrittenEvenIfEqualToTheOldValue()
    {
        var t = Target();
        var cache = new ControlStateCache();
        cache.Set(CameraControlKind.Exposure, -3, false);
        CameraControlAccess.Apply(t, [Val(CameraControlKind.Exposure, null, auto: true)], CameraPowerline.Unchanged, live: true, cache);
        t.Calls.Clear();

        CameraControlAccess.Apply(t, [Val(CameraControlKind.Exposure, -3, auto: false)], CameraPowerline.Unchanged, live: true, cache);

        Assert.Equal(["auto:Exposure=False", "value:Exposure=-3"], t.Calls);
    }

    [Fact]
    public void Cache_CountPending_ReflectsWhatWouldBeWritten()
    {
        var cache = new ControlStateCache();
        cache.Set(CameraControlKind.Brightness, 70, null);
        cache.Set(CameraControlKind.Exposure, null, true);
        cache.Powerline = CameraPowerline.Hz50;

        Assert.Equal(0, cache.CountPending(
            [Val(CameraControlKind.Brightness, 70), Val(CameraControlKind.Exposure, -3, auto: true)], CameraPowerline.Hz50));
        Assert.Equal(1, cache.CountPending([Val(CameraControlKind.Brightness, 71)], CameraPowerline.Unchanged));
        Assert.Equal(2, cache.CountPending([Val(CameraControlKind.Contrast, 5)], CameraPowerline.Hz60));
        Assert.Equal(0, cache.CountPending([], CameraPowerline.Unchanged));
    }

    [Fact]
    public void ControlsTimeout_GrowsWithTheNumberOfWrites_AndIsCapped()
    {
        Assert.Equal(TimeSpan.FromSeconds(5 + 6), WinRtCameraService.ControlsTimeout(1));
        Assert.True(WinRtCameraService.ControlsTimeout(3) > WinRtCameraService.ControlsTimeout(1));
        Assert.Equal(TimeSpan.FromSeconds(30), WinRtCameraService.ControlsTimeout(50));
    }

    [Fact]
    public async Task CombinePending_KeepsOnlyRunningTasks()
    {
        var done = Task.CompletedTask;
        var gate1 = new TaskCompletionSource();
        var gate2 = new TaskCompletionSource();

        Assert.Null(WinRtCameraService.CombinePending(null, done));
        Assert.Null(WinRtCameraService.CombinePending(done, null));
        Assert.Same(gate1.Task, WinRtCameraService.CombinePending(null, gate1.Task));
        Assert.Same(gate1.Task, WinRtCameraService.CombinePending(gate1.Task, done));

        var both = WinRtCameraService.CombinePending(gate1.Task, gate2.Task)!;
        gate1.SetResult();
        Assert.False(both.IsCompleted);
        gate2.SetResult();
        await both.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ValuesFor_DeviceIdsDifferingOnlyInCase_AreTheSameDevice()
    {
        IReadOnlyList<CameraControlValue> saved = [Val(CameraControlKind.Zoom, 2)];
        Assert.Single(CameraControlAccess.ValuesFor(@"\\?\USB#VID_1234", saved, @"\\?\usb#vid_1234"));
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
