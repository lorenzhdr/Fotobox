using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fotobox.Core;

namespace Fotobox.ViewModels.Admin;

/// <summary>Aktionen, denen Tasten zugeordnet werden können.</summary>
public enum KeyAction { Trigger, Admin, Cancel, VideoStart, VideoStop, PerformanceOverlay }

/// <summary>Eine zugeordnete Taste (Chip) mit Entfernen-Befehl.</summary>
public sealed class KeyChordItem
{
    internal KeyChordItem(KeyChord chord, Action<KeyChordItem> remove, Func<bool> canRemove)
    {
        Chord = chord;
        RemoveCommand = new RelayCommand(() => remove(this), canRemove);
    }

    public KeyChord Chord { get; }
    public string DisplayName => Chord.DisplayName;
    public IRelayCommand RemoveCommand { get; }
}

/// <summary>Tastenbelegung einer Aktion im Tab „Tasten“. Änderungen laufen über das <see cref="AdminViewModel"/>.</summary>
public sealed class KeyActionEditor : ObservableObject
{
    private readonly AdminViewModel _owner;
    private bool _isCapturing;

    internal KeyActionEditor(AdminViewModel owner, KeyAction action, string title, string description, bool isRequired)
    {
        _owner = owner;
        Action = action;
        Title = title;
        Description = description;
        IsRequired = isRequired;
        AddKeyCommand = new RelayCommand(() => _owner.BeginKeyCapture(this), () => CanAddKey);
        Keys.CollectionChanged += (_, _) => RefreshCommands();
    }

    public KeyAction Action { get; }
    public string Title { get; }
    public string Description { get; }

    /// <summary>Mindestens eine Taste muss bleiben (Auslösen, Admin-Ansicht).</summary>
    public bool IsRequired { get; }

    public ObservableCollection<KeyChordItem> Keys { get; } = [];
    public IRelayCommand AddKeyCommand { get; }

    public bool IsCapturing
    {
        get => _isCapturing;
        internal set => SetProperty(ref _isCapturing, value);
    }

    public bool CanAddKey => Keys.Count < SettingsLimits.KeysPerActionMax && !_owner.IsKeyCaptureActive;

    public bool Contains(KeyChord chord) => Keys.Any(k => k.Chord == chord);

    internal bool CanRemoveKey() => !IsRequired || Keys.Count > 1;

    internal KeyChordItem Add(KeyChord chord)
    {
        var item = new KeyChordItem(chord, _owner.RemoveKey, CanRemoveKey);
        Keys.Add(item);
        return item;
    }

    internal void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanAddKey));
        AddKeyCommand.NotifyCanExecuteChanged();
        foreach (var key in Keys)
            key.RemoveCommand.NotifyCanExecuteChanged();
    }
}
