using System.Diagnostics;
using Fotobox.Core;
using Fotobox.Services.Detection.Peace;
using Microsoft.ML.OnnxRuntime;

namespace Fotobox.Services.Detection.Acceleration;

/// <summary>Misst die vier Modelle auf einer Geräteliste (Ladezeit, mittlere Laufzeit). Wirft nie, auch nicht bei Abbruch.</summary>
public interface IAcceleratorBenchmark
{
    /// <summary>
    /// Misst jedes Gerät nacheinander mit allen Modellen. <paramref name="lowPriority"/> = niedrige Threadpriorität (Hintergrundmessung).
    /// <paramref name="progress"/> kommt von Hintergrundthreads. Abbruch liefert das bisher Gemessene mit <see cref="BenchmarkResult.Cancelled"/>.
    /// </summary>
    Task<BenchmarkResult> RunAsync(IReadOnlyList<AcceleratorDevice> devices, bool lowPriority, IProgress<BenchmarkProgress>? progress, CancellationToken ct);
}

/// <summary>
/// Echte Messung mit Nullpuffern in den Formen der Modelle: Session laden, ein Aufwärmlauf, dann <see cref="Runs"/> Läufe. Die
/// Sessions entstehen über dieselbe Fabrik wie im Betrieb (also mit denselben Optionen). Jedes Gerät läuft auf einem eigenen
/// Hintergrund-Thread mit Frist (<see cref="AccelerationTimeouts.BenchmarkPerDevice"/>), die ganze Messung hat ebenfalls eine Frist
/// (<see cref="AccelerationTimeouts.BenchmarkTotal"/>). Ein Gerät, das die Frist reißt, gilt als fehlgeschlagen und die Messung geht weiter;
/// ein blockierter nativer Aufruf wird nicht abgewartet, sondern läuft im Hintergrund aus (sein Thread gibt die Session danach selbst frei
/// und wird nie während eines <c>Run</c> freigegeben). Wegen der Priorität laufen die Threads nicht im Threadpool; ONNX Runtimes eigene
/// Rechen-Threads erben die Priorität nicht.
/// </summary>
public sealed class AcceleratorBenchmark : IAcceleratorBenchmark
{
    /// <summary>Gemessene Läufe je Modell nach dem Aufwärmlauf.</summary>
    public const int Runs = 10;

    private readonly Func<IReadOnlyList<ModelSpec>> _specs;
    private readonly TimeSpan _perDevice;
    private readonly TimeSpan _total;

    public AcceleratorBenchmark() : this(DefaultSpecs, AccelerationTimeouts.BenchmarkPerDevice, AccelerationTimeouts.BenchmarkTotal)
    {
    }

    internal AcceleratorBenchmark(Func<IReadOnlyList<ModelSpec>> specs, TimeSpan perDevice, TimeSpan total)
    {
        _specs = specs;
        _perDevice = perDevice;
        _total = total;
    }

    /// <summary>Modell, Datei und Eingabeform für die Messung.</summary>
    internal sealed record ModelSpec(ModelId Model, string Path, long[] Shape);

    internal static IReadOnlyList<ModelSpec> DefaultSpecs()
    {
        var yunet = YuNetPreprocessor.ComputeSize(640, 360);
        return
        [
            new(ModelId.Yolox, YoloxPersonDetector.DefaultModelPath, [1, 3, YoloxPreprocessor.DefaultInputSize, YoloxPreprocessor.DefaultInputSize]),
            new(ModelId.YuNet, YuNetFaceDetector.DefaultModelPath, [1, 3, yunet.PaddedHeight, yunet.PaddedWidth]),
            new(ModelId.Palm, MediaPipeHandDetector.DefaultPalmModelPath, [1, 3, MediaPipeHandDetector.PalmInputSize, MediaPipeHandDetector.PalmInputSize]),
            new(ModelId.Landmark, MediaPipeHandDetector.DefaultLandmarkModelPath, [1, 3, MediaPipeHandDetector.LandmarkInputSize, MediaPipeHandDetector.LandmarkInputSize]),
        ];
    }

    public async Task<BenchmarkResult> RunAsync(IReadOnlyList<AcceleratorDevice> devices, bool lowPriority, IProgress<BenchmarkProgress>? progress, CancellationToken ct)
    {
        var entries = new List<BenchmarkEntry>();
        IReadOnlyList<ModelSpec> specs;
        try
        {
            specs = _specs();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Die Modelle für die Messung sind nicht bestimmbar.", ex);
            return new BenchmarkResult(DateTimeOffset.UtcNow, entries);
        }

        var totalCount = devices.Count * specs.Count;
        var done = 0;
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        totalCts.CancelAfter(_total);

        foreach (var device in devices)
        {
            if (ct.IsCancellationRequested)
                break;

            var collector = new EntryCollector();
            var failure = "Nicht gemessen (Gesamtfrist der Messung überschritten)";
            if (!totalCts.IsCancellationRequested)
            {
                using var deviceCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
                deviceCts.CancelAfter(_perDevice);
                var baseline = done;
                var worker = RunOnThread(() => MeasureDevice(device, specs, collector, deviceCts.Token, count =>
                    Report(progress, new BenchmarkProgress(baseline + count, totalCount, device.Name, null))), lowPriority);
                try
                {
                    await worker.WaitAsync(_perDevice + TimeSpan.FromSeconds(2), totalCts.Token).ConfigureAwait(false);
                    failure = "Nicht gemessen";
                }
                catch (TimeoutException)
                {
                    failure = $"Zeitüberschreitung (Frist {_perDevice.TotalSeconds:0} s je Gerät)";
                    AppLog.Warn($"Beschleunigung: Die Messung auf „{device.Name}“ hat die Frist überschritten und gilt als fehlgeschlagen.");
                }
                catch (OperationCanceledException)
                {
                    failure = ct.IsCancellationRequested ? "Abgebrochen" : "Nicht fertig gemessen (Gesamtfrist der Messung überschritten)";
                }
                catch (Exception ex)
                {
                    failure = "Messung fehlgeschlagen: " + ex.Message;
                    AppLog.Warn($"Beschleunigung: Die Messung auf „{device.Name}“ ist fehlgeschlagen.", ex);
                }
                deviceCts.Cancel(); // ein hängender Lauf bricht zwischen zwei Aufrufen ab
            }

            var measured = collector.Close();
            entries.AddRange(measured);
            if (!ct.IsCancellationRequested)
            {
                // Was nicht gemessen wurde, steht mit Grund in der Tabelle (Gerät gilt dafür als fehlgeschlagen).
                foreach (var spec in specs.Where(s => measured.All(e => e.Model != s.Model)))
                    entries.Add(new BenchmarkEntry(device.Key, device.Name, spec.Model, 0, 0, failure));
            }
            done += specs.Count;
            Report(progress, new BenchmarkProgress(Math.Min(done, totalCount), totalCount, device.Name, null));
        }

        return new BenchmarkResult(DateTimeOffset.UtcNow, entries, ct.IsCancellationRequested);
    }

    private static void MeasureDevice(AcceleratorDevice device, IReadOnlyList<ModelSpec> specs, EntryCollector collector, CancellationToken token, Action<int> started)
    {
        var count = 0;
        foreach (var spec in specs)
        {
            if (token.IsCancellationRequested)
                return;
            started(count++);
            collector.Add(MeasureModel(device, spec, token));
        }
    }

    private static BenchmarkEntry MeasureModel(AcceleratorDevice device, ModelSpec spec, CancellationToken token)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            using var session = OnnxSessionFactory.Create(spec.Path, device);
            var loadMs = watch.Elapsed.TotalMilliseconds;
            var inputName = session.InputNames[0];
            var outputs = session.OutputNames.ToArray();
            var data = new float[spec.Shape.Aggregate(1L, (a, b) => a * b)];

            void RunOnce()
            {
                using var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, data.AsMemory(), spec.Shape);
                using var runOptions = new RunOptions();
                session.Run(runOptions, [inputName], [input], outputs).Dispose();
            }

            RunOnce(); // Aufwärmlauf (Kernel-Initialisierung, Speicher)
            watch.Restart();
            for (var i = 0; i < Runs; i++)
            {
                token.ThrowIfCancellationRequested();
                RunOnce();
            }
            return new BenchmarkEntry(device.Key, device.Name, spec.Model, loadMs, watch.Elapsed.TotalMilliseconds / Runs, null);
        }
        catch (OperationCanceledException)
        {
            return new BenchmarkEntry(device.Key, device.Name, spec.Model, 0, 0, "Abgebrochen");
        }
        catch (Exception ex)
        {
            var message = ex.Message.Split('\n')[0].Trim();
            return new BenchmarkEntry(device.Key, device.Name, spec.Model, 0, 0, message.Length > 200 ? message[..200] : message);
        }
    }

    private static void Report(IProgress<BenchmarkProgress>? progress, BenchmarkProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Der Fortschritt der Messung konnte nicht gemeldet werden.", ex);
        }
    }

    private static Task RunOnThread(Action work, bool lowPriority)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                work();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Fotobox-Messung",
            Priority = lowPriority ? ThreadPriority.BelowNormal : ThreadPriority.Normal,
        };
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Sammelt Messwerte eines Geräts thread-sicher; nach <see cref="Close"/> werden späte Einträge eines ausgelaufenen Threads verworfen.</summary>
    private sealed class EntryCollector
    {
        private readonly object _lock = new();
        private readonly List<BenchmarkEntry> _entries = [];
        private bool _closed;

        public void Add(BenchmarkEntry entry)
        {
            lock (_lock)
            {
                if (!_closed)
                    _entries.Add(entry);
            }
        }

        public List<BenchmarkEntry> Close()
        {
            lock (_lock)
            {
                _closed = true;
                return [.. _entries];
            }
        }
    }
}
