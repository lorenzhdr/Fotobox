using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Fotobox.Core;

namespace Fotobox.Services.Detection.Acceleration;

/// <summary>Ein gestarteter Messprozess (Abstraktion über <see cref="Process"/>, damit der Hauptprozess ohne echten Kindprozess testbar ist).</summary>
internal interface IMeasureProcess : IDisposable
{
    /// <summary>Nächste Ausgabezeile; null = Ende der Ausgabe. Bricht bei <paramref name="ct"/> mit <see cref="OperationCanceledException"/> ab.</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);

    /// <summary>Wartet höchstens <paramref name="timeout"/> auf das Prozessende; true = beendet (<see cref="ExitCode"/> gültig).</summary>
    Task<bool> WaitForExitAsync(TimeSpan timeout);

    int ExitCode { get; }

    /// <summary>Prozess-Id (für das Log).</summary>
    int Id { get; }

    /// <summary>Anfang der Fehlerausgabe (für die Logzeile bei einem Absturz; dort steht die Meldung, danach folgt nur noch der Aufrufstapel).</summary>
    string ErrorOutput { get; }

    /// <summary>Beendet den Prozess samt Prozessbaum. Wirft nie.</summary>
    void Kill();
}

/// <summary>Startet Messprozesse.</summary>
internal interface IMeasureProcessLauncher
{
    /// <summary>Startet den Messprozess mit der Auftragsdatei. Wirft, wenn er nicht startet.</summary>
    IMeasureProcess Start(string jobFile);
}

/// <summary>
/// Messung in einem eigenen Prozess (<see cref="MeasurementChild"/>): Ein Absturz im Grafiktreiber oder in DirectML (nativ, in .NET nicht
/// abfangbar) kostet nur den Messprozess. Läuft das Gerät beim Absturz, gilt es (mit allen Modellen) als fehlgeschlagen und ist im Plan
/// nie schneller als die CPU; die übrigen Geräte misst ein neuer Messprozess (höchstens so viele Neustarts wie Geräte). Fristen: je Gerät
/// <see cref="AccelerationTimeouts.BenchmarkPerDevice"/> (im Messprozess) plus Wachhund hier, gesamt <see cref="AccelerationTimeouts.BenchmarkTotal"/>;
/// danach <see cref="IMeasureProcess.Kill"/>. Wirft nie, auch nicht bei Abbruch.
/// </summary>
public sealed class OutOfProcessBenchmark : IAcceleratorBenchmark
{
    private readonly IMeasureProcessLauncher _launcher;
    private readonly ChildTimeouts _timeouts;
    private readonly TimeSpan _perDevice;
    private readonly TimeSpan _total;

    /// <param name="executablePath">Die laufende <c>Fotobox.exe</c> (<see cref="Environment.ProcessPath"/>).</param>
    public OutOfProcessBenchmark(string? executablePath)
        : this(new SystemMeasureProcessLauncher(executablePath), ChildTimeouts.Default)
    {
    }

    internal OutOfProcessBenchmark(IMeasureProcessLauncher launcher, ChildTimeouts timeouts)
    {
        _launcher = launcher;
        _timeouts = timeouts;
        _perDevice = timeouts.PerDevice;
        _total = timeouts.Total;
    }

    /// <summary>Fristen der Messung (Standard siehe <see cref="AccelerationTimeouts"/>); als Parameter, damit Tests sie verkürzen können.</summary>
    internal sealed record ChildTimeouts(TimeSpan PerDevice, TimeSpan Total, TimeSpan Startup, TimeSpan DeviceReserve, TimeSpan BetweenDevices, TimeSpan Exit)
    {
        public static ChildTimeouts Default { get; } = new(AccelerationTimeouts.BenchmarkPerDevice, AccelerationTimeouts.BenchmarkTotal,
            AccelerationTimeouts.BenchmarkChildStartup, AccelerationTimeouts.BenchmarkChildDeviceReserve,
            AccelerationTimeouts.BenchmarkChildBetweenDevices, AccelerationTimeouts.BenchmarkChildExit);
    }

    public async Task<BenchmarkResult> RunAsync(IReadOnlyList<AcceleratorDevice> devices, bool lowPriority, IProgress<BenchmarkProgress>? progress, CancellationToken ct)
    {
        var models = AccelerationPlanner.AllModels;
        var finished = new Dictionary<string, List<BenchmarkEntry>>(StringComparer.Ordinal);
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = devices.ToList();
        var totalCount = devices.Count * models.Count;
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        totalCts.CancelAfter(_total);

        var launches = 0;
        while (pending.Count > 0 && !ct.IsCancellationRequested && !totalCts.IsCancellationRequested)
        {
            if (launches++ > devices.Count)
            {
                foreach (var device in pending)
                    failures[device.Key] = "Nicht gemessen (zu viele Neustarts des Messprozesses)";
                pending.Clear();
                break;
            }

            var outcome = await RunChildAsync(pending, lowPriority, devices.Count - pending.Count, totalCount, progress, ct, totalCts).ConfigureAwait(false);
            foreach (var (key, entries) in outcome.Finished)
            {
                finished[key] = entries;
                pending.RemoveAll(d => d.Key == key);
            }

            switch (outcome.End)
            {
                case ChildEnd.Completed:
                    foreach (var device in pending)
                        failures[device.Key] = "Nicht gemessen";
                    pending.Clear();
                    break;

                case ChildEnd.Cancelled:
                    pending.Clear();
                    break;

                case ChildEnd.StartFailed:
                    foreach (var device in pending)
                        failures[device.Key] = outcome.Detail;
                    pending.Clear();
                    break;

                case ChildEnd.Total:
                    foreach (var device in pending)
                    {
                        failures[device.Key] = device.Key == outcome.CurrentKey
                            ? $"Zeitüberschreitung (Gesamtfrist {_total.TotalSeconds:0} s der Messung)"
                            : "Nicht gemessen (Gesamtfrist der Messung überschritten)";
                    }
                    pending.Clear();
                    break;

                default: // Crashed, Watchdog
                    var current = pending.FirstOrDefault(d => d.Key == outcome.CurrentKey);
                    if (current is null)
                    {
                        // Vor dem ersten Gerät (Start, Geräteliste): ein Neustart würde dasselbe erleben.
                        foreach (var device in pending)
                            failures[device.Key] = outcome.Detail;
                        pending.Clear();
                    }
                    else
                    {
                        failures[current.Key] = outcome.Detail;
                        AppLog.Warn($"Beschleunigung: Die Messung auf „{current.Name}“ ist fehlgeschlagen: {outcome.Detail}. Das Gerät zählt als fehlgeschlagen; {(pending.Count > 1 ? "die übrigen Geräte werden in einem neuen Messprozess gemessen." : "keine weiteren Geräte.")}");
                        pending.Remove(current);
                    }
                    break;
            }
        }

        // Abbruch oder Gesamtfrist mitten in der Schleife: Offenes ist nicht gemessen.
        if (pending.Count > 0 && !ct.IsCancellationRequested)
        {
            foreach (var device in pending)
                failures[device.Key] = "Nicht gemessen (Gesamtfrist der Messung überschritten)";
        }

        var result = new List<BenchmarkEntry>();
        foreach (var device in devices)
        {
            if (finished.TryGetValue(device.Key, out var entries))
            {
                foreach (var model in models)
                {
                    var entry = entries.FirstOrDefault(e => e.Model == model);
                    result.Add(entry is null
                        ? new BenchmarkEntry(device.Key, device.Name, model, 0, 0, "Nicht gemessen")
                        : entry with { DeviceKey = device.Key, DeviceName = device.Name });
                }
            }
            else if (!ct.IsCancellationRequested)
            {
                var reason = failures.TryGetValue(device.Key, out var text) ? text : "Nicht gemessen";
                result.AddRange(models.Select(m => new BenchmarkEntry(device.Key, device.Name, m, 0, 0, reason)));
            }
        }
        return new BenchmarkResult(DateTimeOffset.UtcNow, result, ct.IsCancellationRequested);
    }

    private enum ChildEnd { Completed, Crashed, Watchdog, Total, Cancelled, StartFailed }

    private sealed record ChildOutcome(ChildEnd End, string Detail, string? CurrentKey, List<(string Key, List<BenchmarkEntry> Entries)> Finished);

    /// <summary>Startet einen Messprozess für die offenen Geräte und liest ihn bis zum Ende, zum Absturz oder zur Frist.</summary>
    private async Task<ChildOutcome> RunChildAsync(IReadOnlyList<AcceleratorDevice> pending, bool lowPriority, int finishedBefore, int totalCount,
        IProgress<BenchmarkProgress>? progress, CancellationToken ct, CancellationTokenSource totalCts)
    {
        var finished = new List<(string Key, List<BenchmarkEntry> Entries)>();
        var jobDirectory = Path.Combine(Path.GetTempPath(), "fotobox-messung-" + Guid.NewGuid().ToString("N"));
        IMeasureProcess? process = null;
        try
        {
            string jobFile;
            try
            {
                Directory.CreateDirectory(jobDirectory);
                jobFile = Path.Combine(jobDirectory, "auftrag.json");
                MeasureProtocol.WriteAtomic(jobFile, new MeasureJob
                {
                    ResultFile = Path.Combine(jobDirectory, "ergebnis.json"),
                    LowPriority = lowPriority,
                    DeviceKeys = pending.Select(d => d.Key).ToList(),
                });
                process = _launcher.Start(jobFile);
                AppLog.Info($"Beschleunigung: Messprozess gestartet (PID {process.Id}, {pending.Count} Gerät(e)).");
            }
            catch (Exception ex)
            {
                AppLog.Warn("Beschleunigung: Der Messprozess konnte nicht gestartet werden.", ex);
                return new ChildOutcome(ChildEnd.StartFailed, "Messprozess konnte nicht gestartet werden: " + ex.Message, null, finished);
            }

            using var childCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
            childCts.CancelAfter(_timeouts.Startup);
            string? current = null;
            List<BenchmarkEntry> currentEntries = [];
            var sawDone = false;
            ChildEnd? abnormal = null;

            while (true)
            {
                string? line;
                try
                {
                    line = await process.ReadLineAsync(childCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    abnormal = ct.IsCancellationRequested ? ChildEnd.Cancelled : totalCts.IsCancellationRequested ? ChildEnd.Total : ChildEnd.Watchdog;
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Beschleunigung: Die Ausgabe des Messprozesses war nicht lesbar.", ex);
                    break;
                }

                if (line is null)
                    break;
                var message = MeasureProtocol.TryParse(line);
                if (message is null)
                    continue;

                switch (message.Type)
                {
                    case "start":
                        current = message.Key;
                        currentEntries = [];
                        childCts.CancelAfter(_perDevice + _timeouts.DeviceReserve);
                        break;
                    case "progress":
                        Report(progress, new BenchmarkProgress(Math.Min(finishedBefore * AccelerationPlanner.AllModels.Count + message.Done, totalCount), totalCount, message.Name ?? "", null));
                        break;
                    case "entry":
                        if (message.Entry is { } entry)
                            currentEntries.Add(entry);
                        break;
                    case "deviceDone":
                        if (current is not null && string.Equals(current, message.Key, StringComparison.Ordinal))
                            finished.Add((current, currentEntries));
                        current = null;
                        currentEntries = [];
                        childCts.CancelAfter(_timeouts.BetweenDevices);
                        break;
                    case "log":
                        Forward(message);
                        break;
                    case "done":
                        sawDone = true;
                        break;
                }
                if (sawDone)
                    break;
            }

            if (abnormal is { } end)
            {
                process.Kill();
                await process.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var detail = end switch
                {
                    ChildEnd.Watchdog => current is null
                        ? "Messprozess antwortet nicht (keine Meldung innerhalb der Frist)"
                        : $"Zeitüberschreitung (Messprozess antwortet nicht, Frist {_perDevice.TotalSeconds:0} s je Gerät)",
                    _ => "",
                };
                if (end == ChildEnd.Watchdog)
                    AppLog.Warn("Beschleunigung: Der Messprozess antwortet nicht mehr und wird beendet.");
                return new ChildOutcome(end, detail, current, finished);
            }

            // Ausgabe zu Ende (oder „fertig“ gemeldet): auf das Prozessende warten, notfalls beenden.
            var exited = await process.WaitForExitAsync(_timeouts.Exit).ConfigureAwait(false);
            if (!exited)
            {
                process.Kill();
                await process.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }

            if (sawDone)
            {
                if (exited && process.ExitCode != 0)
                    AppLog.Warn($"Beschleunigung: Der Messprozess meldete „fertig“, endete aber mit Exit-Code {FormatExitCode(process.ExitCode)}; die Messwerte werden übernommen.");
                else
                    AppLog.Info($"Beschleunigung: Messprozess beendet (PID {process.Id}, {finished.Count} Gerät(e) gemessen).");
                return new ChildOutcome(ChildEnd.Completed, "", null, finished);
            }

            var code = exited ? FormatExitCode(process.ExitCode) : "unbekannt";
            var errorOutput = string.Join(" | ", process.ErrorOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (errorOutput.Length > 400)
                errorOutput = errorOutput[..400] + " …";
            AppLog.Warn($"Beschleunigung: Der Messprozess ist abgestürzt (Exit-Code {code}){(errorOutput.Length == 0 ? "" : ": " + errorOutput)}");
            return new ChildOutcome(ChildEnd.Crashed, $"Messprozess abgestürzt (Exit-Code {code})", current, finished);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Die Messung im Messprozess ist fehlgeschlagen.", ex);
            try
            {
                process?.Kill();
            }
            catch
            {
                // Kill wirft nie; Vorsicht schadet nicht.
            }
            return new ChildOutcome(ChildEnd.Crashed, "Messprozess fehlgeschlagen: " + ex.Message, null, finished);
        }
        finally
        {
            try
            {
                process?.Dispose();
            }
            catch
            {
                // egal
            }
            try
            {
                Directory.Delete(jobDirectory, recursive: true);
            }
            catch
            {
                // Aufräumen ist nur Kosmetik.
            }
        }
    }

    internal static string FormatExitCode(int code) => "0x" + code.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Logzeile des Messprozesses ins Log des Hauptprozesses, gekennzeichnet.</summary>
    private static void Forward(MeasureMessage message)
    {
        var text = message.Text ?? "";
        const string prefix = "Beschleunigung: ";
        text = text.StartsWith(prefix, StringComparison.Ordinal) ? "Beschleunigung (Messprozess): " + text[prefix.Length..] : "Beschleunigung (Messprozess): " + text;
        switch (message.Level)
        {
            case "ERROR":
                AppLog.Error(text);
                break;
            case "WARN":
                AppLog.Warn(text);
                break;
            default:
                AppLog.Info(text);
                break;
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
}

/// <summary>Startet die laufende <c>Fotobox.exe</c> als Messprozess (ohne Fenster, Ausgabe umgeleitet, im Job-Objekt des Hauptprozesses).</summary>
internal sealed class SystemMeasureProcessLauncher(string? executablePath, IReadOnlyDictionary<string, string>? environment = null) : IMeasureProcessLauncher
{
    public IMeasureProcess Start(string jobFile)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new FileNotFoundException("Die Programmdatei für den Messprozess ist nicht bestimmbar.");

        var info = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
        };
        info.ArgumentList.Add(MeasureProtocol.Switch);
        info.ArgumentList.Add(jobFile);
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
                info.Environment[name] = value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("Der Messprozess konnte nicht gestartet werden.");
        MeasureJobObject.TryAssign(process);
        return new SystemMeasureProcess(process);
    }

    private sealed class SystemMeasureProcess : IMeasureProcess
    {
        private const int ErrorOutputLength = 1500;
        private readonly Process _process;
        private readonly StringBuilder _errorOutput = new();

        public SystemMeasureProcess(Process process)
        {
            _process = process;
            // Die Fehlerausgabe wird immer leergelesen (ein volles, ungelesenes Pipe-Fenster würde den Messprozess blockieren).
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                lock (_errorOutput)
                {
                    if (_errorOutput.Length < ErrorOutputLength)
                        _errorOutput.AppendLine(e.Data);
                }
            };
            process.BeginErrorReadLine();
        }

        public int ExitCode => _process.ExitCode;

        public int Id => _process.Id;

        public string ErrorOutput
        {
            get
            {
                lock (_errorOutput)
                    return _errorOutput.ToString().Trim();
            }
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct) =>
            await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);

        public async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch
            {
                return _process.HasExited;
            }
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Schon beendet oder nicht beendbar: nichts weiter zu tun.
            }
        }

        public void Dispose() => _process.Dispose();
    }
}

/// <summary>
/// Job-Objekt mit „beim Schließen alle beenden“: Verschwindet der Hauptprozess (Absturz, Beenden im Task-Manager, Smoke-Test), beendet Windows
/// auch die Messprozesse; sie bleiben nie als Waisen zurück und sperren keine Dateien des Programmordners. Schlägt das Anlegen fehl, bleibt es
/// beim Beenden durch den Hauptprozess und der Selbstbegrenzung des Messprozesses.
/// </summary>
internal static class MeasureJobObject
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    // Das Handle lebt bewusst bis zum Prozessende (dann schließt Windows es und beendet die Zugeordneten).
    private static readonly Lazy<IntPtr> Handle = new(Create);

    public static bool TryAssign(Process process)
    {
        try
        {
            var job = Handle.Value;
            if (job == IntPtr.Zero)
                return false;
            if (AssignProcessToJobObject(job, process.Handle))
                return true;
            AppLog.Warn($"Beschleunigung: Der Messprozess konnte keinem Job-Objekt zugeordnet werden (Fehler {Marshal.GetLastWin32Error()}).");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Der Messprozess konnte keinem Job-Objekt zugeordnet werden.", ex);
            return false;
        }
    }

    private static IntPtr Create()
    {
        try
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
                {
                    AppLog.Warn($"Beschleunigung: Das Job-Objekt für den Messprozess konnte nicht eingerichtet werden (Fehler {Marshal.GetLastWin32Error()}).");
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return job;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Beschleunigung: Das Job-Objekt für den Messprozess konnte nicht angelegt werden.", ex);
            return IntPtr.Zero;
        }
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
