namespace Fotobox.Core.Peace;

/// <summary>Ordnet eine Hand dem nächsten Gesicht zu (die Person, zu der die Hand gehört).</summary>
public static class HandFaceMatcher
{
    /// <summary>Größter Abstand Handgelenk – Gesichtsmitte in Gesichtsbreiten.</summary>
    public const double MaxDistanceInFaceWidths = 4.5;

    /// <param name="aspect">Breite/Höhe des Bilds (isotrope Abstände).</param>
    /// <returns>Das nächste Gesicht oder null, wenn keines innerhalb von <see cref="MaxDistanceInFaceWidths"/> liegt.</returns>
    public static TrackedFace? Match(HandLandmarks hand, IReadOnlyList<TrackedFace> faces, double aspect = 16.0 / 9)
    {
        if (hand.Points is not { Count: > 0 } || faces.Count == 0)
            return null;
        if (!(aspect > 0))
            aspect = 1;

        var wrist = hand.Points[0];
        TrackedFace? best = null;
        var bestRatio = double.MaxValue;
        foreach (var face in faces)
        {
            var fw = face.Face.Box.Width * aspect;
            if (fw <= 0)
                continue;
            var dx = (wrist.X - face.Face.Box.CenterX) * aspect;
            var dy = wrist.Y - face.Face.Box.CenterY;
            var ratio = Math.Sqrt(dx * dx + dy * dy) / fw;
            if (ratio < bestRatio)
            {
                bestRatio = ratio;
                best = face;
            }
        }
        return best is not null && bestRatio <= MaxDistanceInFaceWidths ? best : null;
    }
}
