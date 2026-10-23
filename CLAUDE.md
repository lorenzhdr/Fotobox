# Fotobox – Hinweise für KI-Assistenten und Entwickler

WPF-App (.NET 10, `net10.0-windows10.0.19041.0`, x64 + ARM64, self-contained): Webcam-Livebild im Vollbild, Leertaste → Countdown → Foto(s) → Speichern → Druck. Admin-Ansicht per Strg+Alt+A, PIN-geschützt. Öffentliches Repo: keine persönlichen oder gerätespezifischen Daten (Benutzernamen, lokale Pfade, Hostnamen, Drucker-/Kameramodelle, Portnamen) in Code, Tests, Docs oder Commits.

## Struktur

- `src/Fotobox/Core` – UI-freie Logik: `BoothController`, `Interfaces.cs`, `Settings.cs` (inkl. `SettingsLimits`), `Models.cs`, `AppLog`.
- `src/Fotobox/Services/Camera` – WinRT-Kameradienst, Formatwahl, deutsche Statusmeldungen.
- `src/Fotobox/Services/Printing` – Drucken über System.Printing/XPS, Job-Statusauswertung, Seitengrößen.
- `src/Fotobox/Services/Layout` – Komposition der Druckseite (Einzel, Streifen, Overlay).
- `src/Fotobox/Services/Storage` – Settings-Store (JSON), PIN-Service, Foto-Speicherung, Normalisierung.
- `src/Fotobox/ViewModels`, `Views` – MVVM (CommunityToolkit.Mvvm); `Booth` = Gastansicht, `Admin` = Einstellungen und PIN-Dialoge.
- `tests/Fotobox.Tests` – xUnit (mit Xunit.StaFact für WPF/STA).
- `build/publish.ps1`, `installer/Fotobox.iss`, `build/release-notes.template.md` – Publish, ZIPs, Inno-Setup-Installer, Release-Text.
- `.github/workflows` – `build.yml` (Build + Tests je Push/PR), `release.yml` (Tag `v*`).

## Architektur

- `BoothController` ist eine UI-freie Zustandsmaschine mit den Zuständen `Starting, Idle, Countdown, Capturing, Preview, Printing, Error`. Zeit läuft über `TimeProvider` (Tests mit `FakeTimeProvider`). Ereignisse kommen auf dem `SynchronizationContext` des `TryTrigger`-Aufrufers, Compose muss auf dem UI-Thread laufen. Der Druck läuft parallel zur Vorschau.
- Alle Abhängigkeiten sind Interfaces in `Core/Interfaces.cs`, DI-Registrierung (Singletons) in `App.xaml.cs`. Dort auch Single-Instance-Mutex und globale Exception-Handler.
- `SettingsLimits` (`Core/Settings.cs`) ist die einzige Quelle für Wertebereiche (Admin-UI, Validierung, Normalisierung). Grenzen nicht anderswo duplizieren.
- Settings-Store (`JsonSettingsStore`): `settings.json` in `%LOCALAPPDATA%\Fotobox`, atomar geschrieben. Fail-closed: Ist die Datei vorhanden, aber gesperrt oder nicht lesbar, ist `LoadFailed` gesetzt, `Save` verweigert und der Admin-Zugang bleibt zu. Sonst würde die PIN fälschlich als nicht gesetzt gelten und ein Gast könnte eine neue festlegen. Ungültiges JSON wird als `settings.json.bak-<Zeitstempel>` gesichert und führt zu Defaults, also Erststart. Gelingt die Sicherung nicht, ebenfalls fail-closed.
- Kamera über WinRT `MediaCapture` (`WinRtCameraService`), weil das auf x64 und ARM64 gleichermaßen funktioniert. Auf- und Abbau einer Session laufen immer auf dem Threadpool (MTA), nie im UI-Thread, und WinRT-Ereignisse (`Failed`, `FrameArrived`) werden nie mit `-=` abgemeldet: Das scheitert in CsWinRT sonst mit OutOfMemoryException bzw. stürzt mit 0xC0000005 ab. Die Handler verwerfen veraltete Ereignisse per `ReferenceEquals(sender, _capture/_reader)`.
- Druck über `System.Printing`/XPS (`PrintService`) mit Überwachung des Jobs bis zum Ende. Drucker mit Port `PORTPROMPT` (Dateinamen-Dialog, z. B. Print to PDF) werden abgelehnt.
- PIN: 4–12 Ziffern, PBKDF2-SHA256 mit Salt, Vergleich in konstanter Zeit. `PinAttemptLimiter`: 5 Fehlversuche, dann Sperre ab 30 s mit Eskalation bis 15 min. Ein Limiter pro Prozess (in `AdminLauncher`).
- `PhotoStorage` weicht bei nicht beschreibbarem Zielordner auf `Bilder\Fotobox` aus. Dateinamen `{Prefix}_{yyyyMMdd_HHmmss}.jpg`, Kollisionen `_2`, `_3` …

## Konventionen

- UI-Texte Deutsch. Gast wird geduzt („Bitte versuche es noch einmal“), Admin wird gesiezt.
- Nie hängen bleiben: Alles, was blockieren kann, hat ein Timeout und einen definierten Fehlerpfad zurück zu `Idle` (Seitengröße 5 s, Speichern 30 s, Druck = Druck-Timeout + 30 s Reserve, Kamera-Dispose beim Beenden 5 s). Neue blockierende Aufrufe ebenfalls absichern.
- Logging über `AppLog` (wirft nie, `%LOCALAPPDATA%\Fotobox\logs`).
- Tests mit echter Kamera/echtem Drucker mit `[Trait("Category", "Hardware")]` markieren. Sie laufen nicht in CI.

## Befehle

```
dotnet build Fotobox.sln
dotnet test --filter "Category!=Hardware"
pwsh build/publish.ps1 -Version 1.2.3 [-Arch x64|arm64] [-SkipInstaller] [-RequireInstaller]
```

- `publish.ps1` erzeugt je Architektur self-contained Publish, `Fotobox-<Version>-<arch>-portable.zip` und (mit Inno Setup 6, `ISCC.exe`) `Fotobox-Setup-<Version>-<arch>.exe` in `artifacts/`. Standardversion aus `Directory.Build.props`.
- Release: Tag `v*` pushen, dann läuft `release.yml` (Tests, Publish, Release-Notes aus Template, `gh release create`; Versionen mit `-` werden als Prerelease markiert). `workflow_dispatch` baut nur Artefakte (Standardversion `0.0.0-test`).
- `build.yml`: Release-Build und Tests ohne Hardware auf `windows-latest`.
- Die `AppId`-GUID in `Fotobox.iss` niemals ändern. Der Installer löscht bei Deinstallation bewusst keine Einstellungen oder Fotos.
- Der Installer ist nicht signiert (SmartScreen-Hinweis steht im Release-Template).

## Bekannte Grenzen (nicht blockierend)

- Bei nicht erreichbarem Drucker kostet die Seitengrößen-Abfrage bis zu 5 s pro Durchgang (danach Fallback `PageSpec.Default`, 10 × 15 cm).
- Druckende-Erkennung bei IPP/WSD-Treibern: Ein vollständig gesendeter Auftrag ohne gemeldetes Druckende gilt nach dem Druck-Timeout als fertig.
- Eine kaputte `settings.json` wird gesichert (`.bak-…`) und führt zum Erststart, die Einstellungen müssen neu gesetzt werden.

## Projektstand und Weiterarbeit

- Veröffentlicht: `v0.0.0-beta` (Prerelease) und `v0.0.1` (Tag auf `1e384f1`, `main` = `1e384f1`). Arbeitsbranch: `feature/fotobox-app`. Neue Arbeit auf einem Branch, per CI (`build.yml`) prüfen, nach `main` mergen, dann Tag `v*` pushen.
- Lokal ist nur das SDK 10.0.100-rc.1 installiert, die CI nutzt das finale .NET 10. Der Entwicklungsrechner ist ARM64, x64 läuft dort unter Emulation.
- Tests: `dotnet test --filter "Category!=Hardware"` (274 Tests). Hardware-Tests (echte Kamera, z. B. Kamera-Neustart) laufen nur lokal.
- Manuelle E2E-Prüfung (Erststart, Alt+F4-Sperre, Foto speichern, PIN-Sperre, Kamerawechsel) erfolgte mit einem isolierten Benutzerprofil (Umgebungsvariablen `USERPROFILE`/`LOCALAPPDATA` umbiegen) per UI Automation. Es gab bewusst keinen Testdruck auf einem echten Drucker; der Druck muss mit beliebigen Druckern funktionieren (herstellerneutral).
- Das Repo ist öffentlich: keine persönlichen Daten oder Gerätenamen committen (siehe oben).

## Offene Punkte (noch zu fixen)

1. **Kamerawechsel im Admin:** Direkt nach dem Wechsel der Kamera (z. B. virtuelle Kamera → eingebaute Kamera) erscheint kurz eine Fehlermeldung in der Admin-Vorschau (Media-Foundation-Ressourcenfehler), bis die Retry-Schleife in `WinRtCameraService` die Kamera startet. Kosmetisch; Ziel: Fehler während des Wechsels unterdrücken oder vor dem Neustart kurz warten.
2. **Virtuelle Kamera ohne Bild:** Eine virtuelle Kamera (Camo) meldete „Kamera läuft“, lieferte aber kein Bild. Ursache nicht untersucht (evtl. Treiber/Format). Mögliche Abhilfe: Watchdog „kein Frame innerhalb X s“ mit Fehlermeldung statt stiller Schwarzfläche.
3. **Nicht am echten Bildschirm verifiziert:** (a) Der doppelte „Kamera wird gestartet“-Text in der Gastansicht ist laut Code weg, (b) die Admin-Vorschau zeigt das ganze Livebild. Bitte einmal visuell prüfen.
4. **Seitengröße bei Offline-Drucker:** `GetPageSpec` kostet bis zu 5 s pro Durchgang. Idee: Ergebnis zwischenspeichern oder asynchron vorab holen.
5. **`InitializeAsync` der Kamera hat kein eigenes Timeout** (Start/Stop/Dispose haben eines). Absichern, damit ein hängender Treiber den Start nicht blockiert.
6. **Druck auf echten Fotodruckern ungetestet:** Randloser Druck, Papiergrößen und Druckende-Erkennung (IPP/WSD) sind nur per Unit-Tests und gegen Mock-Zustände abgedeckt. Rückmeldungen von echten Geräten einarbeiten.
7. **Installer unsigniert:** SmartScreen warnt. Code-Signing wäre eine spätere Verbesserung.
8. **Versionierung:** Der nächste Release kann `v0.0.2` sein; `v1.0.0` erst nach Test mit echtem Drucker und Kamera.
