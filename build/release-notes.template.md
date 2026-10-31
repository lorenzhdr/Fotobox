## Fotobox+ {{VERSION}}

### Neu in 2.10.3

- **Tracking-Overlay:** Mit **Strg + Alt + X** blendet die Gastansicht ein, was die Erkennungen gerade sehen: Personen-Boxen, die fünf Punkte der Gesichter, die 21 Punkte jeder Hand, Gesten, Bärte und Herzen, dazu oben links eine Statuszeile mit den laufenden Erkennungen und ihren Analysen pro Sekunde. Ab Werk aus und nach jedem Start wieder aus. Sichtbar ist es im Leerlauf und im Foto-Countdown und liegt über den Fun-Effekten.
- **Einstellbar:** Im Admin (Tab „Fun“, Kachel „Tracking-Overlay“) lassen sich Ebenen, Beschriftungen, Statuszeile, Größe und Stil (Technik oder Sci-Fi) wählen. Optional zeigt eine „Warum-Info“ je Hand, warum eine Geste erkannt oder verworfen wurde. Die Taste ändert man im Tab „Tasten“. Die Einstellungen gehören zu „Einstellungen teilen“.
- **Nur Anzeige:** Das Overlay fordert nichts an und ändert keine Erkennung; Analyseraten und Auslösungen bleiben gleich. Es ist nie im Foto oder Video und kostet nichts, solange es ausgeblendet ist.
- **Hinweis:** Noch nicht mit einer echten Kamera geprüft. Die Statuszeile kann sich mit Hinweisen oben in der Gastansicht überlagern. Die Zahlen an den Händen können von denen der Erkennung abweichen.

Alle Neuerungen von 2.10.2 und den Versionen davor stehen unten unter „Ältere Versionen“.

### Welche Datei passt zu meinem PC?

| Datei | Für welchen PC? |
| --- | --- |
| `FotoboxPlus-{{VERSION}}-x64-portable.zip` | Windows-PCs mit **Intel- oder AMD-Prozessor** (x64) |
| `FotoboxPlus-{{VERSION}}-arm64-portable.zip` | Windows-PCs mit **Snapdragon/ARM-Prozessor** (z. B. Copilot+ PCs) |

Unsicher? Unter *Einstellungen → System → Info* steht bei „Systemtyp“, ob es ein x64- oder ein ARM-basierter Prozessor ist.

### Hinweis zu Windows SmartScreen

Die Programmdatei `Fotobox.exe` ist nicht digital signiert. Windows zeigt deshalb beim ersten Start möglicherweise die Meldung „Der Computer wurde durch Windows geschützt“ an.
Klicke dort auf **Weitere Informationen → Trotzdem ausführen**.

### Hinweise

- Es gibt keine Installation: ZIP entpacken (z. B. in „Dokumente“) und `Fotobox.exe` starten. Der Ordner kann später einfach gelöscht werden.
- Einstellungen und Protokolle liegen in `%LOCALAPPDATA%\Fotobox`, Fotos standardmäßig unter `Bilder\Fotobox+`, Videos unter `Videos\Fotobox+`, Kisscam-Musik unter `Musik\Fotobox+\Kisscam`. Alles bleibt erhalten, wenn der Programmordner gelöscht wird.
- **Umstieg von einer Setup-Version (Installer):** Einstellungen (`%LOCALAPPDATA%\Fotobox`), Fotos und Videos bleiben erhalten und werden von der portablen Version automatisch weiterverwendet. Die alte Installation kann über Windows „Apps“ deinstalliert werden, das löscht keine Daten.
- Beim ersten Start wird eine Admin-PIN festgelegt.
- Standardtasten: **Leertaste** löst das Foto aus, **Eingabetaste** startet und beendet ein Video, **Esc** bricht den Countdown oder einen laufenden Druck ab (eine laufende Videoaufnahme und die Druckersperre nicht) und entfernt im Leerlauf sichtbare Schnurrbärte, **Strg + Alt + A** öffnet die Admin-Ansicht, **Strg + Alt + Y** blendet die Leistungsanzeige ein und aus, **Strg + Alt + X** das Tracking-Overlay. Alle Tasten lassen sich im Admin unter **Tasten** ändern.

---

### Ältere Versionen

Ältere Versionen werden nicht mehr einzeln unter „Releases“ aufgeführt (außer den Versionen 2.9.1 und 1.4.1). Ihr Quellcode ist weiterhin unter [Tags](https://github.com/lorenzhdr/Fotobox/tags) abrufbar.

<details>
<summary>Änderungen der Versionen 0.0.0-beta bis 2.10.2 anzeigen</summary>

#### [Fotobox+ 2.10.2](https://github.com/lorenzhdr/Fotobox/tree/v2.10.2) · 31.10.2026

- **Hinweis während des Drucks:** Unter der Wartezeit steht jetzt zusätzlich „Nimm dein Foto erst aus dem Drucker, wenn hier „Fertig“ steht.“ Am Druck selbst ändert sich nichts.

#### [Fotobox+ 2.10.1](https://github.com/lorenzhdr/Fotobox/tree/v2.10.1) · 31.10.2026

- **Leistungsanzeige:** Mit **Strg + Alt + Y** blendet die Gastansicht oben rechts ein, wie ausgelastet der PC ist: Prozessor, Grafikkarte und NPU (Auslastung, Takt, Temperatur), Grafik- und Arbeitsspeicher, Kamera- und Anzeige-FPS, Analysen pro Sekunde, Rechenzeit und das Gerät je KI-Modell. Ab Werk aus und nach jedem Start wieder aus. Die Anzeige liegt über allem außer dem Blitz und ist nie im Foto oder Video.
- **Einstellbar:** Im Admin (Tab „Leistung“, Karte „Leistungsanzeige“) lassen sich die Werte einzeln wählen, als Text oder Balken, mit Aktualisierung alle 0,5 bis 5 Sekunden, Schriftgröße und Hintergrund. Ab 70 % wird ein Wert gelb, ab 90 % rot. Die Taste ändert man im Tab „Tasten“. Die Einstellungen gehören zu „Einstellungen teilen“.
- **Hinweis:** Gemessen wird nur, solange die Anzeige sichtbar ist, ohne Zusatzprogramm oder Treiber. Windows liefert nicht jedes Gerät alle Werte; fehlende erscheinen als „nicht verfügbar“. Noch nicht auf allen Grafikkarten und NPUs geprüft.

#### [Fotobox+ 2.10.0](https://github.com/lorenzhdr/Fotobox/tree/v2.10.0) · 31.10.2026

- **Countdown-Piepen:** Der Countdown piept jede Sekunde, der letzte Piep ist höher. Drei Klänge zur Auswahl, gilt für Fotos, den Video-Countdown und Peace-Fotos (Tab „Ablauf“, Karte „Töne“, mit „Anhören“-Knopf und eigener Lautstärke).
- **Kamera-Sound:** Beim Foto klingt ein Auslöser, wahlweise Spiegelreflex, Kompaktkamera oder Retro mit Filmtransport. Eigener Schalter und eigene Lautstärke. Beide Töne sind ab Werk an und lassen sich getrennt ausschalten.
- **Kisscam besser sichtbar:** Die großen Countdown-Ziffern laufen jetzt die ganze Kisscam über, das Bild wird dabei nicht abgedunkelt, und der Text bleibt bis zum Foto stehen.
- **Druckbereich anzeigen:** Auf Wunsch zeigt die Gastansicht, welcher Bildausschnitt gedruckt wird, auch bei Streifen (Tab „Gastansicht“, ab Werk aus). Form, Farbe, Deckkraft und Linienstärke sind einstellbar. Am Druck selbst ändert sich nichts.
- **Admin-Menü scrollbar:** Die Liste links lässt sich scrollen, „Fun“ ist auch bei kleinem Fenster erreichbar.

#### [Fotobox+ 2.9.1](https://github.com/lorenzhdr/Fotobox/tree/v2.9.1) · 30.10.2026

- **Schnurrbärte per Esc entfernen:** Im Leerlauf lässt die Abbrechen-Taste (ab Werk Esc) alle sichtbaren Schnurrbärte verschwinden – wie der Mittelfinger, aber auch ohne Mittelfingererkennung. Im Countdown bricht Esc wie bisher nur den Countdown ab; der Bart bleibt.

#### [Fotobox+ 2.9.0](https://github.com/lorenzhdr/Fotobox/tree/v2.9.0) · 30.10.2026

- **Ladekreis bis zum Drucker-Timeout:** Ist der Fortschrittsbalken voll und das Foto noch nicht gedruckt, erscheint darunter ein goldener Ring, der sich bis zum eingestellten Drucker-Timeout füllt.
- **Druckersperre statt Fehlermeldung:** Hat der Drucker kein Papier mehr (oder einen Papierstau, ist nicht erreichbar oder meldet einen Fehler), zeigt die Fotobox einen eigenen Bildschirm, z. B. „Papier leer – Bitte hol jemanden, der es auffüllt. Dein Foto wird danach automatisch gedruckt.“ Meldet der Drucker das Problem selbst, kommt die Meldung nach 5 Sekunden; meldet er nichts, beim Ablauf des Drucker-Timeouts.
- **Foto kommt nach dem Beheben automatisch:** Der Druckauftrag bleibt in der Warteschlange und wird nicht mehr storniert. Sobald Papier nachgelegt ist, wird das Foto gedruckt und der „Fertig“-Bildschirm erscheint. Bis dahin sind neue Fotos und Videos gesperrt, damit sich keine weiteren Aufträge stauen.
- **Sperre über die Admin-Ansicht aufheben:** Während der Sperre lässt sich die Admin-Ansicht (Strg + Alt + A, mit PIN) öffnen. Nach dem Schließen ist die Sperre aufgehoben; der Auftrag bleibt in der Warteschlange und druckt später. Esc wirkt in der Sperre nicht.
- **Hinweis:** Die Druckersperre ist mit automatischen Tests geprüft, aber noch nicht mit einem echten Fotodrucker. Ob ein Drucker „Papier leer“ meldet, hängt vom Treiber ab; Drucker, die nie ein Druckende melden, gelten wie bisher nach dem Timeout als fertig.

#### [Fotobox+ 2.8.0](https://github.com/lorenzhdr/Fotobox/tree/v2.8.0) · 30.10.2026

- **Hardwarebeschleunigung der KI-Erkennung:** Zielerfassung, Peace-, Mittelfinger- und Herzerkennung laufen jetzt wahlweise auf der Grafikkarte (GPU) oder einer NPU statt nur auf dem Prozessor. Grundlage ist Windows ML mit DirectML; es funktioniert mit Grafikkarten von NVIDIA, AMD und Intel sowie mit Snapdragon-Grafik. Läuft ein Gerät nicht sauber, fällt die Erkennung automatisch auf den Prozessor zurück – nichts bleibt hängen.
- **Neuer Admin-Tab „Leistung“:** Auswahl „Automatisch“ (ab Werk), „Nur CPU“, „Grafikkarte“ (mit Liste der erkannten Karten) oder „NPU“. „Automatisch“ misst beim ersten Start im Hintergrund, welches Gerät schneller ist als der Prozessor, und merkt sich das Ergebnis; die Messung wiederholt sich nach Hardware- oder Treiberwechsel. Statuszeile, Messtabelle und der Knopf „Jetzt messen“ zeigen, was aktiv ist. Änderungen wirken nach einem Neustart der App. Die Einstellung gilt nur für das jeweilige Gerät.
- **Messung im eigenen Prozess:** Gerätemessungen laufen in einem getrennten Prozess. Stürzt ein Grafiktreiber dabei ab, bleibt die Fotobox unberührt und nutzt weiter den Prozessor. Die Windows-Softwaregrafik wird nie als Gerät verwendet.
- **Leistungsstärkste Grafikkarte für die ganze App:** Ein eigener Schalter („Windows-Grafikeinstellung“, ab Werk an) legt die Fotobox in den Windows-Grafikeinstellungen auf „Hohe Leistung“. So nutzt sie auf Laptops mit zwei Grafikeinheiten die starke Grafikkarte statt der internen. Der Schalter ändert nur den eigenen Eintrag und wirkt ab dem nächsten Start.
- **Zusätzliche Beschleuniger (optional):** Der Knopf „Beschleuniger installieren“ lädt über Windows ML herstellerspezifische Beschleuniger (z. B. für NVIDIA oder Snapdragon-NPUs) nach. Das braucht Internet und geschieht nie automatisch; danach ist ein Neustart nötig.
- **Hinweis zum Video-Stoppen:** Während einer Videoaufnahme zeigt die Gastansicht unten, wie sich die Aufnahme beenden lässt – ab Werk „Drücke die Eingabetaste, um die Videoaufnahme zu beenden“, sobald die Mindestlänge erreicht ist. Im Admin-Tab „Video“ unter „Anzeige während der Aufnahme“ lässt sich der Hinweis ausschalten und der Text ändern (Platzhalter `{Taste}`, `{Stopptaste_N}` und `{Stopptasten}`), mit Vorschau im gewählten Hinweisstil. Der Hinweis ist nicht im Video zu sehen.
- **Hinweis:** Die Beschleunigung ist mit automatischen Tests und auf einem Snapdragon-Laptop geprüft; auf NVIDIA-Grafikkarten und NPUs noch nicht. Bei Problemen genügt „Nur CPU“ im Tab „Leistung“.

#### [Fotobox+ 2.7.0](https://github.com/lorenzhdr/Fotobox/tree/v2.7.0) · 29.10.2026

- **Kisscam-Musik aus einem Ordner:** Statt einer einzelnen Datei gibt es jetzt den Musikordner `Musik\Fotobox+\Kisscam`. Er wird beim Start angelegt (ein vorhandener Ordner wird weiterverwendet) und lässt sich im Admin-Tab „Fun“ → „Herz“ ändern, öffnen und aktualisieren. Alle MP3-, WAV-, M4A- und WMA-Dateien im Ordner erscheinen in einer Liste; neu hineinkopierte Songs tauchen automatisch auf. Ist der Ordner leer, weist die Liste darauf hin.
- **Mehrere Songs, zufällige Wahl:** Songs werden in der Liste angehakt (neue Songs sind zunächst nicht ausgewählt). Bei jeder Kisscam – und bei „Kisscam testen“ – spielt zufällig einer der ausgewählten Songs. Ist keiner ausgewählt, läuft die Kisscam ohne Musik.
- **Startzeit und Lautstärke je Song:** Für jeden Song lässt sich festlegen, ab welcher Stelle er spielt (Standard 0:00), und wie laut er im Verhältnis zur Gesamtlautstärke ist. „Anspielen“ spielt den Song ab dieser Stelle so lange wie die Kisscam; mit „Position übernehmen“ wird die gerade gehörte Stelle zur Startzeit. Die Liste zeigt die Länge jedes Songs und warnt, wenn die Startzeit hinter dem Ende liegt oder eine Datei sich nicht abspielen lässt.
- **Einstellungen im Musikordner:** Auswahl, Startzeiten und Song-Lautstärken stehen in der Datei `kisscam-musik.json` im Musikordner und werden bei jeder Änderung sofort gespeichert. Wird der Ordner auf einen anderen PC kopiert, kommt die Auswahl mit. Die bisher gewählte Einzeldatei wird nicht übernommen – den Song einfach in den neuen Ordner kopieren und anhaken.
- **Zielerfassung rastet gemeinsam ein:** Kommt während der Einrast-Animation jemand dazu, startet die Animation für alle gleichzeitig neu (jede Person in ihrer eigenen Markierung), und erst danach erscheint die Meldung mit der Gesamtzahl. Wie oft neu gestartet wird, lässt sich im Tab „Zielerfassung“ unter „Maximale Neustarts beim Einrasten“ einstellen (ab Werk 3). Wer während der Meldung oder der Pause danach dazukommt, löst ebenfalls ein gemeinsames Einrasten aller aus.
- **Videos laufen ungestört durch:** Esc beendet eine laufende Videoaufnahme nicht mehr – das geht nur mit der Videotaste (Standard Eingabetaste) oder durch die Maximaldauer. Den Video-Countdown bricht Esc weiterhin ab. Während Video-Countdown und Aufnahme sind wie bisher keine Fun-Funktionen aktiv, und Peace- sowie Herzerkennung lösen nur aus, wenn die Fotobox bereit ist (nicht im Countdown und nicht zwischen den Bildern eines Streifenfotos).

#### [Fotobox+ 2.6.0](https://github.com/lorenzhdr/Fotobox/tree/v2.6.0) · 29.10.2026

- **Easter Egg „Herzerkennung“ mit Kisscam:** Formen zwei Gäste mit ihren Händen ein Herz – oder ein Gast mit beiden Händen – und halten es kurz (ab Werk 1,8 s), startet die Kisscam: Ein Jumbotron-Rahmen mit Lichterkette, Banner „KISS CAM“ und Herzen erscheint über dem Livebild, ein Text fordert dazu auf, sich jemanden zum Küssen zu suchen, die letzten drei Sekunden zählen groß herunter, dann entsteht ein Foto mit dem Rahmen. Es wird wie jedes Foto gespeichert und gedruckt. Esc oder ein gehaltener Mittelfinger brechen die Kisscam ohne Foto ab. Die Gastansicht verrät vorher nichts; nur ein kleines Fortschrittsherz zeigt beim Halten, wie lange es noch dauert.
- **Kisscam einstellen:** Im Admin-Tab „Fun“ → „Herz“ lassen sich erkannte Herzformen (klassisch, Dach, umgedreht, experimentell auch ein Fingerherz mit einer Hand), Haltedauer, Empfindlichkeit, Dauer (3–60 s), Text, Banner-Aufschrift, Farbschema, Dateipräfix, ein Zoom auf die Herz-Gäste und eigene Musik (WAV/MP3) einstellen. Eine Live-Vorschau zeigt die erkannten Hände, „Kisscam testen“ spielt den Ablauf ohne Foto durch. Die Musik gilt nur für das jeweilige Gerät und wird nicht mit „Einstellungen teilen“ weitergegeben.
- **Erkennung an echten Händen abgestimmt:** Eng aneinanderliegende Hände werden zuverlässiger beide erkannt, und die Herzprüfung berücksichtigt gekrümmte Zeigefinger, die sich oben mit den Fingerrücken berühren. Kurze Aussetzer der Erkennung brechen das Halten nicht mehr ab.
- **Diagnose im Protokoll:** Für Praxistests schreibt die Herzerkennung höchstens einmal pro Sekunde, warum ein Herz nicht erkannt wurde (Zeilen „Herz-Diagnose“). Das Verhalten ändert sich dadurch nicht.
- **Hinweis:** Die Herzerkennung ist mit einer Person und beiden Händen im Praxistest geprüft; ein Herz aus zwei Personen mit je einer Hand noch nicht.

#### [Fotobox+ 2.5.2](https://github.com/lorenzhdr/Fotobox/tree/v2.5.2) · 29.10.2026

- **Speichern an einem Ort:** Speicherordner, Tagesunterordner, Dateipräfix und Qualität der Videos stehen jetzt im Admin-Tab „Speicher“ – in einer eigenen Karte „Videos speichern“ direkt unter den Fotos. Die Karte ist ausgegraut, solange der Videomodus aus ist. Der Tab „Video“ enthält nur noch Aufnahme, Anzeige und Ton.
- **Mehr Überblick im Admin:** Der Inhaltsbereich der Einstellungen ist ab Werk auf 80 % verkleinert, damit mehr auf einmal zu sehen ist. Unten im Tab „Darstellung“ lässt sich die Größe zwischen 60 und 100 % einstellen; sie gilt nur für das jeweilige Gerät und wird nicht mit „Einstellungen teilen“ weitergegeben.

#### [Fotobox+ 2.5.1](https://github.com/lorenzhdr/Fotobox/tree/v2.5.1) · 29.10.2026

- **Analysen pro Sekunde auch beim Mittelfinger:** Auf der Admin-Seite „Mittelfinger“ lässt sich die Analyserate jetzt direkt einstellen. Es ist derselbe Wert wie bei der Peaceerkennung, beide Seiten bleiben synchron.
- **Erklärungen per ⓘ:** Neben jeder Einstellung „Analysen pro Sekunde“ (Zielerfassung, Peace, Mittelfinger) erklärt ein Info-Symbol, was der Wert bewirkt, was höhere und niedrigere Werte bedeuten und was sich empfiehlt.
- **Admin übersichtlicher:** „Drucken an/aus“ und die erwartete Druckdauer stehen jetzt im Tab „Drucker“, der Rest des Tabs ist bei ausgeschaltetem Druck ausgegraut. Der Tab „Sicherheit“ heißt „Sicherheit & Daten“ und gliedert PIN, Einstellungen teilen und Standardwerte in eigene Karten. Der Tab „Speicher“ ist eindeutig für Fotos beschriftet, einige Hinweistexte wurden korrigiert.
- **Sicherere PIN:** Ist die Einstellungsdatei beschädigt, wird die gerettete Admin-PIN sofort neu gespeichert – ein Neustart vor dem ersten Speichern führt nicht mehr zum Erststart ohne PIN. Eine nach einem Import per „PIN ändern“ gesetzte PIN hat jetzt Vorrang vor der importierten.
- **Peace löst nicht doppelt aus:** Bleibt die Hand nach einem Bart-Foto über Aufnahme und Vorschau oben, startet das Peace-Zeichen keinen weiteren Durchgang mehr – die Hand muss wie vorgesehen erst herunter.
- **Zielerfassung bei niedriger Analyserate:** Bei 1–3 Analysen pro Sekunde rasten Zielmarken wieder zuverlässig ein und reißen bei einem kurzen Aussetzer nicht mehr ab. Beim Standard (5) ändert sich nichts.
- **Kamera und Ton:** Wird im Admin ein geänderter Ton-/Mikrofonstand verworfen, startet die Kamera danach wieder mit dem gespeicherten Ton (Videos entstehen nicht mehr versehentlich ohne Ton). Schaltet man bei einem Regler die Automatik aus, wird der angezeigte Wert jetzt wirklich übernommen; nach „Alle zurücksetzen“ springen die übrigen Regler beim nächsten Verstellen nicht mehr auf ihre alten Werte.
- **Kleinigkeiten:** `{Klick}` funktioniert jetzt auch im Video-Hinweis, die Vorschau des Foto-Hinweises entspricht bei ausgeschaltetem Videomodus der Gastansicht, und die Lizenzhinweise nennen alle mitgelieferten Komponenten.
- **Stabile Version:** Enthält alles aus den Testversionen 2.3.0-beta bis 2.5.0-beta (unten).

#### [Fotobox+ 2.5.0-beta](https://github.com/lorenzhdr/Fotobox/tree/v2.5.0-beta) · 29.10.2026

- **Kamerasteuerung im Admin:** Im Tab „Kamera“ gibt es die neue Karte „Bildeinstellungen“ mit Schiebereglern für Zoom, Schwenken, Neigen, Helligkeit, Kontrast, Farbton, Belichtungszeit, Belichtungskorrektur, Gegenlichtausgleich, Fokus und Weißabgleich – jeweils mit „Automatisch“, wo die Kamera das anbietet. Die Regler wirken sofort auf das Livebild und damit gleich auf Fotos und Videos; gespeichert wird mit „Übernehmen“, „Schließen“ ohne Speichern stellt den alten Stand wieder her. Welche Regler verfügbar sind, bestimmt die Kamera bzw. ihr Treiber; was sie nicht kann, ist ausgegraut. Dazu Flimmerschutz (50/60 Hz, Standard 50 Hz) gegen Streifen unter LED- und Neonlicht, „Alle zurücksetzen“ und bis zu 10 benannte Profile je Kamera (z. B. „Tag“, „Abend“).
- **Normales Scrollen mit dem Trackpad im Admin:** Die Einstellungsseiten springen beim Scrollen mit einem Touchpad nicht mehr sofort ans Ende, sondern scrollen so fein wie in anderen Programmen.
- **Nur noch portable Version:** Es gibt keinen Installer mehr, sondern nur noch die ZIP-Dateien für x64 und ARM. Wer bisher die Setup-Version nutzt, siehe „Umstieg von der Setup-Version“ unten – Einstellungen, Fotos und Videos bleiben erhalten.
- **Beta:** Enthält auch alles aus 2.4.0-beta (unten). Die Kamerasteuerung ist mit automatischen Tests und einer eingebauten Laptop-Kamera geprüft, mit USB-Webcams noch nicht.

#### [Fotobox+ 2.2.1](https://github.com/lorenzhdr/Fotobox/tree/v2.2.1) · 28.10.2026

- **Zielerfassung auf jedem PC:** Die Pakete enthalten jetzt die Microsoft Visual C++ Runtime, die die KI-Erkennung benötigt. In 2.2.0 fehlte sie; auf PCs, auf denen sie nicht schon installiert war, lief die Zielerfassung dann nur mit der einfacheren Gesichtserkennung. Update empfohlen.
- **Weniger Rechenlast:** Die Personenerkennung braucht spürbar weniger Prozessorzeit und wird beim Start vorbereitet, damit sie auch auf langsameren PCs zuverlässig beim KI-Modell bleibt.
- **Stabiler im Dauerbetrieb:** Hängt der Kameratreiber, sammelt die Fotobox keine Kamera-Verbindungen mehr an; die Signaltöne werden sicherer abgespielt; Protokolle älter als 30 Tage werden automatisch gelöscht.

#### [Fotobox+ 2.4.0-beta](https://github.com/lorenzhdr/Fotobox/tree/v2.4.0-beta) · 29.10.2026

- **Easter Egg „Mittelfingererkennung“:** Zeigt ein Gast den Mittelfinger in die Kamera und hält ihn kurz, verschwinden alle Schnurrbärte mit einem kleinen „Plopp“, und ein laufender Selbstzerstörungs-Countdown bricht still ab – wie mit Esc. Andere Countdowns laufen weiter; ein Foto direkt danach entsteht ohne Bart. Die Erkennung wirkt zusammen mit der Peaceerkennung und ist ab Werk eingeschaltet. Im Admin-Tab „Fun“ → „Mittelfinger“ lassen sich Haltedauer und Empfindlichkeit einstellen, eine Live-Vorschau mit „Aufheben testen“ zeigt die Wirkung. Die Gastansicht verrät nichts.
- **Einstellungen teilen:** Im Admin-Tab *Sicherheit* lassen sich alle Einstellungen als Datei exportieren und auf einem anderen PC importieren. Kamera, Drucker, Papier, Speicherorte, Mikrofon und Bildschirm bleiben dabei immer die des Ziel-PCs; die Admin-PIN wird nur auf Wunsch mitexportiert und beim Import erst nach Rückfrage übernommen. Der Import gilt erst nach „Übernehmen“ bzw. „Speichern & schließen“. Stammt die Datei aus einer anderen Version, werden nur die Einstellungen übernommen, die es in dieser Version gibt; fehlende bekommen den Standardwert. Das Overlay-Bild ist nicht in der Datei enthalten – fehlt es am Ziel, weist die App darauf hin.
- **Weniger Rechenlast im Admin:** Die Kameravorschauen im Tab „Fun“ arbeiten nur noch, solange sie zu sehen sind.
- **Beta:** Die neuen Funktionen sind mit automatischen Tests geprüft, aber noch nicht im Praxistest.

#### [Fotobox+ 2.3.0-beta](https://github.com/lorenzhdr/Fotobox/tree/v2.3.0-beta) · 28.10.2026

- **Easter Egg „Peaceerkennung“:** Zeigt ein Gast im Livebild ein Peace-Zeichen ✌ und hält es kurz, passiert eine Überraschung: ein roter Selbstzerstörungs-Countdown, bei dem das Foto genau im Moment der Explosion entsteht, ein Foto mit eigenem Countdown oder ein Schnurrbart für alle Gesichter bzw. nur für die zeigende Person, live im Bild und im Foto. Die Gastansicht verrät vorher nichts. Ab Werk ist die Selbstzerstörung aktiv; Foto und Schnurrbart lassen sich im Admin-Tab „Fun“ → „Peace“ dazuschalten, dort gibt es auch eine Live-Vorschau mit Testknöpfen. Jede ausgelöste Aktion macht ein Foto und druckt es wie gewohnt: Wer Papier sparen will, verlängert die Haltedauer, setzt eine Sperrzeit oder schaltet die Erkennung ab.
- **Realistische Schnurrbärte:** sieben Formen (Zwirbel, Walross, Chevron, Pinsel, Dalí, Fu Manchu, Hufeisen) mit echt wirkendem Haar und Schatten, in sechs zufälligen Haarfarben. Im Admin lässt sich festlegen, welche Formen beim Zufall vorkommen. Der Bart erscheint mit einem kleinen „Plopp“, wippt beim Sprechen mit, folgt dem Gesicht flüssig und passt seine Größe auch beim Näherkommen richtig an. Im Foto trägt jede Person denselben Bart wie im Livebild.
- **Zielerfassung verbessert:** Dauer (0,5–5 s) und Intensität der Einrast-Animation sind einstellbar, die Intensität regelt Bewegung, Leuchten, Blinken und Lautstärke. Kommt jemand dazu, während der Text läuft, folgt direkt danach eine eigene Meldung. Ein Personentausch löst ebenfalls aus (abschaltbar), und auf Wunsch wiederholt sich die Meldung bei gleicher Gruppe nach einstellbarer Zeit. Kurze Aussetzer der Erkennung lassen den Text stehen, statt ihn abzubrechen.
- **Livebild mittig:** Haben Kamera und Bildschirm unterschiedliche Seitenverhältnisse, wird das Livebild jetzt gleichmäßig an allen Seiten beschnitten statt nur rechts bzw. unten. Zielmarken und Bärte sitzen dadurch genau.
- **Beta:** Die neuen Funktionen sind auf dem Entwicklungsrechner und im Praxistest geprüft, aber noch nicht bei echten Veranstaltungen. Der Download ist durch die zusätzlichen KI-Modelle für Gesichter und Hände etwa 16 MB größer (Lizenzhinweise in `THIRD-PARTY-NOTICES.txt`).

#### [Fotobox+ 2.2.0](https://github.com/lorenzhdr/Fotobox/tree/v2.2.0) · 28.10.2026

> **Hinweis:** In den Paketen von 2.2.0 fehlte die Microsoft Visual C++ Runtime, die die KI-Erkennung (ONNX Runtime) benötigt. Auf PCs, auf denen sie nicht bereits installiert war, lief die Zielerfassung deshalb nur mit der Gesichtserkennung. Behoben in 2.2.1 – bitte 2.2.1 verwenden.

- **Fun-Modus „Zielerfassung“:** Die Fotobox erkennt Personen im Livebild und markiert jede wie im Film mit einer Zielbox „TARGET 1“, „TARGET 2“ … Neue Ziele rasten animiert ein, danach erscheint „Hey du, nimm doch ein Foto oder Video mit mir auf!“ bzw. bei mehreren Personen „Hey ihr, nehmt doch …“. Das passiert nur in der Gastansicht, solange niemand fotografiert oder filmt, und landet nie im Foto oder Video. Standardmäßig eingeschaltet.
- **Neuer Admin-Tab „Fun“** (ganz unten): ein- und ausschalten, Erkennung per KI-Modell (ganzer Körper, auch seitlich oder von hinten) oder sparsamer per Windows-Gesichtserkennung, Box-Umfang (Kopf, Oberkörper, ganze Person), drei Box-Stile, fünf Farben, drei Einrast-Animationen, eigene Texte mit Platzhalter `{Anzahl}`, Position und Stil des Texts (Terminal-Look mit Tipp-Effekt oder einer der Hinweis-Stile), Anzeigedauer, Pause, Empfindlichkeit und optionale Soundeffekte. Eine Live-Vorschau mit Kamerabild und der Knopf „Animation testen“ zeigen die Wirkung sofort.
- **Alles lokal:** Die Erkennung läuft komplett auf dem PC, es werden keine Bilder gespeichert oder verschickt. Der Download ist dadurch etwa 21 MB größer (KI-Modell YOLOX-Nano und ONNX Runtime, Lizenzhinweise in `THIRD-PARTY-NOTICES.txt`).

#### [Fotobox+ 2.1.0](https://github.com/lorenzhdr/Fotobox/tree/v2.1.0) · 28.10.2026

- **Admin: „Übernehmen“.** Die Fußleiste hat jetzt „Schließen“, „Übernehmen“ und „Speichern & schließen“. Mit „Übernehmen“ werden Einstellungen gespeichert, ohne die Admin-Ansicht zu verlassen. Beim Schließen mit ungespeicherten Änderungen fragt die App nach.
- **Mausradzeile einstellbar:** im Admin-Tab *Gastansicht* ein- und ausschaltbar, mit eigenem Text und fünf Darstellungen (passend zum Foto-Hinweis, kleine Pille, schlichter Text, Chip, Pfeile).
- **Sechs neue Hinweis-Stile** für Foto- und Video-Hinweis (jetzt neun): Neon, Sprechblase, Laufschrift, Glas, Zettel und Kino. Die Auswahl im Admin zeigt kleine Skizzen.
#### [Fotobox+ 2.0.0-beta](https://github.com/lorenzhdr/Fotobox/tree/v2.0.0-beta) · 28.10.2026

- **Videomodus:** eigene Start-/Stopptaste (Standard Eingabetaste, dieselbe Taste startet und beendet), Countdown vorab, Clips bis 5 Minuten (einstellbar) mit Ton über ein wählbares Mikrofon, gespeichert als MP4 im eigenen Ordner `Videos\Fotobox+`. Während der Aufnahme zeigt die Gastansicht Aufnahmepunkt, Laufzeit, Restzeit und kurz vor Schluss eine Warnung; danach erscheint „Video gespeichert! 🎬“. Alles einstellbar im neuen Admin-Tab **Video**.
- **Video-Hinweiszeile oben** in der Gastansicht, mit eigenem Text und Stil wie die Fotozeile (Admin → Gastansicht).
- **Neuer Name:** Die App heißt jetzt **Fotobox+** (Programmdatei, Einstellungen und Logs behalten ihre Namen, ein Update übernimmt alle Einstellungen).
- **Fotos** liegen standardmäßig in `Bilder\Fotobox+`. Wer keinen eigenen Fotoordner eingestellt hatte, findet ältere Fotos weiterhin in `Bilder\Fotobox`.
- **Behoben:** In der Admin-Ansicht wechselte nach dem Tab „Gastansicht“ der Inhalt beim Tab-Wechsel teilweise nicht mehr mit.

#### [Fotobox 1.4.0](https://github.com/lorenzhdr/Fotobox/tree/v1.4.0) · 27.10.2026

- **Monitor für die Gastansicht wählen:** In **Admin → Darstellung** legst du unter „Monitor der Gastansicht“ fest, auf welchem Bildschirm die Fotobox im Vollbild läuft (z. B. ein Fernseher oder zweiter Monitor). Die Liste zeigt Nummer, Name und Auflösung, der Hauptbildschirm ist markiert. Ist nur ein Bildschirm angeschlossen, ist die Auswahl ausgegraut und zeigt dessen Namen. Die Wahl gilt nach „Speichern“, die Einstellungen und die PIN-Abfrage öffnen auf demselben Bildschirm.
- **Automatischer Wechsel beim Ein- und Ausstecken:** Ist der gewählte Bildschirm nicht angeschlossen, läuft die Gastansicht auf dem Hauptbildschirm weiter und die Wahl bleibt gespeichert. Sobald der Bildschirm wieder da ist, wechselt die Gastansicht automatisch zurück.

#### [Fotobox 1.3.0](https://github.com/lorenzhdr/Fotobox/tree/v1.3.0) · 27.10.2026

- **Dunkles Farbschema für den Admin:** Im neuen Admin-Tab **Admin → Darstellung** wählst du zwischen **Wie Windows** (Standard, folgt dem hellen bzw. dunklen App-Modus von Windows), **Hell** und **Dunkel**. Die Wahl wirkt sofort als Vorschau und gilt nach „Speichern“ auch für die PIN-Abfrage. Die Gastansicht bleibt unverändert.
- **Standard-Layout per Stern:** In **Admin → Layout** markiert in der Liste „Reihenfolge beim Scrollen“ ein **★** das Standard-Layout, mit dem die Gastansicht startet und zu dem sie nach jedem Foto zurückkehrt. Mit **☆** machst du direkt einen anderen Eintrag zum Standard, die Reihenfolge bleibt dabei erhalten.

#### [Fotobox 1.2.0](https://github.com/lorenzhdr/Fotobox/tree/v1.2.0) · 26.10.2026

- **Eigener Hinweistext:** Im neuen Admin-Tab **Admin → Gastansicht** lässt sich der Hinweis in der Gastansicht frei formulieren (bis 100 Zeichen). Mit den Platzhaltern `{Fototaste_1}`, `{Fototaste_2}` …, `{Fototasten}` und `{Klick}` setzt die Fotobox die tatsächlich eingestellten Auslöser ein. Bleibt das Feld leer, erscheint wie bisher der automatische Text. Eine Live-Vorschau zeigt das Ergebnis sofort.
- **Drei Darstellungsstile:** Unter **Admin → Gastansicht** wählst du für den Hinweis zwischen **Pille**, **Schlicht** (weißer Text mit Schatten) und **Banner** (Band über die volle Breite).
- **Druck abbrechen:** Die Taste **Esc** (im Admin unter **Tasten → Abbrechen**) bricht nun auch einen laufenden Druck ab. Der Druckauftrag wird gestoppt, die Gastansicht meldet kurz „Druck abgebrochen“, und das Foto bleibt trotzdem gespeichert. Lässt sich der Auftrag nicht mehr stoppen, erscheint ein entsprechender Hinweis.

#### [Fotobox 1.1.3](https://github.com/lorenzhdr/Fotobox/tree/v1.1.3) · 24.10.2026

- **Mausrad-Richtung umkehrbar:** Im Admin unter **Layout → Reihenfolge beim Scrollen** kehrt „Mausrad-Richtung umkehren“ die Scrollrichtung bei der Layoutwahl der Gäste um. Ohne Haken wählt Scrollen nach oben wie bisher das vorherige Layout, mit Haken das nächste.

#### [Fotobox 1.1.2](https://github.com/lorenzhdr/Fotobox/tree/v1.1.2) · 24.10.2026

Reines Stabilitäts-Update ohne neue Funktionen:

- **Einstellungen sicherer:** Unbekannte Werte in der Einstellungsdatei (z. B. nach dem Zurückwechseln von einer neueren Version) setzen nicht mehr alles zurück, und die Admin-PIN bleibt auch bei einer beschädigten Datei erhalten.
- **Kamera stabiler:** Schnelles Wechseln, Starten und Stoppen der Kamera kommt nicht mehr durcheinander. Hängende Kameratreiber und Kameras ohne Bild werden zuverlässiger erkannt. Das Livebild läuft flüssiger.
- **Druck und Speichern:** Fristen beim Drucken greifen zuverlässig, ein abgebrochenes Speichern legt keine Datei mehr nachträglich an, und hängende Druckertreiber blockieren weder Gastansicht noch Admin.
- **Admin:** Kein Einfrieren beim Speichern mit nicht erreichbarem Netzwerkordner, richtige Papier- und Auflösungslisten auch nach schnellem Wechsel. Nach dem Speichern läuft die richtige Kamera.
- **Sonstiges:** Ein Fehler beim Start beendet die Fotobox mit einer Meldung, statt sie unsichtbar weiterlaufen zu lassen. Dazu zahlreiche kleinere Fehlerbehebungen.

#### [Fotobox 1.1.1](https://github.com/lorenzhdr/Fotobox/tree/v1.1.1) · 24.10.2026

- **Standardwerte wiederherstellen:** Im Admin unter **Sicherheit** setzt „Standardwerte wiederherstellen“ alle Einstellungen außer der PIN auf die Standardwerte zurück. Die Änderung gilt erst nach **Speichern**. Fotos und Overlay-Dateien bleiben erhalten.
- **Reihenfolge der Gästeauswahl:** Im Admin unter **Layout** legst du mit ▲/▼ fest, in welcher Reihenfolge Gäste per Mausrad durch die Layouts scrollen.

#### [Fotobox 1.1.0](https://github.com/lorenzhdr/Fotobox/tree/v1.1.0) · 24.10.2026

- **Maus als Auslöser:** Linke und rechte Maustaste lösen ein Foto aus. Im Admin unter **Tasten** lässt sich beides einzeln abschalten.
- **Layoutwahl per Mausrad:** Im Admin unter **Layout** legst du das Standard-Layout fest und wählst, welche Layouts Gäste durchscrollen dürfen (Vollbild, 2 Streifen, 1 Streifen, Overlay). In der Gastansicht zeigt eine Liste alle Optionen, die gewählte ist hervorgehoben. Nach jedem Foto gilt wieder das Standard-Layout.
- **Druckdauer:** Die erwartete Druckdauer (Fortschrittsbalken) ist standardmäßig 240 s, das Druck-Timeout 300 s. Bestehende Einstellungen bleiben unverändert.

#### [Fotobox 1.0.0](https://github.com/lorenzhdr/Fotobox/tree/v1.0.0) · 23.10.2026

*Layout und Druck*
- Fotostreifen in zwei Varianten: **2 Streifen nebeneinander** (zum Teilen) oder **1 Streifen** mit den ganzen Fotos untereinander auf der Seite.
- Die Bildunterschrift wird nicht mehr abgeschnitten: Sie bricht auf bis zu zwei Zeilen um und hält einen Sicherheitsabstand zum Papierrand.
- Schriftart der Bildunterschrift frei wählbar (alle installierten Schriften, mit Vorschau).
- Während des Drucks zeigt die Fotobox einen Hinweis mit **Fortschrittsbalken**. Die erwartete Druckdauer lässt sich im Admin einstellen (Standard 2 Minuten).
- Nach dem Druck erscheint 5 Sekunden lang „Fertig! Bitte nimm dein Foto aus dem Drucker.“
- Die Seitengröße des Druckers wird zwischengespeichert und vorab ermittelt, ein nicht erreichbarer Drucker verzögert den Ablauf dadurch kaum noch.

*Bedienung*
- Neuer Admin-Bereich **Tasten**: Für „Foto auslösen“, „Admin öffnen“ und „Countdown abbrechen“ lassen sich beliebig viele Tasten festlegen (auch USB-Buzzer oder Presenter).
- Ein laufender Countdown lässt sich abbrechen (Standard: Esc).
- Die Admin-Vorschau zeigt das ganze Kamerabild.

*Stabilität*
- Nach dem Speichern im Admin wird die Kamera nur noch neu gestartet, wenn sich an ihr etwas geändert hat.
- Beim Kamerawechsel erscheint keine kurze Fehlermeldung mehr.
- Liefert die Kamera kein Bild mehr, zeigt die Fotobox eine Meldung und verbindet sich automatisch neu.
- Ein hängender Kameratreiber blockiert den Start nicht mehr.

#### [Fotobox 0.0.1](https://github.com/lorenzhdr/Fotobox/tree/v0.0.1) · 23.10.2026

Erste Veröffentlichung.

#### [Fotobox 0.0.0-beta](https://github.com/lorenzhdr/Fotobox/tree/v0.0.0-beta) · 23.10.2026

Vorabversion zum Testen (Pre-release).

</details>
