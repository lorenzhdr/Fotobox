using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fotobox.Core;

namespace Fotobox.Views.Controls;

/// <summary>Zeigt das Livebild eines <see cref="ICameraService"/> (Latest-frame-wins) samt Statushinweis.</summary>
public partial class CameraPreview : UserControl
{
    public static readonly DependencyProperty CameraProperty =
        DependencyProperty.Register(nameof(Camera), typeof(ICameraService), typeof(CameraPreview),
            new PropertyMetadata(null, (d, e) => ((CameraPreview)d).OnCameraChanged((ICameraService?)e.NewValue)));

    public static readonly DependencyProperty MirrorProperty =
        DependencyProperty.Register(nameof(Mirror), typeof(bool), typeof(CameraPreview),
            new PropertyMetadata(false, (d, e) => ((CameraPreview)d).ApplyMirror((bool)e.NewValue)));

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(nameof(Stretch), typeof(Stretch), typeof(CameraPreview),
            new PropertyMetadata(Stretch.UniformToFill));

    /// <summary>Zeigt den Statushinweis über dem Bild (die Booth blendet ihn aus und zeigt eigene Meldungen).</summary>
    public static readonly DependencyProperty ShowStatusOverlayProperty =
        DependencyProperty.Register(nameof(ShowStatusOverlay), typeof(bool), typeof(CameraPreview),
            new PropertyMetadata(true, (d, _) => ((CameraPreview)d).UpdateStatus()));

    // Puffer-Tausch (Triple-Buffering): Jeder Puffer gehört zu jedem Zeitpunkt genau einer Stelle (Kamera-Thread beim
    // Kopieren, _ready, UI-Thread beim WritePixels oder _spare). Unter _bufferLock werden nur Referenzen getauscht;
    // die eigentlichen Kopien laufen außerhalb des Locks, so dass weder der UI-Thread die Kamera-Pipeline noch
    // umgekehrt aufhält.
    private readonly object _bufferLock = new();
    private readonly Action _renderAction;
    private ICameraService? _subscribed;
    private byte[]? _ready;
    private byte[]? _spare;
    private int _readyWidth;
    private int _readyHeight;
    private int _readyStride;
    private int _renderPending;
    private WriteableBitmap? _bitmap;

    public CameraPreview()
    {
        InitializeComponent();
        _renderAction = Render;
        ApplyMirror(Mirror);
        // Das Livebild wird nur bei geladenem UND sichtbarem Steuerelement verarbeitet: Ein eingeklapptes Steuerelement
        // (z. B. eine nicht angezeigte Admin-Seite) bekommt zwar Loaded, soll aber keine Frames kopieren.
        Loaded += (_, _) => SyncAttachment();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => SyncAttachment();
    }

    public ICameraService? Camera
    {
        get => (ICameraService?)GetValue(CameraProperty);
        set => SetValue(CameraProperty, value);
    }

    public bool Mirror
    {
        get => (bool)GetValue(MirrorProperty);
        set => SetValue(MirrorProperty, value);
    }

    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public bool ShowStatusOverlay
    {
        get => (bool)GetValue(ShowStatusOverlayProperty);
        set => SetValue(ShowStatusOverlayProperty, value);
    }

    private void OnCameraChanged(ICameraService? camera)
    {
        if (IsLoaded && IsVisible)
            Attach(camera);
    }

    /// <summary>Hängt sich an die Kamera, wenn das Steuerelement geladen und sichtbar ist, sonst ab.</summary>
    private void SyncAttachment()
    {
        if (IsLoaded && IsVisible)
            Attach(Camera);
        else
            Detach();
    }

    private void ApplyMirror(bool mirror) =>
        MirrorHost.RenderTransform = mirror ? new ScaleTransform(-1, 1) : Transform.Identity;

    private void Attach(ICameraService? camera)
    {
        Detach();
        if (camera is not null)
        {
            _subscribed = camera;
            camera.FrameArrived += OnFrameArrived;
            camera.StatusChanged += OnStatusChanged;
        }
        UpdateStatus();
    }

    private void Detach()
    {
        if (_subscribed is { } camera)
        {
            camera.FrameArrived -= OnFrameArrived;
            camera.StatusChanged -= OnStatusChanged;
            _subscribed = null;
        }
        ClearImage();
    }

    // Hintergrund-Thread: Frame kopieren, höchstens ein Render-Auftrag ausstehend.
    private void OnFrameArrived(object? sender, CameraFrame frame)
    {
        var length = Math.Min(frame.Pixels.Length, frame.Stride * frame.Height);

        // Freien Puffer übernehmen (gehört ab jetzt diesem Thread), außerhalb des Locks kopieren, dann veröffentlichen.
        // Ist keiner frei (UI-Thread zeichnet noch), wird das noch nicht angezeigte Bild überholt und sein Puffer
        // wiederverwendet; so bleibt es bei höchstens drei Puffern statt einer Neuanlage je Frame.
        byte[]? buffer;
        lock (_bufferLock)
        {
            if (_spare is not null)
            {
                buffer = _spare;
                _spare = null;
            }
            else
            {
                buffer = _ready;
                _ready = null;
            }
        }
        if (buffer is null || buffer.Length != length)
            buffer = new byte[length];
        Buffer.BlockCopy(frame.Pixels, 0, buffer, 0, length);

        lock (_bufferLock)
        {
            // Ein noch nicht angezeigtes Bild wird überholt (Latest-frame-wins); sein Puffer wird wiederverwendet.
            if (_ready is not null && _spare is null)
                _spare = _ready;
            _ready = buffer;
            _readyWidth = frame.Width;
            _readyHeight = frame.Height;
            _readyStride = frame.Stride;
        }

        if (Interlocked.Exchange(ref _renderPending, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Render, _renderAction);
    }

    private void OnStatusChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, UpdateStatus);

    private void Render()
    {
        Interlocked.Exchange(ref _renderPending, 0);
        if (_subscribed is null)
            return;

        // Fertigen Puffer übernehmen (gehört ab jetzt dem UI-Thread); die Kopie in die Bitmap läuft außerhalb des Locks.
        byte[]? buffer;
        int width, height, stride;
        lock (_bufferLock)
        {
            buffer = _ready;
            _ready = null;
            width = _readyWidth;
            height = _readyHeight;
            stride = _readyStride;
        }

        if (buffer is null || width <= 0 || height <= 0)
            return;

        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            // Bgr32: Alpha der Kamera wird ignoriert (gleiches Speicherlayout wie Bgra32).
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            PreviewImage.Source = _bitmap;
        }

        try
        {
            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, stride, 0);
        }
        finally
        {
            lock (_bufferLock)
            {
                if (_spare is null)
                    _spare = buffer;
            }
        }
    }

    private void ClearImage()
    {
        lock (_bufferLock)
        {
            _ready = null;
            _spare = null;
        }
        _bitmap = null;
        PreviewImage.Source = null;
    }

    private void UpdateStatus()
    {
        var camera = _subscribed;
        if (camera is null)
        {
            StatusOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var status = camera.Status;
        if (status == CameraStatus.Running)
        {
            StatusOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        ClearImage();
        StatusText.Text = !string.IsNullOrWhiteSpace(camera.StatusMessage)
            ? camera.StatusMessage
            : status switch
            {
                CameraStatus.Starting => "Kamera wird gestartet …",
                CameraStatus.NoDevice => "Keine Kamera gefunden.",
                CameraStatus.AccessDenied => "Kein Zugriff auf die Kamera.",
                CameraStatus.Error => "Die Kamera ist nicht verfügbar.",
                _ => "Kamera nicht aktiv.",
            };
        StatusOverlay.Visibility = ShowStatusOverlay ? Visibility.Visible : Visibility.Collapsed;
    }
}
