using System.Diagnostics;
using System.IO;
using System.Text;
using Fotobox.Core;

namespace Fotobox.Services.Detection.Acceleration;

/// <summary>
/// Der Messprozess (<c>Fotobox.exe --beschleunigung-messen &lt;Auftragsdatei&gt;</c>): misst die genannten Geräte nacheinander und meldet
/// jedes Ergebnis sofort (siehe <see cref="MeasureProtocol"/>). Ein Absturz im Grafiktreiber oder in DirectML ist in .NET nicht
/// abfangbar und würde die ganze Fotobox mitreißen; deshalb läuft jede Messung (auch die der CPU, mit demselben Code und denselben
/// Optionen) hier, in einem eigenen Prozess. Der Prozess öffnet kein Fenster, liest keine Einstellungen und PIN, hat keine Einzelinstanz-Sperre
/// und schreibt keine Logdatei (Logzeilen gehen über die Ausgabe an den Hauptprozess). Er wird von <c>Program.Main</c> abgezweigt, bevor
/// <c>App</c> entsteht.
/// </summary>
internal static class MeasurementChild
{
    /// <summary>Sicherung gegen Waisen, falls der Hauptprozess verschwindet und das Job-Objekt nicht griff: Danach beendet sich der Messprozess selbst.</summary>
    internal static readonly TimeSpan MaxRuntime = AccelerationTimeouts.BenchmarkTotal + TimeSpan.FromSeconds(60);

    /// <summary>Einstieg aus <c>Program.Main</c>. <paramref name="args"/> = alle Argumente (das erste ist der Schalter). Gibt den Exit-Code zurück.</summary>
    public static int Run(string[] args)
    {
        try
        {
            // Kein Windows-Absturzdialog auf dem Gäste-Bildschirm, wenn der Treiber den Prozess zu Fall bringt; der Hauptprozess merkt es am Exit-Code.
            SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox);
        }
        catch
        {
            // nicht kritisch
        }

        MessageWriter? writer = null;
        try
        {
            writer = new MessageWriter(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch
        {
            // Ohne Ausgabe kann der Hauptprozess nur das Ende sehen; gemessen wird trotzdem (Ergebnisdatei).
        }

        try
        {
            if (args.Length < 2)
                return 2;
            var job = MeasureProtocol.ReadFile<MeasureJob>(args[1]);
            if (job is null || string.IsNullOrWhiteSpace(job.ResultFile))
                return 2;

            var sink = writer;
            AppLog.Sink = (level, text) => sink?.Send(new MeasureMessage { Type = "log", Level = level, Text = text });

            using var watchdog = new Timer(_ => Environment.Exit(4), null, MaxRuntime, Timeout.InfiniteTimeSpan);
            if (job.LowPriority)
            {
                try
                {
                    // Der ganze Prozess: wirkt auch auf die eigenen Rechen-Threads von ONNX Runtime.
                    using var self = Process.GetCurrentProcess();
                    self.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Beschleunigung: Die Priorität des Messprozesses konnte nicht gesenkt werden.", ex);
                }
            }

            AppLog.Info($"Beschleunigung: Messprozess misst {job.DeviceKeys.Count} Gerät(e){(job.LowPriority ? " mit niedriger Priorität" : "")}.");
            // Die WinRT-Aufrufe der Geräteliste laufen im Threadpool (MTA), wie im Hauptprozess.
            return Task.Run(() => MeasureAsync(job, writer ?? new MessageWriter(null))).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            try
            {
                writer?.Send(new MeasureMessage { Type = "log", Level = "ERROR", Text = "Beschleunigung: Der Messprozess ist fehlgeschlagen. " + ex });
            }
            catch
            {
                // nichts mehr zu retten
            }
            return 1;
        }
    }

    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;
    private const uint SemNoOpenFileErrorBox = 0x8000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    internal static async Task<int> MeasureAsync(MeasureJob job, MessageWriter writer)
    {
        IReadOnlyList<AcceleratorDevice> found = [AcceleratorDevice.CpuDevice];
        if (job.DeviceKeys.Any(k => !string.Equals(k, AcceleratorDevice.CpuKey, StringComparison.Ordinal)))
            found = await new AcceleratorCatalog().DiscoverAsync().ConfigureAwait(false);

        var crashPatterns = (Environment.GetEnvironmentVariable(MeasureProtocol.CrashEnvironmentVariable) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var benchmark = new AcceleratorBenchmark();
        var modelCount = AccelerationPlanner.AllModels.Count;
        var all = new List<BenchmarkEntry>();

        for (var i = 0; i < job.DeviceKeys.Count; i++)
        {
            var key = job.DeviceKeys[i];
            var device = string.Equals(key, AcceleratorDevice.CpuKey, StringComparison.Ordinal)
                ? AcceleratorDevice.CpuDevice
                : found.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));
            writer.Send(new MeasureMessage { Type = "start", Key = key, Name = device?.Name ?? key });

            if (crashPatterns.Any(p => key.Contains(p, StringComparison.OrdinalIgnoreCase)))
                Environment.FailFast("Testabsturz des Messprozesses (" + MeasureProtocol.CrashEnvironmentVariable + ").");

            IReadOnlyList<BenchmarkEntry> entries;
            if (device is null)
            {
                entries = AccelerationPlanner.AllModels
                    .Select(m => new BenchmarkEntry(key, key, m, 0, 0, "Gerät im Messprozess nicht gefunden"))
                    .ToList();
            }
            else
            {
                var offset = i * modelCount;
                var progress = new DirectProgress<BenchmarkProgress>(p => writer.Send(new MeasureMessage
                {
                    Type = "progress",
                    Done = offset + p.Done,
                    Total = job.DeviceKeys.Count * modelCount,
                    Name = p.DeviceName,
                }));
                var result = await benchmark.RunAsync([device], job.LowPriority, progress, CancellationToken.None).ConfigureAwait(false);
                entries = result.Entries;
            }

            foreach (var entry in entries)
                writer.Send(new MeasureMessage { Type = "entry", Entry = entry });
            all.AddRange(entries);
            writer.Send(new MeasureMessage { Type = "deviceDone", Key = key });
        }

        MeasureProtocol.WriteAtomic(job.ResultFile, new MeasureResultFile { MeasuredAt = DateTimeOffset.UtcNow, Entries = all });
        writer.Send(new MeasureMessage { Type = "done" });
        return 0;
    }

    /// <summary>Meldet synchron (in der Reihenfolge der Aufrufe), anders als <see cref="Progress{T}"/>.</summary>
    private sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    /// <summary>Schreibt Nachrichten zeilenweise und thread-sicher; ohne Ausgabe (null) werden sie verworfen.</summary>
    internal sealed class MessageWriter(TextWriter? output)
    {
        private readonly object _lock = new();

        public void Send(MeasureMessage message)
        {
            if (output is null)
                return;
            try
            {
                var line = MeasureProtocol.Serialize(message);
                lock (_lock)
                    output.WriteLine(line);
            }
            catch
            {
                // Pipe geschlossen (Hauptprozess weg): Messen ist dann ohnehin sinnlos, der Wachhund beendet den Prozess.
            }
        }
    }
}
