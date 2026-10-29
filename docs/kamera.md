# Kamera

Dieses Dokument beschreibt den Kameradienst (`WinRtCameraService`), die Formatwahl, die Livebild-Anzeige (`CameraPreview`) und die Kamerasteuerung (Hardware-Regler, Flimmerschutz, Profile). Stand: 2.5.1. Die Videoaufnahme, die auf demselben Dienst aufsetzt, steht in [video.md](video.md); den Gesamtüberblick gibt [ueberblick.md](ueberblick.md).

Pfade in diesem Dokument sind relativ zum Repo-Root.

## Inhalt

- [Warum WinRT `MediaCapture`](#warum-winrt-mediacapture)
- [Dateien und Schnittstelle](#dateien-und-schnittstelle)
- [Sitzungsaufbau](#sitzungsaufbau)
- [Threading-Regeln (Pflicht)](#threading-regeln-pflicht)
- [Generationen und Gate](#generationen-und-gate)
- [Fristen und hängende Operationen](#fristen-und-hängende-operationen)
- [Überwachung und Wiederverbindung](#überwachung-und-wiederverbindung)
- [Ton und Zugriffsmodus](#ton-und-zugriffsmodus)
- [Formatwahl](#formatwahl)
- [Livebild-Anzeige `CameraPreview`](#livebild-anzeige-camerapreview)
- [Kamerasteuerung](#kamerasteuerung)
- [Fristen-Übersicht](#fristen-übersicht)

## Warum WinRT `MediaCapture`

Die Kamera läuft über `Windows.Media.Capture.MediaCapture` samt `MediaFrameReader`, weil das auf x64 und ARM64 gleichermaßen funktioniert (die App wird für beide Architekturen self-contained gebaut, siehe [build-release.md](build-release.md)). `MediaCapture` liefert zugleich das Livebild (Frame-Reader), Fotos (das jeweils neueste Bild des Readers), Videoaufnahme (`StartRecordToStorageFileAsync`) und die Hardware-Regler (`VideoDeviceController`).

## Dateien und Schnittstelle

| Datei | Inhalt |
|---|---|
| `src/Fotobox/Core/Interfaces.cs` | `ICameraService` (Start, Stopp, Foto, Geräte-/Auflösungsliste, `ConfigureAudio`, `ConfigureControls`, `GetControlsAsync`, `ApplyControlsLiveAsync`, `ActiveDeviceId`, `StartRecordingAsync`) |
| `src/Fotobox/Services/Camera/WinRtCameraService.cs` | Sitzung, Gate, Generationen, Watchdog, Wiederverbindung, Foto, Ton |
| `src/Fotobox/Services/Camera/WinRtCameraService.Controls.cs` | Regler-Anteil des Dienstes (Live-Arbeiter, Session-Momentaufnahme, hängende Aufgaben) |
| `src/Fotobox/Services/Camera/CameraControlAccess.cs` | `ICameraControlTarget`, `VideoDeviceControllerTarget`, `ControlStateCache`, `CameraControlAccess.Read/Apply/ValuesFor` |
| `src/Fotobox/Services/Camera/ExtendedCameraControls.cs` | Ersatzweg über die erweiterten WinRT-Regler (`ExtendedControlTarget`, `FallbackControlTarget`) |
| `src/Fotobox/Services/Camera/CameraSupervision.cs` | `FrameWatchdog`, `CameraStartRetry`, `CameraReconnectBackoff`, `CameraTimeoutException`, `FrameErrorThrottle` |
| `src/Fotobox/Services/Camera/CameraFormatSelector.cs` | reine Formatwahl |
| `src/Fotobox/Services/Camera/CameraMessages.cs` | deutsche Statusmeldungen |
| `src/Fotobox/Services/Camera/WinRtVideoRecording.cs`, `VideoBitrate.cs` | Videoaufnahme (siehe [video.md](video.md)) |
| `src/Fotobox/Core/CameraControls.cs`, `CameraControlMath.cs`, `CameraControlFormatter.cs`, `LatestOnlyWorker.cs` | Reglermodelle, Klemmen/Einrasten, Anzeigeformat, „nur der neueste Auftrag zählt“ |
| `src/Fotobox/Core/CameraServiceExtensions.cs` | `StartWithAudioAsync` |
| `src/Fotobox/Views/Controls/CameraPreview.xaml(.cs)` | Livebild-Steuerelement |
| `src/Fotobox/ViewModels/Admin/AdminViewModel.CameraControls.cs`, `CameraControlItem.cs` | Admin-Logik der Kamerasteuerung |

Status des Dienstes: `CameraStatus` (u. a. `Stopped`, `Starting`, `Running`, `NoDevice`, `AccessDenied`, `Error`) mit deutscher `StatusMessage`. Gast-Texte stehen auf Deutsch; die technische Meldung (oft englisch/HRESULT) steht nur im Log.

## Sitzungsaufbau

`StartAsync(settings, ct)` erhöht die Generation, bricht eine laufende Wiederverbindung ab und führt den Start auf dem Threadpool aus (`StartSerializedAsync` → `StartCoreAsync`):

1. Status `Starting` melden (vor dem Abbau, damit während des Abbaus kein Foto mehr angenommen wird), alte Sitzung abbauen (`StopCoreAsync`).
2. Geräte aufzählen (mit Frist). Kein Gerät → `NoDevice`. Gewählt wird `settings.DeviceId`, sonst das erste Gerät.
3. `MediaCapture` initialisieren (`InitializeCaptureAsync`, Rückfallkette siehe [Ton und Zugriffsmodus](#ton-und-zugriffsmodus)).
4. Farbquelle wählen (`VideoPreview`, dann `VideoRecord`, dann irgendeine Farbquelle). Keine Farbquelle → Fehler „liefert kein Farbbild“.
5. Format setzen (`ApplyFormatAsync`, siehe [Formatwahl](#formatwahl)); ein Fehler dabei (z. B. kein exklusiver Zugriff) wird nur geloggt, es gilt das Standardformat. Nur eine Zeitüberschreitung bricht ab.
6. Gespeicherte Regler und Flimmerschutz anwenden (`ApplySavedControlsAsync`, immer vor dem ersten Bild).
7. `MediaFrameReader` mit Ausgabe `Bgra8` erzeugen (Rückfall: Originalformat), `Realtime`-Modus. Zuerst die Session-Felder eintragen (`_controlCache`, `_capture`, `_reader` …), dann erst die Ereignisse `FrameArrived`, `Failed`, `RecordLimitationExceeded` anmelden, dann `reader.StartAsync()`.
8. Im Vordergrund gilt die Kamera mit `reader.StartAsync` als gestartet (Status `Running`, Watchdog läuft). Bei einem Wiederverbindungsversuch (`background`) gilt sie erst mit dem ersten Bild als gestartet; kommt keins, endet der Versuch als `NoImage` ohne Statuswechsel.

**Stille Wiederholung:** Direkt nach einem Kamerawechsel meldet Media Foundation oft kurz einen Ressourcenfehler, solange die alte Sitzung noch freigegeben wird. Solche Fehler werden im Vordergrund still wiederholt (Status bleibt `Starting`): Abstände 500, 750 und 1000 ms, Gesamtbudget 10 s (`CameraStartRetry`). Danach greift die normale Fehleranzeige samt Wiederverbindung.

**Fehlerpfade beim Start:** `UnauthorizedAccessException` → `AccessDenied`; `CameraTimeoutException` (hängender Treiber) → `Error` „reagiert nicht“, nicht still wiederholt, weil jeder Versuch die volle Frist kostet; sonstige Ausnahme → `Error` „Start fehlgeschlagen“. `Failed`-Ereignisse während `Starting` werden nur geloggt (Start bzw. Watchdog übernehmen).

**Foto:** `CapturePhotoAsync` wartet höchstens `CaptureTimeout` (2 s) auf das nächste Bild des Readers. Webcams liefern teils Alpha = 0, das Foto wird immer deckend gesetzt (`| 0xFF000000`). Ohne laufende Kamera wirft der Aufruf. Im Zugriffsmodus `SharedReadOnly` gibt es nur das Livebild, die Fotobox-Logik verlangt aber ohnehin den Status `Running`.

## Threading-Regeln (Pflicht)

- **Auf- und Abbau einer Sitzung laufen immer auf dem Threadpool (MTA)**, nie im Kontext des aufrufenden UI-Threads (STA). Start, Stopp, Dispose, Geräteaufzählung, Mikrofonliste, Regleraufrufe und Videoaufnahme starten daher alle mit `Task.Run`. Wird ein WinRT-Ereignis in einem anderen Apartment abgemeldet als angemeldet, scheitert CsWinRT mit `OutOfMemoryException` oder stürzt mit 0xC0000005 ab.
- **WinRT-Ereignisse (`FrameArrived`, `Failed`, `RecordLimitationExceeded`) werden nie mit `-=` abgemeldet.** `UnsubscribeFromNative` scheitert in CsWinRT sonst mit `OutOfMemoryException` bzw. stürzt ab. Das `Dispose` des Objekts gibt die Handler frei. Nachzügler-Ereignisse verwerfen die Handler selbst: `if (!ReferenceEquals(sender, _reader)) return;` (bzw. `_capture`). Deshalb werden neue Objekte immer **zuerst** in `_capture`/`_reader` eingetragen und **dann** angemeldet.
- `FrameArrived` kommt auf einem Hintergrund-Thread; Bildverarbeitung (Kopie in `_pixels`, Konvertierung fremder Pixelformate nach `Bgra8`) bleibt dort. Fehler pro Bild werden gedrosselt geloggt (`FrameErrorThrottle`: der erste einer Serie, danach jeder 300.).
- Das **Dispose von `MediaCapture`/Reader** läuft auf dem Threadpool mit Frist (`TeardownTimeout` 3 s); danach wird weitergemacht und im Hintergrund zu Ende abgebaut.

## Generationen und Gate

- `_gate` (`SemaphoreSlim(1,1)`) serialisiert Start, Stopp, Auflösungsabfrage und Aufnahmestart. Die Session-Felder (`_capture`, `_reader`, `_source`, `_activeDeviceId`, `_sharingMode` …) werden nur unter dem Gate geändert. Ausnahmen mit atomarem Tausch: `_generation`, `_disposed`, `_reconnectCts`, `_watchdogCts`.
- `_generation` wird bei **jedem** `StartAsync`, `StopAsync` und `DisposeAsync` **vor** dem Gate erhöht. Welcher Threadpool-Thread zuerst das Gate erreicht, ist nicht festgelegt; deshalb prüfen Start und Stopp **nach** dem Gate die Generation: Ein überholter Aufruf tut nichts, der neuere setzt den Status selbst.
- `Failed` (und der Watchdog) halten die beim Ereignis gültige Generation fest und geben sie an `HandleFailureAsync` weiter. Läuft gleichzeitig ein neuer Start, startet die Fehlerbehandlung nie mit der neuen Generation eine Wiederverbindung. Die Wiederverbindung reißt eine inzwischen laufende Kamera nie ab (`Status == Running` → Schleife endet).
- `ActiveDeviceId` ist die aufgelöste Geräte-Id der geöffneten Kamera (nicht `settings.DeviceId`; `null` dort heißt „erstes Gerät“).

## Fristen und hängende Operationen

Alle WinRT-Aufrufe, die im Treiber hängen können, laufen über `AwaitStepAsync` (Frist `StepTimeout` 5 s) bzw. `InitializeOnceAsync` (`InitTimeout` 10 s).

Läuft eine Frist ab, **läuft die Operation im Treiber weiter**. Der Dienst reagiert so:

1. Er zählt `_hangingOperations` hoch (`Interlocked`, **vor** dem Anhängen der Fortsetzung; das Herunterzählen steht in deren `finally`, genau einmal je Increment) – in `DisposeAfterCompletion` (Initialisierung) und im Timeout-Zweig von `AwaitStepAsync`.
2. Er wirft `CameraTimeoutException(step, pending)`; `Pending` ist die noch laufende Operation.
3. Beteiligte Objekte (`MediaCapture`, Reader) werden **erst nach Abschluss** der hängenden Operation freigegeben, nie währenddessen (Zugriffsverletzung/Hänger im Treiber möglich): `DisposeSessionObjectsAsync` hängt die Freigabe an `pending` und läuft im Hintergrund weiter. Ein später gelieferter Wert (z. B. ein Reader) wird freigegeben, Ausnahmen werden beobachtet.
4. `ReconnectLoopAsync` startet keinen neuen Versuch, solange `_hangingOperations > 0` (`CameraReconnectBackoff.ShouldAttempt`); dann höchstens alle `HangingRetryInterval` = 5 min. Sonst bliebe je Versuch ein weiteres hängendes `MediaCapture` liegen. Vordergrund-Starts sind davon nicht betroffen.

Folge: Hängt irgendein Kameraschritt dauerhaft (auch die Mikrofonliste im Admin oder ein Regleraufruf, die über `AwaitStepAsync` laufen), dauert die automatische Wiederverbindung bis zu 5 min. Endet die hängende Operation früher (z. B. Kamera abziehen/einstecken), geht es sofort normal weiter.

## Überwachung und Wiederverbindung

**Bild-Watchdog (`FrameWatchdog`):**

| Wert | Frist |
|---|---|
| Erstes Bild nach dem Start (`FirstFrameTimeout`) | 8 s |
| Kein neues Bild, nachdem schon eins kam (`StallTimeout`) | 5 s |

Der Watchdog zählt ein Bild erst nach erfolgreichem Kopieren (`NotifyFrame` nach der Verarbeitung). Er meldet „kein Bild“ als Fehler (`HandleFailureAsync`: Status `Error`, Sitzung abbauen, Wiederverbindung starten). Seine Schleife ist an Reader und Generation gebunden und endet bei Stopp/Neustart/Dispose. **Grenze:** Er erkennt ausbleibende Frames, keine einfarbigen Platzhalterbilder virtueller Kameras.

**Wiederverbindung (`ReconnectLoopAsync`):**

- Startet nach einem fehlgeschlagenen Start (`NoDevice`, `AccessDenied`, `Error`) und nach `Failed`/Watchdog.
- Intervall `BaseInterval` 5 s; nach 3 aufeinanderfolgenden Versuchen ohne Bild `SlowInterval` 15 s (`SlowAfterNoImageAttempts`).
- Jeder Versuch ist ein `StartCoreAsync(background: true)` unter dem Gate; der Status bleibt dabei unverändert (kein Flackern zwischen Schwarzbild und Fehlermeldung). Eine Wiederverbindung gilt erst mit dem ersten Bild.
- Hängt eine frühere Operation, gilt das 5-Minuten-Intervall (siehe oben). Die Schleife zählt ihren Start als Versuch.
- Der Dienst startet **nur eine** Wiederhol-Schleife (`_reconnectCts`); `StartAsync`/`StopAsync`/`DisposeAsync` brechen sie ab.

**Beenden:** `App.OnExit` gibt den DI-Container einmal mit 5-s-Frist frei; das `DisposeAsync` der Kamera stoppt sie (kein separater Stopp). Ein laufendes Video wird dabei vorher abgeschlossen (siehe [video.md](video.md)).

## Ton und Zugriffsmodus

`ICameraService.ConfigureAudio(CameraAudio?)` legt Mikrofon/Ton **für die nächste Sitzung** fest (`CameraServiceExtensions.StartWithAudioAsync` ruft es vor dem Start auf); Ton wird beim Initialisieren geöffnet, ein Wechsel braucht daher einen Kamera-Neustart (`VideoSettings.RequiresCameraRestartComparedTo`, im Admin setzt `AdminLauncher` dann `CameraChanged`).

Rückfallkette in `InitializeCaptureAsync`, jeder Schritt erst, wenn der vorige scheitert:

| Schritt | Modus | Folge |
|---|---|---|
| 1 | `ExclusiveControl` mit Ton (nur wenn `ConfigureAudio` gesetzt ist) | Livebild, Foto, Video mit Ton |
| 2 | `ExclusiveControl` ohne Ton | Video ohne Ton. Ein fehlendes oder gesperrtes Mikrofon darf die Kamera nie verhindern; exklusiver Zugriff bleibt nötig, weil nur er Aufnahmen erlaubt |
| 3 | `SharedReadOnly` ohne Ton | nur Livebild; `StartRecordingAsync` wirft („von einer anderen Anwendung verwendet“), Regler werden nie geschrieben |

Besonderheiten:

- `UnauthorizedAccessException` mit Ton ist mehrdeutig (Mikrofon oder Kamera gesperrt). Erst Schritt 2 klärt es; scheitert auch er mit `UnauthorizedAccess`, ist die Kamera gesperrt (`AccessDenied`, **kein** Rückfall auf gemeinsamen Zugriff).
- Eine **Zeitüberschreitung mit Ton** (hängender Audiotreiber) wird wie bisher gemeldet; danach läuft der Dienst bis zum nächsten `ConfigureAudio` ohne Ton (`_audioSuspended`), damit die Wiederverbindung nicht endlos am Mikrofon hängt.
- `AudioAvailable` sagt, ob die laufende Sitzung ein Mikrofon geöffnet hat.
- `MemoryPreference` ist `Cpu` (der Frame-Reader soll `SoftwareBitmap` liefern).

## Formatwahl

`CameraFormatSelector.SelectIndex` (rein, ohne WinRT, testbar) wählt aus den `SupportedFormats` der Farbquelle:

1. Ist eine **gewünschte Auflösung** gesetzt (`CameraSettings.ResolutionWidth/Height`) und angeboten, gewinnt sie (bei mehreren Formaten dieser Größe das mit der besten Bildrate, dann Pixelformat).
2. Sonst **„Automatisch“**: höchste Auflösung **bis 1920 × 1080** mit mindestens 15 fps, 16:9 bevorzugt. Gibt es das nicht, dann ≤ 1080p ohne fps-Bedingung, zuletzt das kleinste Format.
3. **Bildrate:** ab 15 fps gültig; bis 30 fps ist mehr besser, darüber weniger (geringere Last).
4. **Pixelformat als Tie-Breaker:** unkomprimiert (NV12, YUY2, …) vor MJPG vor Unbekanntem.
5. **Komprimierte Videoformate (H264/HEVC/MPEG/WMV/VC1/VP8/VP9/AV1)** liefern dem Frame-Reader keine `SoftwareBitmap`; sie kommen **nur infrage, wenn die Kamera sonst nichts anbietet** (zweiter Durchlauf ohne Filter).

`DistinctResolutions` liefert die Auswahlliste im Admin (absteigend nach Pixeln). Die aktive Auflösung (`ActiveResolution`) kommt aus `source.CurrentFormat` nach dem Setzen und dient auch der Bitrate der Videoaufnahme.

## Livebild-Anzeige `CameraPreview`

`src/Fotobox/Views/Controls/CameraPreview.xaml.cs` zeigt das Livebild (Latest-frame-wins) samt Statushinweis.

- **Triple-Buffering:** Jeder Puffer gehört zu jedem Zeitpunkt genau einer Stelle (Kamera-Thread beim Kopieren, `_ready`, UI-Thread beim `WritePixels`, `_spare`). Unter `_bufferLock` werden nur Referenzen getauscht; `WritePixels` und das Kopieren laufen **außerhalb** der Sperre, damit weder der UI-Thread die Kamera-Pipeline noch umgekehrt aufhält. Ist kein Puffer frei, wird das noch nicht angezeigte Bild überholt (höchstens drei Puffer, kein Neuanlegen je Bild). Höchstens ein Render-Auftrag ist ausstehend.
- **`SyncAttachment`:** Das Steuerelement abonniert `FrameArrived`/`StatusChanged` nur, solange es geladen **und** sichtbar ist (`Loaded`, `Unloaded`, `IsVisibleChanged`). Ausgeblendete Admin-Vorschauseiten (z. B. in den Tabs „Fun“) kopieren dadurch keine Bilder. (Das `-=` ist hier erlaubt: es betrifft das eigene .NET-Ereignis des Dienstes, nicht ein WinRT-Ereignis.)
- **Zentriertes `PreviewImage`:** Das `Image` hat ausdrücklich `HorizontalAlignment="Center"`/`VerticalAlignment="Center"`. Bei Stretch-Ausrichtung legt WPF ein überstehendes `UniformToFill`-Bild links/oben an; Overlays von Zielmarken und Bart rechnen mit dem zentrierten Ausschnitt (`OverlayMapper`) und säßen sonst verschoben.
- Spiegeln (`Mirror`) ist ein `RenderTransform` auf dem Host (`ScaleTransform(-1, 1)`), wirkt also ohne Kamera-Neustart.
- In der Gastansicht ist der Statushinweis des Steuerelements ausgeblendet (`ShowStatusOverlay=false`); die Gastansicht zeigt eigene Meldungen.

## Kamerasteuerung

Admin-Tab „Kamera“, Karte „Bildeinstellungen“. Einstellungen: `CameraSettings.ControlsDeviceId`, `ControlValues`, `ControlProfiles`, `Powerline`.

### Regler

`CameraControlKind`: `Zoom`, `Pan`, `Tilt`, `Brightness`, `Contrast`, `Hue`, `Exposure`, `BacklightCompensation`, `Focus`, `WhiteBalance`, `ExposureCompensation`. **Das Enum wird nur hinten erweitert** (Werte stehen als Namen in der `settings.json`, die Reihenfolge bestimmt die Sortierung).

Werte sind **Rohwerte des Treibers** (`CameraControlValue`: `Value` und/oder `Auto`). Die Belichtungszeit (`Exposure`) wird im Admin als Prozent angezeigt (`CameraControlFormatter`); intern sind es im erweiterten Weg Sekunden.

### Zwei Zugriffswege

`CreateTarget` baut ein `FallbackControlTarget(VideoDeviceControllerTarget, ExtendedControlTarget)`; je Regler und Aufruf gilt der **erste Weg, der den Regler unterstützt** (der Weg wird jedes Mal neu bestimmt):

1. **Hauptweg `MediaDeviceControl`** (`VideoDeviceControllerTarget`): Zoom, Pan, Tilt, Brightness, Contrast, Hue, Exposure, BacklightCompensation, Focus, WhiteBalance. Fähigkeiten über `Capabilities` (Min/Max/Step/Default/AutoModeSupported), `TryGetValue/TrySetValue/TryGetAuto/TrySetAuto`.
2. **Ersatzweg erweiterte WinRT-Regler** (`ExtendedControlTarget`): `ZoomControl`, `ExposureCompensationControl`, `ExposureControl` (= Belichtungszeit), `WhiteBalanceControl`, `FocusControl`. Viele eingebaute Kameras bieten **nur** diese. Die **Belichtungskorrektur (`ExposureCompensation`) gibt es nur dort.** Besonderheiten: Weißabgleich – nur das Preset „Auto“ gilt als Automatik, jedes andere als manuell; Fokus – Automatik = Dauerfokus (`FocusMode.Continuous`), Wertregler nur mit `FocusMode.Manual`; Zoom – Übergangsmodus „direkt“, wenn möglich. Die asynchronen WinRT-Aufrufe warten **je Aufruf höchstens 3 s** (`ExtendedControlTarget.CallTimeout`, `TimeoutException`, je Regler gefangen).

Der Flimmerschutz kommt nur vom Hauptweg.

### Flimmerschutz (`Powerline`)

`CameraPowerline`: `Unchanged`, `Off`, `Hz50` (Standard), `Hz60`, `Auto`; Abbildung auf `PowerlineFrequency` in `CameraPowerlineMap`. Bewusste Ausnahme von „nur Verstelltes setzen“: Er wird bei **jedem Kamerastart** gesetzt (`Unchanged` lässt den Treiber unberührt, im Admin als „Nicht verändern“ wählbar). Er ist **übertragbar** beim Einstellungen-Export (hängt vom Ort ab, nicht vom Gerät). Bietet die Kamera ihn nicht an, wird er nicht gesetzt (Log). Admin: Änderungen am Entwurf (ComboBox, Import, Zurücksetzen) gehen über `SyncPowerlineLive` live an die Kamera, wenn sie vom wirksamen Stand abweichen.

### „Nur Verstelltes setzen“

- `ControlValues` enthält **nur im Admin verstellte Regler**. Der Anfangsstand der Anzeige kommt immer vom Treiber (`GetControlsAsync`). Unverstellte Regler bleiben, wie Treiber oder andere Tools sie gesetzt haben (gewollt).
- Die Werte sind an `ControlsDeviceId` gebunden = aufgelöste Geräte-Id der geöffneten Kamera (`ICameraService.ActiveDeviceId`). `CameraControlAccess.ValuesFor(savedDeviceId, values, deviceId)` liefert sie **nur bei passender Id**; ein Kamerawechsel verwirft sie. Geräte-Ids werden überall **ohne Beachtung der Groß-/Kleinschreibung** verglichen (Windows-Gerätepfade: Normalizer, Admin, `ValuesFor`).
- Anwendung beim Start: `WinRtCameraService.ApplySavedControlsAsync` unter dem Gate **vor** `reader.StartAsync`, auch bei Wiederverbindung, **nie bei `SharedReadOnly`**. Fehler und Zeitüberschreitung werden nur geloggt; der Start läuft weiter. Frist: siehe unten.

### `ConfigureControls` – der einzige Weg ohne Neustart

`ICameraService.ConfigureControls(CameraSettings saved)` übergibt den **gespeicherten** Stand für spätere Starts (Wiederverbindung eingeschlossen), ohne die Kamera neu zu starten. Aufrufer: `BoothViewModel` im `SettingsChanged`-Handler und `CameraServiceExtensions.StartWithAudioAsync` vor jedem Start mit gespeicherten Einstellungen (Gastansicht und `AdminViewModel.RestoreCameraAsync`). **`StartAsync` ändert den gespeicherten Stand nie**: Der Admin startet mit dem ENTWURF, und eine Wiederverbindung würde sonst ungespeicherte Werte zurückspielen.

`CameraSettings.RequiresRestartComparedTo` ignoriert Regler, Profile und Flimmerschutz: **Regler lösen nie einen Kamera-Neustart aus.** (Neustart nur bei Gerät oder Auflösung, siehe `AdminResult.CameraChanged`; Spiegeln wirkt ohne Neustart.)

### `ControlStateCache` – nur Abweichendes schreiben

Je Sitzung führt ein `ControlStateCache` (in `CameraControlAccess.cs`) den zuletzt **gelesenen** (`GetControlsAsync` füllt ihn über `Seed`) bzw. **erfolgreich angewendeten** Stand je Regler (Wert, Automatik) und den Flimmerschutz. `Apply(…, cache)` überspringt Gleiches (Wert innerhalb eines halben Schritts), ein fehlgeschlagenes Schreiben verwirft den Eintrag (`Forget`), eine neue Sitzung beginnt mit leerem Cache. Der Admin darf deshalb weiter bei jeder Bewegung **alle** Regler senden, ohne dass der Fokus bei jedem Tick neu fokussiert oder der Weißabgleich sein Preset neu setzt.

**Neu in 2.5.1 – Automatik und „Wert unbekannt“:** Beim `Seed` gilt ein Regler, der beim Lesen **in Automatik** steht, als „Wert unbekannt“ (`Value = null`), weil der Treiber den Wert dann selbst nachführt und der gelesene Wert veraltet. Schaltet der Admin die Automatik aus (`Auto == false`), schreibt `ApplyOne` daher den angezeigten Wert wirklich (Cache-Wert `null` ≠ Zielwert). Ebenso wird nach „Automatik an“ der Cache-Wert auf unbekannt gesetzt, und ein Wert ohne ausdrückliches „Automatik aus“ lässt die Automatik offen (Cache: unbekannt). Vor 2.5.1 wurde der angezeigte Wert beim Ausschalten der Automatik nicht übernommen, wenn er zufällig dem zuletzt gelesenen entsprach.

`CountPending` liefert eine Obergrenze der zu schreibenden Regler (nur für die Fristberechnung).

### Klemmen und Einrasten

Passiert **zur Laufzeit** in `CameraControlMath.ClampAndSnap(value, min, max, step)` mit dem vom Treiber gemeldeten Bereich, **nicht im Normalizer**. `SettingsLimits` kennt nur `CameraProfilesMax` 10, `CameraProfileNameMaxLength` 40 und `CameraDeviceIdMaxLength` 512. Der Normalizer räumt nur die Struktur auf: unbekannte/doppelte/leere Einträge, NaN → null, Profilnamen je Kamera eindeutig ohne Beachtung der Groß-/Kleinschreibung; eine zu lange oder leere `ControlsDeviceId` leert die Werte. Treiber unterscheiden sich in Bereichen und Einheiten.

### Live-Anwenden (Admin-Vorschau)

`ApplyControlsLiveAsync(values, powerline)` wirkt **vorübergehend** (ändert den gespeicherten Stand nie) und gebündelt über `LatestOnlyWorker<LiveControlRequest>`: höchstens ein Auftrag läuft, kommen währenddessen weitere, bleibt nur der zuletzt eingereichte erhalten („neueste gewinnt“). Der Worker arbeitet auf dem Threadpool und wirft nie (Fehler → Log). Nach `DisposeAsync` werden wartende Aufträge verworfen (`Close`), ein laufender Schreibvorgang blockiert den Abbau nicht.

### Fristen, Sitzungsidentität, hängende Aufrufe

- **Frist des Schreibschritts:** `WinRtCameraService.ControlsTimeout(writes) = StepTimeout (5 s) + 2 × 3 s × Zahl der zu schreibenden Regler`, höchstens 30 s (`ControlsTimeoutMax`). Ein einziger `AwaitStepAsync` je Anwendung. Beim Start-Anwenden zählt der Flimmerschutz als ein weiterer Regler (`values.Count + 1`).
- **`SnapshotSessionAsync`:** Hält das Gate **nur kurz** zum Lesen der Session-Felder (Wartezeit `ControlsGateWait` 3 s, sonst entfällt die Anfrage mit Log) und **nie während des Treiberaufrufs**. Start/Stopp/Dispose warten so höchstens auf diesen Moment.
- **Keine fremde Session treffen:** Der Treiberaufruf hält das `MediaCapture` der Momentaufnahme fest. Der Schreibvorgang prüft unmittelbar davor per `ReferenceEquals(_capture, session.Capture)` und die Generation (und `_disposed`); er trifft also nie eine neue Sitzung. Wird die Sitzung währenddessen abgebaut, scheitert der Treiberaufruf mit einer Ausnahme (geloggt).
- **Alle Treiberaufrufe auf dem Threadpool** über `AwaitStepAsync`.
- **Hängt ein Schreib- oder Lesevorgang:** Der Dienst merkt die Aufgabe je Sitzung vor (`_sessionPending`, über `AddSessionPending`; schon **während** des Aufrufs, nicht erst nach dem Timeout; beim Aufbau über `SetBuildPending`). Abbau, Wiederverbindung und Dispose geben das `MediaCapture` erst nach deren Abschluss frei (`TakeSessionPendingAsync` wartet höchstens `SessionPendingGrace` 1 s auf einen gerade laufenden, nicht hängenden Zugriff, sonst Freigabe im Hintergrund). Der Live-Arbeiter wartet nach einem Timeout (höchstens `LiveHangWait` 60 s, Ende bei Dispose über `_lifetime`) auf die hängende Aufgabe, bevor er den nächsten Schreibvorgang beginnt; neuere Anforderungen bleiben „neueste gewinnt“ vorgemerkt.
- Ein hängender Regleraufruf erhöht `_hangingOperations` (siehe oben) und verzögert die automatische Wiederverbindung ggf. bis zu 5 min.

### Admin-Verhalten

- **Anzeige:** `GetControlsAsync` liefert `CameraControlsSnapshot` (je Regler: unterstützt, beschreibbar, Min/Max/Step/Default, Automatik, Wert; dazu Flimmerschutz). Läuft die Kamera nicht oder nur als `SharedReadOnly`, sind die Regler mit Hinweis gesperrt (`ControlsNotRunningText`, `ControlsReadOnlyText`). Nach `RestartCameraAsync` liest nur der Statuswechsel auf „läuft“ die Regler neu.
- **Grundwerte je Gerät:** Der Treiber-Schnappschuss beim ersten Lesen (nach dem Speichern der dann wirksame Stand: gespeicherte Werte über dem Schnappschuss, `GetControlBaseline`).
- **Live-Stand:** Jede Reglerbewegung baut `BuildLiveValues` = verstellte Regler des Entwurfs über einer Grundlage. **Neu in 2.5.1:** Die Grundlage ist der **zuletzt live gesendete Stand desselben Geräts** (`_lastLive`), nicht mehr nur die alten Grundwerte. Sonst sprangen nach „Alle zurücksetzen“ die nicht verstellten Regler bei der nächsten Bewegung wieder auf die alten Grundwerte zurück. „Alle zurücksetzen“ (`ResetControlsCore`) löscht die verstellten Werte im Entwurf, setzt den Flimmerschutz auf 50 Hz und sendet die Treiber-Standardwerte (mit Automatik, wo möglich) live; die Grundwerte des Geräts bleiben, Schließen ohne Speichern nimmt das Zurücksetzen daher zurück.
- **Profile:** Benannte Reglerstände **je Kamera** (`CameraControlProfile`), höchstens `CameraProfilesMax` (10), Name höchstens 40 Zeichen; die Auswahl zeigt „(geändert)“ bei Abweichung (`SameValues` mit halber Schrittweite als Toleranz). **Profile laden weiterhin über den Grundwerten** (`BuildLiveValues(key, continueFromLastLive: false)`), nicht über dem zuletzt gesendeten Stand – ein Profil ist ein vollständiger Stand. Profile gehören zur Kamera, nicht zu den Einstellungen: „Standardwerte wiederherstellen“ behält sie.
- **Zurücknehmen beim Schließen:** Schließen ohne Speichern (nach „Übernehmen“ nur das seither Verstellte) nimmt Live-Änderungen zurück. `AdminLauncher` ruft nach dem Schließen (außer beim Beenden) `AdminViewModel.RestoreAfterCloseAsync(saved, restoreCamera)` auf: ggf. vorher `RevertCameraControlsAsync` (wenn die Wiederherstellung die Kamera wechselt), dann `RestoreCameraAsync`, dann Revert – verkettet statt parallel, höchstens `ControlsRevertTimeout` 5 s. Auch `SelectDeviceAsync` nimmt vor dem Kamerawechsel zurück. Nach einer Wiederverbindung kennt die Kamera nur den gespeicherten Stand; was live verstellt war, sendet der Admin erneut (`_controlsLiveDirty`).
- Nach dem Beginn des Schließens wird nichts Ungespeichertes mehr an die Kamera geschickt (ein spätes Lesen einer neuen Sitzung träfe sonst nach dem Zurücknehmen ein).
- Regleränderungen zählen für `HasUnsavedChanges` (Entwurf-Vergleich), lösen aber keinen Kamera-Neustart aus.

## Fristen-Übersicht

| Wert | Frist | Ort |
|---|---|---|
| Foto: Warten auf das nächste Bild | 2 s | `CaptureTimeout` |
| Aufbauschritt (Format, Reader, Start, Geräteliste, Mikrofonliste, Zielordner der Aufnahme) | 5 s | `StepTimeout` |
| `MediaCapture.InitializeAsync` | 10 s | `InitTimeout` |
| Abbau je Schritt (`StopAsync`, `Dispose`) | 3 s | `TeardownTimeout` |
| Start der Aufnahme im Dienst | 10 s | `RecordStartTimeout` (außen 15 s im Controller, siehe [video.md](video.md)) |
| Erstes Bild nach Start (Watchdog) | 8 s | `FrameWatchdog.FirstFrameTimeout` |
| Bildausfall (Watchdog) | 5 s | `FrameWatchdog.StallTimeout` |
| Stille Start-Wiederholungen | 0,5 / 0,75 / 1 s, Budget 10 s | `CameraStartRetry` |
| Wiederverbindung | 5 s, ab 3 Fehlversuchen ohne Bild 15 s | `CameraReconnectBackoff` |
| Wiederverbindung bei hängender Operation | 5 min | `HangingRetryInterval` |
| Gate-Wartezeit der Regleranfragen | 3 s | `ControlsGateWait` |
| Erweiterte Regler je WinRT-Aufruf | 3 s | `ExtendedControlTarget.CallTimeout` |
| Schreibschritt der Regler | 5 s + 6 s je Regler, höchstens 30 s | `ControlsTimeout` |
| Warten auf laufenden Reglerzugriff beim Abbau | 1 s | `SessionPendingGrace` |
| Live-Arbeiter wartet auf hängenden Schreibvorgang | höchstens 60 s | `LiveHangWait` |
| Zurücknehmen beim Schließen / Kamerawechsel | 5 s | `ControlsRevertTimeout` |
| Kamera-Dispose beim App-Ende | 5 s | `App.OnExit` |

## Bekannte Grenzen

Eine vollständige Liste steht in [bekannte-grenzen.md](bekannte-grenzen.md). Kamerabezogen:

- Der Reconnect-Pfad „Kamera ohne Bild“ ist nur per Unit-Test geprüft, nicht mit echter Hardware.
- Eine virtuelle Kamera ohne verbundene Quelle liefert einfarbige Platzhalterbilder; der Watchdog erkennt das nicht. Ohne gespeicherte Kamera wird das erste Gerät der Liste gewählt (das kann eine virtuelle sein) – Abhilfe: Kamera im Admin explizit wählen.
- Regler sind nur mit einer eingebauten Kamera geprüft (Zoom und Belichtungskorrektur über die erweiterten Regler plus Flimmerschutz). Klassische `MediaDeviceControl`-Regler (USB-Webcams) sowie Belichtungszeit, Weißabgleich und Fokus über die erweiterten Regler sind nur gegen Fakes getestet.
- Der Flimmerschutz 50 Hz überschreibt bei jedem Start die Treiber-Automatik (Standard; „Nicht verändern“ ist wählbar).
- Der Kamera-Abbau nach `TeardownTimeout` gibt trotzdem sofort frei (Aufschieben könnte die Kamera dauerhaft blockieren).
