using System.Windows.Media.Imaging;
using Fotobox.Core;
using Fotobox.Core.Peace;

namespace Fotobox.Tests;

/// <summary>Gestendienst-Attrappe: merkt sich alle Anforderungen und Fotoanfragen.</summary>
internal sealed class FakeGestureService : IGestureDetectionService
{
    public List<FakeGestureRequest> Requests { get; } = [];
    public FakeGestureRequest Last => Requests[^1];
    public int OpenRequests => Requests.Count(r => !r.IsDisposed);

    /// <summary>Antwort von <see cref="DetectFacesInPhotoAsync"/> (null = Fehler/Timeout).</summary>
    public IReadOnlyList<FaceLandmarks>? PhotoFaces { get; set; }
    /// <summary>Wie oft <see cref="DetectFacesInPhotoAsync"/> aufgerufen wurde.</summary>
    public int PhotoDetectCalls { get; private set; }
    public List<BgraImage> PhotoImages { get; } = [];

    public IGestureRequest Request(GestureRequestOptions options)
    {
        var request = new FakeGestureRequest(options);
        Requests.Add(request);
        return request;
    }

    public Task<IReadOnlyList<FaceLandmarks>?> DetectFacesInPhotoAsync(BgraImage photo, TimeSpan timeout, CancellationToken ct = default)
    {
        PhotoDetectCalls++;
        PhotoImages.Add(photo);
        return Task.FromResult(PhotoFaces);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeGestureRequest(GestureRequestOptions initial) : IGestureRequest
{
    /// <summary>Zuletzt gesetzter Bedarf (Anforderung oder <see cref="Update"/>).</summary>
    public GestureRequestOptions Options { get; private set; } = initial;
    public List<GestureRequestOptions> Updates { get; } = [];
    public bool IsDisposed { get; private set; }

    public event EventHandler<GestureUpdate>? Updated;
    public event EventHandler<GestureStatus>? StatusChanged;
    public GestureStatus Status { get; private set; } = GestureStatus.Starting;

    public void Update(GestureRequestOptions options)
    {
        Options = options;
        Updates.Add(options);
    }

    public void Dispose() => IsDisposed = true;

    /// <summary>Löst wie der Dienst eine Analyse aus (auch nach Dispose, um zu prüfen, dass nichts mehr wirkt).</summary>
    public void RaiseUpdated(GestureUpdate update) => Updated?.Invoke(this, update);

    public void RaiseUpdated(IReadOnlyList<FaceLandmarks> faces, IReadOnlyList<HandLandmarks> hands, int width = 1280, int height = 720) =>
        Updated?.Invoke(this, new GestureUpdate(faces, hands, width, height, Status));

    public void RaiseStatus(GestureStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}

/// <summary>Foto-Verzierer-Attrappe: zeichnet Aufrufe auf, kann verzögern, scheitern oder ein anderes Bild liefern.</summary>
internal sealed class FakePhotoDecorator : IPhotoDecorator
{
    public bool IsActive { get; set; } = true;
    public int Calls { get; private set; }
    public List<(BitmapSource Photo, int ShotIndex)> Received { get; } = [];
    /// <summary>Wartezeit vor der Antwort (echte Zeit; <see cref="CancellationToken"/> bricht ab).</summary>
    public TimeSpan? Delay { get; set; }
    public Exception? Throw { get; set; }
    /// <summary>Gelieferte Antwort; null = das empfangene Foto unverändert.</summary>
    public BitmapSource? Result { get; set; }

    public async Task<BitmapSource> DecorateAsync(BitmapSource rawPhoto, int shotIndex, CancellationToken ct)
    {
        Calls++;
        Received.Add((rawPhoto, shotIndex));
        if (Delay is { } delay)
            await Task.Delay(delay, ct);
        if (Throw is not null)
            throw Throw;
        return Result ?? rawPhoto;
    }
}
