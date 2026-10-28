using System.Runtime.InteropServices.WindowsRuntime;
using Fotobox.Core;
using Fotobox.Core.Fun;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace Fotobox.Services.Detection;

// Der Typname Detection kollidiert innerhalb dieses Namespaces mit dem Namespace selbst.
using Detection = Fotobox.Core.Fun.Detection;

/// <summary>
/// Gesichtserkennung mit dem Windows-<c>FaceDetector</c> (WinRT, ohne zusätzliches Paket). Das Verfahren ist leichtgewichtig, liefert
/// aber nur Gesichter, keine Körper. Alle WinRT-Aufrufe laufen auf dem Threadpool; der Detektor wird nie während eines laufenden
/// Aufrufs freigegeben (<see cref="DeferredReleaseGate"/>).
/// </summary>
public sealed class WindowsFaceDetector : IPersonDetector
{
    public static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(3);

    private readonly DeferredReleaseGate _gate;
    private readonly object _initLock = new();
    private readonly object _runLock = new();
    private Task? _initTask;
    private FaceDetector? _detector;
    private BitmapPixelFormat _format = BitmapPixelFormat.Gray8;
    private byte[]? _gray;
    private int _disposeStarted;

    public WindowsFaceDetector()
    {
        _gate = new DeferredReleaseGate(() => _detector = null);
    }

    public DetectionKind Kind => DetectionKind.Face;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_gate.IsReleaseRequested, this);
        Task init;
        lock (_initLock)
            init = _initTask ??= Task.Run(CreateAsync);
        try
        {
            await init.WaitAsync(InitTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Die Gesichtserkennung wurde nicht rechtzeitig geladen.");
        }
    }

    private async Task CreateAsync()
    {
        if (!_gate.TryEnter())
            throw new ObjectDisposedException(nameof(WindowsFaceDetector));
        try
        {
            if (!FaceDetector.IsSupported)
                throw new NotSupportedException("Die Gesichtserkennung wird auf diesem Gerät von Windows nicht unterstützt.");

            var detector = await FaceDetector.CreateAsync();
            var supported = FaceDetector.GetSupportedBitmapPixelFormats();
            var format = BitmapPixelFormat.Gray8;
            if (!supported.Contains(format))
            {
                if (supported.Count == 0)
                    throw new NotSupportedException("Die Gesichtserkennung von Windows meldet kein nutzbares Bildformat.");
                format = supported[0];
            }
            _format = format;
            _detector = detector;
        }
        finally
        {
            _gate.Exit();
        }
    }

    public async Task<IReadOnlyList<Detection>> DetectAsync(DetectionFrame frame, double minConfidence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_gate.IsReleaseRequested, this);
        await InitializeAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return await Task.Run(() => RunAsync(frame, minConfidence), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Detection>> RunAsync(DetectionFrame frame, double minConfidence)
    {
        if (!_gate.TryEnter())
            throw new ObjectDisposedException(nameof(WindowsFaceDetector));
        try
        {
            var detector = _detector ?? throw new InvalidOperationException("Die Gesichtserkennung ist nicht initialisiert.");
            int w = frame.Width, h = frame.Height;
            if (w <= 0 || h <= 0 || frame.Pixels.Length < w * h * 4)
                throw new ArgumentException("Das Analysebild ist ungültig.", nameof(frame));

            SoftwareBitmap? bitmap = null;
            try
            {
                // Eine Analyse zugleich (der Dienst garantiert das); die Sperre schützt nur den wiederverwendeten Graupuffer.
                lock (_runLock)
                {
                    var gray = _gray;
                    if (gray is null || gray.Length != w * h)
                        _gray = gray = new byte[w * h];
                    ToGray8(frame.Pixels, gray, w, h, frame.Stride);
                    bitmap = SoftwareBitmap.CreateCopyFromBuffer(gray.AsBuffer(), BitmapPixelFormat.Gray8, w, h);
                }

                if (_format != BitmapPixelFormat.Gray8)
                {
                    var converted = SoftwareBitmap.Convert(bitmap, _format);
                    bitmap.Dispose();
                    bitmap = converted;
                }

                var faces = await detector.DetectFacesAsync(bitmap);
                var result = new List<Detection>(faces.Count);
                foreach (var face in faces)
                {
                    var b = face.FaceBox;
                    var rect = new NormRect((double)b.X / w, (double)b.Y / h, (double)b.Width / w, (double)b.Height / h).Clamp01();
                    if (rect.IsEmpty)
                        continue;
                    result.Add(new Detection(rect, 1.0, DetectionKind.Face));
                }
                return result;
            }
            finally
            {
                bitmap?.Dispose();
            }
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>Bgra32 → Gray8 (Luma, Gewichte 77/150/29 von 256), ein Durchgang.</summary>
    internal static void ToGray8(byte[] bgra, byte[] gray, int width, int height, int stride)
    {
        for (int y = 0; y < height; y++)
        {
            int src = y * stride;
            int dst = y * width;
            for (int x = 0; x < width; x++, src += 4)
                gray[dst + x] = (byte)((bgra[src] * 29 + bgra[src + 1] * 150 + bgra[src + 2] * 77) >> 8);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;
        await _gate.RequestReleaseAsync(DisposeWait).ConfigureAwait(false);
    }
}
