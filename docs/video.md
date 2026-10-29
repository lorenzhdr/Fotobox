# Videomodus

Stand: 2.5.1. Pfade relativ zum Repo-Root. Kameradienst: [kamera.md](kamera.md); Zustandsmaschine: [architektur.md](architektur.md); Einstellungen: [einstellungen.md](einstellungen.md).

## Überblick

Eigene Start-/Stopptaste (Standard Enter) → Countdown → Aufnahme (mit Ton, MP4/H.264) → Speichern → Erfolgsmeldung. **Nur speichern: kein Druck, keine Vorschau.** `AppSettings.Video` (`VideoSettings`), `Enabled` Standard an.

| Datei | Inhalt |
|---|---|
| `src/Fotobox/Core/BoothController.cs` | `TryStartVideo`, `TryStopVideo`, `TryToggleVideo`, Video-Ablauf, `FinishVideoForShutdown` |
| `src/Fotobox/Services/Camera/WinRtCameraService.cs` | `StartRecordingAsync` (`StartRecordingCoreAsync`), `ConfigureAudio` |
| `src/Fotobox/Services/Camera/WinRtVideoRecording.cs` | eine laufende Aufnahme, Beenden, `Faulted`, MP4-Profil |
| `src/Fotobox/Services/Camera/VideoBitrate.cs` | Bitrate, Speicherplatzschätzung |
| `src/Fotobox/Services/Storage/VideoStorage.cs` | Zielordner, Platzprüfung, `.tmp.mp4`, `CommitAsync` |
| `src/Fotobox/Views/Booth/BoothWindow.xaml`, `ViewModels/Booth/BoothViewModel.cs` | Overlay, Hinweiszeile |
| `src/Fotobox/Views/Admin/AdminWindow.xaml` | Tabs „Video“ und „Speicher“ (Karte „Videos speichern“) |

## Zustände

`VideoCountdown` → `Recording` → `VideoSaving` → `VideoSaved` → `Idle`. Fehler laufen wie beim Foto über `Error` zurück nach `Idle`.

- `TryStartVideo()` nur in `Idle`, bei laufender Kamera (`CameraStatus.Running`), `Video.Enabled` und vorhandenem Video-Speicher; **teilt das Gate (`_busy`) mit dem Foto-Durchgang**, beides läuft nie zugleich. `BoothViewModel.TryStartVideo` ist zusätzlich bei offenem Admin und startender Kamera gesperrt.
- Der Zustand wechselt sofort zu `VideoCountdown` (bei Countdown 0 mit `CountdownValue` 0, bis die Aufnahme beginnt: „Video-Countdown ohne Zahl“ = Vorbereitung/Kamerastart, kann bei Zeitüberschreitungen bis ca. 25 s dauern).
- Der Ablauf liest einen **Snapshot** der Einstellungen; Änderungen wirken erst im nächsten Durchgang.
- `BoothViewModel.CanMoveWindow` ist in allen Video-Zuständen `false` (nur `Idle`, `Starting`, `Error` erlauben das Verschieben auf einen anderen Monitor).

## Tasten

Aktionen `VideoStartKeys`, `VideoStopKeys` (Tab „Tasten“). Standard: **Enter in beiden** = Umschalten (`BoothController.TryToggleVideo`: `Idle` → Start, `Recording` → Stopp, sonst wirkungslos). Die Maus startet nie ein Video. Der Normalizer vergibt eine Taste nur einmal, Vorrang Admin > Auslösen > Abbrechen > Video; Ausnahme: dieselbe Taste in Start **und** Stopp. Die Video-Listen dürfen leer sein (ohne Ersatzwert). Die Videotasten wirken nur bei aktivem Videomodus (`BoothKeyAction.VideoStart/VideoStop/VideoToggle`).

## Dauer, Mindestlänge, Abbruch

| Einstellung | Bereich (`SettingsLimits`) | Standard |
|---|---|---|
| Höchstdauer | `VideoMaxDuration` 10–1800 s | 300 |
| Countdown | `VideoCountdown` 0–30 s | 3 |
| Mindestlänge | `VideoMinDuration` 0–30 s, **immer unter der Höchstdauer** (Normalizer) | 2 |
| Warnung vor Ende | `VideoWarning` 0–120 s (0 = keine) | 10 |
| Anzeigedauer Erfolgstext | `VideoSuccessDisplay` 1–60 s | 3 |
| Erfolgstext | höchstens `VideoSuccessTextMaxLength` 80 Zeichen | „Video gespeichert! 🎬“ |

- `TryStopVideo` wirkt erst ab der Mindestlänge (`CanStopVideo`); davor, im Countdown und sonst `false`. Die Höchstdauer stoppt automatisch (`VideoStopReason.MaxDuration`).
- **Abbrechen-Taste (Esc):** Im `VideoCountdown` bricht sie still ab (nichts aufgenommen), in `Recording` ist sie wirkungslos (die Aufnahme läuft weiter; beendet wird sie nur durch die Videotaste ab der Mindestlänge, die Höchstdauer oder das App-Ende, das ein Teilvideo speichert).
- Gründe (`VideoStopReason`): `User`, `MaxDuration`, `Faulted`, `AppExit`.

## Kameraseite

- `ICameraService.ConfigureAudio` legt Mikrofon/Ton für die **nächste** Sitzung fest (`CameraServiceExtensions.StartWithAudioAsync`). `WinRtCameraService` öffnet exklusiv mit `AudioAndVideo` und fällt ohne Mikrofon bzw. bei gesperrtem Zugriff auf Video ohne Ton zurück, notfalls auf `SharedReadOnly` (nur Livebild; `StartRecordingAsync` wirft dort, also keine Aufnahme). Details: [kamera.md](kamera.md#ton-und-zugriffsmodus). Ändern sich nur Ton/Mikrofon (`VideoSettings.RequiresCameraRestartComparedTo`), setzt `AdminLauncher` ebenfalls `CameraChanged` (Kamera-Neustart nach Speichern).
- Aufgenommen wird per **`StartRecordToStorageFileAsync`/`StopRecordAsync`** (statt `LowLagMediaRecording`): Die Startverzögerung ist unerheblich (der Countdown liegt davor), und es gibt nur eine Operation zum Beenden, die auch den MP4-Index schreibt. Das Livebild (`MediaFrameReader`) läuft **parallel** auf demselben `MediaCapture` weiter.
- `StartRecordingCoreAsync` läuft unter dem Gate (kein Neustart dazwischen; höchstens eine Aufnahme), das Beenden nimmt das Gate bewusst **nicht** (der Abbau hält es und wartet selbst auf das Ende der Aufnahme). Zielordner/Zieldatei über `StorageFolder`/`CreateFileAsync` mit `StepTimeout`. Zeitüberschreitung des Starts (10 s) → eine später doch anlaufende Aufnahme wird sofort wieder beendet und die Datei gelöscht (`StopLateRecording`).
- **`WinRtVideoRecording`** kapselt das Ende: `FinishAsync` ist idempotent und threadsicher, alle Aufrufer (Gast-Stopp, Kamera-Abbau, Dispose) warten auf dieselbe Operation, mit eigener Frist `FinishTimeout` 15 s (nicht am Token eines Einzelnen). Danach: Datei fehlt/leer → `IOException`; Fehler beim Beenden, aber Datei da → Teilvideo bleibt erhalten (evtl. nicht abspielbar). `Faulted` (Aufnahmelimit des Geräts, Kamera-Stopp/-Ausfall) wird höchstens einmal gemeldet; wer sich erst danach anmeldet, wird sofort benachrichtigt.
- **MP4-Profil:** H.264 + AAC passend zum aktiven Kameraformat (kein Umskalieren), Breite/Höhe auf gerade Werte abgerundet, ohne Ton kein Audio-Profil. Qualität `VideoQuality` (Hoch/Mittel/Niedrig) → Bitrate in `VideoBitrate`: Bits je Pixel und Bild 0,15 / 0,09 / 0,05 (1080p30 hoch ≈ 9,3 Mbit/s), geklemmt 0,5–25 Mbit/s, Bildrate 5–60 (ohne Angabe 30). Zugleich Grundlage der Speicherplatzschätzung: AAC 192 kbit/s, Aufschlag 1,10 für Container/Schwankung, Reserve 200 MiB.

## Faulted-Rettung

Fällt die Kamera während der Aufnahme aus (`IVideoRecording.Faulted`, auch nach verspäteter Anmeldung nachgemeldet; `WinRtCameraService` meldet es auch bei Abbau/Wiederverbindung/Fehler), geht das über das Stopp-Signal (`VideoStopReason.Faulted`) in den Ablauf. Der Controller versucht, das **Teilvideo zu übernehmen**: Scheitert das Beenden, wird trotzdem `CommitAsync` versucht; **hängt** das Beenden (Frist), ist die Datei nicht verlässlich und wird verworfen (`Discard`). Bekannte Grenze: die Rettung wurde nur über einen Kamera-Stopp getestet, nicht über echtes Abziehen.

## Beenden der App (Shutdown)

`BoothController.Cancel()` bei laufender Aufnahme (`Recording`/`VideoSaving`): `FinishVideoForShutdown` meldet das Speichern **zuerst** auf dem Threadpool an (`BeginFinalize(onThreadPool: true)`; vor dem Stopp-Signal, weil der Ablauf auf dem UI-Thread inline weiterliefe und sonst mit den langen Fristen selbst speichern würde), setzt dann `AppExit` und blockiert höchstens `ShutdownVideoWait` = **12 s** (echte Zeit) auf dem aufrufenden Thread. Die Fristen sind dabei kurz: Stopp 5 s, Umbenennen 5 s. Danach wird abgebaut. Bekannte Grenze: der UI-Thread blockiert bis zu 12 s.

## Speichern (`VideoStorage`)

Gegenstück zu `PhotoStorage` (siehe [druck.md](druck.md#photostorage)).

1. `PrepareAsync`: Zielordner (`Video.Folder` oder Standardordner `Videos\Fotobox+` = `AppInfo.DefaultVideoFolder`, optional Unterordner `yyyy-MM-dd`) per Schreibtest prüfen (Frist 10 s); ein **nicht voll qualifizierter Pfad gilt als nicht beschreibbar**. Bei Fehler einmaliges **Ausweichen** auf den Standardordner. Dann **freier Platz für die volle Höchstdauer** (Schätzung inkl. Ton plus Reserve, Abfrage 5 s; unbekannt = ok, UNC-Pfade werden nicht geprüft). Zu wenig Platz → `InsufficientDiskSpaceException` („Nicht genug Speicherplatz für ein Video. Bitte sag dem Gastgeber Bescheid.“). Die Prüfung steht bewusst außerhalb des Ordner-Rückfalls.
2. Aufgenommen wird in **`.<guid>.tmp.mp4`** im Zielordner.
3. `CommitAsync` (Frist 30 s, Datei nicht leer) benennt in **`<Präfix>_<yyyyMMdd_HHmmss>.mp4`** um (Standardpräfix `Fotobox+_Video`, Kollisionen `_2`, `_3` …, kulturunabhängig). `Discard` löscht die Temporärdatei (wirft nie).
4. Liegengebliebene `.{guid}.tmp.mp4` (Absturz, Stromausfall, `Discard` scheitert) werden nie aufgeräumt (bekannte Grenze).

## Gastansicht

- **Video-Hinweiszeile oben** (`GuestHint.Position = Top`, nur bei aktivem Videomodus, im Leerlauf): `GuestScreenSettings.VideoHintText`/`VideoHintStyle`; leer = automatischer Text aus den Video-Starttasten, Platzhalter `{Videotaste_N}`, `{Videotasten}`, `{Klick}` (Letzterer ab 2.5.1 auch im Video-Hinweis). Der Fun-Text ersetzt sie nur, wenn er an gleicher Position steht.
- **Aufnahme-Overlay** (oben links, dunkle Kapseln, nur Opacity-Animationen): REC-Punkt (blinkt, `ShowRecDot`), Laufzeit (`ShowElapsed`), Restzeit (`ShowRemaining`, entfällt während der Warnung), **Warnung** („Noch 10 s“, groß, pulsierend, ab `WarningSeconds`) und **„🔇 ohne Ton“**, wenn Ton gewünscht, aber nicht verfügbar (`UsesAudio && !RecordingHasAudio`).
- Video-Countdown, „Video wird gespeichert …“, Erfolgsanzeige (`VideoSavedText`, `SuccessDisplaySeconds`).
- Die Laufzeit wird bei jedem Lesen aus der Zeit berechnet (`TimeProvider`), `Changed` im Sekundentakt.

## Admin-Tabs „Video“ und „Speicher“

Haken „Videomodus aktiv“ (alles Weitere ist sonst ausgegraut), Höchstdauer (mit Text zur geschätzten Dateigröße), Countdown, Mindestlänge, Anzeigeoptionen (Aufnahmepunkt, Laufzeit, Restzeit, Warnung), Erfolgstext/-dauer, „Ton aufnehmen“ samt Mikrofonwahl (Mikrofonliste über `GetMicrophonesAsync`, fehlendes Mikrofon als „nicht angeschlossen“). **Seit 2.5.2:** Speicherordner (ändern/öffnen/Standardordner), Unterordner pro Tag, Videoqualität und Dateipräfix mit Beispielname stehen im Tab „Speicher“ in der Karte „Videos speichern“ (bei ausgeschaltetem Videomodus ausgegraut; der Tab „Video“ verweist darauf). Video-Hinweistext und -Stil im Tab „Gastansicht“; Tasten im Tab „Tasten“.

## Fristen

| Schritt | Frist | Ort |
|---|---|---|
| Vorbereiten (Ordner, Platz, Temp-Datei) | 10 s | `BoothController.VideoPrepareTimeout` |
| Kamerastart der Aufnahme (außen) | 15 s | `VideoStartTimeout` |
| Start im Kameradienst (innen) | 10 s | `RecordStartTimeout` |
| Stoppen (Datei abschließen) | 30 s außen, 15 s innen | `VideoStopTimeout`, `WinRtVideoRecording.FinishTimeout` |
| Umbenennen | 30 s | `VideoCommitTimeout`, `VideoStorage.CommitTimeout` |
| Ordnerprüfung | 10 s | `VideoStorage.ProbeTimeout` |
| Speicherplatzabfrage | 5 s | `VideoStorage.FreeSpaceTimeout` |
| App-Ende: Stopp / Umbenennen | je 5 s | `ShutdownStopTimeout`, `ShutdownCommitTimeout` |
| App-Ende: `Cancel()` blockiert höchstens | 12 s | `ShutdownVideoWait` |

## Bekannte Grenzen

- Im Modus `SharedReadOnly` ist keine Aufnahme möglich (Fehlermeldung); nicht mit einer von einer anderen App belegten Kamera auf echter Hardware geprüft.
- Kameras mit getrennten Vorschau-/Aufnahme-Pins sind ungetestet (Hardware-Tests liefen mit einer einzelnen Webcam).
- `Faulted` nur über Kamera-Stopp getestet. Liste: [bekannte-grenzen.md](bekannte-grenzen.md).
