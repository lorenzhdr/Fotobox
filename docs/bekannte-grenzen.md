# Bekannte Grenzen, offene Punkte und bewusst nicht Geändertes

Stand: 2.5.1. Nichts davon blockiert den Betrieb. Die Einträge sind thematisch gruppiert; jeder nennt kurz, warum er so geblieben ist. „Ungetestet“ heißt: nur per Unit-Test, Fakes oder CI abgedeckt, nicht auf der echten Hardware bzw. unter echten Veranstaltungsbedingungen. Rückmeldungen von echten Geräten sind willkommen.

## Druck

- **Druck auf echten Fotodruckern ungetestet.** Randloser Druck, Papiergrößen, Druckende-Erkennung (IPP/WSD) und die 5-mm-Sicherheitszone gegen echten Overscan sind nur per Unit-Tests und gegen Mock-Zustände abgedeckt. Es gab bewusst keinen Testdruck; der Druck muss herstellerneutral mit beliebigen Druckern funktionieren.
- **Druckabbruch (Spooler-Stornierung) ungeprüft.** Rechte bei Netzwerk-/IPP-/WSD-Druckern sind ungeprüft, und ein bereits beim Drucker liegender Auftrag wird evtl. physisch nicht gestoppt.
- **Erste Seitengrößen-Abfrage bei nicht erreichbarem Drucker.** Sie kostet bis zu 5 s, danach gilt der letzte gute Wert bzw. `PageSpec.Default` (10 × 15 cm); Ergebnisse werden gecacht und vorab im Hintergrund geholt.
- **Druckende bei IPP/WSD-Treibern.** Ein vollständig gesendeter Auftrag ohne gemeldetes Druckende gilt nach dem Druck-Timeout als fertig; es gibt keine bessere Quelle.
- **Hängender Spooler (Thread).** Jeder Druck nach einem hängenden Spooler/Treiber hinterlässt einen dauerhaft blockierten STA-Thread; bewusst nicht geändert, das würde das Verhalten ändern.
- **Hängender Spooler (Seitengröße, neu in der 2.5.1-Prüfung).** Solange eine Seitengrößen-Abfrage hängt, wartet jeder Durchgang erneut bis zu 5 s, weil die geteilte Abfrage (`BoothController.GetPageSpecQuery`) keine Altersprüfung hat; danach greift der Fallback. Bewusst nicht geändert: eine Altersgrenze würde das Verhalten bei langsamen, aber funktionierenden Spoolern ändern.
- **`PrintService.GetPageSpec` ohne eigenes Timeout.** Durch die geteilte Abfrage im Controller blockiert höchstens ein Thread je Drucker/Layout; eine asynchrone Variante mit Timeout fehlt noch.
- **Seitengrößen-Abfrage im Admin.** `AdminViewModel.GetPageSpecAsync` umgeht die geteilte Abfrage des Controllers; bei hängendem Spooler bleibt je Admin-Aktion ein Thread hängen.
- **Druckbild bei abweichendem Seitenverhältnis.** Weicht das vom Treiber gewählte Papier vom berechneten Layout ab (z. B. 5-s-Timeout ohne letzten guten Wert → `PageSpec.Default`, oder der Treiber ersetzt eine eigene Papiergröße), wird beschnitten statt eingepasst; im Extremfall rückt die Bildunterschrift in den Overscan. Bisher nur Warnung im Log (`PrintService.WarnOnAspectMismatch`). Option: ab > 1 % Abweichung einpassen (weiße Ränder möglich) – erst mit echtem Fotodrucker entscheiden.
- **Overlay wird gestreckt.** `LayoutComposer` zieht das Overlay-PNG ohne Rücksicht auf sein Seitenverhältnis auf die ganze Seite; bei Ausrichtung „Auto“ ist die Overlay-Seite quer, ein hochformatiges PNG wird verzerrt.
- **PORTPROMPT bei nicht lesbarem Port.** Lässt sich `QueuePort` nicht lesen, wird der Drucker angenommen (herstellerneutral, Netzwerk-Queues); ein Datei-Dialog-Drucker würde dann bis zum Druck-Timeout hängen.
- **Verschwundener Druckauftrag = fertig.** `PrintJobStateEvaluator` wertet einen aus der Warteschlange verschwundenen Job als erledigt, auch wenn ihn jemand gelöscht hat.
- **Render-Performance der Gastansicht.** Große `DropShadowEffect`s (Countdown, Hinweis-Pille, Vorschau-Rahmen) über dem 30-fps-Livebild wurden nicht optimiert (`CacheMode="BitmapCache"` ungemessen, ebenso Stift-Caching in `TargetVisual` und der 35-ms-Takt in `FunText`; der Countdown könnte beim Skalieren unscharf werden). `LayoutComposer.Compose` erzeugt mehrere Vollseiten-Puffer (Pbgra32→Bgra32), nur entbehrlich, wenn jedes Layout die Seite vollständig deckend malt.

## Speichern von Fotos

- **Speichern auf hängender Netzfreigabe.** Der Ausweichordner greift erst nach einer Exception, nicht wenn der erste Versuch hängt (dann nach 30 s Fehler, kein Druck). Ein einzelnes `WriteJpeg` und die `File.Move`-Phase unter `NamingGate` sind nicht abbrechbar. Liegengebliebene `*.tmp` (Fotoordner, `settings.json.*.tmp`) werden nie aufgeräumt.

## Kamera

- **Reconnect-Pfad „Kamera ohne Bild“.** Nur per Unit-Test geprüft, nicht mit echter Hardware.
- **Virtuelle Kamera mit Platzhalterbild.** Eine virtuelle Kamera ohne verbundene Quelle liefert einfarbige Frames; der Watchdog erkennt nur ausbleibende Frames, daher steht „Kamera läuft“. Ohne gespeicherte Kamera wird das erste Gerät der Liste gewählt (kann eine virtuelle Kamera sein). Abhilfe: Kamera im Admin explizit wählen.
- **Kamera-Abbau nach `TeardownTimeout`.** Überschreitet `reader.StopAsync` beim Abbau die Frist, wird trotzdem sofort freigegeben; Aufschieben könnte die Kamera dauerhaft blockieren.
- **Regler nur mit eingebauter Kamera am Entwicklungsrechner geprüft.** Dort nur Zoom und Belichtungskorrektur über die erweiterten Regler plus Flimmerschutz, live und beim Neustart. Klassische `MediaDeviceControl`-Regler (USB-Webcams) sowie Belichtungszeit, Weißabgleich und Fokus über die erweiterten Regler nur gegen Fakes getestet.
- **Treiber unterscheiden sich** in Bereichen und Einheiten der Regler; Klemmen und Einrasten nutzen die gemeldeten Werte, die Belichtungszeit wird als Prozent angezeigt.
- **Hängender Regleraufruf.** Erhöht ggf. `_hangingOperations` (Timeout in `AwaitStepAsync`); die automatische Wiederverbindung verzögert sich dann bis zu 5 min, damit nicht je Versuch ein weiteres hängendes `MediaCapture` liegen bleibt.
- **Flimmerschutz 50 Hz überschreibt bei jedem Start** die Treiber-Automatik (Standard; „Nicht verändern“ ist wählbar). Er ist die bewusste Ausnahme von „nur Verstelltes setzen“.
- **Unverstellte Regler bleiben, wie Treiber oder andere Tools sie gesetzt haben** (gewollt, „nur Verstelltes setzen“).
- **„Standardwerte wiederherstellen“ mit gleichzeitigem Kamera-Neustart (neu in der 2.5.1-Prüfung).** Auf demselben Gerät kann es zeitabhängig die alten Live-Reglerwerte statt der Standardwerte anzeigen/anwenden. Bewusst nicht geändert: Die Abläufe von Zurücksetzen und Neustart sind bewusst entkoppelt.
- **Direkt nach dem Schließen des Admins mit Kameraänderung (neu in der 2.5.1-Prüfung).** Während der Monitorabfrage (bis 3 s) kann ein Durchgang ausgelöst werden, der dann mit einem Kamerafehler endet; die Kamera-Entscheidung läuft bewusst erst nach dem Schließen (`AdminLauncher.DecideCameraHandling`, genau ein Weg gegen doppelten Neustart).

## Video

- **Video im Modus `SharedReadOnly`.** Dort ist keine Aufnahme möglich (der Start scheitert mit Fehlermeldung); nicht mit einer von einer anderen App belegten Kamera auf echter Hardware geprüft.
- **Getrennte Preview-/Record-Pins.** Kameras mit getrennten Vorschau- und Aufnahme-Pins sind ungetestet (die Hardware-Tests liefen mit einer einzelnen Webcam).
- **`Faulted` nur simuliert.** Die Teilvideo-Rettung bei Gerätefehler wurde nur über einen Kamera-Stopp getestet, nicht über echtes Abziehen der Kamera.
- **`Cancel()` beim App-Ende.** Bei laufender Aufnahme blockiert es bis zu 12 s (`ShutdownVideoWait`) auf dem aufrufenden Thread, um das Video zu speichern.
- **Liegengebliebene `.{guid}.tmp.mp4`.** Nach Absturz/Stromausfall während der Aufnahme oder wenn `Discard` scheitert (Media Foundation hält die Datei noch) werden sie nie aufgeräumt.
- **Stoppen länger als 15 s (neu in der 2.5.1-Prüfung).** Hängt das Stoppen einer Videoaufnahme länger, versucht die App das Teilvideo zu übernehmen (kann unvollständig oder nicht abspielbar sein), statt es zu verwerfen – ein evtl. unbrauchbares Video wurde gegenüber Datenverlust bevorzugt.
- **Spät fehlschlagender Aufnahmestart (neu in der 2.5.1-Prüfung).** Schlägt der Start erst nach dem 10-s-Timeout fehl, wird das nur als Warnung geloggt; der Durchgang ist dann bereits weiter.

## Fun: Zielerfassung und Peace/Mittelfinger

- **Lizenz der Modell-Gewichte (`yolox_nano.onnx`).** Stammt aus YOLOX (Repo Apache-2.0); die Gewichte selbst werden dort nicht ausdrücklich lizenziert. Dokumentiert in `THIRD-PARTY-NOTICES.txt`; veröffentlicht trotzdem, bei Rückfragen des Rechteinhabers zu klären.
- **Lizenzlage der Peace-Modelle.** MediaPipe-Modelle (via PINTO) laut Model Card Apache-2.0, YuNet MIT; in `THIRD-PARTY-NOTICES.txt` aufgeführt.
- **Zielerfassung bei echten Veranstaltungen.** Auf einem Laptop geprüft; Verhalten mit vielen Gästen, schwacher Beleuchtung und Gegenlicht ist ungetestet.
- **Verdeckte Personen.** YOLOX-Nano erkennt teilweise verdeckte Personen schwach.
- **Fun-Text unten und Foto-Hinweis.** Beim Ausblenden des Fun-Texts überlappt der Foto-Hinweis kurz (ca. 260 ms).
- **Fun-Text in der Mitte** kann die Layout-Vorschauliste (Mausrad) verdecken.
- **Admin-Vorschau Fun.** Der Kein-Kamera-Hinweis erscheint auch während „Kamera startet“.
- **CPU-Last auf schwachen Geräten ungemessen.** Benchmark (ORT, 2 Threads, 416er Eingabe) ca. 45–55 ms je YOLOX-Analyse auf dem ARM64-Entwicklungsrechner; gemessen YuNet ca. 22 ms, Palm ca. 25 ms, Landmark ca. 16 ms je Lauf. Die Last auf schwachen x64-Geräten und beider Fun-Funktionen gleichzeitig ist ungemessen.
- **Wanduhr statt monotoner Zeit.** `FunEngine`, Druckfortschritt, `TargetTracker` und die Peace-Tracker messen mit `GetUtcNow`; ein Rücksprung der Systemuhr kann die Zielerfassung bis zur Sprungweite anhalten.
- **`SwapTriggers` und Fehlzuordnungen.** Eine Fehlzuordnung des `TargetTracker` (schnelle Bewegung) zählt als neue Spur und kann eine Fehlauslösung der Zielerfassung verursachen.
- **Peaceerkennung nur mit Beispielbildern/Entwicklungsrechner geprüft.** Nicht auf Veranstaltungen (Abstand 1–3 m, Licht, Gruppen, Gegenlicht).
- **Fehlauslösungen kosten Papier.** Gegenmittel: Haltedauer, „Hand muss erst runter“, Sperrzeit und Empfindlichkeit; jede Auslösung steht im Log.
- **Schwellen der Peace- und der Mittelfinger-Regel nur mit synthetischen Händen getestet** (`PeaceHandFixtures`), nicht mit echten Händen aus verschiedenen Winkeln.
- **Anker der Bartgeometrie nur auf synthetische Proportionen kalibriert.** Die Faktoren 1,16 (Breite) und 0,325 (Höhe, je Augenabstand) sind auf die Gesichter der Tests abgestimmt; am echten Gerät mit „Gesichtspunkte anzeigen“ prüfen.
- **Alarm-Ansicht bleibt nach der Explosion bis 1,2 s sichtbar.** Kosmetisch; endet die Aufnahme schneller, endet sie mit dem Zustandswechsel.
- **Textur-Erzeugung kalt bis ca. 2–3 s.** Walross in Stufe 2048 (Debug); bei 4K-Kameras nutzt das Foto dann die kleinere fertige Stufe (leicht unschärfer), ohne jede fertige Stufe bleibt es unverziert. Ohne Prewarm fehlt der Bart im Livebild bis zur ersten fertigen Textur (ca. 0,1–0,3 s).
- **Wippen nur über die Mundwinkel.** YuNet liefert keine Lippenpunkte.
- **Große Bärte (über ca. 900 px)** wirken leicht borstig.
- **Handstufe im Selbstzerstörungs-/Bart-Countdown.** Mit Mittelfinger (Standard an) läuft dort die Handerkennung (sonst nur Gesichter); CPU-Last auf schwachen Geräten ungemessen.
- **Mittelfinger nur mit Peace oder Herz wirksam.** Sind beide Erkennungen ausgeschaltet, passiert nichts (der Admin warnt nur in diesem Fall).
- **Überzählige Palm-Läufe (neu in der 2.5.1-Prüfung).** Belegen verfolgte Hände schon alle Landmark-Plätze, macht die Handerkennung trotzdem Palm-Läufe, deren Ergebnis nicht mehr verwendet wird – eine kleine CPU-Verschwendung ohne Funktionsfolge; bewusst nicht geändert. Mit `PairedHands` kann das auch eintreten, wenn gescheiterte verfolgte Hände alle vier Läufe verbraucht haben und die vorgezogene Palm-Suche trotzdem läuft.

## Fun: Herzerkennung und Kisscam

- **Schwellen an einer Person mit zwei Händen kalibriert.** Die Herzregeln (`HeartGestureClassifier`) sind mit synthetischen Händen (`HeartHandFixtures`) und mit Aufnahmen einer Person abgestimmt, die mit beiden Händen ein Herz formt (gekrümmte Zeigefinger, Kontakt über die Fingerrücken, Neigung bis etwa 50°). Das Zwei-Personen-Herz (je eine Hand zweier Gäste) ist ungeprüft, ebenso andere Winkel, Abstände und Lichtverhältnisse; Fehl- und Nichterkennungen auf echten Veranstaltungen sind ungeprüft. Bei strenger Empfindlichkeit (10–30 %) werden manche echten Herzen noch nicht erkannt. Gegenmittel: Haltedauer, Empfindlichkeit, einzelne Formen abwählen.
- **Fingerherz experimentell.** Es wird öfter fälschlich erkannt (daher standardmäßig aus); der Admin weist darauf hin.
- **Klassisch = „nicht Dach“.** Die Form „Klassisch“ prüft die übrigen Finger nicht (der Admin-Text nennt sie „eingerollt oder locker gekrümmt“); sie gilt für jede aufrechte Herzgeste, die kein Dach ist.
- **Halte- und Wiederscharf-Fristen hängen an der eingestellten Analyserate.** Aussetzer-Toleranz und Ruhefrist der `HeartEngine` richten sich nach `PeaceSettings.AnalysesPerSecond` (Toleranz mindestens 700 ms bzw. 4 Analyseabstände, Ruhefrist Toleranz + 1 Analyseabstand; siehe `docs/fun-herz-kisscam.md`). Liefert ein schwacher Rechner deutlich weniger Analysen als eingestellt, wird das Herz zwischen zwei Analysen zu lange nicht „gesehen“ und kann nie lange genug gehalten werden (wie bei Peace). Dann „Analysen pro Sekunde“ senken, bis die Statuszeile der Vorschau die Rate tatsächlich erreicht.
- **Timer bis 3 Sekunden zeigt keinen Gasttext.** Der Gasttext erscheint nur, solange mehr als `SettingsLimits.KisscamFinalDigitsSeconds` (3 s) übrig sind; bei einem Timer von höchstens 3 s sieht der Gast nur den Countdown 3 – 2 – 1 (der Admin weist unter dem Timer darauf hin).
- **Höchstens vier Hände je Analyse.** `MaxLandmarkRuns` = 4 (und `MaxPalmRuns` = 3 je Zyklus) begrenzen die Handstufe; bei großen Gruppen kann ein Herz einen Zyklus später erkannt werden (der Klassifikator selbst prüft bis zu 8 Hände). Mit eingeschalteter Herzerkennung läuft die Handstufe öfter länger (zweite Hand wird gesucht, Duplikatprüfung rechnet), die CPU-Last auf schwachen Geräten ist ungemessen.
- **Kein Personenbezug.** Zwei beliebige Hände genügen; bei dichtem Gedränge kann ein Herz aus Händen verschiedener Gäste „zufällig“ entstehen (der Zoom nimmt dann deren Gesichter).
- **Zoom kostet Auflösung.** Digital: Das Foto wird zugeschnitten (Zoom 110–200 %), im Druck sinkt die Schärfe entsprechend. Live-Ausschnitt (füllend in der Ansicht) und Foto (crop-to-fill auf die Seite) sind bei abweichendem Seitenverhältnis minimal verschieden.
- **Rahmen bis zum Seitenrand.** Das Kisscam-Foto füllt die ganze Seite; bei randlosem Druck mit Beschnitt kann der äußere Rahmen dünner wirken (die 5-mm-Sicherheitszone gilt nur für Text der übrigen Layouts).
- **Musik braucht Media Foundation.** Der `MusicPlayer` nutzt den WPF-`MediaPlayer`; auf Windows N ohne Media Feature Pack oder mit unbekanntem Codec bleibt die Kisscam stumm (nur Logwarnung). Berücksichtigt werden `.mp3 .wav .m4a .wma` der obersten Ebene des Musikordners; Wiedergabe auf echter Hardware nur lokal geprüft (`Hardware`-Test).
- **Musikordner auf Netz- oder USB-Laufwerk.** Der `FileSystemWatcher` kann dort Ereignisse verlieren oder ausfallen (dann wird er abgeschaltet, es bleibt „Aktualisieren“); ein hängendes Laufwerk führt nach 3 s zu „Ordner nicht erreichbar“, die Kisscam läuft dann mit dem letzten bekannten Stand der Songs weiter (schlägt das Abspielen fehl, ohne Ton). Ein dauerhaft hängender Zugriff blockiert weitere Zugriffe, bis er endet.
- **Startzeit bei VBR-MP3.** Das Positionieren in MP3 mit variabler Bitrate ohne Index kann ungenau sein (Abweichung um Sekunden); „Position übernehmen“ nimmt die vom Player gemeldete Position, die beim Wiedergeben gleich ungenau ist. Die Songlänge kommt aus den Windows-Dateieigenschaften und fehlt bei Formaten ohne Metadaten (Anzeige „–“, dann auch keine Warnung „Startzeit liegt hinter dem Songende“).
- **Songeinstellungen sind pro Rechner und Ordner.** Die Config `kisscam-musik.json` liegt im Musikordner, nicht in den Einstellungen: „Einstellungen teilen“ überträgt sie nicht; wer den Ordner kopiert, nimmt sie mit. Die frühere Einzeldatei `KisscamMusicPath` wird nicht übernommen. Ist ein Song kurz nach einer Änderung noch nicht gespeichert (300 ms) und die App wird hart beendet, geht diese Änderung verloren.
- **Ein Song gilt nach einem echten Fehlschlag als nicht abspielbar** (bis die Datei sich ändert oder „Aktualisieren“ gedrückt wird), auch wenn die Ursache vorübergehend war (z. B. gesperrte Datei). Eine bloß verpasste Öffnungsfrist (`MusicPlaybackFailedEventArgs.TimedOut`, z. B. langsame Platte) markiert nicht; dafür kann ein tatsächlich defekter Song, den Media Foundation nur durch Zeitüberschreitung meldet, bei jedem Kisscam-Start erneut bis zu 3 s Stille verursachen.
- **Dateien, die sich nur in der Groß-/Kleinschreibung unterscheiden** (Ordner mit Unterscheidung) erscheinen in der Songliste nur einmal (die in Ordinal-Reihenfolge erste); Config und Anzeige kennen Namen ohne Unterschied.
- **Kurzer Timer ohne Gasttext.** Bei `KisscamSeconds` = 3 ist der Countdown von Anfang an in der Ziffernphase (die letzten 3 s zeigen die Ziffern), der Gasttext wird nie sichtbar.
- **Beschnittene Originale.** Mit Zoom sind auch die gespeicherten Originalfotos zugeschnitten.

## Admin und Einstellungen

- **Export enthält bewusst weder Overlay-PNG noch Geräte/Ordner** (Kamera, Drucker, Foto-/Videoordner, Mikrofon, Monitor); der Zielrechner wählt sie selbst, ein fehlendes Overlay entfällt mit Hinweis.
- **Import aus neuerer Version.** Ändert sich der Typ eines Abschnitts, wird nur dieser auf Standard zurückgesetzt, der Rest bleibt.
- **Timeout beim Schreiben der Exportdatei.** Danach kann eine `.tmp` neben der Zieldatei liegen bleiben (der Vorgang läuft im Hintergrund aus).
- **Trackpad-Scrollen nur per Unit-Test geprüft.** Praxistest durch den User steht aus.
- **Ungültige `settings.json`.** Sie wird gesichert (`.bak-…`), die übrigen Einstellungen müssen neu gesetzt werden. Die PIN bleibt erhalten, sofern `JsonDocument` die Datei noch lesen kann (z. B. falscher Typ an anderer Stelle, Kommentare, abschließende Kommas); bei echtem Syntaxfehler folgt der Erststart.
- **Dunkles Schema.** `MessageBox` und Datei-/Ordnerdialoge bleiben systemgestaltet; Scrollleisten übernehmen nur den Hintergrund (Daumen/Pfeile aus dem Windows-Template). Der Modus „System“ wird beim Öffnen gelesen, ein Wechsel in Windows bei offenem Admin wird erst beim nächsten Öffnen übernommen.
- **Standard-Layout bleibt in der Gästeauswahl.** `AdminViewModel.EnsureDefaultInGuestChoices` hängt das gewählte Standard-Layout an `GuestChoices` an. Ein versehentlicher Probeklick (z. B. auf Overlay und zurück) hinterlässt den Eintrag dauerhaft; ohne Overlay-PNG blockiert dann die Prüfung das Speichern mit einer schwer zuordenbaren Meldung.
- **Monitorwahl.** Der Wechsel auf einen echten Zweitmonitor wurde manuell erfolgreich geprüft. Ungetestet sind unterschiedliche Skalierung von Haupt- und Zweitmonitor sowie der Klonmodus. Es gibt kein Per-Monitor-DPI-Manifest (die App ist systemweit DPI-bewusst); bei unterschiedlicher Skalierung kann die Gastansicht auf dem Zweitmonitor leicht unscharf wirken.
- **Overlay-PNG-Import (neu in der 2.5.1-Prüfung).** Der Import im Admin-Tab Layout liest, prüft und kopiert die Datei synchron auf dem UI-Thread ohne Frist; bei einem langsamen Netzlaufwerk friert das Fenster (und die Gastansicht) kurz ein. Bewusst nicht geändert: Das Verhalten bei Fehlern und Abbruch bräuchte eine neue asynchrone Strecke.
- **Änderungen während des Ladens der Listen (neu in der 2.5.1-Prüfung).** Ändert der Admin etwas, während die Listen beim Öffnen noch laden (langsamer Drucker/Monitor/Mikrofon), wird das als Ausgangsstand übernommen; „Schließen“ fragt dann nicht nach ungespeicherten Änderungen.
- **Speichern und Ordner-Schreibtest (neu in der 2.5.1-Prüfung).** Speichern prüft den Entwurf vor dem bis zu 10 s dauernden Ordner-Schreibtest; währenddessen weiter bearbeitete Felder werden ungeprüft gespeichert.
- **Tastentests lesen echten Tastaturzustand.** `AdminWindow.xaml.cs` nutzt `Keyboard.Modifiers`; hält man lokal während des Testlaufs Strg/Alt gedrückt, schlagen die `AdminWindow_KeyCapture_*`-Tests fehl (CI unbetroffen). Ein Fix bräuchte eine injizierbare Modifier-Quelle.

## Build und Release

- **`Fotobox.exe` unsigniert.** SmartScreen warnt; Code-Signing braucht ein gekauftes Zertifikat.
- **Asset-Namen mit „+“.** Ungeklärt, ob GitHub das „+“ behält; deshalb heißen die Downloads `FotoboxPlus-…`. Nach dem ersten Release prüfen.
- **x64 nicht auf echter Hardware geprüft.** Nur per CI (Tests, Abhängigkeitsprüfung, Smoke-Test ohne Kamera) und x64-Emulation auf dem ARM64-Rechner, nicht auf echter Intel/AMD-Hardware. Die app-lokale VC++-Runtime ist ab 2.2.1 im Paket.
- **Lokal gebaute Pakete enthalten keine VC++-Runtime** (lokal ist nur eine zu alte x64-CRT und keine arm64-CRT installiert) und dürfen nicht weitergegeben werden; ausgelieferte Pakete kommen aus der CI.

## Tests

- **Tests schreiben ins echte App-Log** (`%LOCALAPPDATA%\Fotobox\logs`) statt in ein isoliertes Verzeichnis.
- **Testlaufzeit (neu in der 2.5.1-Prüfung).** `GuestHintStyleTests` nutzt feste Wartezeiten (ca. 22 s der Laufzeit); bewusst nicht auf Polling umgestellt, weil das ein Flakiness-Risiko brächte.

## Hinweis zur Nummerierung

Frühere Listen führten „Offene Punkte“ mit Nummern; Punkt 13 (Installer-Update einer 1.4.1-Installation) entfällt, weil es seit 2.5.0-beta keinen Installer mehr gibt. Erledigte Punkte (Kamerawechsel-Fehler, Bild-Watchdog, Sichtprüfung der Gast-/Admin-Ansicht, Seitengrößen-Cache, Init-Timeout, Versionierung) sind aus der Liste entfernt.
