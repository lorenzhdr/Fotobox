using CommunityToolkit.Mvvm.Input;
using Fotobox.Core;
using Fotobox.Core.Peace;
using Fotobox.ViewModels.Booth;

namespace Fotobox.ViewModels.Admin;

// Mittelfingererkennung – Entwurfs-Properties, Status und Test der Vorschau (die Vorschau selbst teilt sich mit der Peaceerkennung: PeacePreview)
public sealed partial class AdminViewModel
{
    /// <summary>Dauer des Test-Bartes „Aufheben testen“ in Sekunden (lang genug, um den Mittelfinger in Ruhe zu zeigen).</summary>
    internal const int MiddleFingerTestMoustacheSeconds = 20;

    /// <summary>Hinweis, wenn die Peaceerkennung aus ist (es gibt dann nichts aufzuheben).</summary>
    public const string MiddleFingerPeaceDisabledText = "Die Peaceerkennung ist aus – ohne sie gibt es nichts aufzuheben.";

    /// <summary>Hinweis, wenn die Gesichtserkennung fehlt (Hände werden dann schlechter gefunden).</summary>
    public const string MiddleFingerFaceUnavailableText =
        "Die Gesichtserkennung ist nicht verfügbar – die Handerkennung arbeitet dadurch eingeschränkt.";

    /// <summary>Hinweis nach „Aufheben testen“, solange der Test-Bart aufgehoben werden kann.</summary>
    public const string MiddleFingerTestHintText = "Zeigen Sie jetzt den Mittelfinger, um den Bart zu entfernen.";

    // ---------------------------------------------------------------- Entwurfswerte

    /// <summary>Mittelfingererkennung aktiv; die Einstellungen darunter sind sonst ausgegraut.</summary>
    public bool MiddleFingerEnabled
    {
        get => _draft.MiddleFinger.Enabled;
        set
        {
            if (SetProperty(_draft.MiddleFinger.Enabled, value, v => _draft.MiddleFinger.Enabled = v))
            {
                OnPropertyChanged(nameof(MiddleFingerStatusText));
                OnPropertyChanged(nameof(MiddleFingerHasNoAction));
            }
        }
    }

    /// <summary>Aktion „Peace-Effekte aufheben“ (Bart entfernen, Selbstzerstörung abbrechen).</summary>
    public bool MiddleFingerActionCancelPeace
    {
        get => _draft.MiddleFinger.ActionCancelPeace;
        set
        {
            if (SetProperty(_draft.MiddleFinger.ActionCancelPeace, value, v => _draft.MiddleFinger.ActionCancelPeace = v))
            {
                OnPropertyChanged(nameof(MiddleFingerHasNoAction));
                OnPropertyChanged(nameof(MiddleFingerActionCancelPeaceEnabled));
            }
        }
    }

    /// <summary>
    /// Der Haken „Peace-Effekte aufheben“ lässt sich ändern. Der letzte gesetzte Haken ist gesperrt (wie bei den Zufallsformen der
    /// Peaceerkennung): Es soll immer mindestens eine Aktion übrig bleiben. Künftige Aktionen erhöhen <see cref="MiddleFingerActionCount"/>.
    /// </summary>
    public bool MiddleFingerActionCancelPeaceEnabled => !(_draft.MiddleFinger.ActionCancelPeace && MiddleFingerActionCount <= 1);

    private int MiddleFingerActionCount => _draft.MiddleFinger.ActionCancelPeace ? 1 : 0;

    /// <summary>Mittelfingererkennung ist an, aber keine Aktion gewählt (Warnung; blockiert das Speichern).</summary>
    public bool MiddleFingerHasNoAction => _draft.MiddleFinger.Enabled && !_draft.MiddleFinger.HasAnyAction;

    /// <summary>Haltedauer in Millisekunden (<see cref="SettingsLimits.MiddleFingerHoldMillisecondsMin"/> bis <c>Max</c>).</summary>
    public int MiddleFingerHoldMilliseconds
    {
        get => _draft.MiddleFinger.HoldMilliseconds;
        set => SetProperty(_draft.MiddleFinger.HoldMilliseconds,
            Math.Clamp(value, SettingsLimits.MiddleFingerHoldMillisecondsMin, SettingsLimits.MiddleFingerHoldMillisecondsMax),
            v => _draft.MiddleFinger.HoldMilliseconds = v);
    }

    /// <summary>Empfindlichkeit in Prozent (<see cref="SettingsLimits.MiddleFingerSensitivityMin"/> bis <c>Max</c>).</summary>
    public int MiddleFingerSensitivityPercent
    {
        get => _draft.MiddleFinger.SensitivityPercent;
        set => SetProperty(_draft.MiddleFinger.SensitivityPercent,
            Math.Clamp(value, SettingsLimits.MiddleFingerSensitivityMin, SettingsLimits.MiddleFingerSensitivityMax),
            v => _draft.MiddleFinger.SensitivityPercent = v);
    }

    // ---------------------------------------------------------------- Startseite und Vorschau

    /// <summary>„An“ bzw. „Aus“ für die Kachel „Mittelfinger“ (gleiche Darstellung wie die anderen Kacheln).</summary>
    public string MiddleFingerStatusText => _draft.MiddleFinger.Enabled ? "An" : "Aus";

    /// <summary>Warnung „Peaceerkennung aus“ auf der Mittelfinger-Seite (es gäbe nichts aufzuheben).</summary>
    public string MiddleFingerPeaceDisabledWarning => HasMiddleFingerPeaceDisabledWarning ? MiddleFingerPeaceDisabledText : string.Empty;

    public bool HasMiddleFingerPeaceDisabledWarning => !_draft.Peace.Enabled;

    /// <summary>Statuszeile unter der Vorschau der Mittelfinger-Seite (Hände, Gesichter, Rate, „Mittelfinger erkannt“).</summary>
    public string MiddleFingerPreviewStatusText => MiddleFingerPreviewStatus.Text;

    public string MiddleFingerPreviewWarningText => MiddleFingerPreviewStatus.Warning ?? string.Empty;

    public bool HasMiddleFingerPreviewWarning => MiddleFingerPreviewStatus.Warning is not null;

    /// <summary>Hinweis „Zeigen Sie jetzt den Mittelfinger …“, solange etwas aufzuheben ist und die Erkennung scharf ist.</summary>
    public string MiddleFingerPreviewHint => HasMiddleFingerPreviewHint ? MiddleFingerTestHintText : string.Empty;

    public bool HasMiddleFingerPreviewHint => PeacePreview.Mode != PeaceMode.Off && PeacePreview.MiddleFingerWatching;

    /// <summary>Gesichtserkennung nicht verfügbar (nur bei laufender Vorschau; ohne Anforderung ist sie noch unbekannt).</summary>
    public string MiddleFingerPreviewFaceWarning => HasMiddleFingerPreviewFaceWarning ? MiddleFingerFaceUnavailableText : string.Empty;

    public bool HasMiddleFingerPreviewFaceWarning => PeacePreview.Mode != PeaceMode.Off && !PeacePreview.MoustacheAvailable;

    private PeacePreviewStatusLines MiddleFingerPreviewStatus =>
        PeacePreview.Mode == PeaceMode.Off
            ? new PeacePreviewStatusLines(string.Empty, null)
            : PeacePreviewStatusFormatter.Format(PeacePreview.Status, PeacePreview.Hands.Count, PeacePreview.FaceCount,
                peaceNow: false, FunPreviewCameraRunning, PeacePreview.MiddleFingerDetectedNow);

    /// <summary>Test-Knopf: Test-Bart in der Vorschau, den der Mittelfinger aufheben kann (ohne Foto oder Druck).</summary>
    public IRelayCommand TestMiddleFingerCancelCommand { get; }

    private void RaiseMiddleFingerPreviewStatus()
    {
        OnPropertyChanged(nameof(MiddleFingerPreviewStatusText));
        OnPropertyChanged(nameof(MiddleFingerPreviewWarningText));
        OnPropertyChanged(nameof(HasMiddleFingerPreviewWarning));
        OnPropertyChanged(nameof(MiddleFingerPreviewHint));
        OnPropertyChanged(nameof(HasMiddleFingerPreviewHint));
        OnPropertyChanged(nameof(MiddleFingerPreviewFaceWarning));
        OnPropertyChanged(nameof(HasMiddleFingerPreviewFaceWarning));
    }

    private void StartMiddleFingerCancelTest()
    {
        if (_disposed)
            return;
        PeacePreview.StartTestMoustache(MiddleFingerTestMoustacheSeconds);
    }

    // Der Mittelfinger wurde in der Vorschau lange genug gehalten: Bärte verschwinden, ein Selbstzerstörungs-Test endet.
    private void OnPeacePreviewMiddleFinger(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        PeacePreview.VanishMoustaches();
        StopPeaceSelfDestructTest();
    }

    /// <summary>Mittelfingererkennung: mindestens eine Aktion und alle Zahlen im Wertebereich.</summary>
    private void ValidateMiddleFinger(List<string> errors)
    {
        var m = _draft.MiddleFinger;
        // Nur bei eingeschalteter Erkennung, sonst ließe sich der Fehler bei ausgegrauten Aktionen nicht beheben.
        if (m.Enabled && !m.HasAnyAction)
            errors.Add("Wählen Sie für die Mittelfingererkennung mindestens eine Aktion aus.");

        CheckPeaceRange(m.HoldMilliseconds, SettingsLimits.MiddleFingerHoldMillisecondsMin, SettingsLimits.MiddleFingerHoldMillisecondsMax, "Die Haltedauer der Mittelfingererkennung", "Millisekunden", errors);
        CheckPeaceRange(m.SensitivityPercent, SettingsLimits.MiddleFingerSensitivityMin, SettingsLimits.MiddleFingerSensitivityMax, "Die Empfindlichkeit der Mittelfingererkennung", "Prozent", errors);
    }
}
