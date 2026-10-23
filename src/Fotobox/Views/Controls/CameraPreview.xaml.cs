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

    private readonly object _bufferLock = new();
    private readonly Action _renderAction;
    private ICameraService? _subscribed;
    private byte[]? _back;
    private int _backWidth;
    private int _backHeight;
    private int _backStride;
    private int _renderPending;
    private WriteableBitmap? _bitmap;

    public CameraPreview()
    {
        InitializeComponent();
        _renderAction = Render;
        ApplyMirror(Mirror);
        Loaded += (_, _) => Attach(Camera);
        Unloaded += (_, _) => Detach();
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
        if (IsLoaded)
            Attach(camera);
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
        lock (_bufferLock)
        {
            if (_back is null || _back.Length != length)
                _back = new byte[length];
            Buffer.BlockCopy(frame.Pixels, 0, _back, 0, length);
            _backWidth = frame.Width;
            _backHeight = frame.Height;
            _backStride = frame.Stride;
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

        lock (_bufferLock)
        {
            if (_back is null || _backWidth <= 0 || _backHeight <= 0)
                return;

            if (_bitmap is null || _bitmap.PixelWidth != _backWidth || _bitmap.PixelHeight != _backHeight)
            {
                // Bgr32: Alpha der Kamera wird ignoriert (gleiches Speicherlayout wie Bgra32).
                _bitmap = new WriteableBitmap(_backWidth, _backHeight, 96, 96, PixelFormats.Bgr32, null);
                PreviewImage.Source = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, _backWidth, _backHeight), _back, _backStride, 0);
        }
    }

    private void ClearImage()
    {
        lock (_bufferLock)
            _back = null;
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
