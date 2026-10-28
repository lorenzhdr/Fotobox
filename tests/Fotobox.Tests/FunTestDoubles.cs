using Fotobox.Core;
using Fotobox.Core.Fun;
using Microsoft.Extensions.Time.Testing;

namespace Fotobox.Tests;

/// <summary>Erkennungsdienst-Attrappe: merkt sich alle Anforderungen.</summary>
internal sealed class FakeDetectionService : IPersonDetectionService
{
    public List<FakeDetectionRequest> Requests { get; } = [];
    public FakeDetectionRequest Last => Requests[^1];
    public int OpenRequests => Requests.Count(r => !r.IsDisposed);

    public IPersonDetectionRequest Request(FunSettings settings)
    {
        var request = new FakeDetectionRequest(settings);
        Requests.Add(request);
        return request;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeDetectionRequest(FunSettings initial) : IPersonDetectionRequest
{
    public FunSettings Settings { get; private set; } = initial;
    public List<FunSettings> Updates { get; } = [];
    public bool IsDisposed { get; private set; }

    public event EventHandler<DetectionUpdate>? Updated;
    public event EventHandler<DetectionStatus>? StatusChanged;
    public DetectionStatus Status { get; private set; } = DetectionStatus.Starting(DetectionMethod.Body);

    public void UpdateSettings(FunSettings settings)
    {
        Settings = settings;
        Updates.Add(settings);
    }

    public void Dispose() => IsDisposed = true;

    /// <summary>Löst wie der Erkennungsdienst eine Analyse aus (auch nach Dispose, um zu prüfen, dass nichts mehr wirkt).</summary>
    public void RaiseUpdated(IReadOnlyList<Detection> detections, int width = 1280, int height = 720, DetectionStatus? status = null) =>
        Updated?.Invoke(this, new DetectionUpdate(detections, width, height, status ?? Status));

    public void RaiseStatus(DetectionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}

internal sealed class FakeSoundPlayer : IFunSoundPlayer
{
    public List<FunSoundCue> Played { get; } = [];
    /// <summary>Intensität je Aufruf von <see cref="Play(FunSoundCue, int)"/> (gleiche Reihenfolge wie <see cref="Played"/> bei diesen Aufrufen).</summary>
    public List<int> Intensities { get; } = [];
    public int Stops { get; private set; }

    public void Play(FunSoundCue cue) => Played.Add(cue);

    public void Play(FunSoundCue cue, int intensityPercent)
    {
        Played.Add(cue);
        Intensities.Add(intensityPercent);
    }
    public void Stop() => Stops++;
}

/// <summary>Fake-Zeit, die mitzählt, wie viele Timer angelegt und wieder freigegeben wurden.</summary>
internal sealed class TrackingTimeProvider() : FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero))
{
    public int TimersCreated { get; private set; }
    public int TimersDisposed { get; private set; }
    public int ActiveTimers => TimersCreated - TimersDisposed;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        TimersCreated++;
        return new Tracked(base.CreateTimer(callback, state, dueTime, period), this);
    }

    private sealed class Tracked(ITimer inner, TrackingTimeProvider owner) : ITimer
    {
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner.TimersDisposed++;
            inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
