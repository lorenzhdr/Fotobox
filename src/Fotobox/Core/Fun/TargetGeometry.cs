namespace Fotobox.Core.Fun;

/// <summary>
/// Berechnet aus einer Erkennung die Zielbox: Kopf, Oberkörper oder ganze Person (<see cref="TargetScope"/>). Bei Gesichtern
/// wird hochgerechnet, bei Ganzkörperboxen ein Ausschnitt genommen. Ergebnis immer auf das Bild (0..1) gekappt.
/// </summary>
public static class TargetGeometry
{
    // Gesicht → Kopf: leicht vergrößert, etwas nach oben verschoben (Haare).
    public const double FaceHeadScale = 1.4;
    public const double FaceHeadRaise = 0.1;          // Anteil der Gesichtshöhe
    // Gesicht → Oberkörper / ganze Person: Breite und Höhe als Vielfaches des Gesichts, Oberkante = Kopfoberkante.
    public const double FaceUpperBodyWidth = 3.0;
    public const double FaceUpperBodyHeight = 4.0;
    public const double FaceFullBodyWidth = 3.5;
    public const double FaceFullBodyHeight = 8.0;
    // Ganzkörperbox → Kopf / Oberkörper.
    public const double BodyHeadHeight = 0.18;
    public const double BodyHeadWidth = 0.40;
    public const double BodyUpperBodyHeight = 0.55;

    public static NormRect ToTargetBox(Detection d, TargetScope scope)
    {
        var box = d.Kind == DetectionKind.Face ? FromFace(d.Box, scope) : FromBody(d.Box, scope);
        return box.Clamp01();
    }

    private static NormRect FromFace(NormRect face, TargetScope scope)
    {
        var cx = face.CenterX;
        // Kopfoberkante: vergrößerter Kopf, um FaceHeadRaise nach oben verschoben.
        var headTop = face.CenterY - face.Height * FaceHeadScale / 2 - face.Height * FaceHeadRaise;

        switch (scope)
        {
            case TargetScope.UpperBody:
                return Centered(cx, headTop, face.Width * FaceUpperBodyWidth, face.Height * FaceUpperBodyHeight);
            case TargetScope.FullBody:
                return Centered(cx, headTop, face.Width * FaceFullBodyWidth, face.Height * FaceFullBodyHeight);
            default:
                return Centered(cx, headTop, face.Width * FaceHeadScale, face.Height * FaceHeadScale);
        }
    }

    private static NormRect FromBody(NormRect body, TargetScope scope)
    {
        switch (scope)
        {
            case TargetScope.UpperBody:
                return new NormRect(body.X, body.Y, body.Width, body.Height * BodyUpperBodyHeight);
            case TargetScope.FullBody:
                return body;
            default:
                return Centered(body.CenterX, body.Y, body.Width * BodyHeadWidth, body.Height * BodyHeadHeight);
        }
    }

    private static NormRect Centered(double centerX, double top, double width, double height) =>
        new(centerX - width / 2, top, width, height);
}
