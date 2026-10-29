# Fun „Herzerkennung“ und „Kisscam“

Stand: nach 2.5.2 (Branch `feature/herz-kisscam`, noch nicht veröffentlicht). Die Herzerkennung ist ein verstecktes Extra („Easter Egg“) wie Peace und Mittelfinger: Die Gastansicht verrät nichts davon (kein Hinweistext). Sie nutzt Dienst, Modelle, Analyserate und Vorschau der Peaceerkennung mit. Aktuell gibt es genau eine Herz-Aktion: die **Kisscam**. Pfade sind relativ zum Repo-Root.

| Schicht | Dateien |
|---|---|
| Einstellungen | `src/Fotobox/Core/Settings.cs` (`HeartSettings`, Enums `HeartShape`/`HeartAction`/`KisscamColor`, `SettingsLimits.Heart*`/`Kisscam*`, `MiddleFingerSettings.ActionCancelKisscam`, `MiddleFingerTargets.Kisscam`), Normalisierung `Services/Storage/SettingsNormalizer.cs` (`NormalizeHeart`), `Core/DeviceSpecificSettings.cs` |
| UI-freie Logik | `src/Fotobox/Core/Heart/` (`HeartGestureClassifier`, `HeartEngine`, `HeartActions`, `HeartContracts`, `KisscamTextFormatter`, `KisscamFrameGeometry`, `KisscamZoom`) |
| Durchgang | `Core/BoothController.cs` (`TryTrigger`, `RunPhasesAsync`, `CropPhoto`), `Core/Peace/PeaceContracts.cs` (`TriggerOptions`, `CountdownStyle.Kisscam`, `PhotoFrame`), `Services/Layout/LayoutComposer.cs` (Compose mit Rahmen) |
| Erkennung | `Services/Detection/Peace/PeaceDetectionService.Pipeline.cs` (Option `PairedHands`), `MediaPipeHandDetector.cs` (Handseite) |
| Darstellung | `src/Fotobox/Views/Booth/Heart/` (`KisscamFrameRenderer`, `KisscamPalette`, `KisscamOverlay`, `KisscamFrameElement`, `HeartFeedbackOverlay`, `LiveZoomHost`) |
| Ansichtsmodelle | `ViewModels/Booth/PeaceOverlayViewModel.Heart.cs` (hostet die `HeartEngine`), `BoothViewModel.cs` (`OnHeartAction`, `StartKisscam`, Anzeige-Properties, Musik, `OnMiddleFinger`) |
| Musik | `Services/Audio/MusicPlayer.cs` (`IMusicPlayer`, `Core/Interfaces.cs`) |
| Admin | `Views/Admin/HeartSettingsView`, `KisscamFrameSketch`, `ViewModels/Admin/AdminViewModel.Heart.cs`, `AdminViewModel.MiddleFinger.cs`, `AdminInfoTexts.cs`, `WpfAdminUi.PickAudioFile` |

---

## Funktion und Bedienung

Formen zwei Gäste im `Idle` mit ihren Händen ein Herz (oder ein Gast mit beiden Händen; optional ein Fingerherz mit einer Hand) und halten es (Standard 1 s), startet die Aktion Kisscam:

1. Ein Jumbotron-Rahmen (Lichterkette, Banner „KISS CAM“, vier Eckherzen) erscheint über dem Livebild, ein Hinweistext fordert die Gäste auf, sich einen Kusspartner zu suchen. Der Timer läuft (Standard 10 s, 3–60 s).
2. In den letzten drei Sekunden ersetzen die großen Ziffern 3 – 2 – 1 den Text. Bei einem Timer von höchstens 3 s erscheinen von Anfang an nur die Ziffern, nie der Gasttext (der Admin weist unter dem Timer darauf hin: „Bei 3 Sekunden erscheint nur der Countdown 3 – 2 – 1, kein Text.“). Optional zoomt das Livebild auf die Herz-Gäste, optional läuft Musik.
3. Am Ende folgt der Blitz und ein **Einzelfoto** mit dem Rahmen. Es wird wie jedes andere Foto gespeichert (eigener Dateipräfix) und nach dem Druckschalter im Tab „Drucker“ gedruckt.

Die Kisscam lässt sich mit den Abbrechen-Tasten (Standard Esc) oder per Mittelfinger (Aktion „Kisscam abbrechen“) beenden; es entsteht dann kein Foto. Das Fortschrittsherz (`ShowHoldFeedback`) zeigt dem Gast beim Halten, wie lange er noch durchhalten muss; es ist die einzige sichtbare Spur der Funktion im `Idle`.

## Einstellungen (`AppSettings.Heart`, `HeartSettings`)

Flacher, eigener Abschnitt (Wertebereiche in `SettingsLimits.Heart*`/`Kisscam*`). Er ist unabhängig von `Peace.Enabled` ein- und ausschaltbar und übertragbar (Export/Import) – außer `KisscamMusicPath`.

| Eigenschaft | Standard | Bereich | Bedeutung |
|---|---|---|---|
| `Enabled` | an | | Herzerkennung insgesamt |
| `ShapeClassic` / `ShapeRoof` / `ShapeInverted` / `ShapeFingerHeart` | an / an / aus / aus | | erkannte Formen; der Normalizer erzwingt mindestens eine (sonst Klassisch), im Admin ist die letzte gewählte nicht abwählbar |
| `HoldMilliseconds` | 1000 | 300–3000 ms, Schritt 100 | Haltedauer |
| `SensitivityPercent` | 50 | 10–95 | Empfindlichkeit (legt alle Schwellen und den Mindest-Handscore fest) |
| `ShowHoldFeedback` | an | | Fortschrittsherz beim Halten |
| `Action` | `Kisscam` | `HeartAction` | ausgelöste Aktion (nur hinten erweitern) |
| `KisscamSeconds` | 10 | 3–60 s | Dauer des Countdowns |
| `KisscamText` | `null` | höchstens `HintTextMaxLength` | Gasttext mit Platzhalter `{Sekunden}`; `null`/leer = „Du hast {Sekunden} Sekunden Zeit, dir jemanden zum Küssen zu suchen!“ |
| `KisscamBannerText` | `null` | höchstens 30 Zeichen (`KisscamBannerMaxLength`) | Aufschrift im Banner; `null` = „KISS CAM“ |
| `KisscamColor` | `PinkRed` | `PinkRed`, `Red`, `Gold`, `Purple`, `BlackWhite` | Farbschema des Rahmens (nur hinten erweitern) |
| `KisscamZoomEnabled` | aus | | Digitalzoom auf die Herz-Gäste |
| `KisscamZoomPercent` | 140 | 110–200 %, Schritt 10 | gewünschte Zoomstärke |
| `KisscamFilePrefix` | `Kisscam` | wie `FilePrefixMaxLength` | Dateipräfix des Fotos |
| `KisscamMusicPath` | `null` | höchstens 1024 Zeichen | WAV/MP3; `null` = keine Musik; **gerätespezifisch** |
| `KisscamMusicVolumePercent` | 80 | 0–100 %, Schritt 5 | Lautstärke |

Abgeleitet (`[JsonIgnore]`): `HasAnyShape`, `MinHandScore` (gemeinsame Abbildung `MiddleFingerSettings.MinHandScoreFor`, 10 → 0,9, 95 → 0,55). Details zur Normalisierung: `docs/einstellungen.md`.

## Erkennung (`HeartGestureClassifier`)

Reine Rechnung auf den 21 Handpunkten ohne Zustand. Gerechnet wird **2D-isotrop** (`P = (x · Seitenverhältnis, y)`, Einheit Bildhöhe), die Prüfungen sind also unabhängig von Seitenverhältnis und Bildgröße. Maß für die Handgröße ist `S` = Abstand Handgelenk (Punkt 0) – Mittelfingerwurzel (Punkt 9), bei Paaren gemittelt. Jede Schwelle liegt zwischen **streng** (Empfindlichkeit 10, `t` = 0) und **locker** (95, `t` = 1) und wird linear dazwischen gelegt (`Thresholds.For`). Zusätzlich muss jeder Handscore den `MinHandScore` erreichen.

### Zwei-Hände-Herzen (Paare, `ClassifyPair`)

Alle Bedingungen müssen gelten, sonst wird das Paar mit einem kurzen deutschen Grund (`RejectReason`, nur Log/Admin) verworfen:

| Prüfung | streng → locker (fest, wo ohne Pfeil) | Bedeutung |
|---|---|---|
| Größenverhältnis der Hände | höchstens 1,6 → 2,2 | zwei Hände ähnlicher Entfernung |
| Handgelenke auseinander | mindestens 1,5 S (fest) | nicht übereinanderliegend; schließt Gebetshände aus |
| Nicht dieselbe Hand | mittlerer Punktabstand ≥ 0,5 S | Doppelerkennungen derselben Hand |
| Zeigefinger-Kontakt | `K` = kleinster Abstand zwischen den Zeigefingerpunkten 6/7/8 (PIP, DIP, Spitze) der beiden Hände ≤ 0,30 → 0,55 S | oben berühren sich bei gekrümmten Zeigefingern die Fingerrücken, nicht die Spitzen |
| Daumen-Kontakt | Abstand der Daumenspitzen (Punkt 4) ≤ 0,30 → 0,55 S | die beiden Daumenspitzen unten |
| Höhe des Herzens | `h` = Strecke Daumenspitzen-Mitte → Gipfel-Mitte, 0,60 → 0,40 S bis höchstens 3,0 S | „Gipfel“ je Hand = der vom eigenen Daumen (Punkt 4) am weitesten entfernte Zeigefingerpunkt aus 6/7/8 |
| Neigung | Herzachse (Daumenspitzen-Mitte → Gipfel-Mitte) höchstens 30° → 70° aus der Senkrechten (aufrecht oder auf dem Kopf) | |
| Handgelenke beidseits | auf verschiedenen Seiten der Herzachse, je ≥ 0,3 S entfernt | schließt Gebetshände aus |
| Daumen und Zeigefinger geöffnet | `O` = größte Entfernung der Daumenspitze zu den Punkten 5…8 derselben Hand ≥ 0,80 → 0,55 der eigenen Handgröße (je Hand) | |

Die Werte (gekrümmte Zeigefinger statt gestreckter Spitzen, größere Neigung, geringere Höhe/Öffnung, größerer Handgelenkabstand) sind an echten Aufnahmen kalibriert: Ein Herz zwischen Gesicht und Brust zeigt stark gekrümmte Zeigefinger, deren Spitzen weit auseinander stehen, während sich PIP/DIP berühren; Kopf- und Körperneigung bis etwa 50° kommen vor.

**Formen** (disjunkt, in dieser Reihenfolge entschieden):

- **Umgedreht** (`Inverted`): Die Herzachse zeigt nach unten (Gipfel unter den Daumenspitzen innerhalb der Neigungsgrenze).
- **Dach** (`Roof`): aufrecht, und bei beiden Händen sind mindestens zwei der Finger Mittel/Ring/klein gestreckt (`ExtensionFactor` 1,25 → 1,05, PIP-Winkel ≥ 165° → 140°) und zeigen höchstens 60° von der Senkrechten ab.
- **Klassisch** (`Classic`): aufrecht und nicht Dach – es gibt keine eigene Prüfung der übrigen Finger (der Admin-Text nennt sie „eingerollt oder locker gekrümmt“; das ist die Absicht, keine Bedingung im Code). Der Admin-Text zu Dach lautet „Mindestens zwei der übrigen Finger gestreckt nach oben“.

Die Güte (`Score`) fällt linear mit den Kontaktabständen; die Kosten (`Cost` = (`K` + Daumenspitzen-Abstand) / S) entscheiden über die Paarwahl. Herzmitte und Größe (`0,8 · h`) beziehen sich auf die Herzachse Daumenspitzen-Mitte → Gipfel-Mitte.

### Fingerherz (`ClassifyFingerHeart`, experimentell)

Eine Hand: Daumenspitze am Zeigefinger (Spitze oder DIP) ≤ 0,30 → 0,45 S; Zeigefinger nicht in der Faust (Spitze ≥ 0,7 S von der Handfläche, Reichweite > 1,1 × Handgelenk–Zeigefingerwurzel); Mittel-, Ring- und kleiner Finger gebeugt (ab `t` ≥ 0,5 genügen Mittelfinger plus ein weiterer); nur bei strenger Einstellung (`t` < 0,5) muss der Daumen den Zeigefinger kreuzen. Es wird öfter fälschlich erkannt (daher standardmäßig aus).

### Paarbildung (`FindHearts`)

- **Nicht endliche Werte:** Eine Hand mit NaN/Unendlich in Punkten, Score oder Box (kaputtes Modellergebnis) wird früh verworfen (`IsFinite` in `ClassifyPair`/`ClassifyFingerHeart`, Grund „Ungültige Handpunkte.“); sonst würde `Math.Sign` auf NaN eine `ArithmeticException` werfen, und Vergleiche mit NaN bestünden die Schwellen fälschlich.
- Bis `MaxHands` = 8 Hände werden paarweise geprüft; Treffer werden gierig nach Kosten vergeben, **jede Hand höchstens einmal**. Eine Hand eines nicht aktivierten Zwei-Hände-Herzens gilt trotzdem als vergeben (sonst würde daraus ein Fingerherz).
- Danach prüft das Fingerherz die übrigen Hände (nur mit `ShapeFingerHeart`).
- Ergebnis: Treffer nach Güte absteigend (`HeartMatch`: Form, Handindizes, Mitte, Größe, Box, Score).
- Es gibt **keine Personenbindung**: Ein Herz können zwei Gäste mit je einer Hand oder ein Gast mit zwei Händen bilden. Die Gesichter der beteiligten Hände (`HandFaceMatcher`) dienen nur dem Kisscam-Zoom.
- `HeartSettings.Enabled` prüft die Engine, nicht der Klassifikator.

## Handerkennung für Paare: `PairedHands`

`GestureRequestOptions.PairedHands` (gesetzt von `PeaceOverlayViewModel.BuildOptions`, wenn die Herzerkennung an ist) ändert die Hand-Pipeline, weil sich bei einem Herz die Hände berühren und die normale Duplikatprüfung die zweite Hand schlucken würde. Drei Regeln (`PeaceDetectionService.Pipeline.cs`):

1. **Suchbereich erst bei zwei verfolgten Händen abgedeckt:** Normal genügt eine verfolgte Hand im Suchbereich eines Gesichts, um die Palm-Erkennung dort zu sparen. Mit `PairedHands` müssen es `PalmsPerRegion` (2) sein, sonst wird weiter nach der zweiten Hand gesucht (`RegionCovered`).
2. **Eine verfolgte Hand absorbiert höchstens eine nahe Palm-ROI:** Pro Palm-Lauf gilt eine Palm-ROI nur dann als Duplikat einer verfolgten Hand, wenn ihre Mitte näher als 0,25 × ROI-Größe am Handflächenschwerpunkt (Mittel der Punkte 0, 5, 9, 13, 17) liegt (`PairedTrackedPalmDistance`), und jede verfolgte Hand absorbiert davon nur die nächste. Weitere Kandidaten werden nie wegen derselben verfolgten Hand verworfen: Sie werden entweder als Palm-ROI übernommen (Palm-gegen-Palm-Prüfung mit der üblichen Schwelle bleibt) oder landen im Landmark-Lauf, wo `IsPairedDuplicate` (Regel 3) echte Doppelerkennungen zusammenfasst. Gemessen im Diagnose-Log (eine Person formt allein ein Herz): Doppelerkennungen der verfolgten Hand liegen bei 6 bis 47 px, die zweite Hand bei ca. 100 bis 160 px; die frühere Schwelle 0,5 × ROI-Größe (140 bis 175 px) verschluckte die zweite Hand fast immer (`AddPalmRois`). Die Obergrenzen `MaxPalmRuns`/`MaxLandmarkRuns` bleiben unberührt.
3. **Ergebnis-Duplikate über Landmarkabstand und Handseite:** Zwei Handergebnisse sind dieselbe Hand, wenn der mittlere 2D-Punktabstand (in Pixeln) unter 0,3 × der kleineren Handlänge (Punkt 0 – 9) liegt **und** die Handseiten nicht eindeutig verschieden sind (`IsPairedDuplicate`). Statt der normalen Box-IoU-Schwelle (> 0,5).

**Handseite:** `MediaPipeHandDetector` liest, falls das Landmark-Modell die Ausgabe `lefthand_0_or_righthand_1` liefert, die Händigkeit (`HandLandmarks.Handedness`, 0 = links, 1 = rechts, sonst `null`). Eindeutig verschieden heißt: beide Werte mehr als 0,3 von 0,5 entfernt und auf verschiedenen Seiten (`HandednessMargin`). Eine linke und eine rechte Hand, die sich überlagern, werden so nie zusammengefasst; fehlt die Ausgabe, zählt nur der Abstand.

Es bleibt bei `MaxPalmRuns` = 3 und `MaxLandmarkRuns` = 4 je Zyklus: **Höchstens vier Hände** werden je Analyse ausgewertet (siehe `docs/bekannte-grenzen.md`).

## `HeartEngine` (UI-frei, `TimeProvider`)

`Core/Heart/HeartEngine.cs`, Zustandsmaschine wie `PeaceEngine`/`MiddleFingerEngine`, nicht threadsicher (UI-Thread):

- **Halten global:** Es gibt keine Paar-Identität über Bilder hinweg; das Halten gilt für „irgendein Herz“, ein Wechsel der beteiligten Hände ist unerheblich. Nach `HoldMilliseconds` löst die Engine genau einmal `Triggered` aus (`HeartTrigger`: Aktion, Form, Mitte, `HeartBox`, `FaceBoxes` der beteiligten Personen).
- **Aussetzer:** Ein kurzer Ausfall des Herzens setzt das Halten nicht zurück. Toleranz `GapTolerance` = max(`PeaceEngine.MinGapTolerance`, 2,5 Analyseabstände); dieselbe Regel wie bei Peace (`Configure(settings, analysesPerSecond)`).
- **Wiederscharf nur nach beobachteter Ruhe:** Nach einer Auslösung ist die Engine entwaffnet und wird erst wieder scharf, wenn `RearmQuietTime` (mindestens `PeaceEngine.MinRearmQuietTime`, sonst 2,5 Analyseabstände) lang **in Hand-Updates beobachtet** kein Herz zu sehen war. Während Foto/Kisscam laufen nur Gesichts-Updates; die reine Uhrzeit genügt deshalb nicht (ein stehengebliebenes Herz würde sofort wieder auslösen). Lücken zwischen Hand-Updates, länger als die Ruhefrist, zählen als unbeobachtet.
- **`Configure` löst nie aus:** Es übernimmt Einstellungen und Rate und aktualisiert nur Snapshot und `Changed`; `Triggered` kommt ausschließlich aus `OnUpdate` und `Tick`. Der Aufrufer konfiguriert mitten in `PeaceOverlayViewModel.SetMode`, ein Auslösen dort würde synchron einen Kisscam-Durchgang starten und die Betriebsart verschachtelt umschalten. Zusätzlich verwirft `SetMode` nach dem Konfigurieren den Rest des Aufrufs, falls sich `_mode` (oder die Entsorgung) in der Zwischenzeit geändert hat.
- **Nur im Leerlauf:** `GesturesEnabled` ist nur im `PeaceMode.Watching` (also `BoothState.Idle`) und bei eingeschalteter Erkennung wahr. Sonst wird weder ausgelöst noch gehalten; die Prüfung „Herz wieder weg“ läuft weiter. `Reset()` behält die Entwaffnung.
- `Snapshot` (`HeartSnapshot`: ein `HeartView` mit Mitte, Größe, Fortschritt 0..1, Form; `Armed`) speist das Fortschrittsherz (`HeartFeedbackOverlay`), `DetectedNow`/`DetectedShape` die Admin-Anzeige.
- Log: `Herzerkennung: Herz erkannt (Form …, 1 Hand/2 Hände), Aktion …`.

### Warum die Engine im `PeaceOverlayViewModel` sitzt

`PeaceDetectionService` bedient nur die **oberste** Anforderung (`IGestureRequest`-Stapel). Die Gastansicht hat genau eine Anforderung – die des `PeaceOverlayViewModel`. Ein eigenes Herz-ViewModel mit eigener Anforderung würde die der Peaceerkennung verdrängen (oder umgekehrt). Deshalb hostet das `PeaceOverlayViewModel` (partial `PeaceOverlayViewModel.Heart.cs`) zusätzlich die `HeartEngine`, teilt Hände, Analyserate (`PeaceSettings.AnalysesPerSecond`) und Mindestscore (der niedrigste der aktiven Funktionen gilt) und füttert die Engine im selben Update. Die Herz-Engine bekommt die Hände **zuletzt**: Löst sie aus, startet der Aufrufer synchron einen Durchgang und schaltet die Erkennung dabei um oder aus. `SetMode(…, heart, peaceGestures)`: `peaceGestures` = false schaltet nur die Peace-Gesten ab (Herz ohne Peace; Bärte und Mittelfinger bleiben unberührt). `BoothViewModel.UpdatePeace` läuft, sobald Peace **oder** Herz eingeschaltet ist.

### Neue Herz-Aktion ergänzen

1. Enum `HeartAction` in `Core/Settings.cs` **hinten** erweitern.
2. Eintrag im Katalog `HeartActions.All` (`Core/Heart/HeartActions.cs`; Anzeigename + Beschreibung für die Admin-Wahlkarte; ein Test prüft, dass Enum und Katalog übereinstimmen).
3. Fall im `switch` von `BoothViewModel.OnHeartAction`.
4. Admin: Aktions-Optionen kommen automatisch aus dem Katalog; Einstellungskarte der Aktion in `HeartSettingsView` sichtbar machen (`IsKisscamActionSelected` als Vorbild), Validierung in `ValidateHeart` und Normalisierung in `NormalizeHeart` ergänzen.
5. Neue Properties in `AppSettings`-Abschnitten einordnen (siehe `docs/einstellungen.md`), Tests.

## Kisscam-Ablauf

`BoothViewModel.OnHeartAction` ist wie `OnPeaceAction` gesperrt bei Shutdown, offener Admin-Ansicht, startender Kamera und außerhalb von `Idle`. `StartKisscam`:

1. Zoomausschnitt planen (nur bei `KisscamZoomEnabled`, siehe unten), `PhotoFrame(Kisscam, Banner, Farbe)` bauen und beides **vor** dem Start merken (der Zustandswechsel aktualisiert die Anzeige sofort).
2. `Controller.TryTrigger(TriggerOptions { SingleShotOnly, CountdownSeconds = KisscamSeconds, Style = Kisscam, CropRegion, FilePrefix, Frame })`. Scheitert der Start, verfallen die gemerkten Werte.
3. `ResetLayoutSelection()`, danach Musik starten (falls Pfad gesetzt), Logzeile `Kisscam gestartet (… s, Zoom …, Musik …)`.

Im `BoothController` (`TryTrigger`):

- **Countdown-Klemmung stilabhängig:** Normale und Peace-Durchgänge werden auf 0–30 s begrenzt, `CountdownStyle.Kisscam` auf 3–60 s (`SettingsLimits.Kisscam*`).
- **Frame erzwingt das Einzelfoto** (`GuestLayout.Single`, auch bei Streifen oder Overlay). Das Layout der Einstellungen ändert sich nicht: Der Durchgang arbeitet auf einer Kopie (`AppSettings.Clone`), ebenso der abweichende `FilePrefix` und die Countdown-Dauer.
- **Reihenfolge der Aufnahme:** Aufnahme → `Decorate` (Bart, 3 s Frist) → Zuschnitt (`CropPhoto`, wenn `CropRegion`) → Spiegeln (`Camera.MirrorPhoto`). Der Zuschnitt kommt nach dem Verzieren, weil der Bartplaner mit den Koordinaten des ganzen Bildes rechnet, und vor dem Spiegeln, weil der Ausschnitt für das **ungespiegelte** Rohfoto gilt. Fehler beim Zuschnitt ergeben das unbeschnittene Foto (Warnung im Log), nie einen Fehlerzustand.
- **Komposition:** `LayoutComposer.Compose(…, frame)` lässt das erste Foto die ganze Seite füllen (crop-to-fill, schwarzer Grund) und zeichnet darüber nur den Rahmen. **Keine** Bildunterschrift, kein Datum, kein Overlay-PNG, kein Streifen. Seitengröße und dpi bleiben unverändert.
- **Speichern und Druck** wie bei jedem Foto (`Flow.PrintingEnabled`, Vorschau, Abbruch des Drucks). Die gespeicherten **Originale** (falls aktiviert) sind die zugeschnittenen, ungerahmten Fotos.
- `CountdownStyle` bleibt bis nach dem Blitz `Kisscam`; die Anzeige liest ihn bis dahin.

### Anzeige in der Gastansicht

| Property (`BoothViewModel`) | Wann |
|---|---|
| `IsKisscamCountdown` | Countdown mit Stil Kisscam |
| `ShowKisscamFrame` | Stil Kisscam in `Countdown` und `Capturing` (der Rahmen bleibt bis zum Blitz-Ende sichtbar) |
| `ShowKisscamText` | Kisscam-Countdown mit `CountdownValue > 3` (`KisscamFinalDigitsSeconds`) |
| `ShowCountdown` | wie üblich, aber solange der Gasttext läuft **nicht**: die Abdunklung würde den Rahmen verdecken; in den letzten 3 s kommen die großen Ziffern |
| `KisscamText` | `KisscamTextFormatter.Format(KisscamText, CountdownValue)`: ersetzt `{Sekunden}` (Groß-/Kleinschreibung egal) durch die Restzeit; leerer Text = Standardtext; unbekannte Platzhalter bleiben stehen (`FindUnknownPlaceholders` meldet sie dem Admin) |
| `KisscamFrame` / `KisscamZoomRegion` | Rahmen bzw. Ausschnitt des laufenden Durchgangs |

`KisscamOverlay` (UserControl, nach dem Countdown-Raster in `BoothWindow.xaml`) zeichnet den Rahmen über die ganze Fläche und den Gasttext unten zwischen den unteren Eckherzen auf einem dunklen, halbtransparenten Band (bewusst ohne Schatteneffekte über dem Livebild). Der Zoom steckt im `LiveZoomHost` um das Livebild.

## Rahmen (`KisscamFrameGeometry`, `KisscamFrameRenderer`)

Eine Geometrie für Livebild und Foto: Alle Maße hängen nur von der kürzeren Kante `m` der Zeichenfläche ab (das Banner zusätzlich von der Breite). UI-frei (`double`), damit ohne WPF testbar.

| Maß | Wert |
|---|---|
| Rahmenstärke | 0,03 m |
| Banner | Höhe 0,13 m, Eckradius 0,03 m, ragt um 35 % der Rahmenstärke in den Rahmen; Breite mindestens 0,46 × Breite, wächst mit dem gemessenen Text bis höchstens 0,90 × Breite und so, dass die oberen Eckherzen frei bleiben |
| Schrift | 0,075 m (Segoe UI Black), bei zu langem Text bis 50 % verkleinert (`MinTextScale`), danach mit „…“ gekürzt |
| Eckherzen | vier, Kante 0,11 m, Abstand 0,02 m zum Rahmen |
| Lichterkette | auf der Mitte des Rahmenbandes im Abstand 0,05 m, Radius 0,0075 m, ohne Stellen unter dem Banner |

- **Bézier-Herzen statt Emojis:** Die Eckherzen sind Geometrien (zwei Lappen, Spitze unten), keine Emoji-Zeichen – Emojis würden in einer `RenderTargetBitmap` nicht farbig. Dieselbe Herzform nutzt das Fortschrittsherz (`HeartFeedbackOverlay`).
- **Farben** (`KisscamPalette.For(KisscamColor)`): je Schema `Frame`, `FrameDark`, `Banner`, `BannerEdge`, `Accent`, `Text`, `Heart`, `HeartEdge`. Es sind Bildfarben (wie `MoustachePalette`), keine Admin-Farben, daher **nicht** in `AdminColors.*.xaml`. Das Rahmenband ist vollflächig in `Frame` gefüllt (darauf prüfen Tests).
- `KisscamFrameRenderer.Draw(dc, area, frame, dpi)` zeichnet live (`KisscamFrameElement`) und im Foto (`DrawingVisual` im Composer, 96 dpi).

## Zoom (`KisscamZoom`, `LiveZoomHost`)

- **Planung** (`Plan(heartBox, faceBoxes, zoomPercent)`): Mitte = Mitte der Vereinigung aus Herzbox und den Gesichtern der beteiligten Personen. Der Zoom wird so weit gesenkt, dass die Vereinigung samt 15 % Rand (`Margin`) hineinpasst. Ergebnis: Rechteck mit normierter Breite = Höhe = 1 / Zoom (behält also das Bildseitenverhältnis), ins Bild verschoben, nie verkleinert; `null`, wenn kein Zoom übrig bleibt (Zoom ≤ 100 %, leere Herzbox, ungültige Werte).
- **Live:** `LiveZoomHost` (Decorator um das Livebild samt Zielmarken, Bärten, Herz-Anzeige) skaliert und verschiebt sein Kind (`ViewTransform`), sodass der Ausschnitt die Ansicht füllt und das Bild die Ansicht ohne Rand bedeckt. 400 ms Animation (`AnimationMilliseconds`); alle Overlays liegen im selben Kind und bleiben deckungsgleich. Berücksichtigt Spiegelung und `OverlayFit` (Standard füllend).
- **Foto:** `BoothController.CropPhoto` schneidet das **ungespiegelte** Rohfoto mit `KisscamZoom.ToPixelRect` zu (Pixelrechteck, auf das Bild geklemmt, mindestens 1 × 1). Der Rahmen wird danach um den Ausschnitt gelegt; das Foto behält das Seitenverhältnis der Kamera.
- **Grenzen:** Der Zoom ist digital und kostet Auflösung (im Admin mit Hinweis). Weicht das Seitenverhältnis der Seite von dem des Kamerabilds ab, sind Live-Ausschnitt (füllend in der Ansicht) und Foto (crop-to-fill auf die Seite) minimal verschieden – gleiche Mitte, leicht anderer Rand.

## Musik (`MusicPlayer`, `IMusicPlayer`)

`IMusicPlayer` (`Play(path, volume, loop)`, `FadeOut(duration)`, `Stop()`, `IsPlaying`, `PlaybackChanged`, `PlaybackFailed`) wirft nie und blockiert nie; Singleton in `App.RegisterServices`, im Konstruktor von `BoothViewModel` und `AdminViewModel` optional (ohne Player läuft die Kisscam stumm).

- **Verhalten:** Die Musik startet mit dem Kisscam-Countdown (in Schleife, Lautstärke 0–100 %), blendet beim Blitz in 800 ms aus und endet; bei Abbruch (Taste, Mittelfinger, Fehler → Zustand `Idle`/`Error`/`Starting`) und beim Beenden der App stoppt sie sofort. `UpdateKisscamMusic` merkt sich per `_kisscamPending`, dass ein Kisscam-Countdown gesehen wurde.
- **Technik:** WPF-`MediaPlayer` (Media Foundation) auf einem eigenen STA-Thread mit `Dispatcher`, der erst beim ersten `Play` entsteht; Aufrufer posten nur per `BeginInvoke` und warten nie. Jedes `Play`/`Stop` erhöht einen Generationszähler, Ereignisse und Timer älterer Generationen werden verworfen.
- **Fristen:**

| Konstante | Wert | Bedeutung |
|---|---|---|
| `OpenTimeout` | 3 s | Öffnen der Datei (`MediaOpened`); danach still aufgegeben |
| `DisposeWait` | 1 s | längste Wartezeit von `Dispose` auf das Thread-Ende |
| `FadeStep` | 50 ms | Schrittweite des Ausblendens (Dauer 800 ms beim Blitz) |

- **Fehlschlag melden:** Scheitert ein Start (Datei fehlt, Pfad ungültig, kein Media Foundation, `MediaFailed`, verpasste Öffnungsfrist), feuert `PlaybackFailed` (nur für die aktuelle Generation, Handler dürfen werfen), weil `PlaybackChanged` dann nie kommt (es wurde nie gespielt). Der Admin setzt darauf das Anhören zurück (Knopf wieder „Anhören“) und zeigt `KisscamMusicPlayFailedText` („Die Datei konnte nicht abgespielt werden.“); der Hinweis verfällt bei neuem Anhören, Teststart, Dateiwechsel und Seiten-/Tabwechsel. Im Kisscam-Test läuft der Test ohne Ton weiter. Die Gastansicht reagiert nicht darauf (die Warnung steht im Log, die Kisscam läuft stumm).
- **Stumm ohne Datei:** Kein Pfad, fehlende/kaputte Datei, fehlendes Media Foundation (Windows N ohne Media Feature Pack) oder verpasste Öffnungsfrist ergeben nur eine Logwarnung und Stille – nie einen Fehlerzustand und nie einen Hänger der Kisscam.
- **Gerätespezifisch:** `Heart.KisscamMusicPath` steht in `DeviceSpecificSettings.Paths` (wird beim Teilen nicht übertragen, beim Import behält der Zielrechner seinen Pfad), der Normalizer verwirft zu lange Pfade (> 1024 Zeichen) und Pfade mit Steuerzeichen.
- Der Admin spielt die Musik zum Anhören über denselben Player (siehe `docs/admin.md`).

## Abbruch

- **Abbrechen-Tasten** (`Input.CancelCountdownKeys`, Standard Esc): `BoothViewModel.TryCancel` → `TryCancelCountdown`; zurück zu `Idle`, kein Foto. Ab der Aufnahme (Blitz) ist es zu spät.
- **Mittelfinger:** Mit `MiddleFinger.ActionCancelKisscam` beendet ein gehaltener Mittelfinger den laufenden Kisscam-Countdown wie die Abbrechen-Taste (`OnMiddleFinger`, nur `BoothState.Countdown` mit Stil Kisscam). Im Kisscam-Countdown zählt die Kisscam als `MiddleFingerTargets.Kisscam`: Die Erkennung bleibt dann im Countdown mit Händen an (`PeaceMode.FollowMoustache`), sobald `MiddleFingerSettings.Wants` es verlangt. Log: `Mittelfinger: Kisscam abgebrochen.`
- Beide Wege enden in `UpdateKisscamMusic` (Musik aus, Merker verfallen, Log `Kisscam abgebrochen.`). Die Peace-Zweige des Mittelfingers (Bart entfernen, Selbstzerstörung abbrechen) sind seit der Kisscam an `ActionCancelPeace` gebunden (siehe `docs/fun-peace-mittelfinger.md`).

## Logging

`AppLog`: `Herzerkennung: Herz erkannt (Form …, 1 Hand/2 Hände), Aktion Kisscam.`, `Kisscam gestartet (… s, Zoom aus/… %, Musik an/aus).`, `Kisscam abgebrochen.`, `Mittelfinger: Kisscam abgebrochen.`, Warnungen bei Musik- und Zuschnittfehlern. Jede Fun-Auslösung steht im Log.

**`Herz-Diagnose:`-Zeilen (Level Info, gedrosselt, ohne Einfluss auf die Erkennung):** Für Nachtests, wenn ein Herz nicht erkannt wird. Alle Zeilen beginnen mit `Herz-Diagnose:` (Filter z. B. `Select-String "Herz-Diagnose"` im Tageslog); es werden nur Zahlen und normierte Landmarks geloggt, nie Bilder. Auswertung: „Pipeline“ unterscheidet Handverlust in der Hand-Pipeline (Landmark-Scores auch verworfener Hände gegen den Mindestscore, von einer verfolgten Hand absorbierte Palm-ROIs, Paar-Duplikatprüfung mit Abstand, Schwelle und Handseiten); „Hände ohne Herz“ zeigt, welche Klassifikatorprüfung scheitert; die Ereigniszeilen zeigen, ob ein Treffer nur vereinzelt kommt und das Halten wegen `GapTolerance` neu beginnt.
- `Herz-Diagnose: Pipeline: …` (`PeaceDetectionService.Pipeline.cs`, `PairedHandDiagnostics`): nur bei `PairedHands`, höchstens 1 Zeile/s und nur, wenn Gesicht, verfolgte Hand, Palm- oder Landmark-Ergebnis vorhanden sind. Enthält Gesichter, verfolgte Hände, Palmsuchen (Gesichtsbereiche, Vollbild), Palm-Treffer, übernommene und verworfene Palm-ROIs (mit Abstand/Schwelle in Pixeln), Mindestscore, Roh-Score jedes Landmark-Laufs (`IHandLandmarkDetector.SetScoreObserver`, auch unter dem Mindestscore verworfene), Hände vor/nach der Paar-Duplikatprüfung samt mittlerem Abstand, Schwelle und Handseiten. Der Sammler wird wiederverwendet; in Zyklen ohne Zeile entsteht keine Allokation und keine Formatierung.
- `Herz-Diagnose: Hände ohne Herz: …` (`HeartEngine.OnUpdate`): bei mindestens einer Hand ohne Herz höchstens 1 Zeile/s (Engine-Zeit). Hände, Scores, Seitenverhältnis, Empfindlichkeit, Mindest-Handscore, scharf/Gesten erlaubt, dann für das nächstliegende Paar (`HeartGestureClassifier.FindNearestPair`: kleinste Summe aus Zeigefinger-Kontakt `K` und Daumenspitzenabstand relativ zu S) die komplette `HeartGestureClassifier.Diagnose` (jede Prüfgröße mit Schwelle und `ok`/`FEHL`, ohne beim ersten Fehlschlag abzubrechen; `ClassifyPair` bleibt unverändert und liefert das Urteil) und die rohen 21 Landmarks beider Hände (normiert, 3 Nachkommastellen) zum Nachrechnen; bei einer einzelnen Hand `DiagnoseFingerHeart`.
- Ereigniszeilen (ungedrosselt): `Herz-Diagnose: Herz-Treffer beginnt …` und `Herz-Diagnose: Halten abgebrochen (Grund); Lücke … ms, GapTolerance … ms; Fortschritt … von … ms`.

## Tests

Siehe `docs/tests.md`: `HeartGestureClassifierTests` (mit `HeartHandFixtures`), `HeartDiagnoseTests`, `HeartEngineTests`, `HeartActionsTests`, `HeartSettingsTests`, `HeartPipelineTests` (`PairedHands`), `KisscamFrameGeometryTests`, `KisscamZoomTests`, `KisscamTextFormatterTests`, `BoothControllerKisscamTests`, `BoothViewModelKisscamTests`, `PeaceOverlayViewModelHeartTests`, `LayoutComposerKisscamTests`, `MusicPlayerTests`, `AdminHeartTabTests`, `HeartViewsTests`, `KisscamVisualRenderTests` (Visual).
