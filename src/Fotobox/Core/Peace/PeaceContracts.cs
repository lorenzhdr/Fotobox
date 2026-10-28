using Fotobox.Core.Fun;

namespace Fotobox.Core.Peace;

/// <summary>Aktion, die ein Peace-Zeichen auslöst.</summary>
public enum PeaceAction { SelfDestruct, Photo, Moustache }

/// <summary>Darstellung des Countdowns einer Aufnahme.</summary>
public enum CountdownStyle { Normal, Peace, SelfDestruct }

/// <summary>Ausgelöste Peace-Aktion mit der auslösenden Hand (und dem zugeordneten Gesicht, falls bekannt).</summary>
public sealed record PeaceTrigger(PeaceAction Action, int HandTrackId, int? FaceTrackId, NormPoint HandCenter);

/// <summary>Lage eines Bartes: Mitte normiert (ungespiegelt), Breite und Winkel in Bildpixeln bzw. Bogenmaß.</summary>
public readonly record struct MoustachePose(NormPoint Center, double WidthPx, double AngleRad);

/// <summary>Ein zu zeichnender Bart im Livebild (Stil nie <see cref="MoustacheStyle.Random"/>).</summary>
public sealed record MoustacheView(int FaceId, MoustacheStyle Style, MoustachePose Pose);

/// <summary>Zustand der Bart-Verfolgung für die Foto-Planung.</summary>
public sealed record MoustachePlanInput(MoustacheTargets Targets, int? OwnerFaceId, IReadOnlyList<MoustacheTrack> Tracks);

/// <summary>Eine verfolgte Bartspur; <see cref="Style"/> ist nie <see cref="MoustacheStyle.Random"/>.</summary>
public sealed record MoustacheTrack(int FaceId, MoustacheStyle Style, NormRect FaceBox, MoustachePose Pose, TimeSpan Age);

/// <summary>Eine verfolgte Hand für Anzeige und Admin-Vorschau; <see cref="Progress"/> 0..1 = Haltefortschritt.</summary>
public sealed record HandView(int TrackId, IReadOnlyList<HandPoint> Points, NormRect Box, bool IsPeace, double Progress, int? FaceId);

/// <summary>Momentaufnahme der Peaceerkennung.</summary>
public sealed record PeaceSnapshot(IReadOnlyList<HandView> Hands, IReadOnlyList<FaceLandmarks> Faces)
{
    public static PeaceSnapshot Empty { get; } = new([], []);
}

/// <summary>Optionen für einen Durchgang des <see cref="BoothController"/>.</summary>
public sealed record TriggerOptions
{
    /// <summary>Gast-Layout für diesen Durchgang (null = Standard).</summary>
    public GuestLayout? Layout { get; init; }
    /// <summary>Nur ein Foto: Ein Streifen-Layout wird durch das Einzelfoto ersetzt (Overlay und Einzelfoto bleiben).</summary>
    public bool SingleShotOnly { get; init; }
    /// <summary>Countdown in Sekunden (0 = sofort), null = Einstellung aus dem Ablauf.</summary>
    public int? CountdownSeconds { get; init; }
    public CountdownStyle Style { get; init; } = CountdownStyle.Normal;
}
