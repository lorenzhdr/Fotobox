using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fotobox.Core;

namespace Fotobox.Services.Printing;

/// <summary>Drucken über System.Printing (XPS-Writer) mit Überwachung des Auftrags bis zum Ende.</summary>
public sealed class PrintService : IPrintService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    /// <summary>Frist für Druckerliste und Papiergrößen (Admin): ein hängender Spooler darf die Ansicht nicht blockieren.</summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    private readonly PageSpecCache _pageSpecs;

    public PrintService() : this(TimeProvider.System) { }

    public PrintService(TimeProvider time) => _pageSpecs = new PageSpecCache(time);

    /// <summary>Port von Druckern, die beim Drucken nach einem Dateinamen fragen (z. B. „Microsoft Print to PDF“).</summary>
    private const string FilePromptPort = "PORTPROMPT";

    public const string FailedFilePrompt =
        "Dieser Drucker fragt nach einem Dateinamen (z. B. „Microsoft Print to PDF“) und kann in der Fotobox nicht verwendet werden. Bitte einen echten Drucker wählen.";

    /// <summary>Papiergröße samt Treiber-Medium (für das PrintTicket).</summary>
    private sealed record PaperEntry(PaperSizeInfo Info, PageMediaSize Media);

    private sealed class JobSnapshot
    {
        public required PrintJobFlags Flags { get; init; }
    }

    public IReadOnlyList<PrinterInfo> GetPrinters()
    {
        try
        {
            return RunSta(() =>
            {
                using var server = new LocalPrintServer();
                var defaultName = TryGetDefaultQueueName();
                var result = new List<PrinterInfo>();
                using var queues = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);
                foreach (var queue in queues)
                {
                    using (queue)
                    {
                        var name = queue.FullName;
                        if (string.IsNullOrWhiteSpace(name) || result.Any(p => p.Name == name))
                            continue;
                        result.Add(new PrinterInfo(name, string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase)));
                    }
                }

                return (IReadOnlyList<PrinterInfo>)result;
            }).WaitAsync(QueryTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            AppLog.Warn($"Druckerliste nicht rechtzeitig gelesen (Spooler antwortet nicht, {QueryTimeout.TotalSeconds:0} s).");
            return [];
        }
        catch (Exception ex)
        {
            AppLog.Error("Druckerliste konnte nicht gelesen werden.", ex);
            return [];
        }
    }

    public IReadOnlyList<PaperSizeInfo> GetPaperSizes(string? printerName)
    {
        try
        {
            return RunSta(() =>
            {
                using var server = new LocalPrintServer();
                using var queue = OpenQueue(server, printerName);
                if (queue is null)
                    return (IReadOnlyList<PaperSizeInfo>)[];
                return ReadPaperEntries(queue).Select(e => e.Info).ToList();
            }).WaitAsync(QueryTimeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            AppLog.Warn($"Papiergrößen für „{printerName ?? "Standarddrucker"}“ nicht rechtzeitig gelesen (Spooler antwortet nicht, {QueryTimeout.TotalSeconds:0} s).");
            return [];
        }
        catch (Exception ex)
        {
            AppLog.Error($"Papiergrößen für „{printerName ?? "Standarddrucker"}“ konnten nicht gelesen werden.", ex);
            return [];
        }
    }

    public PageSpec GetPageSpec(PrinterSettings printer, LayoutKind layout)
    {
        var key = PageSpecKey.From(printer, layout);
        if (_pageSpecs.TryGetFresh(key, out var cached))
            return cached;

        IReadOnlyList<PaperSizeInfo> sizes = [];
        PaperSizeInfo? driverDefault = null;
        var queueRead = false;
        try
        {
            (sizes, driverDefault, queueRead) = RunSta(() =>
            {
                using var server = new LocalPrintServer();
                using var queue = OpenQueue(server, printer.PrinterName);
                if (queue is null)
                    return ((IReadOnlyList<PaperSizeInfo>)[], (PaperSizeInfo?)null, false);
                var entries = ReadPaperEntries(queue);
                return ((IReadOnlyList<PaperSizeInfo>)entries.Select(e => e.Info).ToList(), ReadDefaultPaper(queue), true);
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Seitengröße des Druckers nicht lesbar – Standardgröße wird verwendet.", ex);
        }

        // Nur echte Treiberabfragen zwischenspeichern; ein Rückfall (Drucker offline/nicht gefunden) darf nicht
        // für Minuten festgeschrieben werden. Stattdessen gilt dann der zuletzt gute Wert, falls vorhanden.
        if (queueRead)
        {
            var spec = PrintLayoutResolver.ResolvePageSpec(printer, layout, sizes, driverDefault);
            _pageSpecs.Store(key, spec);
            return spec;
        }

        if (_pageSpecs.TryGetLastGood(key, out var lastGood))
        {
            AppLog.Warn("Seitengröße des Druckers nicht lesbar – letzter bekannter Wert wird verwendet.");
            return lastGood;
        }

        return PrintLayoutResolver.ResolvePageSpec(printer, layout, sizes, driverDefault);
    }

    public async Task<PrintResult> PrintAsync(BitmapSource page, PrinterSettings printer, IProgress<string>? status = null,
        CancellationToken ct = default, CancellationToken userCancel = default)
    {
        try
        {
            if (ct.IsCancellationRequested)
                return new PrintResult(PrintOutcome.Cancelled);
            if (userCancel.IsCancellationRequested)
                return new PrintResult(PrintOutcome.UserCancelled);

            var frozen = EnsureFrozen(page);
            return await RunSta(() => PrintCore(frozen, printer, status, ct, userCancel)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new PrintResult(PrintOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            AppLog.Error("Druck fehlgeschlagen.", ex);
            return new PrintResult(PrintOutcome.Failed, $"Der Druck ist fehlgeschlagen: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- Druck

    private static PrintResult PrintCore(BitmapSource page, PrinterSettings settings, IProgress<string>? status,
        CancellationToken ct, CancellationToken userCancel)
    {
        // Die Frist läuft ab Beginn des Druckvorgangs (inkl. Ticket und Schreiben), damit sie vor dem äußeren Limit
        // des Controllers (Frist + Reserve ab Aufruf) greift und die Kulanzregel unten überhaupt zum Zug kommt.
        var total = Stopwatch.StartNew();
        // Gleiche Untergrenze wie beim harten Limit des Controllers (er addiert dort seine Reserve darauf).
        var timeout = TimeSpan.FromSeconds(Math.Max(SettingsLimits.PrintTimeoutMin, settings.TimeoutSeconds));
        var lastStatus = PrintJobStateEvaluator.StatusPrinting;
        status?.Report(lastStatus);

        using var server = new LocalPrintServer();
        using var queue = OpenQueue(server, settings.PrinterName);
        if (queue is null)
        {
            var name = settings.PrinterName ?? "Standarddrucker";
            AppLog.Error($"Drucker „{name}“ nicht gefunden.");
            return new PrintResult(PrintOutcome.Failed, $"Der Drucker „{name}“ wurde nicht gefunden.");
        }

        // Ein modaler "Speichern unter"-Dialog würde hinter der Vollbild-Box hängen und den Druck blockieren.
        if (IsFilePromptQueue(queue))
        {
            AppLog.Error($"Drucker „{queue.FullName}“ fragt nach einem Dateinamen (Port {FilePromptPort}) – Druck abgelehnt.");
            return new PrintResult(PrintOutcome.Failed, FailedFilePrompt);
        }

        // Kulturunabhängig (kein anderer Kalender/andere Ziffern im Jobnamen), da der Name im Spooler gesucht wird.
        var jobName = string.Create(CultureInfo.InvariantCulture, $"Fotobox {DateTime.Now:yyyyMMdd-HHmmss-fff}");
        var ticket = BuildTicket(queue, settings, out var media);
        var (mediaW, mediaH) = media;
        WarnOnAspectMismatch(page, mediaW, mediaH);

        var visual = RenderVisual(page, mediaW, mediaH);
        var knownIds = SnapshotJobIds(queue);

        // Wurde während Ticket/Rendern abgebrochen (z. B. Timeout des Controllers oder Wunsch des Gastes), darf nichts
        // mehr gesendet werden. Bereits gesendete Aufträge werden bei ct-Abbruch dagegen nicht storniert (laufender
        // Gastdruck soll fertig werden); nur ein Abbruchwunsch des Gastes (userCancel) storniert sie unten.
        if (ct.IsCancellationRequested)
            return new PrintResult(PrintOutcome.Cancelled);
        if (userCancel.IsCancellationRequested)
        {
            AppLog.Info($"Druck vor dem Senden auf Wunsch des Gastes abgebrochen (Auftrag „{jobName}“ wurde nicht gesendet).");
            return new PrintResult(PrintOutcome.UserCancelled);
        }

        AppLog.Info(string.Create(CultureInfo.InvariantCulture,
            $"Sende Druckauftrag „{jobName}“ an „{queue.FullName}“ ({PrintLayoutResolver.DipToMm(mediaW):0.#} × {PrintLayoutResolver.DipToMm(mediaH):0.#} mm, {ticket.CopyCount} Kopie(n))."));
        queue.CurrentJobSettings.Description = jobName;
        // Write ist synchron und nicht abbrechbar; ein Abbruchwunsch während des Sendens greift erst danach (unten).
        PrintQueue.CreateXpsDocumentWriter(queue).Write(visual, ticket);

        // Überwachung bis fertig (die Gesamtfrist läuft seit Beginn dieser Methode).
        var sinceSubmit = Stopwatch.StartNew();
        var lastJobCompleted = false;
        var seenBefore = false;
        int? trackedId = null;
        long? errorSince = null;   // Stopwatch-Zeitstempel (monotone Uhr, unabhängig von Zeitumstellung/Uhrensprung)
        long? userCancelSince = null;   // Zeitpunkt, an dem der Abbruchwunsch zuerst bemerkt wurde
        var queryFailures = 0;

        while (true)
        {
            if (ct.IsCancellationRequested)
                return new PrintResult(PrintOutcome.Cancelled);

            JobSnapshot? job = null;
            PrintQueueFlags queueFlags = default;
            var queryOk = true;
            try
            {
                queue.Refresh();
                queueFlags = ReadQueueFlags(queue);
                job = FindJob(queue, jobName, knownIds, ref trackedId);
            }
            catch (Exception ex)
            {
                queryOk = false;
                if (queryFailures++ == 0)
                    AppLog.Warn("Druckwarteschlange konnte nicht abgefragt werden.", ex);
            }

            if (queryOk)
            {
                seenBefore |= job is not null;
                lastJobCompleted = job is { Flags.IsCompleted: true };
                var errorFor = errorSince is { } since ? Stopwatch.GetElapsedTime(since) : TimeSpan.Zero;
                var decision = PrintJobStateEvaluator.Evaluate(job?.Flags, queueFlags, seenBefore, sinceSubmit.Elapsed, errorFor);

                switch (decision.Phase)
                {
                    case PrintJobPhase.Completed:
                        AppLog.Info($"Druckauftrag „{jobName}“ abgeschlossen.");
                        return new PrintResult(PrintOutcome.Completed);
                    case PrintJobPhase.Failed:
                        var message = decision.Message;
                        if (decision.GenericError && trackedId is { } failedId)
                        {
                            // Sonst bliebe der Job in der Queue und käme später doch noch heraus (doppelter Druck).
                            message += TryCancelJob(queue, failedId)
                                ? " Der Auftrag wurde abgebrochen."
                                : " Der Auftrag konnte nicht abgebrochen werden und bleibt evtl. in der Warteschlange.";
                        }

                        AppLog.Error($"Druckauftrag „{jobName}“ fehlgeschlagen: {message}");
                        return new PrintResult(PrintOutcome.Failed, message);
                }

                errorSince = decision.GenericError ? errorSince ?? Stopwatch.GetTimestamp() : null;
                if (decision.Message != lastStatus)
                {
                    lastStatus = decision.Message;
                    status?.Report(lastStatus);
                }
            }

            // Abbruchwunsch des Gastes: erst nach der Auswertung oben, damit ein inzwischen fertiger oder fehlgeschlagener
            // Auftrag sein reguläres Ergebnis behält. Ist der Auftrag noch unbekannt, wird bis zu 5 s weiter gesucht.
            if (userCancel.IsCancellationRequested)
            {
                userCancelSince ??= Stopwatch.GetTimestamp();
                var step = PrintJobStateEvaluator.DecideUserCancel(
                    cancelRequested: true, jobKnown: trackedId is not null, Stopwatch.GetElapsedTime(userCancelSince.Value));
                switch (step)
                {
                    case UserCancelStep.CancelJob when trackedId is { } cancelId:
                        if (TryCancelJob(queue, cancelId))
                        {
                            AppLog.Info($"Druckauftrag „{jobName}“ auf Wunsch des Gastes abgebrochen.");
                            return new PrintResult(PrintOutcome.UserCancelled);
                        }

                        AppLog.Warn($"Druckauftrag „{jobName}“ ließ sich auf Wunsch des Gastes nicht abbrechen.");
                        return new PrintResult(PrintOutcome.UserCancelFailed);
                    case UserCancelStep.GiveUp:
                        AppLog.Warn($"Druckauftrag „{jobName}“ für den Abbruch nicht in der Warteschlange gefunden.");
                        return new PrintResult(PrintOutcome.UserCancelFailed);
                }
            }

            if (total.Elapsed >= timeout)
            {
                AppLog.Warn($"Zeitüberschreitung beim Druckauftrag „{jobName}“ (letzter Status: {lastStatus}).");
                // Vollständig an den Drucker gesendet, aber kein Druckende gemeldet (manche IPP/WSD-Treiber):
                // Das Foto kommt in aller Regel trotzdem heraus – dem Gast keinen Fehler melden.
                if (lastJobCompleted && lastStatus == PrintJobStateEvaluator.StatusPrinting)
                {
                    AppLog.Warn($"Druckauftrag „{jobName}“ wurde gesendet, der Drucker meldet aber kein Druckende – gilt als fertig.");
                    return new PrintResult(PrintOutcome.Completed);
                }
                var detail = lastStatus == PrintJobStateEvaluator.StatusPrinting ? string.Empty : $" ({lastStatus})";
                return new PrintResult(PrintOutcome.TimedOut,
                    $"Der Druck wurde nicht rechtzeitig fertig{detail}. Der Auftrag bleibt in der Warteschlange.");
            }

            // Auf beide Token warten, damit ein Abbruchwunsch sofort greift. Ist er schon gesetzt (Suche nach dem
            // Auftrag läuft), nur auf ct warten – sonst kehrte WaitAny sofort zurück und die Schleife liefe heiß.
            bool cancelled;
            try
            {
                cancelled = userCancel.IsCancellationRequested
                    ? ct.WaitHandle.WaitOne(PollInterval)
                    : WaitHandle.WaitAny([ct.WaitHandle, userCancel.WaitHandle], PollInterval) == 0;
            }
            catch (ObjectDisposedException)
            {
                // Der Controller hat nach hartem Limit bzw. Abbruch die Token-Quelle schon freigegeben: wie Abbruch behandeln.
                cancelled = true;
            }
            if (cancelled || ct.IsCancellationRequested)
                return new PrintResult(PrintOutcome.Cancelled);
        }
    }

    private static PrintTicket BuildTicket(PrintQueue queue, PrinterSettings settings, out (double Width, double Height) mediaDip)
    {
        PrintCapabilities? caps = null;
        try { caps = queue.GetPrintCapabilities(); }
        catch (Exception ex) { AppLog.Warn("PrintCapabilities nicht lesbar.", ex); }

        var entries = caps is null ? [] : ReadPaperEntries(caps);
        var defaultTicket = queue.DefaultPrintTicket;
        var infos = entries.Select(e => e.Info).ToList();
        var paper = PrintLayoutResolver.ResolvePaper(settings, infos, ReadDefaultPaper(defaultTicket));

        var ticket = defaultTicket.Clone();
        if (paper is not null)
        {
            var entry = entries.FirstOrDefault(e => e.Info.Name.Equals(paper.Name, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                // Eine eigene, namenlose Größe ersetzen viele Treiber still durch ihre Standardgröße. Deshalb zuerst
                // ein Treiberpapier mit (fast) gleichem Maß suchen (±1 mm, beide Ausrichtungen).
                var index = PrintLayoutResolver.FindPaperByDimensions(infos, paper.WidthMm, paper.HeightMm);
                if (index >= 0)
                {
                    entry = entries[index];
                    AppLog.Info(string.Create(CultureInfo.InvariantCulture,
                        $"Papier „{paper.Name}“ nicht im Treiber – verwende Treiberpapier „{entry.Info.Name}“ mit gleichem Maß ({entry.Info.WidthMm:0.#} × {entry.Info.HeightMm:0.#} mm)."));
                }
            }

            ticket.PageMediaSize = entry?.Media
                ?? new PageMediaSize(PrintLayoutResolver.MmToDip(paper.WidthMm), PrintLayoutResolver.MmToDip(paper.HeightMm));
        }

        ticket.CopyCount = Math.Clamp(settings.Copies, SettingsLimits.CopiesMin, SettingsLimits.CopiesMax);
        if (caps?.PageBorderlessCapability.Contains(PageBorderless.Borderless) == true)
            ticket.PageBorderless = PageBorderless.Borderless;
        // Inhalt wird immer in der nativen Medienausrichtung gesendet (Bild wird bei Bedarf selbst gedreht).
        ticket.PageOrientation = System.Printing.PageOrientation.Portrait;

        var merge = queue.MergeAndValidatePrintTicket(defaultTicket, ticket);
        if (merge.ConflictStatus != ConflictStatus.NoConflict)
            AppLog.Info("PrintTicket wurde vom Treiber angepasst.");
        var validated = merge.ValidatedPrintTicket;

        // Maße aus dem validierten Ticket (der Treiber darf ersetzen); sonst Papiergröße bzw. 4 × 6 Zoll.
        var size = validated.PageMediaSize;
        if (size?.Width is > 0 and { } w && size.Height is > 0 and { } h)
            mediaDip = (w, h);
        else if (paper is not null)
            mediaDip = (PrintLayoutResolver.MmToDip(paper.WidthMm), PrintLayoutResolver.MmToDip(paper.HeightMm));
        else
            mediaDip = (PrintLayoutResolver.MmToDip(PageSpec.Default.WidthMm), PrintLayoutResolver.MmToDip(PageSpec.Default.HeightMm));

        return validated;
    }

    /// <summary>
    /// Warnt im Log, wenn das Seitenverhältnis der komponierten Seite um mehr als 1 % vom validierten Medium abweicht
    /// (das Bild wird dann beim Druck beschnitten). Das Druckbild bleibt unverändert.
    /// </summary>
    private static void WarnOnAspectMismatch(BitmapSource page, double mediaWDip, double mediaHDip)
    {
        try
        {
            if (!PrintLayoutResolver.HasAspectMismatch(page.PixelWidth, page.PixelHeight, mediaWDip, mediaHDip))
                return;

            var pageW = PrintLayoutResolver.PixelsToMm(page.PixelWidth, page.DpiX);
            var pageH = PrintLayoutResolver.PixelsToMm(page.PixelHeight, page.DpiY);
            AppLog.Warn(string.Create(CultureInfo.InvariantCulture,
                $"Seitenverhältnis der Seite ({pageW:0.#} × {pageH:0.#} mm) weicht vom Druckmedium ({PrintLayoutResolver.DipToMm(mediaWDip):0.#} × {PrintLayoutResolver.DipToMm(mediaHDip):0.#} mm) um mehr als 1 % ab – das Bild wird beschnitten."));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Seitenverhältnis konnte nicht geprüft werden.", ex);
        }
    }

    /// <summary>Bild als DrawingVisual über die gesamte Mediengröße (crop-to-fill, ohne Rand, ohne Strecken).</summary>
    private static DrawingVisual RenderVisual(BitmapSource page, double mediaW, double mediaH)
    {
        BitmapSource source = page;
        if (PrintLayoutResolver.NeedsRotation(page.PixelWidth, page.PixelHeight, mediaW, mediaH))
        {
            var rotated = new TransformedBitmap(page, new RotateTransform(90));
            rotated.Freeze();
            source = rotated;
        }

        var rect = PrintLayoutResolver.ComputeFillRect(source.PixelWidth, source.PixelHeight, mediaW, mediaH);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, mediaW, mediaH)));
            dc.DrawImage(source, rect);
            dc.Pop();
        }

        return visual;
    }

    // ---------------------------------------------------------------- Überwachung

    private static HashSet<int> SnapshotJobIds(PrintQueue queue)
    {
        var ids = new HashSet<int>();
        try
        {
            queue.Refresh();
            using var jobs = queue.GetPrintJobInfoCollection();
            foreach (var job in jobs)
            {
                using (job)
                    ids.Add(job.JobIdentifier);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Vorhandene Druckaufträge nicht lesbar.", ex);
        }

        return ids;
    }

    /// <summary>Sucht den Job per Name (bzw. gemerkter ID); Fallback: genau ein neuer, unbekannter Job.</summary>
    private static JobSnapshot? FindJob(PrintQueue queue, string jobName, HashSet<int> knownIds, ref int? trackedId)
    {
        using var jobs = queue.GetPrintJobInfoCollection();
        PrintSystemJobInfo? match = null;
        var newOnes = new List<PrintSystemJobInfo>();
        var others = new List<PrintSystemJobInfo>();

        foreach (var job in jobs)
        {
            var isMatch = trackedId is { } id
                ? job.JobIdentifier == id
                : (job.Name?.Contains(jobName, StringComparison.Ordinal) ?? false);
            if (isMatch && match is null)
                match = job;
            else if (!knownIds.Contains(job.JobIdentifier))
                newOnes.Add(job);
            else
                others.Add(job);
        }

        try
        {
            if (match is null && trackedId is null && newOnes.Count == 1)
                match = newOnes[0];
            if (match is null)
                return null;

            match.Refresh();
            trackedId = match.JobIdentifier;
            return new JobSnapshot { Flags = ReadJobFlags(match) };
        }
        finally
        {
            match?.Dispose();
            foreach (var j in newOnes.Concat(others).Where(j => !ReferenceEquals(j, match)))
                j.Dispose();
        }
    }

    /// <summary>Bricht den Auftrag ab. Fehler werden geloggt und geschluckt; true = Abbruch wurde ausgelöst.</summary>
    private static bool TryCancelJob(PrintQueue queue, int jobId)
    {
        try
        {
            using var job = queue.GetJob(jobId);
            job.Cancel();
            AppLog.Info($"Druckauftrag {jobId} wurde abgebrochen.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Druckauftrag {jobId} konnte nicht abgebrochen werden.", ex);
            return false;
        }
    }

    /// <summary>true, wenn der Port beim Drucken nach einem Dateinamen fragt (PORTPROMPT:).</summary>
    public static bool IsFilePromptPort(string? portName) =>
        portName is not null && portName.Trim().StartsWith(FilePromptPort, StringComparison.OrdinalIgnoreCase);

    private static bool IsFilePromptQueue(PrintQueue queue)
    {
        try
        {
            return IsFilePromptPort(queue.QueuePort?.Name);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Druckerport nicht lesbar.", ex);
            return false;
        }
    }

    private static PrintJobFlags ReadJobFlags(PrintSystemJobInfo job) => new(
        IsCompleted: job.IsCompleted,
        IsPrinted: job.IsPrinted,
        IsPrinting: job.IsPrinting,
        IsInError: job.IsInError,
        IsDeleted: job.IsDeleted,
        IsDeleting: job.IsDeleting,
        IsPaperOut: job.IsPaperOut,
        IsOffline: job.IsOffline,
        IsBlocked: job.IsBlocked,
        IsUserInterventionRequired: job.IsUserInterventionRequired,
        IsRetained: job.IsRetained);

    private static PrintQueueFlags ReadQueueFlags(PrintQueue queue) => new(
        IsInError: queue.IsInError,
        IsPaperOut: queue.IsOutOfPaper,
        IsPaperJammed: queue.IsPaperJammed,
        IsOffline: queue.IsOffline,
        IsNotAvailable: queue.IsNotAvailable,
        NeedUserIntervention: queue.NeedUserIntervention,
        IsDoorOpened: queue.IsDoorOpened,
        IsOutputBinFull: queue.IsOutputBinFull);

    // ---------------------------------------------------------------- Hilfen

    private static List<PaperEntry> ReadPaperEntries(PrintQueue queue) => ReadPaperEntries(queue.GetPrintCapabilities());

    private static List<PaperEntry> ReadPaperEntries(PrintCapabilities caps)
    {
        var list = new List<PaperEntry>();
        foreach (var media in caps.PageMediaSizeCapability)
        {
            if (media.Width is not > 0 || media.Height is not > 0)
                continue;
            var nameText = media.PageMediaSizeName?.ToString();
            var info = PrintLayoutResolver.CreatePaperSize(nameText, media.Width.Value, media.Height.Value);
            if (list.Any(e => e.Info.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            list.Add(new PaperEntry(info, media));
        }

        return list;
    }

    private static PaperSizeInfo? ReadDefaultPaper(PrintQueue queue) => ReadDefaultPaper(queue.DefaultPrintTicket);

    private static PaperSizeInfo? ReadDefaultPaper(PrintTicket ticket)
    {
        var size = ticket.PageMediaSize;
        if (size?.Width is > 0 and { } w && size.Height is > 0 and { } h)
            return PrintLayoutResolver.CreatePaperSize(size.PageMediaSizeName?.ToString(), w, h);
        return null;
    }

    /// <summary>Öffnet die Queue (null/leer = Standarddrucker). Gibt null zurück, wenn es sie nicht gibt.</summary>
    private static PrintQueue? OpenQueue(LocalPrintServer server, string? printerName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(printerName))
                return LocalPrintServer.GetDefaultPrintQueue();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kein Standarddrucker verfügbar.", ex);
            return null;
        }

        try
        {
            return new PrintQueue(server, printerName, PrintSystemDesiredAccess.UsePrinter);
        }
        catch (Exception)
        {
            // z. B. Netzwerk-Verbindungen (\\server\drucker): über die Aufzählung suchen.
        }

        try
        {
            PrintQueue? found = null;
            try
            {
                using var queues = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);
                foreach (var queue in queues)
                {
                    // Auch wenn eine Eigenschaft wirft, wird die Queue freigegeben (bzw. als Treffer behalten).
                    var keep = false;
                    try
                    {
                        keep = found is null && (queue.FullName.Equals(printerName, StringComparison.OrdinalIgnoreCase)
                                                 || queue.Name.Equals(printerName, StringComparison.OrdinalIgnoreCase));
                    }
                    finally
                    {
                        if (keep)
                            found = queue;
                        else
                            queue.Dispose();
                    }
                }

                return found;
            }
            catch
            {
                // Mitten in der Aufzählung abgebrochen: bereits gefundene Queue nicht auslaufen lassen.
                found?.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Drucker „{printerName}“ nicht gefunden.", ex);
            return null;
        }
    }

    private static string? TryGetDefaultQueueName()
    {
        try
        {
            using var queue = LocalPrintServer.GetDefaultPrintQueue();
            return queue.FullName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Nicht eingefrorene Bitmaps werden über ihren Dispatcher kopiert (Thread-Affinität).</summary>
    private static BitmapSource EnsureFrozen(BitmapSource page)
    {
        if (page.IsFrozen)
            return page;
        return page.Dispatcher.Invoke(() =>
        {
            var copy = page.Clone();
            copy.Freeze();
            return copy;
        });
    }

    /// <summary>PrintQueue-Objekte sind thread-affin → jede Operation läuft auf einem eigenen STA-Thread.</summary>
    private static Task<T> RunSta<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Nach einer Zeitüberschreitung des Aufrufers wird das Ergebnis nicht mehr abgeholt: Fehler nicht unbeobachtet lassen.
        _ = tcs.Task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
            finally
            {
                // WPF-Objekte (z. B. DrawingVisual) legen pro Thread einen Dispatcher an; ohne Shutdown bliebe er je Aufruf übrig.
                try { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
                catch (Exception ex) { AppLog.Warn("Dispatcher des Druck-Threads konnte nicht beendet werden.", ex); }
            }
        })
        {
            IsBackground = true,
            Name = "Fotobox-Print",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}

/// <summary>
/// Thread-sicherer Zwischenspeicher für ermittelte Seitengrößen je Druckereinstellung. Einträge gelten
/// <see cref="Lifetime"/> lang als frisch; danach bleibt der Wert als „letzter guter“ für den Notfall erhalten
/// (Drucker offline), wird aber nicht mehr ohne neue Abfrage geliefert.
/// </summary>
public sealed class PageSpecCache(TimeProvider time, TimeSpan? lifetime = null)
{
    /// <summary>Standard-Gültigkeit eines Eintrags.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<PageSpecKey, Entry> _entries = new();

    private sealed record Entry(PageSpec Spec, long Timestamp);

    public TimeSpan Lifetime { get; } = lifetime ?? DefaultLifetime;

    /// <summary>Wert, der jünger als <see cref="Lifetime"/> ist.</summary>
    public bool TryGetFresh(PageSpecKey key, out PageSpec spec)
    {
        if (_entries.TryGetValue(key, out var entry) && time.GetElapsedTime(entry.Timestamp) < Lifetime)
        {
            spec = entry.Spec;
            return true;
        }

        spec = PageSpec.Default;
        return false;
    }

    /// <summary>Zuletzt gespeicherter Wert, egal wie alt.</summary>
    public bool TryGetLastGood(PageSpecKey key, out PageSpec spec)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            spec = entry.Spec;
            return true;
        }

        spec = PageSpec.Default;
        return false;
    }

    public void Store(PageSpecKey key, PageSpec spec) =>
        _entries[key] = new Entry(spec, time.GetTimestamp());
}
