using Fotobox.Core;

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
    bool IsUserInterventionRequired = false,
    bool IsRetained = false);

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
/// (der Aufrufer misst, wie lange das schon anhält). <paramref name="Attention"/> = konkrete Ursache, die ein Eingreifen
/// braucht (bei einem allgemeinen Fehlerflag erst nach <see cref="PrintJobStateEvaluator.GenericErrorGrace"/> als
/// <see cref="PrinterAttention.NeedsHelp"/>); der Aufrufer misst, wie lange sie am Stück ansteht.
/// </summary>
public sealed record PrintJobDecision(PrintJobPhase Phase, string Message, bool GenericError = false,
    PrinterAttention Attention = PrinterAttention.None);

/// <summary>Ergebnis von <see cref="PrintJobStateEvaluator.DecideBlock"/>: nichts tun, sperren oder (Kulanz) als fertig werten.</summary>
public enum PrintBlockStep { None, Block, CompletedByGrace }

/// <summary>
/// Entscheidung über die Druckersperre. <paramref name="Reason"/> gilt bei <see cref="PrintBlockStep.Block"/>;
/// <paramref name="ByTimeout"/> = die Sperre kommt durch den Fristablauf (ohne bestätigte Ursache) zustande.
/// </summary>
public readonly record struct PrintBlockDecision(PrintBlockStep Step, PrinterAttention Reason = PrinterAttention.None, bool ByTimeout = false);

/// <summary>Nächster Schritt, nachdem der Gast den Druck abbrechen will (siehe <see cref="PrintJobStateEvaluator.DecideUserCancel"/>).</summary>
public enum UserCancelStep { NotRequested, CancelJob, KeepSearching, GiveUp }

/// <summary>Reine Zustandsauswertung: Flags + Zeiten → Entscheidung + deutsche Meldung. Keine Uhr, kein Spooler-Zugriff.</summary>
public static class PrintJobStateEvaluator
{
    /// <summary>
    /// So lange wird nach einem Abbruchwunsch noch nach dem Auftrag gesucht, wenn er (noch) nicht in der Warteschlange
    /// erschienen ist; danach gilt der Abbruch als gescheitert.
    /// </summary>
    public static readonly TimeSpan UserCancelFindWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Entscheidet nach einem Abbruchwunsch, was mit dem Auftrag geschieht: stornieren, weitersuchen oder aufgeben.
    /// </summary>
    /// <param name="cancelRequested">Der Gast hat den Abbruch angefordert.</param>
    /// <param name="jobKnown">Die ID des Auftrags in der Warteschlange ist bekannt.</param>
    /// <param name="sinceRequested">Zeit seit dem Abbruchwunsch.</param>
    public static UserCancelStep DecideUserCancel(bool cancelRequested, bool jobKnown, TimeSpan sinceRequested)
    {
        if (!cancelRequested)
            return UserCancelStep.NotRequested;
        if (jobKnown)
            return UserCancelStep.CancelJob;
        return sinceRequested >= UserCancelFindWindow ? UserCancelStep.GiveUp : UserCancelStep.KeepSearching;
    }

    public const string StatusPrinting = "Wird gedruckt…";
    public const string StatusPaperOut = "Papier leer – bitte nachlegen";
    public const string StatusOffline = "Drucker offline";
    public const string StatusPaperJam = "Papierstau";
    public const string StatusAttention = "Drucker benötigt Aufmerksamkeit";

    public const string FailedDeleted = "Der Druckauftrag wurde abgebrochen.";

    /// <summary>Wird ein Job nach dieser Zeit nie gesehen, gilt er als abgeschlossen (schnell durchgelaufen).</summary>
    public static readonly TimeSpan NeverSeenCompletedAfter = TimeSpan.FromSeconds(10);

    /// <summary>
    /// So lange muss ein Fehlerflag ohne erkennbare Ursache anhalten, bevor es als <see cref="PrinterAttention.NeedsHelp"/> gilt
    /// (der Auftrag wird dabei nie storniert; er bleibt in der Warteschlange).
    /// </summary>
    public static readonly TimeSpan GenericErrorGrace = TimeSpan.FromSeconds(8);

    /// <summary>So lange muss eine vom Treiber gemeldete Ursache am Stück anstehen, bevor die Druckersperre greift.</summary>
    public static readonly TimeSpan AttentionConfirm = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Entscheidet, ob die Druckersperre greift. Eine Ursache sperrt sofort, wenn sie <see cref="AttentionConfirm"/> am Stück ansteht.
    /// Läuft die Frist ab (<paramref name="timedOut"/>): Wurde der Auftrag vollständig gesendet und es gibt weder Ursache noch Fehler
    /// (<paramref name="lastStatusPrinting"/>), gilt er wie bisher als fertig (Kulanz, manche IPP/WSD-Treiber melden kein Druckende).
    /// Sonst sperrt der Fristablauf: mit der anstehenden Ursache, ohne Ursache als <see cref="PrinterAttention.PaperOut"/>.
    /// </summary>
    /// <param name="attention">Aktuell anstehende Ursache (<see cref="PrinterAttention.None"/> = keine).</param>
    /// <param name="attentionDuration">Wie lange diese Ursache schon am Stück ansteht.</param>
    /// <param name="timedOut">Die Druckfrist ist abgelaufen.</param>
    /// <param name="lastJobCompleted">Der Auftrag ist laut Warteschlange vollständig an den Drucker gesendet.</param>
    /// <param name="lastStatusPrinting">Der letzte Status war „Wird gedruckt…“ (keine Ursache, kein Fehler).</param>
    public static PrintBlockDecision DecideBlock(
        PrinterAttention attention,
        TimeSpan attentionDuration,
        bool timedOut,
        bool lastJobCompleted,
        bool lastStatusPrinting)
    {
        if (attention != PrinterAttention.None && attentionDuration >= AttentionConfirm)
            return new PrintBlockDecision(PrintBlockStep.Block, attention);
        if (!timedOut)
            return new PrintBlockDecision(PrintBlockStep.None);
        if (lastJobCompleted && lastStatusPrinting)
            return new PrintBlockDecision(PrintBlockStep.CompletedByGrace);
        return new PrintBlockDecision(PrintBlockStep.Block,
            attention == PrinterAttention.None ? PrinterAttention.PaperOut : attention, ByTimeout: true);
    }

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
            // Gedruckt zählt vor allem anderen: ein normal fertiger Job trägt oft noch Deleting/Deleted.
            if (j.IsPrinted)
                return new PrintJobDecision(PrintJobPhase.Completed, string.Empty);
            // Abgebrochen = gelöscht, ohne dass der Spooler ihn vollständig an den Drucker gesendet hat.
            if (j.IsDeleted || j.IsDeleting)
                return j.IsCompleted
                    ? new PrintJobDecision(PrintJobPhase.Completed, string.Empty)
                    : new PrintJobDecision(PrintJobPhase.Failed, FailedDeleted);
            // IsCompleted heißt nur "an den Drucker gesendet" – bei IPP/WSD-Druckern läuft der Druck da oft noch.
            // Fertig ist der Job erst, wenn er gedruckt ist oder (nach dem Sehen) aus der Queue verschwindet;
            // nur bei aufbewahrten Jobs (bleiben absichtlich in der Queue) genügt IsCompleted.
            if (j.IsCompleted && j.IsRetained)
                return new PrintJobDecision(PrintJobPhase.Completed, string.Empty);
        }
        else if (jobSeenBefore || sinceSubmitted >= NeverSeenCompletedAfter)
        {
            return new PrintJobDecision(PrintJobPhase.Completed, string.Empty);
        }

        // Konkrete Ursache (Papier, Stau, offline, Eingriff) hat Vorrang vor einem generischen Fehlerflag:
        // viele Treiber setzen IsInError zusammen mit "Papier leer" – der Nutzer kann das beheben.
        var (attention, text) = DescribeAttention(job, queue);
        if (attention != PrinterAttention.None)
            return new PrintJobDecision(PrintJobPhase.InProgress, text, Attention: attention);

        // Ein Auftrag, der ohne eigenes Fehlerflag gerade druckt, wird nicht wegen eines Queue-Fehlers beanstandet.
        var jobPrintingFine = job is { IsPrinting: true, IsInError: false };
        var generic = (job is { IsInError: true } || (queue.IsInError && !jobPrintingFine));
        // Ein anhaltendes allgemeines Fehlerflag gilt als „braucht Hilfe“ – nie als Fehlschlag und ohne Stornierung:
        // der Auftrag bleibt in der Warteschlange und druckt nach dem Beheben von selbst.
        if (generic && genericErrorDuration >= GenericErrorGrace)
            return new PrintJobDecision(PrintJobPhase.InProgress, StatusAttention, GenericError: true, Attention: PrinterAttention.NeedsHelp);

        return new PrintJobDecision(PrintJobPhase.InProgress, StatusPrinting, generic);
    }

    private static (PrinterAttention Attention, string Text) DescribeAttention(PrintJobFlags? job, PrintQueueFlags queue)
    {
        if (queue.IsPaperJammed)
            return (PrinterAttention.PaperJam, StatusPaperJam);
        if (queue.IsPaperOut || job is { IsPaperOut: true })
            return (PrinterAttention.PaperOut, StatusPaperOut);
        if (queue.IsOffline || queue.IsNotAvailable || job is { IsOffline: true })
            return (PrinterAttention.Offline, StatusOffline);
        if (queue.NeedUserIntervention || queue.IsDoorOpened || queue.IsOutputBinFull
            || job is { IsUserInterventionRequired: true } or { IsBlocked: true })
            return (PrinterAttention.NeedsHelp, StatusAttention);
        return (PrinterAttention.None, string.Empty);
    }
}
