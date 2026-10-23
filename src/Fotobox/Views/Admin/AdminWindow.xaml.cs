using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fotobox.Core;
using Fotobox.ViewModels.Admin;

namespace Fotobox.Views.Admin;

/// <summary>Modale Admin-Ansicht (Einstellungen). Die Logik liegt im <see cref="AdminViewModel"/>.</summary>
public partial class AdminWindow : Window
{
    private readonly AdminViewModel _viewModel;

    public AdminWindow(AdminViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // Auf kleinen Bildschirmen nicht höher als der Arbeitsbereich.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 20);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 20);

        viewModel.CloseRequested += OnCloseRequested;
        viewModel.PinFieldsCleared += () =>
        {
            NewPinBox.Clear();
            RepeatPinBox.Clear();
        };
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // async void: Eine Ausnahme würde sonst den Dispatcher erreichen und die Anwendung beenden.
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Die Admin-Ansicht konnte nicht vollständig geladen werden.", ex);
        }
    }

    private void OnCloseRequested(bool save)
    {
        // DialogResult schließt das modale Fenster; true = gespeichert.
        try
        {
            DialogResult = save;
        }
        catch (InvalidOperationException ex)
        {
            // Fenster wird bereits geschlossen (z. B. doppelter Klick).
            AppLog.Warn("Admin-Fenster war beim Schließen bereits geschlossen.", ex);
        }
    }

    /// <summary>
    /// Aufnahmemodus der Tasten-Einstellungen: Die nächste Taste (mit Strg/Alt/Umschalt) wird nicht ausgeführt,
    /// sondern der gewählten Aktion zugeordnet. Handled=true, damit weder Leertaste/Eingabetaste den fokussierten
    /// Button auslösen noch Tab den Fokus verschiebt. Abbrechen geht nur über den Button.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsKeyCaptureActive)
            return;

        var key = ResolveKey(e);
        e.Handled = true;

        // Reine Modifikatoren abwarten: erst die eigentliche Taste (z. B. Strg gedrückt halten, dann A) zählt.
        if (IsModifierKey(key))
            return;

        _viewModel.CaptureKey(KeyChord.FromKeyEvent(key, Key.None, Keyboard.Modifiers));
    }

    private static Key ResolveKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin or Key.None;

    /// <summary>Beim Wechsel des Tabs endet die Tastenaufnahme (sonst würden Eingaben auf anderen Tabs verschluckt).</summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged der ComboBoxen im Tab-Inhalt bubbelt bis hierher.
        if (ReferenceEquals(e.OriginalSource, sender) && _viewModel.IsKeyCaptureActive)
            _viewModel.CancelKeyCapture();
    }

    private void OnNewPinChanged(object sender, RoutedEventArgs e) =>
        _viewModel.NewPin = ((PasswordBox)sender).Password;

    private void OnRepeatPinChanged(object sender, RoutedEventArgs e) =>
        _viewModel.RepeatPin = ((PasswordBox)sender).Password;
}
