namespace Fotobox.Core.Peace;

/// <summary>
/// Plant die Bärte eines Fotos: ordnet die im Foto gefundenen Gesichter den Live-Spuren zu (Stil bleibt je Person gleich) und
/// rechnet die Pose aus den Gesichtspunkten des Fotos. Rein und UI-frei.
/// </summary>
public static class MoustachePhotoPlanner
{
    /// <summary>Mindest-IoU der Gesichtsboxen für eine Zuordnung.</summary>
    public const double MinIoU = 0.3;
    /// <summary>Alternativ: Mittenabstand kleiner als dieser Anteil der Gesichtsbreite.</summary>
    public const double MaxCenterDistanceFaceWidths = 0.5;
    /// <summary>Spuren, die höchstens so lange verloren sind, behalten ihre letzte Live-Pose, wenn das Foto sie nicht findet.</summary>
    public static readonly TimeSpan LivePoseMaxAge = TimeSpan.FromMilliseconds(500);

    private static readonly MoustacheStyle[] RandomStyles =
        [MoustacheStyle.Twirl, MoustacheStyle.Walrus, MoustacheStyle.Chevron, MoustacheStyle.Brush];

    /// <param name="live">Bart-Zustand der Gastansicht.</param>
    /// <param name="photoFaces">Gesichter im Foto (normiert, ungespiegelt); <c>null</c> = Erkennung fehlgeschlagen → Live-Posen.</param>
    /// <param name="imageW">Foto-Breite in Pixeln.</param>
    /// <param name="imageH">Foto-Höhe in Pixeln.</param>
    /// <param name="random"><c>random(n)</c> liefert 0 … n−1 (Stil für Gesichter ohne Spur).</param>
    public static IReadOnlyList<(MoustachePose Pose, MoustacheStyle Style)> Plan(
        MoustachePlanInput live, IReadOnlyList<FaceLandmarks>? photoFaces, int imageW, int imageH, Func<int, int> random)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(random);

        var ownerOnly = live.Targets == MoustacheTargets.PeaceShower;
        var tracks = ownerOnly
            ? live.Tracks.Where(t => live.OwnerFaceId is { } id && t.FaceId == id).ToList()
            : live.Tracks.ToList();
        var result = new List<(MoustachePose Pose, MoustacheStyle Style)>();

        if (photoFaces is null)
        {
            result.AddRange(tracks.Select(t => (t.Pose, t.Style)));
            return result;
        }

        // Alle möglichen Paare, beste zuerst (IoU-Treffer vor reinen Abstandstreffern), gierig vergeben.
        var candidates = new List<(int Face, int Track, double Score)>();
        for (var f = 0; f < photoFaces.Count; f++)
        {
            for (var t = 0; t < tracks.Count; t++)
            {
                if (Score(photoFaces[f], tracks[t], imageW, imageH) is { } score)
                    candidates.Add((f, t, score));
            }
        }

        var faceTrack = new int[photoFaces.Count];
        Array.Fill(faceTrack, -1);
        var trackUsed = new bool[tracks.Count];
        foreach (var (face, track, _) in candidates.OrderByDescending(c => c.Score))
        {
            if (faceTrack[face] >= 0 || trackUsed[track])
                continue;
            faceTrack[face] = track;
            trackUsed[track] = true;
        }

        for (var f = 0; f < photoFaces.Count; f++)
        {
            if (faceTrack[f] >= 0)
            {
                result.Add((MoustacheGeometry.FromFace(photoFaces[f], imageW, imageH), tracks[faceTrack[f]].Style));
            }
            else if (!ownerOnly)
            {
                var style = RandomStyles[Math.Clamp(random(RandomStyles.Length), 0, RandomStyles.Length - 1)];
                result.Add((MoustacheGeometry.FromFace(photoFaces[f], imageW, imageH), style));
            }
        }

        // Spuren, die das Foto nicht fand, aber eben noch im Livebild da waren: letzte Live-Pose.
        for (var t = 0; t < tracks.Count; t++)
        {
            if (!trackUsed[t] && tracks[t].Age < LivePoseMaxAge)
                result.Add((tracks[t].Pose, tracks[t].Style));
        }

        return result;
    }

    /// <summary>Zuordnungsgüte oder null: IoU ≥ <see cref="MinIoU"/> (Güte 1 + IoU) bzw. Mittenabstand &lt; 0,5 Gesichtsbreiten (Güte 0 … 1).</summary>
    private static double? Score(FaceLandmarks face, MoustacheTrack track, int imageW, int imageH)
    {
        var iou = face.Box.IoU(track.FaceBox);
        if (iou >= MinIoU)
            return 1 + iou;

        var widthPx = face.Box.Width * imageW;
        if (widthPx <= 0)
            return null;
        var dx = (face.Box.CenterX - track.FaceBox.CenterX) * imageW;
        var dy = (face.Box.CenterY - track.FaceBox.CenterY) * imageH;
        var limit = MaxCenterDistanceFaceWidths * widthPx;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        return dist < limit ? 1 - dist / limit : null;
    }
}
