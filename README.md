# Fotobox+

Fotobox+ (bis Version 1.4.1 „Fotobox“) ist eine Windows-App für Fotoboxen auf Veranstaltungen. Sie zeigt das Live-Bild einer Webcam im Vollbild. Per Tastendruck (Standard: Leertaste) startet ein Gast den Ablauf:

**Taste → Countdown → Foto(s) → Speichern im Ordner → Druck auf einem USB-/Fotodrucker → Warten aufs Druckende → bereit für das nächste Foto**

Es gibt eine Gastansicht und eine PIN-geschützte Admin-Ansicht für alle Einstellungen.

Neu in 2.5.2: Speicherort, Dateipräfix und Qualität der Videos im Admin-Tab „Speicher“ neben den Fotos sowie ein Regler für die Größe der Admin-Einstellungen (Standard 80 %). Neu in 2.5.1: Erklärungen (ⓘ) zu den Analyseraten, „Analysen pro Sekunde“ auch auf der Mittelfinger-Seite, übersichtlichere Tabs „Drucker“ und „Sicherheit & Daten“ sowie zahlreiche Fehlerbehebungen. Seit 2.2.1 hinzugekommen: Kamerasteuerung im Admin (Zoom, Helligkeit, Belichtung, Weißabgleich, Fokus u. a. live regeln, Flimmerschutz, Profile), Peace- und Mittelfingererkennung als versteckte Extras mit realistischen Schnurrbärten, Einstellungen exportieren/importieren und gleichmäßiges Trackpad-Scrollen im Admin. Seit 2.2: Fun-Modus „Zielerfassung“. Seit 2.0: Videomodus mit eigener Start-/Stopptaste (Standard Eingabetaste), Clips bis 5 Minuten (einstellbar) mit Ton, gespeichert in `Videos\Fotobox+`.

Die ausführliche Entwicklerdokumentation (Architektur, Einstellungen, Kamera, Druck, Video, Fun-Funktionen, Build, Tests, bekannte Grenzen) steht in [`docs/`](docs/README.md).

## Download und Installation

Die aktuelle Version steht unter **GitHub → Releases**:

| Datei | Für welchen PC? |
| --- | --- |
| `FotoboxPlus-<Version>-x64-portable.zip` | Windows-PCs mit Intel- oder AMD-Prozessor (x64) |
| `FotoboxPlus-<Version>-arm64-portable.zip` | Windows-on-ARM-PCs (ARM64) |

Es gibt keine Installation: ZIP entpacken (z. B. in „Dokumente“) und `Fotobox.exe` starten. Der Ordner kann später einfach gelöscht werden.

Welcher Typ der eigene PC ist, steht unter *Einstellungen → System → Info* bei „Systemtyp“.

**SmartScreen-Warnung:** Die Programmdatei `Fotobox.exe` ist nicht digital signiert. Windows zeigt deshalb eventuell „Der Computer wurde durch Windows geschützt“. Klicke auf **Weitere Informationen → Trotzdem ausführen**.

Systemvoraussetzungen:

- Windows 10 Version 2004 (Build 19041) oder neuer, Windows 11 eingeschlossen
- Die App ist self-contained, eine separate .NET-Installation ist nicht nötig
- Eine Webcam und ein Windows-Drucker (für den Druck)

Einstellungen, Logs, Fotos und Videos liegen außerhalb des Programmordners und bleiben erhalten, wenn er gelöscht wird.

**Umstieg von einer Setup-Version (Installer):** Einstellungen (`%LOCALAPPDATA%\Fotobox`), Fotos und Videos werden von der portablen Version automatisch weiterverwendet. Die alte Installation kann über Windows „Apps“ deinstalliert werden, das löscht keine Daten.

## Erster Start

Beim ersten Start legst du eine Admin-PIN fest (4 bis 12 Ziffern, mit Wiederholung). Bricht man den Dialog ab, beendet sich die App. Danach erscheint das Live-Bild.

## Videomodus

Neben Fotos nimmt Fotobox+ kurze Videos mit Ton auf (im Admin unter *Video* abschaltbar).

- **Start und Stopp:** Die Eingabetaste (`Enter`) startet ein Video, ein zweiter Druck beendet es und speichert es. Start und Stopp lassen sich im Tab *Tasten* auch auf verschiedene Tasten legen.
- **Ablauf:** Zuerst läuft ein Countdown (Standard 3 s), dann beginnt die Aufnahme. Ein früheres Beenden wird erst nach der Mindestlänge (Standard 2 s) angenommen. Die Höchstdauer ist einstellbar (10 s bis 30 min, Standard 5 min), danach endet das Video automatisch. Die Abbruchtaste (`Esc`) bricht den Countdown ab bzw. beendet und speichert die laufende Aufnahme.
- **Anzeige:** Oben in der Gastansicht steht eine Hinweiszeile mit der Videotaste. Während der Aufnahme zeigt die Gastansicht einen roten Aufnahmepunkt, die Laufzeit, die Restzeit und kurz vor dem Ende eine Warnung. Ein durchgestrichenes Mikrofon weist auf eine Aufnahme ohne Ton hin. Danach erscheint kurz „Video gespeichert!“. Videos werden nur gespeichert, nicht angezeigt oder gedruckt.
- **Wo liegen die Videos?** Standardmäßig in `Videos\Fotobox+` (mit Unterordner pro Tag). Ist der gewählte Ordner nicht beschreibbar, weicht Fotobox+ auf den Standardordner aus. Reicht der freie Speicherplatz nicht, zeigt die Gastansicht einen Hinweis.
- **Ton und Mikrofon:** Im Tab *Video* lässt sich der Ton abschalten oder ein Mikrofon wählen (Standard: Windows-Standardmikrofon). Fehlt das Mikrofon oder ist der Zugriff in Windows gesperrt (*Datenschutz und Sicherheit → Mikrofon*), wird ohne Ton aufgenommen. Eine Änderung beim Ton startet die Kamera nach dem Schließen der Admin-Ansicht kurz neu.
- **Einstellungen im Tab Video:** Videomodus ein/aus, Höchstdauer, Countdown, Mindestlänge, Videoqualität, Anzeige (Aufnahmepunkt, Laufzeit, Restzeit, Warnung), Text nach dem Speichern, Ton und Mikrofon, Speicherordner, Unterordner pro Tag und Dateipräfix. Den Hinweistext oben stellst du im Tab *Gastansicht* ein.

Fotos früherer Versionen bleiben in `Bilder\Fotobox`; Fotobox+ legt neue Fotos in `Bilder\Fotobox+` ab und verschiebt nichts.

## Leistungsanzeige

Mit `Strg` + `Alt` + `Y` (Tab *Tasten* änderbar oder entfernbar) blendet die Gastansicht oben rechts ein, wie stark der PC gerade arbeitet: Auslastung von Prozessor, Grafikkarte und KI-Einheit (NPU), Takt, Temperatur, Grafik- und Arbeitsspeicher, Bildraten von Kamera und Anzeige, Analysen pro Sekunde, Rechenzeit und das Gerät, auf dem jedes KI-Modell läuft. Ab 70 % wird ein Wert gelb, ab 90 % rot. Die Anzeige ist nach jedem Start aus, liegt über allem außer dem Blitz und ist nie im Foto oder Video. Gemessen wird nur, solange sie sichtbar ist (ohne Treiber oder Zusatzprogramm); was Windows für ein Gerät nicht liefert, steht als „nicht verfügbar“ da. Im Admin (Tab *Leistung*, Karte *Leistungsanzeige*) wählt man die Werte einzeln sowie Text oder Balken, Aktualisierung (0,5 bis 5 s), Schriftgröße und Hintergrund. Die Einstellungen gehören zu „Einstellungen teilen“.

## Peace-Zeichen (Easter Egg)

Zeigt ein Gast im Leerlauf mit der Hand das Peace-Zeichen ✌ und hält es kurz (Standard 1 s), führt Fotobox+ eine Überraschung aus. Die Gastansicht verrät nichts davon (kein Hinweistext). Die Funktion ist standardmäßig eingeschaltet und im Admin unter *Fun → Peaceerkennung* einstellbar.

- **Aktionen:** Im Admin sind eine oder mehrere Aktionen anhakbar; bei mehreren wird bei jeder Auslösung zufällig eine gewählt (Standard: nur Selbstzerstörung).
  - **Selbstzerstörung:** Roter Alarm-Countdown (5–30 s, Standard 10 s), im Moment der „Explosion“ entsteht das Foto. Optional mit Sirene, Pieptönen und Explosionsgeräusch (Standard aus). Die Abbruchtaste (`Esc`) bricht still ab.
  - **Foto:** Foto mit eigenem Countdown (0–30 s, Standard 3 s, 0 = sofort); neben den Ziffern erscheint ein kleines ✌.
  - **Schnurrbart:** Gesichter im Livebild bekommen einen Schnurrbart (sieben Formen – Zwirbel, Walross, Chevron, Pinsel, Dalí, Fu Manchu, Hufeisen – in sechs Haarfarben, fest oder zufällig aus einer wählbaren Auswahl), der auch im gespeicherten und gedruckten Foto zu sehen ist (ebenso in den Originalen, falls diese zusätzlich gespeichert werden). Wahlweise für alle im Bild oder nur für die Person mit dem Peace-Zeichen, bis zum nächsten Foto oder für eine feste Dauer. Optional folgt danach automatisch ein Foto mit eigenem Countdown.
- **Peace-Fotos** sind immer Einzelfotos: Ist ein Fotostreifen gewählt, entsteht stattdessen das randlose Einzelfoto (Foto mit Overlay bleibt erhalten). Danach läuft alles wie gewohnt (Vorschau, Speichern, Druck).
- **Auslösung:** Haltedauer (0,3–3 s), Fortschrittsring an der Hand (Standard aus, verrät die Funktion), Sperrzeit (0–600 s; zusätzlich muss die Hand erst heruntergenommen werden), Empfindlichkeit und Analysen pro Sekunde (1–10, Standard 5). Die Peaceerkennung hat Vorrang vor der Zielerfassung.
- **Vorschau und Tests:** Die Admin-Seite zeigt ein Livebild mit erkannten Handpunkten und dem Hinweis „Peace erkannt“ sowie die Knöpfe „Selbstzerstörung testen“ und „Bart testen“ (ohne Foto und Druck).
- **Hinweis:** Fehlauslösungen sind möglich und kosten Papier. Die Erkennung braucht zusätzliche Rechenleistung; bei schwachen Geräten die Analysen pro Sekunde senken oder die Funktion ausschalten.

## Mittelfinger (Easter Egg)

Zeigt ein Gast den Mittelfinger und hält ihn kurz (Standard 0,5 s), verschwinden alle Schnurrbärte mit einem „Plopp“, und ein laufender Selbstzerstörungs-Countdown bricht still ab. Andere Countdowns laufen weiter, ein Foto direkt danach entsteht ohne Bart. Die Funktion ist ab Werk eingeschaltet, wirkt nur zusammen mit der Peaceerkennung und ist im Admin unter *Fun → Mittelfinger* einstellbar (Haltedauer, Empfindlichkeit, Analysen pro Sekunde – derselbe Wert wie bei Peace – sowie „Aufheben testen“).

## Zielerfassung (Fun-Modus)

Im Leerlauf erkennt Fotobox+ Personen im Livebild und markiert sie wie im Film mit Zielboxen „TARGET 1“, „TARGET 2“ …; neue Ziele rasten animiert ein, danach erscheint ein Spaßtext („Hey du, nimm doch ein Foto oder Video mit mir auf!“). Das passiert nur in der Gastansicht und landet nie im Foto oder Video. Ab Werk eingeschaltet, im Admin unter *Fun → Personenerkennung* einstellbar (Erkennung per KI-Modell oder Windows-Gesichtserkennung, Box-Umfang, Stil, Farben, Animation, Texte, Töne, Wiederholung). Die Erkennung läuft komplett lokal, es werden keine Bilder gespeichert oder verschickt.

## Bedienung

**Gast:** Auslösetaste drücken (Standard: Leertaste). Es läuft ein Countdown, dann wird fotografiert. Mit der Abbruchtaste (Standard: `Esc`) lässt sich der Countdown oder ein laufender Druck abbrechen (der Druckauftrag wird gestoppt, das Foto bleibt gespeichert). Beim Fotostreifen folgen mehrere Aufnahmen. Danach erscheint kurz die Vorschau, das Foto wird gespeichert und gedruckt. Während des Drucks zeigt die Gastansicht einen Fortschrittsbalken. Ist der Drucker langsam, steht dort „Der Drucker ist etwas langsam – dein Foto kommt in ca. X Minuten“. Darunter steht „Nimm dein Foto erst aus dem Drucker, wenn hier „Fertig“ steht.“ Nach dem Druck erscheint 5 Sekunden lang „Fertig! Bitte nimm dein Foto aus dem Drucker“, danach ist die Fotobox wieder bereit (ebenso nach einer Fehlermeldung). Alt+F4 ist in der Gastansicht wirkungslos.

**Admin:** Standardmäßig `Strg` + `Alt` + `A` drücken (änderbar, siehe *Tasten*) und die PIN eingeben. Nach 5 falschen Eingaben ist die PIN-Eingabe für 30 Sekunden gesperrt. Jeder weitere Fehlversuch verlängert die Sperre (60 s, 2 min, 5 min, höchstens 15 min). Eine richtige PIN setzt den Zähler zurück.

Die Admin-Ansicht hat diese Bereiche. Unten rechts stehen „Schließen“, „Übernehmen“ und „Speichern & schließen“. „Übernehmen“ speichert, die Admin-Ansicht bleibt offen (Anzeige „Einstellungen gespeichert.“). „Schließen“ (auch über das X) verwirft ungespeicherte Änderungen, vorher fragt die App nach. Tasten, Hinweise und Layout wirken nach „Übernehmen“ sofort in der Gastansicht, Mikrofon/Ton und Monitorwahl erst nach dem Schließen der Admin-Ansicht. Ausnahme: Eine geänderte PIN gilt sofort.

- **Kamera:** Kamera und Auflösung wählen (Automatisch = höchste Auflösung bis 1920 × 1080), Live-Vorschau und/oder Foto spiegeln, Live-Vorschau zur Kontrolle (zeigt das ganze Bild). Die Karte *Bildeinstellungen* bietet die Regler, die die Kamera anbietet (z. B. Zoom, Belichtungskorrektur, Helligkeit, Kontrast, Weißabgleich, Fokus), mit Live-Wirkung, „Alle zurücksetzen“ und Profilen je Kamera (bis 10). Gespeichert werden nur verstellte Regler (mit „Übernehmen“); ohne Speichern werden sie beim Schließen zurückgenommen. Der Flimmerschutz (Netzfrequenz, Standard 50 Hz) wird bei jedem Kamerastart gesetzt. Welche Regler es gibt, hängt von Kamera und Treiber ab.
- **Ablauf:** Countdown (1–30 s, Standard 5), Dauer der Vorschau (1–60 s, Standard 4), Pause zwischen mehreren Fotos (0–30 s, Standard 2), Dauer der Fehleranzeige (2–60 s, Standard 6).
- **Layout:** Einzelfoto randlos, Fotostreifen (2–4 Fotos, Standard 3) oder Foto mit Overlay (eigene PNG-Grafik darüber). Beim Fotostreifen gibt es zwei Varianten: „2 Streifen nebeneinander (zum Teilen)“ oder „1 Streifen – Fotos untereinander“ (ganze Fotos im Kameraformat, mittig). Optional Bildunterschrift (max. 80 Zeichen, bis zu 2 Zeilen) und Datum auf dem Ausdruck. Die Schriftart der Bildunterschrift ist aus allen installierten Schriften wählbar (Standard Segoe UI). Unten bleibt 5 mm Sicherheitsabstand, damit nichts abgeschnitten wird. Unter „Auswahl für Gäste (Mausrad)“ lassen sich weitere Layouts anhaken, zwischen denen Gäste per Mausrad wechseln. Die Reihenfolge ist per ▲/▼ einstellbar, ★ markiert das Standard-Layout, und mit ☆ wird ein anderer Eintrag zum Standard.
- **Gastansicht:** Eigener Hinweistext für die Gastansicht (bis 100 Zeichen; leer = automatischer Text) mit den Platzhaltern `{Fototaste_1}`, `{Fototaste_2}` … (die jeweilige Taste), `{Fototasten}` (alle Auslösetasten) und `{Klick}` (Mausklick). Neun Darstellungsstile für den Foto- und den Video-Hinweis: Pille, Schlicht, Banner, Neon, Sprechblase, Laufschrift, Glas, Zettel und Kino (Auswahl als Raster mit Skizzen). Eine Live-Vorschau zeigt das Ergebnis. Im Abschnitt „Mausradzeile“ (erscheint nur bei mindestens zwei Gast-Layouts) lässt sich die Zeile „Mausrad: Layout wechseln“ ausblenden, mit eigenem Text versehen (leer = automatisch) und in fünf Darstellungen zeigen: passend zum Foto-Hinweis, kleine Pille, schlichter Text, Chip oder Pfeile.
- **Drucker:** Drucken ein/aus (bei „aus“ werden die Fotos nur gespeichert, der übrige Tab ist dann ausgegraut), erwartete Druckdauer (30–600 s, Standard 240: nach dieser Zeit ist der Fortschrittsbalken in der Gastansicht voll – auf die tatsächliche Druckzeit des Druckers einstellen), Drucker (Standard: Windows-Standarddrucker), Papiergröße (für randlosen Druck die randlose Größe des Fotodruckers wählen, z. B. „10 x 15 cm randlos“), Ausrichtung, Kopien (1–20), Zeitlimit für den Druck (10–900 s, Standard 300; automatisch mindestens erwartete Druckdauer + 60 s), Testseite drucken (mit den aktuellen, noch nicht gespeicherten Einstellungen).
- **Tasten:** Belegung der Tasten für „Foto auslösen“ (Standard Leertaste), „Admin-Ansicht öffnen“ (Standard `Strg` + `Alt` + `A`) und „Abbrechen“ (Countdown oder Druck, Standard `Esc`) sowie „Video starten“ und „Video beenden“ (Standard je `Enter`) und „Leistungsanzeige ein/aus“ (Standard `Strg` + `Alt` + `Y`, darf leer bleiben). Pro Aktion sind mehrere Tasten möglich (bis zu 8). Systemkombinationen wie Alt+F4, Alt+Tab oder die Windows-Taste sind nicht erlaubt. Merke dir die Admin-Taste, sonst kommst du nicht mehr in die Einstellungen.
- **Speicher (Fotos):** Speicherordner (ändern, öffnen, Standardordner), Unterordner pro Tag, Originalfotos zusätzlich speichern, JPEG-Qualität (50–100, Standard 92), Dateipräfix (Standard „Fotobox+“).
- **Leistung:** Hardwarebeschleunigung der KI-Erkennung: Automatisch (misst und nimmt das schnellste Gerät), nur Prozessor, Grafikkarte oder KI-Beschleuniger; Betriebsstand je Funktion, „Jetzt messen“ mit Messtabelle, Installation zusätzlicher Hersteller-Beschleuniger (Internet nötig) und die Windows-Grafikeinstellung „Hohe Leistung“. Änderungen wirken nach einem Neustart der Fotobox. Details in `docs/beschleunigung.md`. Dort steht auch die Karte *Leistungsanzeige* (siehe unten).
- **Sicherheit & Daten:** PIN ändern (4–12 Ziffern, gilt sofort). „Einstellungen teilen“: alle Einstellungen als Datei exportieren und auf einem anderen PC importieren – Kamera, Drucker, Papier, Speicherorte, Mikrofon und Bildschirm bleiben die des Ziel-PCs, die PIN wird nur auf Wunsch mitexportiert und beim Import erst nach Rückfrage übernommen; der Import gilt erst nach „Übernehmen“ bzw. „Speichern & schließen“. „Standardwerte wiederherstellen …“ setzt alle Einstellungen außer der PIN zurück (nach Bestätigung, gilt ebenfalls erst nach dem Speichern; Fotos und Overlay-Dateien bleiben erhalten).
- **Darstellung:** Farbschema der Admin-Ansicht und der PIN-Abfrage: „Wie Windows“ (Standard, folgt dem hellen bzw. dunklen App-Modus von Windows), „Hell“ oder „Dunkel“. Die Wahl wirkt sofort als Vorschau. Die Gastansicht bleibt davon unverändert. Außerdem „Monitor der Gastansicht“: Bei mehreren Monitoren lässt sich wählen, auf welchem die Gastansicht läuft (Standard: Hauptbildschirm); die Admin-Ansicht öffnet auf demselben Monitor. Der Wechsel erfolgt nach dem Schließen der Admin-Ansicht. Ist der gewählte Monitor nicht angeschlossen, erscheint ein Hinweis und die Gastansicht läuft vorübergehend auf dem Hauptbildschirm; sobald der Monitor wieder angeschlossen ist, wechselt sie automatisch zurück. Bei nur einem Monitor ist die Auswahl ausgegraut.
- **Video:** siehe Abschnitt *Videomodus*.
- **Fun:** Startseite mit Kacheln für Personenerkennung (Zielerfassung), Peace und Mittelfinger, jeweils mit Live-Vorschau und Testknöpfen. Ein ⓘ neben „Analysen pro Sekunde“ erklärt, was der Wert bewirkt und was sich empfiehlt.
- **Fotobox+ beenden:** Button unten links in der Admin-Ansicht (beendet die App).

## Wo liegt was?

- **Fotos:** standardmäßig `Bilder\Fotobox+` (bis 1.4.1: `Bilder\Fotobox`), mit Unterordner pro Tag (`JJJJ-MM-TT`, abschaltbar). Dateinamen: `<Präfix>_<JJJJMMTT_HHMMSS>.jpg`, Originale zusätzlich als `…_orig1.jpg` usw. Ist der gewählte Ordner nicht beschreibbar, weicht Fotobox+ auf `Bilder\Fotobox+` aus.
- **Einstellungen:** `%LOCALAPPDATA%\Fotobox\settings.json` (Overlay-Grafiken in `%LOCALAPPDATA%\Fotobox\overlays`).
- **Videos:** standardmäßig `Videos\Fotobox+`, Dateinamen `<Präfix>_<JJJJMMTT_HHMMSS>.mp4` (Standardpräfix `Fotobox+_Video`).
- **Logs:** `%LOCALAPPDATA%\Fotobox\logs\fotobox-JJJJMMTT.log`

## Problemlösung

- **„Die Kamera liefert kein Bild“:** Die Kamera ist erkannt, liefert aber kein Bild (z. B. virtuelle Kamera, die noch nicht aktiv ist, oder von einer anderen App belegt). Die Fotobox versucht es automatisch erneut. Hilft das nicht, die andere App schließen, Kamera neu anstecken oder in der Admin-Ansicht eine andere Kamera bzw. Auflösung wählen.
- **Kamera wird nicht erkannt:** Kamera anschließen, die Fotobox zeigt „Keine Kamera gefunden“. In der Admin-Ansicht unter *Kamera* prüfen, ob sie erscheint.
- **„Kein Zugriff auf die Kamera“:** In den Windows-Einstellungen unter *Datenschutz und Sicherheit → Kamera* den Zugriff für Desktop-Apps erlauben.
- **Drucker fragt nach einem Dateinamen** (z. B. „Microsoft Print to PDF“): Solche Drucker sind nicht geeignet und werden abgelehnt. Bitte einen echten Drucker wählen.
- **Papier leer, Drucker offline, Papierstau, Drucker braucht Hilfe:** Das Foto ist zu diesem Zeitpunkt bereits gespeichert. Die Fotobox zeigt einen Sperrbildschirm („Papier leer – bitte hol jemanden, der es auffüllt …“) und bleibt gesperrt, bis das Foto gedruckt ist; der Auftrag bleibt in der Druckerwarteschlange und druckt nach dem Beheben von selbst. Esc wirkt in der Sperre nicht. Ausweg: Admin-Ansicht öffnen (PIN) und schließen, dann endet die Sperre. Meldet der Treiber nichts, sperrt die Fotobox erst nach Ablauf des Zeitlimits (als „Papier leer“). Meldet ein Drucker (manche IPP/WSD-Treiber) nach vollständigem Senden kein Druckende, gilt der Auftrag nach Ablauf des Zeitlimits als fertig.
- **PIN vergessen:** Fotobox beenden (z. B. über den Task-Manager), die Datei `%LOCALAPPDATA%\Fotobox\settings.json` löschen und neu starten. Es erscheint wieder der Erststart mit PIN-Festlegung. Achtung: Dabei gehen auch alle übrigen Einstellungen verloren. Die Fotos bleiben unberührt.
- **„Die Einstellungsdatei (settings.json) konnte nicht gelesen werden“:** Meist greift ein anderes Programm (Virenscanner, Datensicherung) darauf zu. „OK“ versucht es erneut, „Abbrechen“ beendet die Fotobox. Einstellungen und PIN bleiben dabei unverändert.
- **Defekte `settings.json`:** Sie wird als `settings.json.bak-<Zeitstempel>` gesichert, danach startet die Fotobox mit Standardwerten. Die PIN bleibt erhalten, wenn ihr Abschnitt noch lesbar ist (z. B. wenn nur an anderer Stelle ein falscher Wert steht), und wird dann sofort wieder gespeichert. Ist die Datei selbst kaputt (Syntaxfehler), folgt der Erststart mit PIN-Festlegung.
- **„Die Fotobox läuft bereits“:** Es kann nur eine Instanz laufen.

## Für Entwickler

Voraussetzung: .NET 10 SDK unter Windows.

```
dotnet build Fotobox.sln
dotnet test --filter "Category!=Hardware"
```

Tests mit Kamera- oder Drucker-Hardware tragen `[Trait("Category", "Hardware")]` und laufen in CI nicht.

Die portablen ZIPs für x64 und ARM64 erzeugt `build/publish.ps1` (z. B. `pwsh build/publish.ps1 -Version 1.0.0`, Optionen `-Arch`, `-RequireVcRuntime`). Die Ergebnisse (Publish-Ordner und ZIPs) liegen in `artifacts/`.

Ein Release entsteht über GitHub Actions: Ein Tag `v*` (z. B. `v1.2.3`) startet den Workflow `release.yml` (Tests, Publish, GitHub-Release mit den portablen ZIPs). Der Workflow `build.yml` baut und testet jeden Push und Pull Request.

Ausführliche Entwicklerdokumentation: [`docs/README.md`](docs/README.md).
