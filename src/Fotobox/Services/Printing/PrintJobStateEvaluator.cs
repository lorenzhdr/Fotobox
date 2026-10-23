namespace Fotobox.Services.Printing;

/// <summary>Statusflags eines Druckauftrags (Abbild von PrintSystemJobInfo).</summary>
public readonly record struct PrintJobFlags(
    bool IsCompleted = false,
    bool IsPrinted = false,
    bool IsPrinting = false,
    bool IsInError = false,
    bool IsDeleted = false,
    bool IsDeleting = false,
    bool IsPaperOut = false,
    bool IsOffline = false,
    bool IsBlocked = false,
    bool IsUserInterventionRequired = false);

/// <summary>Statusflags der Druckwarteschlange (Abbild von PrintQueue).</summary>
public readonly record struct PrintQueueFlags(
    bool IsInError = false,
    bool IsPaperOut = false,
    bool IsPaperJammed = false,
    bool IsOffline = false,
    bool IsNotAvailable = false,
    bool NeedUserIntervention = false,
    bool IsDoorOpened = false,
    bool IsOutputBinFull = false);

public enum PrintJobPhase { InProgress, Completed, Failed }

/// <summary>
/// Entscheidung der Auswertung. <paramref name="Message"/> ist bei InProgress die Statusmeldung,
/// bei Failed die Fehlermeldung. <paramref name="GenericError"/> = Fehlerflag ohne erkennbare Ursache
/// (der Aufrufer misst, wie lange das schon anhält).
/// </summary>
public sealed record PrintJobDecision(PrintJobPhase Phase, string Message, bool GenericError = false);

/// <summary>Reine Zustandsauswertung: Flags + Zeiten → Entscheidung + deutsche Meldung. Keine Uhr, kein Spooler-Zugriff.</summary>
public static class PrintJobStateEvaluator
{
    public const string StatusPrinting = "Wird gedruckt…";
    public const string StatusPaperOut = "Papier leer – bitte nachlegen";
    public const string StatusOffline = "Drucker offline";
    public const string StatusPaperJam = "Papierstau";
    public const string StatusAttention = "Drucker benötigt Aufmerksamkeit";

    public const string FailedDeleted = "Der Druckauftrag wurde abgebrochen.";
    public const string FailedError = "Der Drucker meldet einen Fehler.";

    /// <summary>Wird ein Job nach dieser Zeit nie gesehen, gilt er als abgeschlossen (schnell durchgelaufen).</summary>
    public static readonly TimeSpan NeverSeenCompletedAfter = TimeSpan.FromSeconds(10);

    /// <summary>So lange muss ein Fehler ohne erkennbare Ursache anhalten, bevor der Druck als fehlgeschlagen gilt.</summary>
    public static readonly TimeSpan GenericErrorGrace = TimeSpan.FromSeconds(8);

    /// <param name="job">Auftrag oder null, wenn er (aktuell) nicht in der Warteschlange gefunden wurde.</param>
    /// <param name="queue">Zustand der Warteschlange.</param>
    /// <param name="jobSeenBefore">Der Auftrag wurde in einem früheren Durchlauf gesehen.</param>
    /// <param name="sinceSubmitted">Zeit seit Abschluss des Sendens an den Spooler.</param>
    /// <param name="genericErrorDuration">Wie lange bereits ein Fehler ohne erkennbare Ursache gemeldet wird.</param>
    public static PrintJobDecision Evaluate(
        PrintJobFlags? job,
        PrintQueueFlags queue,
        bool jobSeenBefore,
        TimeSpan sinceSubmitted,
        TimeSpan genericErrorDuration)
    {
        if (job is { } j)
        {
            if (j.IsDeleted || j.IsDeleting)
                return new PrintJobDecision(PrintJobPhase.Failed, FailedDeleted);
            if (j.IsCompleted || j.IsPrinted)
                return new PrintJobDecision(PrintJobPhase.Completed, string.Empty);
        }
        else if (jobSeenBefore || sinceSubmitted >= NeverSeenCompletedAfter)
        {
            return new PrintJobDecision(PrintJobPhase.Completed, string.Empty);
        }

        // Konkrete Ursache (Papier, Stau, offline, Eingriff) hat Vorrang vor einem generischen Fehlerflag:
        // viele Treiber setzen IsInError zusammen mit "Papier leer" – der Nutzer kann das beheben.
        var attention = DescribeAttention(job, queue);
        if (attention is not null)
            return new PrintJobDecision(PrintJobPhase.InProgress, attention);

        // Ein Auftrag, der ohne eigenes Fehlerflag gerade druckt, wird nicht wegen eines Queue-Fehlers abgebrochen.
        var jobPrintingFine = job is { IsPrinting: true, IsInError: false };
        var generic = (job is { IsInError: true } || (queue.IsInError && !jobPrintingFine));
        if (generic && genericErrorDuration >= GenericErrorGrace)
            return new PrintJobDecision(PrintJobPhase.Failed, FailedError, GenericError: true);

        return new PrintJobDecision(PrintJobPhase.InProgress, StatusPrinting, generic);
    }

    private static string? DescribeAttention(PrintJobFlags? job, PrintQueueFlags queue)
    {
        if (queue.IsPaperJammed)
            return StatusPaperJam;
        if (queue.IsPaperOut || job is { IsPaperOut: true })
            return StatusPaperOut;
        if (queue.IsOffline || queue.IsNotAvailable || job is { IsOffline: true })
            return StatusOffline;
        if (queue.NeedUserIntervention || queue.IsDoorOpened || queue.IsOutputBinFull
            || job is { IsUserInterventionRequired: true } or { IsBlocked: true })
            return StatusAttention;
        return null;
    }
}
