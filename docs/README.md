# Entwicklerdokumentation Fotobox+ (Stand 2.5.1)

Fotobox+ ist eine WPF-Anwendung (.NET 10, Windows, x64 und ARM64, self-contained) für Fotoboxen auf Veranstaltungen: Das Livebild einer Webcam läuft im Vollbild, ein Tastendruck (Standard Leertaste) startet Countdown, Foto(s), Speichern und Druck. Dazu kommen ein Videomodus, ein PIN-geschützter Admin-Bereich und spielerische Zusatzfunktionen (Zielerfassung, Peaceerkennung, Mittelfingererkennung, Herzerkennung mit Kisscam).

Diese Dokumentation richtet sich an Menschen und an KI-Agenten, die am Code weiterarbeiten. Sie beschreibt den tatsächlichen Stand des Codes; bei Abweichungen gilt der Code. Nutzerorientierte Kurzinfos stehen in der `README.md` im Repo-Root, Hinweise für KI-Assistenten in `CLAUDE.md`.

Hinweis zum Namen: Die App hieß bis 1.4.1 „Fotobox“. Der sichtbare Name ist `AppInfo.DisplayName` (`src/Fotobox/Core/Models.cs`), Exe, Namespace und der Datenordner `%LOCALAPPDATA%\Fotobox` heißen weiterhin „Fotobox“.

## Lesereihenfolge

1. `ueberblick.md` – was die App kann, wie man sie bedient, wo im Repo was liegt.
2. `architektur.md` – Schichten, Zustandsmaschine, Threading, Fristen („Nie hängen bleiben“).
3. `einstellungen.md` – `settings.json`, Normalisierung, PIN, Einstellungen teilen.
4. `admin.md` – das Admin-Fenster.
5. Danach nach Bedarf: Kamera, Druck, Video, Fun-Funktionen, Build, Tests, Grenzen.

## Dateien

| Datei | Inhalt |
|---|---|
| `ueberblick.md` | Funktionen aus Nutzersicht, Standardbedienung (Tasten/Maus), Projektstruktur. |
| `architektur.md` | Schichten, DI, Start/Ende, Threading, `BoothController`-Zustandsmaschine, Druckabbruch, Layoutwahl, Tasten, Monitorplatzierung, Logging, Tabelle aller Fristen. |
| `einstellungen.md` | `AppSettings`-Abschnitte mit Standardwerten und Bereichen, `JsonSettingsStore`, Normalizer, PIN, Einstellungen teilen, Standardwerte wiederherstellen. |
| `admin.md` | Tabs des Admin-Fensters, Speichern und Rückfragen, Kamera-Entscheidung nach dem Schließen, Farbschema, Scrollen, PIN-Dialoge, Live-Vorschauen. |
| `kamera.md` | WinRT-Kameradienst, Wiederverbindung, Watchdog, Formatwahl, Kamerasteuerung (Regler, Profile, Flimmerschutz). |
| `druck.md` | Drucken über System.Printing/XPS, Jobüberwachung, Seitengrößen, Layout-Komposition, Fotostreifen. |
| `video.md` | Videomodus: Ablauf, Aufnahme, Speicherung, Gastansicht. |
| `fun-zielerfassung.md` | Zielerfassung: Personenerkennung (YOLOX/Windows-Gesichter), Engine, Tracker, Animation, Töne. |
| `fun-peace-mittelfinger.md` | Peaceerkennung (Selbstzerstörung, Foto, Bart) und Mittelfingererkennung. |
| `fun-herz-kisscam.md` | Herzerkennung (Formen, Paarbildung, `PairedHands`, Engine) und Aktion Kisscam (Ablauf, Rahmen, Zoom, Musikordner mit Songliste, Abbruch). |
| `beschleunigung.md` | Beschleunigung der KI-Erkennung über Windows ML: Modi (Auto/CPU/GPU/NPU), Messung und Cache, Rückfälle, Fristen, Windows-Grafikeinstellung, Installation der Hersteller-Anbieter, API für die Oberfläche, Logzeilen. |
| `build-release.md` | Publish, CI, Release-Ablauf, VC++-Runtime, Smoke-Test. |
| `tests.md` | Teststrategie, Befehle, Konventionen (STA, Collections, Hardware-Tests). |
| `bekannte-grenzen.md` | Bekannte Grenzen, offene Punkte und bewusst nicht Geändertes. |

## Für KI-Agenten

### Wo anfangen

1. `CLAUDE.md` im Repo-Root lesen (dichte Zusammenfassung), danach die Detaildatei zum Thema aus der Tabelle oben.
2. Den Code gegenprüfen, bevor Aussagen weitergegeben werden. Wertebereiche stehen in `SettingsLimits` (`src/Fotobox/Core/Settings.cs`), Dienste als Interfaces in `src/Fotobox/Core/Interfaces.cs`, die Registrierung in `src/Fotobox/App.xaml.cs` (`RegisterServices`).
3. Bauen und testen: `dotnet build Fotobox.sln`, `dotnet test --filter "Category!=Hardware"` (Details in `tests.md`).

### Invarianten, die nie gebrochen werden

| Invariante | Details |
|---|---|
| Nie hängen bleiben: Alles, was blockieren kann, hat ein Timeout und einen Fehlerpfad zurück zu `Idle`. Neue blockierende Aufrufe ebenfalls absichern. | `architektur.md`, Abschnitt „Nie hängen bleiben“ |
| `SettingsLimits` ist die einzige Quelle für Wertebereiche (Admin-UI, Validierung, Normalisierung); Grenzen nirgends duplizieren. | `einstellungen.md` |
| Settings-Store ist fail-closed: Nicht lesbare `settings.json` darf nie zu „PIN nicht gesetzt“ führen. | `einstellungen.md` |
| Die PIN-Iterationen und der gespeicherte Hash werden nie geklemmt oder verändert (außer durch `SetPin`). | `einstellungen.md` |
| Neue Felder in `AppSettings` müssen für „Einstellungen teilen“ eingeordnet werden (`SettingsSections.ExcludedProperties` oder `DeviceSpecificSettings.Paths`); ein Test erzwingt das. | `einstellungen.md` |
| Enums in Einstellungen nur hinten erweitern (Reihenfolge ist teils UI-relevant, Werte stehen in Dateien). | `einstellungen.md` |
| WinRT-Ereignisse der Kamera (`Failed`, `FrameArrived`) werden nie mit `-=` abgemeldet; Auf-/Abbau einer Kamera-Session läuft immer auf dem Threadpool, nie im UI-Thread. | `kamera.md` |
| Native ONNX-Sessions werden nie während eines `Run` freigegeben (`DeferredReleaseGate`). | `fun-zielerfassung.md` |
| Fotos und Videos enthalten keine Zielmarken; nur der Schnurrbart wird ins Foto gezeichnet. | `fun-zielerfassung.md`, `fun-peace-mittelfinger.md` |
| Der `BoothController` ist UI-frei; Zeit läuft über `TimeProvider`; Compose läuft auf dem UI-Thread. | `architektur.md` |
| Alle Admin-Farben stehen nur in den beiden Paletten (`AdminColors.Light.xaml`/`AdminColors.Dark.xaml`, gleiche Schlüssel) und werden per `DynamicResource` bezogen. | `admin.md` |
| Der `MusicPlayer` läuft auf eigenem Thread; Aufrufer posten nur und warten nie synchron (UI-Thread), ohne Song bzw. bei Fehlern bleibt es still. Die Musikbibliothek blockiert nie (Snapshot `Current`, Dateizugriffe im Hintergrund mit Frist). | `fun-herz-kisscam.md` |
| Jede neue WPF-Testklasse trägt `[Collection(WpfCollection.Name)]`. | `tests.md` |
| Öffentliches Repo: keine persönlichen oder gerätespezifischen Daten (Benutzernamen, lokale Pfade, Hostnamen, Drucker-/Kameramodelle, Portnamen) in Code, Tests, Docs oder Commits. | – |

### Konventionen

- UI-Texte sind Deutsch. Der Gast wird geduzt („Bitte versuche es noch einmal“), der Admin gesiezt.
- Doku und Kommentare: Deutsch mit echten Umlauten.
- Logging über `AppLog` (wirft nie); jede Auslösung der Fun-Funktionen steht im Log.
- Tests mit echter Kamera oder echtem Drucker tragen `[Trait("Category", "Hardware")]` und laufen nicht in der CI.
- Neue Arbeit auf einem Branch, per CI prüfen, nach `main` mergen, dann Tag `v*` (siehe `build-release.md`).
