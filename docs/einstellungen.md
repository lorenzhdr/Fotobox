# Einstellungen

Alle Einstellungen liegen in einem Objekt `AppSettings` (`src/Fotobox/Core/Settings.cs`), das als `settings.json` in `%LOCALAPPDATA%\Fotobox` gespeichert wird. Daneben liegen dort `overlays\` (Overlay-PNGs) und `logs\`.

Wertebereiche stehen ausschließlich in `SettingsLimits` (gleiche Datei). Admin-UI, Validierung und Normalisierung nutzen sie gemeinsam; Grenzen dürfen nirgends dupliziert werden. Das gilt auch für Textlängen (`FilePrefixMaxLength` 40, `CaptionMaxLength` 80, `HintTextMaxLength` 100 …); zu lange Werte aus der Datei kürzt der Normalizer.

## Abschnitte von `AppSettings`

`AppSettings.Version` (aktuell 2, `CurrentVersion`) und `Security` sind keine übertragbaren Abschnitte; alle anderen Klassen-Eigenschaften sind es (siehe „Einstellungen teilen“).

| Abschnitt | Klasse | Inhalt |
|---|---|---|
| `Camera` | `CameraSettings` | Gerät, Auflösung, Spiegeln, Reglerwerte/Profile (gerätegebunden), Flimmerschutz |
| `Flow` | `FlowSettings` | Countdown, Vorschau, Pausen, Fehleranzeige, Druck an/aus, erwartete Druckdauer, Töne (Countdown-Piepen, Kamera-Sound) |
| `Layout` | `LayoutSettings` | Layoutart, Streifen, Overlay, Bildunterschrift, Schrift, Gast-Auswahl |
| `Printer` | `PrinterSettings` | Drucker, Papier, Ausrichtung, Kopien, Timeout |
| `Storage` | `StorageSettings` | Fotoordner, Unterordner pro Tag, Originale, JPEG-Qualität, Präfix |
| `Security` | `SecuritySettings` | PIN-Hash, Salt, Iterationen |
| `Input` | `InputSettings` | Tastenbelegung, Mausklicks, Mausrad |
| `GuestScreen` | `GuestScreenSettings` | Hinweistexte und -stile der Gastansicht |
| `Appearance` | `AppearanceSettings` | Farbschema des Admins, Zoom des Admin-Inhaltsbereichs, Monitor der Gastansicht |
| `PrintArea` | `PrintAreaSettings` | Druckbereich-Markierung in der Gastansicht (seit 2.10.0) |
| `PerformanceOverlay` | `PerformanceOverlaySettings` | Leistungsanzeige oben rechts in der Gastansicht (seit 2.10.1); übertragbar |
| `Video` | `VideoSettings` | Videomodus |
| `Fun` | `FunSettings` | Zielerfassung |
| `Peace` | `PeaceSettings` | Peaceerkennung (Easter Egg) |
| `MiddleFinger` | `MiddleFingerSettings` | Mittelfingererkennung (Easter Egg) |
| `Heart` | `HeartSettings` | Herzerkennung und Kisscam (Easter Egg, `fun-herz-kisscam.md`) |
| `Performance` | `PerformanceSettings` | Beschleuniger der KI-Erkennung (CPU/GPU/NPU/Auto), gewählte Grafikkarte, Windows-Grafikeinstellung (`beschleunigung.md`); komplett gerätespezifisch |

### Wichtige Felder mit Standard und Bereich

| Feld | Standard | Bereich (`SettingsLimits`) |
|---|---|---|
| `Camera.MirrorPreview` / `MirrorPhoto` | an / aus | – |
| `Camera.Powerline` | 50 Hz | Enum `CameraPowerline` (inkl. „Nicht verändern“) |
| `Camera.ControlProfiles` | leer | höchstens `CameraProfilesMax` 10 je Kamera, Name ≤ `CameraProfileNameMaxLength` 40, Id ≤ `CameraDeviceIdMaxLength` 512 |
| `Flow.CountdownSeconds` | 5 | 1–30 |
| `Flow.PreviewSeconds` | 4 | 1–60 |
| `Flow.PauseBetweenShotsSeconds` | 2 | 0–30 |
| `Flow.ErrorDisplaySeconds` | 6 | 2–60 |
| `Flow.PrintingEnabled` | an | – |
| `Flow.ExpectedPrintSeconds` | 240 | 30–600 |
| `Flow.CountdownBeepEnabled` / `CountdownBeepSound` | an / `Soft` | Enum `Soft, Bright, WoodBlock` (nur hinten erweitern; unbekannt → `Soft`) |
| `Flow.ShutterSoundEnabled` / `ShutterSound` | an / `Slr` | Enum `Slr, Compact, Retro` (nur hinten erweitern; unbekannt → `Slr`) |
| `Flow.CountdownBeepVolumePercent` / `ShutterVolumePercent` | 70 / 70 | 0–100 in 5er-Schritten (`SoundVolumeMin`/`Max`/`Step`/`Default`; der Normalizer klemmt und rundet, `NormalizeSoundVolume`); 0 = still |
| `Layout.Kind` | `SingleBorderless` | `SingleBorderless`, `Strip`, `Overlay` |
| `Layout.StripPhotoCount` | 3 | 2–4 |
| `Layout.StripStyle` | `DoubleCut` | `DoubleCut`, `Single` |
| `Layout.CaptionText` | – | ≤ 80 Zeichen |
| `Layout.CaptionFontFamily` | Segoe UI | ≤ `FontNameMaxLength` 100 |
| `Layout.GuestChoices` | leer (kein Wechsel) | `GuestLayout`-Liste, Reihenfolge = Scroll-Reihenfolge |
| `Printer.Copies` | 1 | 1–20 |
| `Printer.TimeoutSeconds` | 300 | 10–900, mindestens `ExpectedPrintSeconds` + 60 |
| `Printer.Orientation` | `Auto` | `Auto`, `Portrait`, `Landscape` |
| `Storage.JpegQuality` | 92 | 50–100 |
| `Storage.FilePrefix` | `Fotobox+` | ≤ 40 Zeichen |
| `Storage.DateSubfolder` / `SaveOriginals` | an / an | – |
| `Input.TriggerKeys` / `AdminKeys` / `CancelCountdownKeys` | `Space` / `Ctrl+Alt+A` / `Escape` | je ≤ 8 Tasten |
| `Input.VideoStartKeys` / `VideoStopKeys` | `Enter` / `Enter` | je ≤ 8 Tasten, dürfen leer sein |
| `Input.PerformanceOverlayKeys` | `Ctrl+Alt+Y` | ≤ 8 Tasten, darf leer sein; Vorrang nach allen anderen Aktionen |
| `PerformanceOverlay.Show…` | alle an | `ShowCpu`, `ShowCpuClock`, `ShowCpuTemperature`, `ShowGpu`, `ShowGpuClock`, `ShowGpuTemperature`, `ShowGpuMemory`, `ShowNpu`, `ShowRam`, `ShowCameraFps`, `ShowDisplayFps`, `ShowAnalysisRate`, `ShowCycleTime`, `ShowModelDevices` |
| `PerformanceOverlay.Style` | `Text` | `Text, Bars` (nur hinten erweitern; unbekannt → Text) |
| `PerformanceOverlay.RefreshMs` | 1000 | 500–5000 (`PerformanceOverlayRefreshMs*`) |
| `PerformanceOverlay.FontSize` | 18 | 12–36 (`PerformanceOverlayFontSize*`) |
| `PerformanceOverlay.BackgroundOpacityPercent` | 60 | 0–100 (`PerformanceOverlayBackgroundOpacity*`) |
| Ein/Aus der Leistungsanzeige | aus | wird nicht gespeichert; nach dem App-Start immer ausgeblendet |
| `Input.TriggerOnLeftClick` / `TriggerOnRightClick` | an / an | – |
| `Input.InvertMouseWheel` | aus | – |
| `GuestScreen.HintText` / `VideoHintText` / `WheelHintText` | leer (automatisch) | ≤ 100 Zeichen |
| `GuestScreen.HintStyle` / `VideoHintStyle` | `Pill` | `Pill, Plain, Banner, Neon, Bubble, Ticker, Glass, Note, Cinema` |
| `GuestScreen.ShowWheelHint` / `WheelHintStyle` | an / `Matching` | `Matching, SmallPill, Plain, Chip, Arrows` |
| `Appearance.AdminTheme` | `System` | `System, Light, Dark` |
| `Appearance.AdminZoomPercent` | 80 | 60–100 in 5er-Schritten (`AdminZoomMin`/`AdminZoomMax`/`AdminZoomStep`/`AdminZoomDefault`); der Normalizer klemmt zuerst und rundet dann auf das nächste Vielfache von 5 (62 → 60, 63 → 65, 83 → 85, 30 → 60, 150 → 100); fehlt das Feld in der Datei, gilt 80 |
| `Appearance.GuestMonitorId` / `GuestMonitorName` | null (Hauptbildschirm) | Id ≤ 512, Name ≤ 200 |
| `PrintArea.Enabled` | aus | – |
| `PrintArea.Style` | `LinesShaded` | `Lines, LinesShaded, Corners` (nur hinten erweitern; unbekannt → Standard) |
| `PrintArea.Color` | `White` | `White, Black, Red, Yellow, Green, Blue, Pink, Orange` (feste Palette, nur hinten erweitern; unbekannt → Standard) |
| `PrintArea.OpacityPercent` | 80 | 20–100 in 5er-Schritten (`PrintAreaOpacity*`) |
| `PrintArea.ThicknessPx` | 3 | 1–10 (`PrintAreaThickness*`) |
| `PrintArea.ShadePercent` | 40 | 10–80 in 5er-Schritten (`PrintAreaShade*`), wirkt nur bei `LinesShaded` |
| `Video.Enabled` | an | – |
| `Video.MaxDurationSeconds` | 300 | 10–1800 |
| `Video.CountdownSeconds` | 3 | 0–30 |
| `Video.MinDurationSeconds` | 2 | 0–30, immer < Höchstdauer (Normalizer setzt sonst auf Höchstdauer − 1) |
| `Video.WarningSeconds` | 10 | 0–120 (0 = aus) |
| `Video.SuccessDisplaySeconds` | 3 | 1–60 |
| `Video.SuccessText` | „Video gespeichert! 🎬“ | ≤ 80 Zeichen |
| `Video.ShowStopHint` | an | – |
| `Video.StopHintText` | leer (Standard „Drücke {Taste}, um die Videoaufnahme zu beenden“) | ≤ `HintTextMaxLength` (100) Zeichen, Platzhalter `{Taste}`, `{Stopptaste_N}`, `{Stopptasten}` |
| `Video.Quality` | `High` | `High, Medium, Low` |
| `Video.RecordAudio` | an | – |
| `Video.MicrophoneId` / `MicrophoneName` | null (Windows-Standard) | Id ≤ 512, Name ≤ 200 |
| `Video.Folder` / `FilePrefix` | `Videos\Fotobox+` / `Fotobox+_Video` | Präfix ≤ 40 |
| `Fun.Enabled` | an | – |
| `Fun.Method` / `Scope` | `Body` / `FullBody` | – |
| `Fun.TextSeconds` | 6 | 2–30 |
| `Fun.CooldownSeconds` | 20 | 0–600 |
| `Fun.RepeatEnabled` / `RepeatSeconds` | aus / 60 | 10–600 |
| `Fun.SwapTriggers` | an | – |
| `Fun.MinSizePercent` | 15 | 5–80 |
| `Fun.MaxTargets` | 6 | 1–10 |
| `Fun.SensitivityPercent` | 50 | 10–95 |
| `Fun.DwellMilliseconds` | 800 | 0–3000 |
| `Fun.LockOnMilliseconds` | 1200 | 500–5000 (100er-Schritte) |
| `Fun.LockOnIntensityPercent` | 50 | 0–100 (5er-Schritte) |
| `Fun.MaxLockOnRestarts` | 3 | 0–10 |
| `Fun.AnalysesPerSecond` | 5 | 1–10 |
| `Peace.Enabled` | an | – |
| `Peace.ActionSelfDestruct` / `ActionPhoto` / `ActionMoustache` | an / aus / aus | mindestens eine (Normalizer erzwingt Selbstzerstörung) |
| `Peace.HoldMilliseconds` | 1000 | 300–3000 (100er) |
| `Peace.CooldownSeconds` | 0 | 0–600 |
| `Peace.SensitivityPercent` | 50 | 10–95 |
| `Peace.AnalysesPerSecond` | 5 | 1–10 (gemeinsam mit Mittelfinger) |
| `Peace.SelfDestructSeconds` | 10 | 5–30 |
| `Peace.PhotoCountdownSeconds` | 3 | 0–30 |
| `Peace.MoustacheDurationSeconds` | 15 | 3–120 |
| `Peace.MoustacheAutoPhotoCountdownSeconds` | 3 | 0–30 |
| `Peace.RandomStyles` | alle sieben Formen | Normalizer verwirft `Random`, Unbekanntes, Duplikate, sortiert; leer = alle |
| `MiddleFinger.Enabled` / `ActionCancelPeace` / `ActionCancelKisscam` | an / an / an | mindestens eine Aktion (Normalizer setzt sonst `ActionCancelPeace`) |
| `MiddleFinger.HoldMilliseconds` | 500 | 200–3000 (100er) |
| `MiddleFinger.SensitivityPercent` | 50 | 10–95 |
| `Heart.Enabled` | an | – |
| `Heart.ShapeClassic` / `ShapeRoof` / `ShapeInverted` / `ShapeFingerHeart` | an / an / aus / aus | mindestens eine (Normalizer erzwingt Klassisch) |
| `Heart.HoldMilliseconds` | 1800 | 300–3000 (100er, `HeartHoldMilliseconds*`) |
| `Heart.SensitivityPercent` | 50 | 10–95 (`HeartSensitivity*`) |
| `Heart.ShowHoldFeedback` | an | – |
| `Heart.Action` | `Kisscam` | Enum `HeartAction` |
| `Heart.KisscamSeconds` | 10 | 3–60 |
| `Heart.KisscamText` | null (Standardtext) | ≤ `HintTextMaxLength` 100, Platzhalter `{Sekunden}` |
| `Heart.KisscamBannerText` | null („KISS CAM“) | ≤ `KisscamBannerMaxLength` 30 |
| `Heart.KisscamColor` | `PinkRed` | `PinkRed, Red, Gold, Purple, BlackWhite` |
| `Heart.KisscamZoomEnabled` / `KisscamZoomPercent` | aus / 140 | 110–200 in 10er-Schritten |
| `Heart.KisscamFilePrefix` | `Kisscam` | ≤ 40 Zeichen, nie leer |
| `Heart.KisscamMusicFolder` | null (Standard `Musik\Fotobox+\Kisscam`) | ≤ `KisscamMusicFolderMaxLength` 1024, gerätespezifisch; Songwerte (Häkchen, Startzeit 0–3599 s, Song-Lautstärke 10–100 % in 5er-Schritten) stehen nicht hier, sondern in `kisscam-musik.json` im Musikordner (`KisscamSongStartSeconds*`, `KisscamSongVolume*`, `KisscamMusicMaxSongs` 500 in `SettingsLimits`) |
| `Heart.KisscamMusicVolumePercent` | 80 | 0–100 in 5er-Schritten |
| `Performance.Accelerator` | `Auto` | Enum `AcceleratorMode` (`Auto, Cpu, Gpu, Npu`); undefiniert → `Auto`; gerätespezifisch, wirkt nach Neustart |
| `Performance.GpuDeviceKey` | null (leistungsstärkste Grafikkarte) | ≤ `GpuDeviceKeyMaxLength` 200, ohne Steuerzeichen, getrimmt; sonst/leer → null; gerätespezifisch |
| `Performance.PreferHighPerformanceGpu` | an | setzt die Windows-Grafikeinstellung „Hohe Leistung“ für die Fotobox; gerätespezifisch |

Die Bedeutung der Fun-, Video-, Kamera- und Druckfelder erklären die jeweiligen Detaildateien. `Tasten`-Felder: `architektur.md`.

Enums werden in Dateien als Namen geschrieben. Neue Enum-Werte nur hinten anfügen (`HintStyle`, `TargetBoxStyle`, `TargetColor`, `MoustacheStyle`, `CameraControlKind`, `FunPage`, `HeartShape`, `HeartAction`, `KisscamColor`, `AcceleratorMode` …). `MiddleFingerTargets` ist ein Flags-Enum (`None`, `Moustache`, `SelfDestruct`, `Kisscam`).

## `JsonSettingsStore` (`src/Fotobox/Services/Storage/JsonSettingsStore.cs`)

### Schreiben

- `Save` normalisiert eine Kopie (`SettingsNormalizer.Normalize(settings.Clone())`), serialisiert sie und schreibt atomar: erst in `settings.json.<guid>.tmp` (UTF-8 ohne BOM, `Flush(true)`), dann `File.Replace` bzw. `File.Move`. Bei Fehlern wird die `.tmp` entfernt und die Ausnahme weitergereicht.
- Danach wird `SettingsChanged` ausgelöst. Die Handler werden einzeln aufgerufen; wirft einer, wird das geloggt, `Save` gilt trotzdem als erfolgreich.

### Laden und Fail-closed

- Fehlt die Datei: Erststart mit Standardwerten (ohne PIN).
- Ist die Datei vorhanden, aber gesperrt/nicht lesbar: 3 Leseversuche mit je 200 ms Pause; danach `LoadFailed = true`, `Save` wirft eine `IOException`, und der Admin-Zugang bleibt zu. Hintergrund: Sonst würde die PIN fälschlich als „nicht gesetzt“ gelten und ein Gast könnte eine neue festlegen. `AdminLauncher.EnsurePinConfigured` zeigt „Erneut versuchen / Beenden“ und ruft `TryReload()`.
- Enums liest `TolerantEnumConverterFactory`: unbekannte Namen (z. B. von einer neueren Version) machen die Datei nicht ungültig; der Normalizer setzt sie auf den Standard bzw. verwirft sie einzeln aus Listen (`GuestChoices`, `RandomStyles`).
- Kommentare und abschließende Kommas sind erlaubt (`ReadCommentHandling = Skip`, `AllowTrailingCommas`); Eigenschaftsnamen ohne Beachtung der Groß-/Kleinschreibung.

### Ungültiges JSON

Syntaxfehler oder falscher Typ:

1. Die Datei wird als `settings.json.bak-<yyyyMMddHHmmss>` gesichert. Gelingt das nicht, wird fail-closed `null` geliefert (sonst würde ein späteres Speichern die Datei ungesichert überschreiben).
2. Die PIN (`Security`: Hash, Salt, Iterationen) wird per `JsonDocument` gerettet (`SettingsJson.TryRescueSecurity`). Gelingt das, startet die App mit Standardwerten plus gerettetem `Security`.
3. **Neu in 2.5.1:** Die gerettete PIN wird sofort in eine neue `settings.json` geschrieben (`WriteAtomic`, ohne `SettingsChanged`). Sonst gäbe es nach dem Sichern keine Datei mehr, und ein Neustart vor dem ersten Speichern im Admin würde zum Erststart ohne PIN führen. Scheitert das Schreiben, wird gewarnt; die PIN gilt dann nur bis zum Beenden.
4. Ist keine PIN rettbar (echter Syntaxfehler, kein `Security`-Objekt), folgt der Erststart.

Die übrigen Einstellungen müssen nach einer defekten Datei neu gesetzt werden (die Sicherung bleibt als `.bak`).

## `SettingsNormalizer` und Migration

`SettingsNormalizer.Normalize` (internal, `Services/Storage/SettingsNormalizer.cs`) ersetzt fehlende Unterobjekte, klemmt Zahlen auf `SettingsLimits`, setzt undefinierte Enum-Werte auf den Standard und räumt Texte auf (Steuerzeichen/Zeilenumbrüche → Leerzeichen, Länge kürzen, nur Leerraum → null). Besonderheiten:

- `Printer.TimeoutSeconds` ≥ `Flow.ExpectedPrintSeconds` + 60 (`PrintTimeoutReserveSeconds`), dann auf 10–900 geklemmt.
- `Layout.GuestChoices`: undefinierte und doppelte Werte raus, `Overlay` nur mit gesetzter `OverlayFileName`, Reihenfolge bleibt, der Standard wird hinten angehängt, falls die Liste nicht leer ist und ihn nicht enthält.
- Tasten: siehe `architektur.md` (Vorrang, Ersatztasten, Sperrliste).
- Kamera: unbekannter Flimmerschutz → 50 Hz; Reglerwerte ohne unbekannte/doppelte/leere Einträge, NaN → null; leere oder zu lange `ControlsDeviceId` leert die Werte; Profile je Kamera mit eindeutigen Namen (ohne Beachtung der Groß-/Kleinschreibung), Geräte-Ids werden ohne Beachtung der Groß-/Kleinschreibung verglichen.
- Monitor-, Mikrofon- und Kamera-Ids werden nie gekürzt (gekürzt passten sie nie), zu lange werden verworfen; zugehörige Namen werden nur gekürzt.
- Peace/Mittelfinger: Haltedauer auf 100er-Schritte gerundet; ohne gewählte Aktion gilt Selbstzerstörung bzw. „Peace-Effekte aufheben“.
- Herz (`NormalizeHeart`): Haltedauer auf 100er-Schritte, Zoom auf 10er-, Lautstärke auf 5er-Schritte gerundet, Empfindlichkeit und `KisscamSeconds` geklemmt; `KisscamMusicFolder` getrimmt, bei Leerraum, Steuerzeichen oder > 1024 Zeichen `null` (Standardordner); das alte Feld `KisscamMusicPath` ist unbekannt und wird beim Laden ignoriert (die `CurrentVersion` bleibt 2, keine Migration); ohne gewählte Form gilt Klassisch; undefinierte `Action`/`KisscamColor` → Standard; `KisscamText` wie die Hinweistexte bereinigt (leer → null); Banner-Text einzeilig auf 30 Zeichen gekürzt (leer → null); Präfix über `SanitizePrefix` (leer/ungültig → „Kisscam“). Der Musikordner wird getrimmt und **verworfen** (nicht gekürzt, gekürzt passte er nie), wenn er leer, länger als 1024 Zeichen ist oder Steuerzeichen enthält.
- **Migration** über `AppSettings.Version` (aktuell 2): Ist die geladene Version < 2, wird der alte Standardpräfix exakt „Fotobox“ zu „Fotobox+“ (eigene Präfixe bleiben). Danach steht `Version` auf dem aktuellen Stand; eine neuere Version wird nie herabgesetzt. Fehlt `Version` in der Datei, gilt der aktuelle Standard (keine Migration). Die Migration läuft im Speicher und wird beim nächsten Speichern festgeschrieben; sie ist idempotent.

## PIN

- 4–12 Ziffern (`SettingsLimits.PinMinLength`/`PinMaxLength`), Prüfung in `Core/PinRules` (genutzt von Admin und `PinService`).
- PBKDF2-SHA256 mit 16-Byte-Salt und 32-Byte-Hash, Vergleich in konstanter Zeit (`CryptographicOperations.FixedTimeEquals`). Standard-Iterationen 100 000.
- `SetPin` nutzt mindestens `SettingsNormalizer.MinPinIterations` (10 000); eine unplausibel hohe gespeicherte Zahl (> `MaxPlausiblePinIterations` = 10 000 000) wird durch den Standard ersetzt.
- `Verify` lehnt unplausible Daten sofort ab (Iterationen > `MaxPlausiblePinIterations`, Hashlänge ≠ 32 Byte, ungültiges Base64), damit eine manipulierte Datei die Box nicht minutenlang rechnen lässt.
- Der gespeicherte Wert wird nie geklemmt, auch nicht vom Normalizer (sonst passte der Hash nicht mehr).
- `PinService.IsPlausibleStoredPin(security, minIterations)` ist die gemeinsame Plausibilitätsregel (auch für den PIN-Import).
- `PinAttemptLimiter` (`ViewModels/Admin/PinAttemptLimiter.cs`): 5 Fehlversuche, dann Sperre 30 s, bei weiteren Fehlversuchen mit Faktoren 1, 2, 4, 10, 30 (30 s, 60 s, 2 min, 5 min, 15 min; die letzte Stufe gilt dauerhaft). Der Zähler bleibt nach Ablauf der Sperre stehen, ein weiterer Fehlversuch löst sofort die nächste Stufe aus. Gemessen mit monotoner Zeit. Ein Limiter pro Prozess (in `AdminLauncher`), die Sperre überdauert das Schließen des PIN-Dialogs.
- Erststart ohne PIN: `FirstRunPinDialog`; bei `LoadFailed` wird er nie angeboten.

## Einstellungen teilen (Export/Import)

Code: `AdminViewModel.Transfer.cs`, `Services/Storage/SettingsTransfer.cs`, `SettingsJson.cs`, `Core/SettingsSections.cs`, `Core/DeviceSpecificSettings.cs`. Bedienung: Admin-Tab „Sicherheit & Daten“ (`admin.md`).

### Dateiformat

Umhüllung: `Format` = `FotoboxPlus.Settings`, `FormatVersion` = 1, `AppVersion` (`AppInfo.Version`), `SettingsVersion`, `ExportedAt`, `Settings`. Standardname `FotoboxPlus-Einstellungen-yyyyMMdd-HHmm.json`. Exportiert wird der normalisierte Entwurf (auch ungespeichert). Der Import akzeptiert zusätzlich eine rohe `settings.json`.

### Was übertragen wird

- `SettingsSections.PortableSections`: alle Klassen-Eigenschaften von `AppSettings` außer denen in `SettingsSections.ExcludedProperties` (`Version`, `Security`). Neue Abschnitte sind automatisch dabei.
- Ein Test erzwingt, dass jedes neue Feld entweder übertragbar ist oder in `SettingsSections.ExcludedProperties` bzw. `DeviceSpecificSettings.Paths` eingeordnet wird.
- `DeviceSpecificSettings.Paths` (18 Felder) steht nie in der Datei und wird nie importiert: `Camera.DeviceId`, `Camera.ResolutionWidth`, `Camera.ResolutionHeight`, `Camera.ControlsDeviceId`, `Camera.ControlValues`, `Camera.ControlProfiles`, `Printer.PrinterName`, `Printer.PaperSizeName`, `Printer.PaperWidthMm`, `Printer.PaperHeightMm`, `Storage.PhotoFolder`, `Video.MicrophoneId`, `Video.MicrophoneName`, `Video.Folder`, `Appearance.GuestMonitorId`, `Appearance.GuestMonitorName`, `Appearance.AdminZoomPercent`, `Heart.KisscamMusicFolder`. Der Zielrechner behält seine Werte (`Clear`/`CopyFrom`; `Clear` setzt den Zoom auf 80 und den Musikordner auf null zurück, die Schlüssel fehlen in der Exportdatei ganz); die Kamera wird durch den Import nie neu gestartet. `Camera.Powerline` ist übertragbar (ortsabhängig, nicht gerätespezifisch).
- Das Overlay-PNG wird nicht exportiert. Fehlt es (oder ist der Name ein Pfad), entfällt das Overlay samt Gastwahl-Eintrag (Standard-Layout dann erste verbleibende Gastwahl bzw. Einzelfoto) mit Hinweis `OverlayMissingNote` (`ApplyOverlayFallback`).
- `SettingsSections.CopyPortableSections(from, to)` kopiert tief (`AppSettings.Clone`); „Standardwerte wiederherstellen“ und der Import nutzen sie gemeinsam.

### Import-Regeln (`SettingsTransfer.Parse`)

- Umhüllung oder rohe Datei; Namen ohne Beachtung der Groß-/Kleinschreibung; Kommentare und abschließende Kommas erlaubt (`SettingsJson`, gemeinsam mit dem Store).
- Jeder Abschnitt wird einzeln deserialisiert; ein ungültiger setzt nur diesen auf Standard (`ResetSections`, Hinweis im Admin). Fehlende Abschnitte/Werte = Standardwerte (nicht die des Zielrechners). Unbekanntes wird ignoriert und setzt `FromNewerVersion` (neuere App-/Einstellungsversion, unbekannte Eigenschaften oder Enum-Werte).
- Migration des Dateipräfixes über die Dateiversion (< 2: „Fotobox“ → „Fotobox+“); die `Version` des Ziels ändert sich nie.
- Fremdes `Format`, kein Objekt oder keine bekannten Abschnitte → `SettingsImportException`. Dateien > 1 MB (`MaxFileBytes`) werden abgelehnt.
- Lesen und Schreiben laufen auf dem Threadpool mit 10 s (`IoTimeout`); geschrieben wird UTF-8 ohne BOM erst nach `path.tmp`, dann ersetzt. Nach einem Timeout kann eine `.tmp` neben der Zieldatei liegen bleiben.
- Der Import füllt nur den Entwurf (`RebuildAfterDraftReplaced(keepDeviceSelections: true)`) und wirkt erst nach „Übernehmen“ bzw. „Speichern & schließen“.

### PIN beim Export und Import

- Export: nur auf Wunsch (Haken „PIN mit exportieren“, Standard aus, Prüfwert aus dem Store, nicht gespeichert).
- Import: Die PIN wird nur angeboten, wenn sie plausibel ist (`PinService.IsPlausibleStoredPin` mit Untergrenze `MinPinIterations`: Hash 32 Byte, Salt, Iterationen 10 000 bis `MaxPlausiblePinIterations`; schwächere PINs werden mit Hinweis ignoriert), sie sich von der aktuellen unterscheidet und der Admin bestätigt. Jeder Import entscheidet die PIN neu (`_pendingImportedSecurity`; eine vorgemerkte PIN eines früheren Imports gilt nicht weiter).
- Vorrang beim Speichern: eingetippte neue PIN > importierte PIN > PIN aus dem Store.
- **Neu in 2.5.1:** Eine nach dem Import per „PIN ändern“ gesetzte PIN hat Vorrang vor der importierten. `AdminViewModel.TryApplyPin` verwirft dabei `_pendingImportedSecurity`, weil die neue PIN sofort im Store gilt (sonst würde die importierte beim Speichern gewinnen).
- Eine importierte PIN zählt für `HasUnsavedChanges`. Während Export/Import (`IsTransferring`) sind „Übernehmen“ und „Speichern & schließen“ gesperrt; ein Import bricht ab, falls doch gespeichert wird (`ImportMustAbort`).
- UI-Zugriffe laufen über `IAdminUi.PickExportFile`/`PickImportFile`/`ShowInfo` (`WpfAdminUi`).

## Standardwerte wiederherstellen

`AdminViewModel.ResetToDefaultsAsync` (Tab „Sicherheit & Daten“, Karte „Standardwerte“):

- Nach Bestätigung wird der Entwurf auf `new AppSettings()` zurückgesetzt (über `SettingsSections.CopyPortableSections`), behält aber `Security` (PIN) und `Version`.
- Die Profile der Kamerasteuerung bleiben erhalten (sie gehören zur Kamera, nicht zu den Einstellungen). Eine noch nicht gespeicherte importierte PIN fällt weg.
- Das Zurücksetzen gilt wie alles im Admin erst nach „Übernehmen“ bzw. „Speichern & schließen“. Danach werden UI-Zustand (Tasten, Listenauswahl, Schrift, Overlay) neu aufgebaut (`RebuildAfterDraftReplaced(keepDeviceSelections: false)`), Listen neu geladen und die Kamera nur bei geänderten Kameraeinstellungen neu gestartet.
- Weitere Teilzurücksetzer: „Tasten zurücksetzen“ (behält `InvertMouseWheel`, setzt `GuestScreen` nicht zurück), „Alle zurücksetzen“ der Kamerasteuerung (Treiberwerte).
- Folgen: `GuestScreen` und `Fun` werden zurückgesetzt, die Monitorwahl wird zu Hauptbildschirm, das Farbschema zu System, der Admin-Zoom zu 80 %.
