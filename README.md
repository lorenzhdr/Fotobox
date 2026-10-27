# Fotobox

Fotobox ist eine Windows-App für Fotoboxen auf Veranstaltungen. Sie zeigt das Live-Bild einer Webcam im Vollbild. Per Tastendruck (Standard: Leertaste) startet ein Gast den Ablauf:

**Taste → Countdown → Foto(s) → Speichern im Ordner → Druck auf einem USB-/Fotodrucker → Warten aufs Druckende → bereit für das nächste Foto**

Es gibt eine Gastansicht und eine PIN-geschützte Admin-Ansicht für alle Einstellungen.

## Download und Installation

Die aktuelle Version steht unter **GitHub → Releases**:

| Datei | Für welchen PC? |
| --- | --- |
| `Fotobox-Setup-<Version>-x64.exe` | Installer für Windows-PCs mit Intel- oder AMD-Prozessor (x64) |
| `Fotobox-Setup-<Version>-arm64.exe` | Installer für Windows-on-ARM-PCs (ARM64) |
| `Fotobox-<Version>-x64-portable.zip` / `-arm64-portable.zip` | Portable Version ohne Installation: entpacken und `Fotobox.exe` starten |

Welcher Typ der eigene PC ist, steht unter *Einstellungen → System → Info* bei „Systemtyp“.

**SmartScreen-Warnung:** Der Installer ist nicht digital signiert. Windows zeigt deshalb eventuell „Der Computer wurde durch Windows geschützt“. Klicke auf **Weitere Informationen → Trotzdem ausführen**.

Systemvoraussetzungen:

- Windows 10 Version 2004 (Build 19041) oder neuer, Windows 11 eingeschlossen
- Die App ist self-contained, eine separate .NET-Installation ist nicht nötig
- Eine Webcam und ein Windows-Drucker (für den Druck)
- Der Installer installiert standardmäßig nur für den aktuellen Benutzer und braucht keine Administratorrechte (im Dialog auf „für alle Benutzer“ umschaltbar)

Bei der Deinstallation bleiben Einstellungen, Logs und Fotos erhalten.

## Erster Start

Beim ersten Start legst du eine Admin-PIN fest (4 bis 12 Ziffern, mit Wiederholung). Bricht man den Dialog ab, beendet sich die App. Danach erscheint das Live-Bild.

## Bedienung

**Gast:** Auslösetaste drücken (Standard: Leertaste). Es läuft ein Countdown, dann wird fotografiert. Mit der Abbruchtaste (Standard: `Esc`) lässt sich der Countdown oder ein laufender Druck abbrechen (der Druckauftrag wird gestoppt, das Foto bleibt gespeichert). Beim Fotostreifen folgen mehrere Aufnahmen. Danach erscheint kurz die Vorschau, das Foto wird gespeichert und gedruckt. Während des Drucks zeigt die Gastansicht einen Fortschrittsbalken. Ist der Drucker langsam, steht dort „Der Drucker ist etwas langsam – dein Foto kommt in ca. X Minuten“. Nach dem Druck erscheint 5 Sekunden lang „Fertig! Bitte nimm dein Foto aus dem Drucker“, danach ist die Fotobox wieder bereit (ebenso nach einer Fehlermeldung). Alt+F4 ist in der Gastansicht wirkungslos.

**Admin:** Standardmäßig `Strg` + `Alt` + `A` drücken (änderbar, siehe *Tasten*) und die PIN eingeben. Nach 5 falschen Eingaben ist die PIN-Eingabe für 30 Sekunden gesperrt. Jeder weitere Fehlversuch verlängert die Sperre (60 s, 2 min, 5 min, höchstens 15 min). Eine richtige PIN setzt den Zähler zurück.

Die Admin-Ansicht hat diese Bereiche. Änderungen gelten erst nach „Speichern“, „Abbrechen“ verwirft sie.

- **Kamera:** Kamera und Auflösung wählen (Automatisch = höchste verfügbare), Live-Vorschau und/oder Foto spiegeln, Live-Vorschau zur Kontrolle (zeigt das ganze Bild).
- **Ablauf:** Countdown (1–30 s, Standard 5), Dauer der Vorschau (1–60 s, Standard 4), Pause zwischen mehreren Fotos (0–30 s, Standard 2), Dauer der Fehleranzeige (2–60 s, Standard 6), Drucken ein/aus (bei „aus“ werden die Fotos nur gespeichert), erwartete Druckdauer (30–600 s, Standard 240): Nach dieser Zeit ist der Fortschrittsbalken in der Gastansicht voll. Stelle sie auf die tatsächliche Druckzeit deines Druckers ein.
- **Layout:** Einzelfoto randlos, Fotostreifen (2–4 Fotos, Standard 3) oder Foto mit Overlay (eigene PNG-Grafik darüber). Beim Fotostreifen gibt es zwei Varianten: „2 Streifen nebeneinander (zum Teilen)“ oder „1 Streifen – Fotos untereinander“ (ganze Fotos im Kameraformat, mittig). Optional Bildunterschrift (max. 80 Zeichen, bis zu 2 Zeilen) und Datum auf dem Ausdruck. Die Schriftart der Bildunterschrift ist aus allen installierten Schriften wählbar (Standard Segoe UI). Unten bleibt 5 mm Sicherheitsabstand, damit nichts abgeschnitten wird. Unter „Gästeauswahl“ lassen sich weitere Layouts anhaken, zwischen denen Gäste per Mausrad wechseln. Die Reihenfolge ist per ▲/▼ einstellbar, ★ markiert das Standard-Layout, und mit ☆ wird ein anderer Eintrag zum Standard.
- **Gastansicht:** Eigener Hinweistext für die Gastansicht (bis 100 Zeichen; leer = automatischer Text) mit den Platzhaltern `{Fototaste_1}`, `{Fototaste_2}` … (die jeweilige Taste), `{Fototasten}` (alle Auslösetasten) und `{Klick}` (Mausklick). Drei Darstellungsstile: Pille, Schlicht (weißer Text mit Schatten) und Banner (Band über die volle Breite). Eine Live-Vorschau zeigt das Ergebnis.
- **Drucker:** Drucker (Standard: Windows-Standarddrucker), Papiergröße (für randlosen Druck die randlose Größe des Fotodruckers wählen, z. B. „10 x 15 cm randlos“), Ausrichtung, Kopien (1–20), Zeitlimit für den Druck (10–900 s, Standard 300; automatisch mindestens erwartete Druckdauer + 60 s), Testseite drucken (mit den aktuellen, noch nicht gespeicherten Einstellungen).
- **Tasten:** Belegung der Tasten für „Foto auslösen“ (Standard Leertaste), „Admin-Ansicht öffnen“ (Standard `Strg` + `Alt` + `A`) und „Abbrechen“ (Countdown oder Druck, Standard `Esc`). Pro Aktion sind mehrere Tasten möglich (bis zu 8). Systemkombinationen wie Alt+F4, Alt+Tab oder die Windows-Taste sind nicht erlaubt. Merke dir die Admin-Taste, sonst kommst du nicht mehr in die Einstellungen.
- **Speicher:** Speicherordner (ändern, öffnen, Standardordner), Unterordner pro Tag, Originalfotos zusätzlich speichern, JPEG-Qualität (50–100, Standard 92), Dateipräfix (Standard „Fotobox“).
- **Sicherheit:** PIN ändern (4–12 Ziffern). „Standardwerte wiederherstellen …“ setzt alle Einstellungen außer der PIN zurück (nach Bestätigung, gilt erst nach „Speichern“; Fotos und Overlay-Dateien bleiben erhalten).
- **Darstellung:** Farbschema der Admin-Ansicht und der PIN-Abfrage: „Wie Windows“ (Standard, folgt dem hellen bzw. dunklen App-Modus von Windows), „Hell“ oder „Dunkel“. Die Wahl wirkt sofort als Vorschau. Die Gastansicht bleibt unverändert.
- **Fotobox beenden:** Button unten links in der Admin-Ansicht.

## Wo liegt was?

- **Fotos:** standardmäßig `Bilder\Fotobox`, mit Unterordner pro Tag (`JJJJ-MM-TT`, abschaltbar). Dateinamen: `<Präfix>_<JJJJMMTT_HHMMSS>.jpg`, Originale zusätzlich als `…_orig1.jpg` usw. Ist der gewählte Ordner nicht beschreibbar, weicht die Fotobox auf `Bilder\Fotobox` aus.
- **Einstellungen:** `%LOCALAPPDATA%\Fotobox\settings.json` (Overlay-Grafiken in `%LOCALAPPDATA%\Fotobox\overlays`).
- **Logs:** `%LOCALAPPDATA%\Fotobox\logs\fotobox-JJJJMMTT.log`

## Problemlösung

- **„Die Kamera liefert kein Bild“:** Die Kamera ist erkannt, liefert aber kein Bild (z. B. virtuelle Kamera, die noch nicht aktiv ist, oder von einer anderen App belegt). Die Fotobox versucht es automatisch erneut. Hilft das nicht, die andere App schließen, Kamera neu anstecken oder in der Admin-Ansicht eine andere Kamera bzw. Auflösung wählen.
- **Kamera wird nicht erkannt:** Kamera anschließen, die Fotobox zeigt „Keine Kamera gefunden“. In der Admin-Ansicht unter *Kamera* prüfen, ob sie erscheint.
- **„Kein Zugriff auf die Kamera“:** In den Windows-Einstellungen unter *Datenschutz und Sicherheit → Kamera* den Zugriff für Desktop-Apps erlauben.
- **Drucker fragt nach einem Dateinamen** (z. B. „Microsoft Print to PDF“): Solche Drucker sind nicht geeignet und werden abgelehnt. Bitte einen echten Drucker wählen.
- **Druckfehler, Papier leer, Drucker offline, Papierstau:** Die Fotobox zeigt den Grund an, das Foto ist zu diesem Zeitpunkt bereits gespeichert. Nach der Fehleranzeige (Standard 6 s) ist sie wieder bereit. Dauert der Druck länger als das Zeitlimit, meldet die Fotobox einen Druckfehler, der Auftrag bleibt in der Druckerwarteschlange. Meldet ein Drucker (manche IPP/WSD-Treiber) nach vollständigem Senden kein Druckende, gilt der Auftrag nach Ablauf des Zeitlimits als fertig.
- **PIN vergessen:** Fotobox beenden (z. B. über den Task-Manager), die Datei `%LOCALAPPDATA%\Fotobox\settings.json` löschen und neu starten. Es erscheint wieder der Erststart mit PIN-Festlegung. Achtung: Dabei gehen auch alle übrigen Einstellungen verloren. Die Fotos bleiben unberührt.
- **„Die Einstellungsdatei (settings.json) konnte nicht gelesen werden“:** Meist greift ein anderes Programm (Virenscanner, Datensicherung) darauf zu. „OK“ versucht es erneut, „Abbrechen“ beendet die Fotobox. Einstellungen und PIN bleiben dabei unverändert.
- **Defekte `settings.json`:** Sie wird als `settings.json.bak-<Zeitstempel>` gesichert, danach startet die Fotobox mit Standardwerten und dem Erststart.
- **„Die Fotobox läuft bereits“:** Es kann nur eine Instanz laufen.

## Für Entwickler

Voraussetzung: .NET 10 SDK unter Windows.

```
dotnet build Fotobox.sln
dotnet test --filter "Category!=Hardware"
```

Tests mit Kamera- oder Drucker-Hardware tragen `[Trait("Category", "Hardware")]` und laufen in CI nicht.

Installer und portable ZIPs für x64 und ARM64 erzeugt `build/publish.ps1` (z. B. `pwsh build/publish.ps1 -Version 1.0.0`, Optionen `-Arch`, `-SkipInstaller`, `-RequireInstaller`). Der Installer wird mit [Inno Setup](https://jrsoftware.org/isinfo.php) 6 aus `installer/Fotobox.iss` gebaut. Ohne Inno Setup entstehen nur Publish-Ordner und ZIPs. Die Ergebnisse liegen in `artifacts/`.

Ein Release entsteht über GitHub Actions: Ein Tag `v*` (z. B. `v1.2.3`) startet den Workflow `release.yml` (Tests, Publish, GitHub-Release mit Installern und ZIPs). Der Workflow `build.yml` baut und testet jeden Push und Pull Request.
