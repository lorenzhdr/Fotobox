# Fotobox+ – Hinweise für KI-Assistenten und Entwickler

WPF-App „Fotobox+“ (bis 1.4.1 „Fotobox“; sichtbarer Name `AppInfo.DisplayName` in `Core/Models.cs`, Exe, Namespace und `%LOCALAPPDATA%\Fotobox` heißen weiter „Fotobox“), .NET 10 (`net10.0-windows10.0.19041.0`), x64 + ARM64, self-contained. Webcam-Livebild im Vollbild, Auslösetaste (Standard Leertaste) → Countdown → Foto(s) → Speichern → Druck; Videomodus (Standard Enter) mit Ton, nur Speichern; Fun-Funktionen Zielerfassung, Peace- und Mittelfingererkennung sowie Herzerkennung (Kisscam); PIN-geschützte Admin-Ansicht (Standard Strg+Alt+A) mit Kamerasteuerung und „Einstellungen teilen“.

**Öffentliches Repo:** keine persönlichen oder gerätespezifischen Daten (Benutzernamen, lokale Pfade, Hostnamen, Mailadressen, Drucker-/Kameramodelle, Portnamen) in Code, Tests, Docs oder Commits. Commits nur mit der GitHub-noreply-Adresse (repo-lokale `user.email`).

## Dokumentation

Die ausführliche Doku steht in [`docs/`](docs/README.md) und ist die Detailquelle – vor Änderungen die passende Datei lesen und bei Verhaltensänderungen mitpflegen (bei Widerspruch gilt der Code):

| Thema | Datei |
|---|---|
| Funktionen, Bedienung, Projektstruktur | `docs/ueberblick.md` |
| Schichten, DI, Threading, `BoothController`-Zustände, Druckabbruch, Tasten, Monitorwahl, alle Fristen | `docs/architektur.md` |
| `AppSettings`, `SettingsLimits`, Store, Normalizer/Migration, PIN, Export/Import | `docs/einstellungen.md` |
| Admin-Fenster, Speichern, Kamera-Entscheidung nach dem Schließen, Farbschema, Scrollen | `docs/admin.md` |
| WinRT-Kamera, Wiederverbindung, Formatwahl, Kamerasteuerung | `docs/kamera.md` |
| Druck, Seitengrößen, Layouts, Fotospeicherung | `docs/druck.md` |
| Videomodus | `docs/video.md` |
| Zielerfassung | `docs/fun-zielerfassung.md` |
| Peace- und Mittelfingererkennung, Bärte | `docs/fun-peace-mittelfinger.md` |
| Herzerkennung, Kisscam | `docs/fun-herz-kisscam.md` |
| Beschleunigung der KI-Erkennung (Windows ML: GPU/NPU, Messung, Rückfall auf die CPU) | `docs/beschleunigung.md` |
| Publish, VC++-Runtime, CI, Release | `docs/build-release.md` |
| Tests | `docs/tests.md` |
| Bekannte Grenzen, offene Punkte, bewusst nicht Geändertes | `docs/bekannte-grenzen.md` |

## Invarianten (nie brechen)

- **Nie hängen bleiben:** Alles, was blockieren kann, hat ein Timeout und einen definierten Fehlerpfad zurück zu `Idle`; neue blockierende Aufrufe ebenfalls absichern (Fristen-Tabelle in `docs/architektur.md`).
- `SettingsLimits` (`Core/Settings.cs`) ist die einzige Quelle für Wertebereiche (Admin-UI, Validierung, Normalisierung) – nie duplizieren.
- Settings-Store ist **fail-closed**: Eine vorhandene, aber nicht lesbare `settings.json` darf nie zu „PIN nicht gesetzt“ führen. Gespeicherte PIN-Werte (Hash, Salt, Iterationen) werden nie geklemmt; nur `SetPin` nutzt `MinPinIterations`.
- Neue `AppSettings`-Eigenschaften in `SettingsSections.ExcludedProperties` oder `DeviceSpecificSettings.Paths` einordnen (ein Test erzwingt das). Enums nur hinten erweitern.
- Kamera: Auf-/Abbau einer `MediaCapture`-Session nur auf dem Threadpool (MTA), WinRT-Ereignisse (`Failed`, `FrameArrived`) nie mit `-=` abmelden (CsWinRT: OutOfMemory/0xC0000005), veraltete Ereignisse per `ReferenceEquals` verwerfen. Kamera-Regler lösen nie einen Kamera-Neustart aus; „nur Verstelltes setzen“.
- Native ONNX-Sessions nie während eines `Run` freigeben (`DeferredReleaseGate`). Töne liegen bewusst in nie freigegebenem nativem Speicher.
- ONNX-Sessions baut nur `OnnxSessionFactory` (`Services/Detection/Acceleration`); die Detektoren laden über `AcceleratedSession` (Gerät laut Plan, bei Fehler/Frist einmal Rückfall auf die CPU, nie ein Hänger). Katalog und Installation (Windows ML) nur auf dem Threadpool, mit Fristen; Messungen und Prüfungen von Geräten nur im eigenen Messprozess (`OutOfProcessBenchmark`, `Program.Main` zweigt `--beschleunigung-messen` vor `App` ab; ein Treiberabsturz ist in .NET nicht abfangbar), Software-Adapter (WARP) nie als Gerät, Grafikkarte/NPU im Hauptprozess erst nach bestandener Messung; `Performance`-Felder sind gerätespezifisch.
- Zielmarken nie im Foto/Video; nur der Schnurrbart (und beim Kisscam-Foto der Rahmen) wird ins Foto gezeichnet.
- Herzerkennung: Die `HeartEngine` sitzt im `PeaceOverlayViewModel` (der Gestendienst bedient nur die oberste Anforderung; kein eigenes Herz-ViewModel mit eigener Anforderung). Wiederscharf nur nach in Hand-Updates beobachteter Ruhe. Neue Herz-Aktionen: Enum hinten, `HeartActions`-Katalog, `switch` in `BoothViewModel.OnHeartAction`.
- `MusicPlayer` (Kisscam-Musik): eigener STA-Thread, Aufrufer posten nur und warten nie synchron (schon gar nicht auf dem UI-Thread); fehlende Datei, kaputtes Media Foundation oder Fehler führen nur zu Stille und Logwarnung, nie zu einem Hänger. Der Musikordner ist gerätespezifisch; die Song-Config liegt im Ordner.
- `BoothController` ist UI-frei, Zeit über `TimeProvider`; Compose auf dem UI-Thread.
- Admin-Farben nur in `AdminColors.Light.xaml`/`AdminColors.Dark.xaml` (gleiche Schlüssel, Test prüft das), Bezug nur per `DynamicResource`; neue Farben in beiden Paletten anlegen.
- Jede neue WPF-Testklasse trägt `[Collection(WpfCollection.Name)]` (sonst BAML-Deadlock); Pack-URIs in Tests vorher `WpfCollection.EnsurePackUris()`.

## Konventionen

- UI-Texte Deutsch; der Gast wird geduzt, der Admin gesiezt. Doku und Kommentare Deutsch mit echten Umlauten.
- Logging über `AppLog` (wirft nie, `%LOCALAPPDATA%\Fotobox\logs`, 30 Tage); jede Fun-Auslösung steht im Log.
- Tests mit echter Kamera/echtem Drucker: `[Trait("Category", "Hardware")]` (nicht in der CI). Render-Tests: `[Trait("Category", "Visual")]`, aktiv nur mit `FOTOBOX_RENDER_DIR`.
- Der Druck muss herstellerneutral mit beliebigen Druckern funktionieren; kein Testdruck auf echter Hardware ohne Rückfrage.

## Befehle

```
dotnet build Fotobox.sln
dotnet test --filter "Category!=Hardware"
pwsh build/publish.ps1 -Version 1.2.3 [-Arch x64|arm64] [-RequireVcRuntime]
pwsh build/smoke-test.ps1 -PublishDir artifacts/publish/x64
```

- Lokal ist nur das SDK 10.0.100-rc.1 installiert, die CI nutzt das finale .NET 10. Der Entwicklungsrechner ist ARM64 (x64 unter Emulation). Lokal ohne `-RequireVcRuntime` bauen (passende CRT fehlt lokal); lokal gebaute Pakete enthalten dann keine VC++-Runtime und dürfen nicht weitergegeben werden – ausgelieferte Pakete kommen aus der CI. Details in `docs/build-release.md`.
- Die Tests schreiben ins echte App-Log. Lokal gelegentlich ein zeitabhängiger Einzelfehlschlag möglich; bei Verdacht mehrfach wiederholen.

## Arbeitsablauf und Release

- Neue Arbeit auf einem Branch, per CI (`build.yml`) prüfen, nach `main` mergen, dann Tag `v*` pushen → `release.yml` (Tests, Publish, Release-Text aus `build/release-notes.template.md`, `gh release create`; Versionen mit `-` werden Prerelease). Version in `Directory.Build.props`.
- Nur portable ZIPs (`FotoboxPlus-<Version>-<arch>-portable.zip`, kein „+“ im Namen), kein Installer seit 2.5.0-beta.
- Auf GitHub sind nur das neueste Release und `v1.4.1` sichtbar; ältere Releases werden gelöscht (Tags bleiben), ihre Texte stehen im Abschnitt „Ältere Versionen“ (`<details>`) der Release-Vorlage. Release-Texte immer als Datei mit echten Zeilenumbrüchen (`--notes-file`). Nach jedem Release die gemergten Arbeitsbranches löschen; dauerhaft gibt es nur `main`.

## Projektstand

- Aktuell: **`v2.8.0`** (stabil, „Latest“). Neu in 2.8.0: Beschleunigung der KI-Erkennung über Windows ML (GPU/NPU, Auto/CPU/GPU/NPU, Messung im eigenen Prozess, Admin-Tab „Leistung“, Windows-Grafikeinstellung, `docs/beschleunigung.md`) und Stopp-Hinweis während der Videoaufnahme (`VideoSettings.ShowStopHint`/`StopHintText`). Davor 2.7.0: Kisscam-Musikordner (`Musik\Fotobox+\Kisscam`, `HeartSettings.KisscamMusicFolder` gerätespezifisch, ersetzt `KisscamMusicPath` ohne Übernahme; `KisscamMusicLibrary` mit Watcher, Songliste mit Auswahl, Startzeit, Song-Lautstärke und Anspielen, Song-Config `kisscam-musik.json` im Ordner, Zufallswahl `KisscamSongPicker`), Zielerfassung rastet bei Zuwachs gemeinsam neu ein (`FunSettings.MaxLockOnRestarts`, Standard 3), Esc beendet keine laufende Videoaufnahme mehr. Davor 2.6.0: Herzerkennung mit Kisscam (`docs/fun-herz-kisscam.md`), nach Praxistest an echten Händen nachkalibriert (PairedHands-Pipeline lässt die zweite Hand durch, Klassifikator V4 mit Zeigefinger-Kontakt über PIP/DIP/Spitze, Haltetoleranz max(700 ms, 4 Analyseabstände), Standard-Haltedauer 1800 ms) sowie gedrosselte `Herz-Diagnose:`-Logzeilen. Davor 2.5.2: Video-Speicheroptionen (Ordner, Tagesunterordner, Präfix, Qualität) in eigener Karte im Tab „Speicher“ (ausgegraut bei Videomodus aus), Zoom des Admin-Inhaltsbereichs (`Appearance.AdminZoomPercent`, 60–100 %, Standard 80 %, gerätespezifisch, Regler unten im Tab „Darstellung“). Davor 2.5.1: Enthält die Betas 2.3.0 (Peaceerkennung, realistische Bärte, Zielerfassung-Erweiterungen), 2.4.0 (Mittelfingererkennung, Einstellungen teilen) und 2.5.0 (Kamerasteuerung, Trackpad-Scrollen, nur ZIPs). Neu in 2.5.1: „Analysen pro Sekunde“ auch auf der Mittelfinger-Seite (gemeinsamer Wert mit Peace), ⓘ-Tooltips zu allen Analyseraten (`InfoBadge`, `AdminInfoTexts`), Druck-Schalter im Tab „Drucker“, Tab „Sicherheit & Daten“, sowie Fehlerbehebungen aus einem Gesamt-Audit (gerettete PIN sofort speichern, PIN-Änderung schlägt importierte PIN, Peace-Wiederscharf nur nach beobachteter Ruhe, ratenabhängige Verlustfrist der Zielerfassung, verworfener Ton-Entwurf → Kamera-Neustart, Regler-Cache in Automatik, Regler nach „Alle zurücksetzen“, `{Klick}` im Video-Hinweis).
- Sichtbare Releases: `v2.8.0` und `v1.4.1`. Alle übrigen (`v0.0.0-beta` … `v2.7.0`) sind als Tags vorhanden, Texte im Archiv.
- Praxistests: Der User hat 2.5.x kurz getestet („sollte alles funktionieren“). Nicht auf echten Veranstaltungen geprüft: Peace/Mittelfinger, Zielerfassung mit vielen Gästen, Kamera-Regler mit USB-Webcams, echte Fotodrucker (siehe `docs/bekannte-grenzen.md`).
- Tests: `dotnet test --filter "Category!=Hardware"` (über 3400 Tests, ca. 60 s; kritischer Pfad ist die serielle WPF-Collection).
