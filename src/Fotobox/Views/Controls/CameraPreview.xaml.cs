// Stub – wird von Stream A implementiert
using System.Windows;
using System.Windows.Controls;
using Fotobox.Core;

namespace Fotobox.Views.Controls;

public partial class CameraPreview : UserControl
{
    public static readonly DependencyProperty CameraProperty =
        DependencyProperty.Register(nameof(Camera), typeof(ICameraService), typeof(CameraPreview), new PropertyMetadata(null));

    public static readonly DependencyProperty MirrorProperty =
        DependencyProperty.Register(nameof(Mirror), typeof(bool), typeof(CameraPreview), new PropertyMetadata(false));

    public CameraPreview()
    {
        InitializeComponent();
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
}
