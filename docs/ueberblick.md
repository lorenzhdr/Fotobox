# Überblick

Fotobox+ ist eine Windows-Desktop-App (WPF, .NET 10, `net10.0-windows10.0.19041.0`, x64 und ARM64, self-contained). Sie zeigt das Livebild einer Webcam im Vollbild („Gastansicht“). Ein Gast drückt eine Taste (oder klickt), es läuft ein Countdown, die App nimmt Foto(s) auf, speichert sie und druckt sie auf Wunsch. Der Betreiber stellt alles in einer PIN-geschützten Admin-Ansicht ein.

## Funktionen aus Nutzersicht

### Foto

- Ablauf: Auslösen → Countdown (Standard 5 s) → Aufnahme (weißer Blitz) → Speichern → Vorschau (Standard 4 s) → Druck → „Fertig“ (5 s) → zurück zum Livebild.
- Fotos werden als JPEG gespeichert (Standardordner `Bilder\Fotobox+`, Unterordner pro Tag, Dateiname `<Präfix>_<yyyyMMdd_HHmmss>.jpg`, Kollisionen `_2`, `_3` …). Auf Wunsch werden zusätzlich die Originale der Einzelbilder gespeichert.
- Ein Foto besteht aus den rohen Kamera-Pixeln; Zielmarken der Zielerfassung und Overlays der Gastansicht sind nie im Foto. Einzige Ausnahme ist der Schnurrbart der Peaceerkennung; das Kisscam-Foto trägt zusätzlich den Jumbotron-Rahmen (gezeichnet bei der Komposition, die Originale bleiben ungerahmt).

### Layouts

| Layout | Beschreibung |
|---|---|
| Einzelfoto | Ein Foto, randlos über die ganze Seite (`LayoutKind.SingleBorderless`). |
| Fotostreifen, doppelt | 2 gleiche Streifen nebeneinander (zum Teilen), 2–4 Fotos je Streifen (Standard 3), optional Bildunterschrift und Datum (`StripStyle.DoubleCut`). |
| Fotostreifen, einzeln | 1 Streifen mit ganzen Fotos untereinander (`StripStyle.Single`). |
| Overlay | Foto mit einem eigenen PNG-Rahmen darüber. |

Die Gäste können (wenn der Admin mindestens zwei Layouts freigibt) im Leerlauf per Mausrad zwischen den Layouts wechseln; eine Vorschauliste mit Skizzen blendet nach 2 s aus. Die Wahl gilt nur für den nächsten Durchgang.

### Druck

Druck über Windows (`System.Printing`/XPS) auf einen frei wählbaren Drucker, Papiergröße, Ausrichtung und Kopienzahl einstellbar. Während des Drucks zeigt die Gastansicht einen Fortschrittsbalken (erwartete Dauer, Standard 240 s). Ist der Balken voll und der Druck noch nicht fertig, füllt sich darunter ein Ring bis zum Druck-Timeout. Der Druck lässt sich mit der Abbrechen-Taste stornieren. Meldet der Drucker ein Problem (Papier leer, Papierstau, offline, Aufmerksamkeit), erscheint ein Sperrbildschirm („Papier leer – bitte hol jemanden, der es auffüllt …“); die Box bleibt gesperrt (auch Esc wirkt nicht), bis das Foto gedruckt ist, oder der Betreiber die Admin-Ansicht (Strg+Alt+A + PIN) öffnet und schließt – dann endet die Sperre, der Auftrag bleibt in der Warteschlange. Der Druck kann im Admin ganz abgeschaltet werden; dann werden Fotos nur gespeichert. Details: `druck.md`.

### Video

Eigene Start-/Stopptaste (Standard Enter, Umschalten) → optionaler Countdown (3 s) → Aufnahme mit Ton (MP4/H.264, Qualität einstellbar) → Speichern (Standardordner `Videos\Fotobox+`) → Erfolgsmeldung. Kein Druck, keine Vorschau. Details: `video.md`.

### Hinweise in der Gastansicht

Ein Hinweistext (automatisch aus den Tasten oder eigener Text mit Platzhaltern `{Fototaste_N}`, `{Fototasten}`, `{Klick}`) in neun Stilen (Pille, Schlicht, Band, Neon, Sprechblase, Laufschrift, Glas, Zettel, Kino), eine Mausradzeile (fünf Stile) und eine Video-Hinweiszeile oben.

### Admin mit PIN

Strg+Alt+A (frei belegbar) öffnet nach PIN-Abfrage das Einstellungsfenster. Die PIN hat 4–12 Ziffern; nach 5 Fehlversuchen sperrt ein Limiter mit steigender Dauer. Mehr in `admin.md` und `einstellungen.md`.

### Fun-Funktionen

| Funktion | Kurzbeschreibung | Standard |
|---|---|---|
| Zielerfassung | Zielmarken „TARGET n“ über erkannten Personen im Livebild mit Einrast-Animation und Spaßtext (nur Gastansicht, im Leerlauf). | an |
| Peaceerkennung (Easter Egg) | Peace-Zeichen halten löst Selbstzerstörung, Foto mit Countdown oder Schnurrbart aus. Gastansicht verrät nichts. | an |
| Mittelfingererkennung (Easter Egg) | Mittelfinger halten hebt Peace-Effekte auf (Bart entfernen, Selbstzerstörung abbrechen) oder bricht eine laufende Kisscam ab. Wirkt, wenn Peace oder Herz an ist. | an |
| Herzerkennung → Kisscam (Easter Egg) | Zwei Gäste formen mit den Händen ein Herz (oder ein Fingerherz) und halten es: Jumbotron-Rahmen mit Timer zum Kusspartner-Suchen, optional Zoom und Musik, am Ende ein Einzelfoto mit Rahmen. Gastansicht verrät nichts. | an |

Details: `fun-zielerfassung.md`, `fun-peace-mittelfinger.md`, `fun-herz-kisscam.md`.

### Kamerasteuerung

Admin-Tab „Kamera“, Karte „Bildeinstellungen“: Hardware-Regler der Webcam (Zoom, Pan, Tilt, Helligkeit, Kontrast, Farbton, Belichtung, Gegenlicht, Fokus, Weißabgleich, Belichtungskorrektur) mit Live-Wirkung, Flimmerschutz (50/60 Hz) und benannten Profilen je Kamera. Details: `kamera.md`.

### Leistung (Hardwarebeschleunigung)

Admin-Tab „Leistung“: Auswahl, auf welchem Gerät die KI-Erkennung rechnet (Automatisch per Messung, nur Prozessor, Grafikkarte, KI-Beschleuniger), Betriebsstand, Messung per Knopfdruck, Installation zusätzlicher Hersteller-Beschleuniger und die Windows-Grafikeinstellung „Hohe Leistung“. Änderungen wirken nach einem Neustart. Details: `beschleunigung.md`.

### Einstellungen teilen

Admin-Tab „Sicherheit & Daten“: Export und Import der Einstellungen als JSON-Datei (ohne gerätespezifische Werte wie Kamera, Drucker, Ordner), optional mit PIN. Dazu „Standardwerte wiederherstellen“. Details: `einstellungen.md`.

## Bedienung (Standardbelegung)

| Aktion | Standard | Hinweis |
|---|---|---|
| Foto auslösen | Leertaste, linke und rechte Maustaste | Mausklicks einzeln abschaltbar (`TriggerOnLeftClick`/`TriggerOnRightClick`); Doppelklick zählt einmal. Die Maus startet nie ein Video. |
| Admin öffnen | Strg+Alt+A | Frei belegbar. |
| Abbrechen | Esc | Bricht Countdown (auch die Kisscam) still ab, bricht den Video-Countdown ab, storniert einen laufenden Druck (nicht mehr in der Druckersperre). Eine laufende Videoaufnahme beendet sie nicht (dafür die Videotaste). |
| Video starten/beenden | Enter | Gleiche Taste in Start und Stopp = Umschalten. |
| Layout wechseln | Mausrad (im Leerlauf) | Nach oben = vorheriges Layout; `InvertMouseWheel` kehrt um. |

Pro Aktion sind bis zu 8 Tasten möglich (`SettingsLimits.KeysPerActionMax`). Systemkombinationen (Alt+F4, Alt+Tab, Alt+Leertaste, Alt+Esc, Strg+Esc, Strg+Alt+Entf, Windows-Taste) sind als Belegung gesperrt; Alt+F4 und Alt+Leertaste sind in der Gastansicht zusätzlich wirkungslos.

## Projektstruktur

| Ordner | Zweck |
|---|---|
| `src/Fotobox/Core` | UI-freie Logik: `BoothController` (Zustandsmaschine), `Interfaces.cs`, `Settings.cs` (inkl. `SettingsLimits`), `KeyChord`, `PinRules`, `Models.cs` (inkl. `AppInfo`), `AppLog`, `GuestLayouts`, `HintTextFormatter`, `DisplayMonitor`, `CameraServiceExtensions` (`StartWithAudioAsync`), Kamerasteuerung (`CameraControls`, `CameraControlMath`, `CameraControlFormatter`, `LatestOnlyWorker`), `WheelScrollMath`, `SettingsSections`, `DeviceSpecificSettings`. |
| `src/Fotobox/Core/Fun` | UI-freie Logik der Zielerfassung (`FunEngine`, `TargetTracker`, `LockOnTuning`, `ToneSynth`, `OverlayMapper`). |
| `src/Fotobox/Core/Peace` | UI-freie Logik der Peace- und Mittelfingererkennung (Engines, Klassifikatoren, Tracker, Bartgeometrie und -bewegung, `OneEuroFilter`). |
| `src/Fotobox/Core/Heart` | UI-freie Logik der Herzerkennung und Kisscam (`HeartGestureClassifier`, `HeartEngine`, `HeartActions`, `KisscamFrameGeometry`, `KisscamZoom`, `KisscamTextFormatter`). |
| `src/Fotobox/Services/Camera` | WinRT-Kameradienst, Formatwahl, `CameraSupervision` (Timeouts, Watchdog), Videoaufnahme (`WinRtVideoRecording`, `VideoBitrate`), Hardware-Regler (`CameraControlAccess`, `ExtendedCameraControls`, `WinRtCameraService.Controls`). |
| `src/Fotobox/Services/Printing` | Drucken über System.Printing/XPS, Jobstatus-Auswertung, Seitengrößen (`PageSpecCache`). |
| `src/Fotobox/Services/Display` | Monitoraufzählung per Win32 (`Win32DisplayService`, `DisplayNative`). |
| `src/Fotobox/Services/Layout` | Komposition der Druckseite (Einzel, Streifen in zwei Varianten, Overlay; `LayoutMath`). |
| `src/Fotobox/Services/Storage` | Settings-Store (`JsonSettingsStore`), `SettingsNormalizer`, `SettingsTransfer`/`SettingsJson`, `PinService`, `PhotoStorage`, `VideoStorage`, `FolderProbe`. |
| `src/Fotobox/Services/Detection` | Personenerkennung (YOLOX per ONNX, Windows `FaceDetector`); `Detection/Peace`: Gesichts- und Handerkennung (YuNet, MediaPipe); `Detection/Acceleration`: Beschleunigung der ONNX-Modelle über Windows ML (Geräteliste, Messung, Plan, Rückfall auf die CPU; `beschleunigung.md`). |
| `src/Fotobox/Services/Peace` | Foto-Verzierer (Bart im Foto: `MoustachePhotoDecorator`). |
| `src/Fotobox/Core/Music` | UI-freie Logik der Kisscam-Musik (Config, Codec, Zeitformat, Songs, Snapshot, `KisscamSongPicker`). |
| `src/Fotobox/Services/Audio` | Tonausgabe (`FunSoundPlayer`) und Kisscam-Musik (`MusicPlayer`, `KisscamMusicLibrary`, `KisscamMusicFileSystem`). |
| `src/Fotobox/ViewModels` | MVVM mit CommunityToolkit.Mvvm; `Booth` (Gastansicht, Fun-/Peace-Overlays), `Admin` (Einstellungen, PIN-Limiter, Info-Texte). |
| `src/Fotobox/Views` | `Booth` (Gastansicht, `GuestHint`, Overlays; `Booth/Heart`: Kisscam-Rahmen, Zoom, Fortschrittsherz), `Admin` (Fenster, Themes, PIN-Dialoge, Skizzen), `Controls`, `Converters`. |
| `src/Fotobox/Assets/Models` | ONNX-Modelle (`yolox_nano`, YuNet, MediaPipe Palm und Hand-Landmark). |
| `tests/Fotobox.Tests` | xUnit mit Xunit.StaFact für WPF/STA. |
| `build/` | `publish.ps1`, `smoke-test.ps1`, `release-notes.template.md`. |
| `.github/workflows` | `build.yml` (Build + Tests je Push/PR), `release.yml` (Tag `v*`). |
| Repo-Root | `THIRD-PARTY-NOTICES.txt` (Lizenzhinweise, wird mit ausgeliefert), `Directory.Build.props` (Version). |

Laufzeitdaten liegen in `%LOCALAPPDATA%\Fotobox`: `settings.json`, `overlays\`, `logs\`.
