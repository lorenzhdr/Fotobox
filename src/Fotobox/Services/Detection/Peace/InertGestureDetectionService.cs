using Fotobox.Core;
using Fotobox.Core.Peace;

namespace Fotobox.Services.Detection.Peace;

/// <summary>Platzhalter ohne Erkennung (bis der echte Dienst eingebunden ist): feuert nie und meldet „nicht verfügbar“.</summary>
public sealed class InertGestureDetectionService : IGestureDetectionService
{
    public IGestureRequest Request(GestureRequestOptions options) => new InertRequest();

    public Task<IReadOnlyList<FaceLandmarks>?> DetectFacesInPhotoAsync(BgraImage photo, TimeSpan timeout, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FaceLandmarks>?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class InertRequest : IGestureRequest
    {
        public event EventHandler<GestureUpdate>? Updated { add { } remove { } }
        public event EventHandler<GestureStatus>? StatusChanged { add { } remove { } }
        public GestureStatus Status { get; } = new(false, false, "Nicht verfügbar.", "Nicht verfügbar.", 0);
        public void Update(GestureRequestOptions options) { }
        public void Dispose() { }
    }
}
