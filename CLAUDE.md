# Fotobox – Hinweise für KI-Assistenten und Entwickler

WPF-App (.NET 10, `net10.0-windows10.0.19041.0`, x64 + ARM64, self-contained): Webcam-Livebild im Vollbild, Auslösetaste (Standard Leertaste) → Countdown → Foto(s) → Speichern → Druck. Admin-Ansicht (Standard Strg+Alt+A, Tasten frei belegbar), PIN-geschützt. Öffentliches Repo: keine persönlichen oder gerätespezifischen Daten (Benutzernamen, lokale Pfade, Hostnamen, Drucker-/Kameramodelle, Portnamen) in Code, Tests, Docs oder Commits.

## Struktur

- `src/Fotobox/Core` – UI-freie Logik: `BoothController`, `Interfaces.cs`, `Settings.cs` (inkl. `SettingsLimits`), `KeyChord.cs`, `Models.cs`, `AppLog`.
- `src/Fotobox/Services/Camera` – WinRT-Kameradienst, Formatwahl, deutsche Statusmeldungen, `CameraSupervision.cs` (Timeouts, Bild-Watchdog).
- `src/Fotobox/Services/Printing` – Drucken über System.Printing/XPS, Job-Statusauswertung, Seitengrößen (`PageSpecCache`).
- `src/Fotobox/Services/Layout` – Komposition der Druckseite (Einzel, Streifen in zwei `StripStyle`-Varianten, Overlay; `LayoutMath`).
- `src/Fotobox/Services/Storage` – Settings-Store (JSON), PIN-Service, Foto-Speicherung, Normalisierung.
- `src/Fotobox/ViewModels`, `Views` – MVVM (CommunityToolkit.Mvvm); `Booth` = Gastansicht, `Admin` = Einstellungen und PIN-Dialoge.
- `tests/Fotobox.Tests` – xUnit (mit Xunit.StaFact für WPF/STA).
- `build/publish.ps1`, `installer/Fotobox.iss`, `build/release-notes.template.md` – Publish, ZIPs, Inno-Setup-Installer, Release-Text.
- `.github/workflows` – `build.yml` (Build + Tests je Push/PR), `release.yml` (Tag `v*`).

## Architektur

- `BoothController` ist eine UI-freie Zustandsmaschine mit den Zuständen `Starting, Idle, Countdown, Capturing, Preview, Printing, Printed, Error`. `Printed` zeigt 5 s „Fertig“ nach dem Druck; die Gastansicht zeigt beim Drucken einen Fortschrittsbalken über `FlowSettings.ExpectedPrintSeconds` (Standard 240 s). Zeit läuft über `TimeProvider` (Tests mit `FakeTimeProvider`). Ereignisse kommen auf dem `SynchronizationContext` des `TryTrigger`-Aufrufers, Compose muss auf dem UI-Thread laufen. Der Druck läuft parallel zur Vorschau.
- Alle Abhängigkeiten sind Interfaces in `Core/Interfaces.cs`, DI-Registrierung (Singletons) in `App.xaml.cs`. Dort auch Single-Instance-Mutex und globale Exception-Handler.
- `SettingsLimits` (`Core/Settings.cs`) ist die einzige Quelle für Wertebereiche (Admin-UI, Validierung, Normalisierung), auch für Textlängen (`FilePrefixMaxLength` 40, `CaptionMaxLength` 80; zu lange Werte aus der Datei kürzt der Normalizer). Grenzen nicht anderswo duplizieren. Das Druck-Timeout (Standard 300 s) wird automatisch auf mindestens erwartete Druckdauer + 60 s angehoben.
- Tasten: `KeyChord` (Strings in `settings.json`, kein WPF-`KeyGesture`, weil das Buchstaben ohne Modifikator ablehnt). Pro Aktion bis `SettingsLimits.KeysPerActionMax`; Systemkombinationen (Alt+F4, Alt+Tab, Win-Taste …) sind gesperrt; ungültige Einträge werden beim Laden einzeln verworfen. Zusätzlich lösen linke/rechte Maustaste aus (`InputSettings.TriggerOnLeftClick`/`TriggerOnRightClick`, Standard an; Doppelklick zählt einmal).
- Gast-Layoutwahl: `LayoutSettings.GuestChoices` (`GuestLayout`: Single, StripDouble, StripSingle, Overlay; Abbildung in `Core/GuestLayouts.cs`). Ab zwei Einträgen wechselt der Gast im Idle per Mausrad, eine Vorschauliste mit allen Wahlmöglichkeiten untereinander (je Skizze + Name, die gewählte hervorgehoben; `BoothViewModel.LayoutOptions`) blendet nach 2 s aus. Reihenfolge der Liste = Scroll-Reihenfolge, im Admin per ▲/▼ einstellbar (`AdminViewModel.GuestOrder`, `MoveGuestLayoutUp/Down`); neu angehakte Layouts kommen ans Ende, der Normalizer sortiert nie. Standard bleibt `Layout.Kind`/`StripStyle` (kann an beliebiger Position stehen und ist der Start-Eintrag); der Normalizer hängt ihn hinten an, falls er fehlt. Die Wahl gilt nur für den nächsten Durchgang (`BoothController.TryTrigger(GuestLayout?)` arbeitet dann auf einer Kopie der Settings) und springt danach zurück.
- Settings-Store (`JsonSettingsStore`): `settings.json` in `%LOCALAPPDATA%\Fotobox`, atomar geschrieben. Fail-closed: Ist die Datei vorhanden, aber gesperrt oder nicht lesbar, ist `LoadFailed` gesetzt, `Save` verweigert und der Admin-Zugang bleibt zu. Sonst würde die PIN fälschlich als nicht gesetzt gelten und ein Gast könnte eine neue festlegen. Enums liest `TolerantEnumConverterFactory` (`Core/Settings.cs`): Unbekannte Namen (z. B. von einer neueren Version) machen die Datei nicht ungültig, sondern werden vom Normalizer auf den Standard gesetzt bzw. aus `GuestChoices` einzeln verworfen. Ungültiges JSON (Syntaxfehler, falscher Typ) wird als `settings.json.bak-<Zeitstempel>` gesichert und führt zu Defaults; die PIN (`Security`: Hash, Salt, Iterationen) wird dabei per `JsonDocument` gerettet, nur wenn das nicht geht, folgt der Erststart. Gelingt die Sicherung nicht, ebenfalls fail-closed.
- Kamera über WinRT `MediaCapture` (`WinRtCameraService`), weil das auf x64 und ARM64 gleichermaßen funktioniert. Auf- und Abbau einer Session laufen immer auf dem Threadpool (MTA), nie im UI-Thread, und WinRT-Ereignisse (`Failed`, `FrameArrived`) werden nie mit `-=` abgemeldet: Das scheitert in CsWinRT sonst mit OutOfMemoryException bzw. stürzt mit 0xC0000005 ab. Die Handler verwerfen veraltete Ereignisse per `ReferenceEquals(sender, _capture/_reader)`. Start/Kamerawechsel wiederholen kurz und still; ein Bild-Watchdog meldet „kein Bild“ als Fehler, und eine Wiederverbindung gilt erst mit dem ersten Bild. `InitializeAsync` hat wie Start/Stop/Dispose und die Geräteaufzählung ein Timeout; nach einem Step-Timeout werden MediaCapture/Reader erst nach Abschluss der hängenden Operation freigegeben. Start und Stopp prüfen nach dem Gate die Generation (ein überholter Aufruf tut nichts). `Failed` während `Starting` wird nur geloggt (Start bzw. Watchdog übernehmen); der Watchdog zählt ein Bild erst nach erfolgreichem Kopieren. `CameraPreview` tauscht Puffer (Triple-Buffering), `WritePixels` läuft außerhalb der Sperre. Nach dem Admin-Speichern wird die Kamera nur neu gestartet, wenn `AdminResult.CameraChanged` gesetzt ist oder sie nicht läuft.
- Druck über `System.Printing`/XPS (`PrintService`) mit Überwachung des Jobs bis zum Ende. Die Seitengröße wird in `PageSpecCache` gecacht (Vorabruf im Controller, bei Timeout letzter guter Wert; der Admin ermittelt sie asynchron). Vorabruf und Durchgang teilen sich je Drucker/Layout eine laufende Abfrage (`BoothController.GetPageSpecQuery`), damit sich bei hängendem Spooler keine Threads stapeln. Die Druckfrist läuft ab Beginn von `PrintCore` und greift so vor dem harten Limit des Controllers (Timeout + 30 s). Findet `BuildTicket` den Papiernamen nicht, sucht es Treiberpapier per Maß (±1 mm); eine Abweichung des Seitenverhältnisses wird nur geloggt, das Druckbild bleibt unverändert. Drucker mit Port `PORTPROMPT` (Dateinamen-Dialog, z. B. Print to PDF) werden abgelehnt.
- PIN: 4–12 Ziffern, PBKDF2-SHA256 mit Salt, Vergleich in konstanter Zeit. `Verify` lehnt unplausible Iterationszahlen (> `SettingsNormalizer.MaxPlausiblePinIterations`) und Hashlängen ≠ 32 Byte sofort ab; der gespeicherte Wert wird nie geklemmt (sonst passte der Hash nicht mehr). `PinAttemptLimiter`: 5 Fehlversuche, dann Sperre ab 30 s mit Eskalation bis 15 min, gemessen mit monotoner Zeit. Ein Limiter pro Prozess (in `AdminLauncher`).
- Admin „Standardwerte wiederherstellen“ (Tab Sicherheit, `AdminViewModel.ResetToDefaultsAsync`): setzt nach Bestätigung den Entwurf auf `new AppSettings()` zurück, behält aber `Security` (PIN) und `Version`; gilt wie alles im Admin erst mit „Speichern“. Danach werden UI-Zustand (Tasten, Listenauswahl, Schrift, Overlay) neu aufgebaut und die Kamera nur bei geänderten Kameraeinstellungen neu gestartet.
- Fotostreifen (`StripStyle`: 2 Streifen nebeneinander oder 1 Streifen mit ganzen Fotos untereinander): Bildunterschrift bis 2 Zeilen, unten `LayoutMath.SafeBottomMm` (5 mm) Sicherheitsabstand gegen Overscan; Schriftart wählbar (Fallback Segoe UI).
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

- Bei nicht erreichbarem Drucker kostet die erste Seitengrößen-Abfrage bis zu 5 s (danach Fallback auf den letzten guten Wert bzw. `PageSpec.Default`, 10 × 15 cm); Ergebnisse werden gecacht und vorab im Hintergrund geholt.
- Druckende-Erkennung bei IPP/WSD-Treibern: Ein vollständig gesendeter Auftrag ohne gemeldetes Druckende gilt nach dem Druck-Timeout als fertig.
- Eine syntaktisch kaputte `settings.json` wird gesichert (`.bak-…`), die übrigen Einstellungen müssen neu gesetzt werden; die PIN bleibt erhalten, sofern der `Security`-Block lesbar ist.

## Projektstand und Weiterarbeit

- Veröffentlicht: `v0.0.0-beta` (Prerelease), `v0.0.1`, `v1.0.0`, `v1.1.0`, `v1.1.1` und `v1.1.2` (Tag auf dem `main`-Stand des Releases). Arbeitsbranch zuletzt: `fix/stabilitaet-v1.1.2`. Neue Arbeit auf einem Branch, per CI (`build.yml`) prüfen, nach `main` mergen, dann Tag `v*` pushen.
- Lokal ist nur das SDK 10.0.100-rc.1 installiert, die CI nutzt das finale .NET 10. Der Entwicklungsrechner ist ARM64, x64 läuft dort unter Emulation.
- Tests: `dotnet test --filter "Category!=Hardware"` (636 Tests). Hardware-Tests (echte Kamera, z. B. Kamera-Neustart) laufen nur lokal. Die Tests schreiben ins echte App-Log (`%LOCALAPPDATA%\Fotobox\logs`).
- Manuelle E2E-Prüfung (Erststart, Alt+F4-Sperre, Foto speichern, PIN-Sperre, Kamerawechsel) erfolgte mit einem isolierten Benutzerprofil (Umgebungsvariablen `USERPROFILE`/`LOCALAPPDATA` umbiegen) per UI Automation. Es gab bewusst keinen Testdruck auf einem echten Drucker; der Druck muss mit beliebigen Druckern funktionieren (herstellerneutral).
- Das Repo ist öffentlich: keine persönlichen Daten oder Gerätenamen committen (siehe oben).

## Offene Punkte

Erledigt bis v1.1.2 (v1.1.0: Maus als Auslöser, Layoutwahl per Mausrad, Druckdauer-Standard 240 s; v1.1.1: Standardwerte im Admin wiederherstellen, einstellbare Reihenfolge der Gästeauswahl; v1.1.2: reines Stabilitäts-Release – tolerantes Laden mit PIN-Rettung, Kamera-Start/Stopp-Reihenfolge, Druckfristen, abbrechbares Speichern, Startfehlerpfad, Livebild-Puffer, kulturunabhängige Logs/Dateinamen): Kamerawechsel-Fehler, Bild-Watchdog (fehlende Frames), Sichtprüfung der Gast-/Admin-Ansicht, Seitengrößen-Cache, Init-Timeout, Versionierung.

1. **Druck auf echten Fotodruckern ungetestet:** Randloser Druck, Papiergrößen, Druckende-Erkennung (IPP/WSD) und die 5-mm-Sicherheitszone gegen echten Overscan sind nur per Unit-Tests und gegen Mock-Zustände abgedeckt. Rückmeldungen von echten Geräten einarbeiten.
2. **Installer unsigniert:** SmartScreen warnt. Code-Signing braucht ein gekauftes Zertifikat.
3. **Reconnect-Pfad „Kamera ohne Bild“:** Nur per Unit-Test geprüft, nicht mit echter Hardware.
4. **Tests schreiben ins echte App-Log** (`%LOCALAPPDATA%\Fotobox\logs`) statt in ein isoliertes Verzeichnis.
5. **Virtuelle Kamera mit Platzhalterbild:** Eine virtuelle Kamera ohne verbundene Quelle liefert Frames mit einem einfarbigen Platzhalterbild. Der Watchdog erkennt nur ausbleibende Frames, nicht einfarbige Bilder, daher steht „Kamera läuft“. Ohne gespeicherte Kamera wird einfach das erste Gerät der Liste gewählt (das kann eine virtuelle Kamera sein). Abhilfe bisher: Kamera im Admin explizit wählen.
6. **Parallele Testläufe können hängen:** Laufen zwei `dotnet test` gleichzeitig (z. B. aus mehreren Worktrees), blieben einzelne Läufe in WPF-Fenstertests hängen (`AdminViewTests.AdminWindow_CanBeCreated`, `CameraPreviewTests.Mirror_…`, `BoothViewModelTests.BoothWindow_LayoutList_…`). Einzelne Läufe und die CI sind stabil. Ursache ungeklärt; bis dahin Tests nie parallel starten.

### Bewusst nicht geändert (Audit v1.1.2, später entscheiden)

Beim Audit für v1.1.2 gefunden, aber absichtlich nicht angefasst, weil es Verhalten oder Druckbild ändern würde bzw. ohne Hardware nicht prüfbar ist:

- **Druckbild bei abweichendem Seitenverhältnis:** Weicht das vom Treiber gewählte Papier vom berechneten Layout ab (z. B. 5-s-Timeout ohne letzten guten Wert → `PageSpec.Default`, oder der Treiber ersetzt eine eigene Papiergröße), wird beschnitten statt eingepasst. Im Extremfall rückt die Bildunterschrift an die Unterkante bzw. in den Overscan. Bisher nur Warnung im Log (`PrintService.WarnOnAspectMismatch`). Option: ab > 1 % Abweichung einpassen (weiße Ränder möglich) – erst mit echtem Fotodrucker entscheiden.
- **Overlay wird gestreckt:** `LayoutComposer` zieht das Overlay-PNG ohne Rücksicht auf sein Seitenverhältnis auf die ganze Seite. Bei Ausrichtung „Auto“ ist die Overlay-Seite quer, ein hochformatiges PNG wird verzerrt.
- **Standard-Layout bleibt in der Gästeauswahl:** `AdminViewModel.EnsureDefaultInGuestChoices` hängt das gewählte Standard-Layout an `GuestChoices` an. Ein versehentlicher Probeklick (z. B. auf Overlay und zurück) hinterlässt den Eintrag dauerhaft; ohne Overlay-PNG blockiert dann die Prüfung das Speichern mit einer schwer zuzuordnenden Meldung.
- **PORTPROMPT bei nicht lesbarem Port:** Lässt sich `QueuePort` nicht lesen, wird der Drucker angenommen (herstellerneutral, Netzwerk-Queues). Ein Datei-Dialog-Drucker würde dann bis zum Druck-Timeout hängen.
- **Verschwundener Druckauftrag = fertig:** `PrintJobStateEvaluator` wertet einen aus der Warteschlange verschwundenen Job als erledigt, auch wenn ihn jemand gelöscht hat.
- **`PrintService.GetPageSpec` blockiert ohne eigenes Timeout:** Durch die geteilte Abfrage im Controller höchstens ein blockierter Thread je Drucker/Layout; eine asynchrone Variante mit Timeout fehlt noch.
- **Speichern auf hängender Netzfreigabe:** Der Ausweichordner greift erst nach einer Exception, nicht wenn der erste Versuch hängt (dann nach 30 s Fehler, kein Druck). Ein einzelnes `WriteJpeg` und die `File.Move`-Phase unter `NamingGate` sind nicht abbrechbar. Liegengebliebene `*.tmp` (Fotoordner, `settings.json.*.tmp`) werden nie aufgeräumt.
- **Render-Performance der Gastansicht:** Große `DropShadowEffect`s (Countdown, Hinweis-Pille, Vorschau-Rahmen) über dem 30-fps-Livebild wurden nicht optimiert (`CacheMode="BitmapCache"` ungemessen, Countdown könnte beim Skalieren unscharf werden). `LayoutComposer.Compose` erzeugt mehrere Vollseiten-Puffer (Pbgra32→Bgra32-Umwandlung, nur entbehrlich, wenn jedes Layout die Seite vollständig deckend malt).
- **Kamera-Abbau nach `TeardownTimeout`:** Überschreitet `reader.StopAsync` beim Abbau die Frist, wird trotzdem sofort freigegeben (Aufschieben könnte die Kamera dauerhaft blockieren).
