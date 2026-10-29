# Admin-Ansicht

Das Admin-Fenster (`src/Fotobox/Views/Admin/AdminWindow.xaml`, Logik in `AdminViewModel*.cs` unter `src/Fotobox/ViewModels/Admin`) ist modal über der Gastansicht, 1100 × 750 (Minimum 900 × 620, auf kleinen Monitoren auf dessen Arbeitsfläche begrenzt). Der Admin wird gesiezt. Geöffnet wird es von `AdminLauncher.OpenAdmin` (Standard Strg+Alt+A) nach der PIN-Abfrage.

## Öffnen

1. `AdminLauncher.OpenAdmin` prüft `LoadFailed`/`HasPin` (sonst Erststart-Dialog bzw. Wiederholen/Beenden, siehe `einstellungen.md`).
2. `PinDialog` mit dem prozessweiten `PinAttemptLimiter` (5 Fehlversuche, dann Sperre).
3. Es merkt sich den Stand vor dem Öffnen (`cameraBefore`, `videoBefore`) für die Kamera-Entscheidung nach dem Schließen.
4. `AdminViewModel` bearbeitet einen Entwurf (Kopie der Einstellungen); gespeichert wird erst durch „Übernehmen“ oder „Speichern & schließen“. Während der Admin offen ist, ist die Gastansicht gesperrt (`BoothViewModel.IsAdminOpen`: kein Auslösen, kein Fun, kein Verschieben des Fensters).

## Tabs (Reihenfolge wie im Fenster)

| Tab | Inhalt |
|---|---|
| Kamera | Karte „Kamera“: Gerät, Auflösung („Automatisch“ = höchste flüssige Auflösung bis 1920 × 1080), Live-Vorschau spiegeln, Foto spiegeln, Live-Vorschau mit Kamerastatus. Karte „Bildeinstellungen“: Live-Regler, Profile, Flimmerschutz, „Alle zurücksetzen“ (Details `kamera.md`). |
| Ablauf | Countdown vor jedem Foto, Dauer der Vorschau, Pause zwischen mehreren Fotos, Dauer der Fehleranzeige. Hinweis: Drucken wird im Tab „Drucker“ eingestellt. |
| Video | Videomodus aktiv, Höchstdauer, Countdown, Mindestlänge, Anzeige während der Aufnahme (REC-Punkt, Laufzeit, Restzeit, Warnung), Text und Dauer nach dem Speichern, Ton/Mikrofon; Hinweis auf den Tab „Speicher“ für Ort, Präfix und Qualität (Details `video.md`). |
| Layout | Karten für Einzelfoto randlos, Fotostreifen, Foto mit Overlay (Standard-Layout); Auswahl für Gäste (Mausrad) mit Reihenfolge-Liste (▲/▼, ★/☆ für den Standard) und Umkehr-Checkbox; Anzahl der Fotos im Streifen, Streifenvariante, Overlay-PNG, Bildunterschrift, Schriftart. |
| Gastansicht | Foto-Hinweis (unten): Hinweistext mit Platzhaltern und Stilwahl (3 × 3-Kartenraster, neun Stile); Mausradzeile (Text, fünf Stile, an/aus); Video-Hinweiszeile oben (Text, Stile); Live-Vorschau der Gastansicht „nach Layout“ (Quelle des Aussehens ist `Views/Booth/GuestHint`). |
| Darstellung | Monitor der Gastansicht (ComboBox „Bildschirm N – Name (B × H)“, bei nur einem Monitor ausgegraut, fehlender Monitor als „… (nicht angeschlossen)“), Farbschema (Wie Windows / Hell / Dunkel) und „Größe der Einstellungen“ (Zoom des Inhaltsbereichs, Regler 60–100 %, Standard 80 %). |
| Drucker | **Seit 2.5.1:** „Fotos nach der Aufnahme drucken“ und „Erwartete Druckdauer“ stehen hier. Alles darunter (Drucker, Papiergröße, Ausrichtung, Kopien, Zeitlimit, Testdruck) ist bei ausgeschaltetem Druck ausgegraut (`PrinterSettingsPanel.IsEnabled = PrintingEnabled`, Deckkraft 0,55). Das Zeitlimit wird automatisch auf mindestens erwartete Dauer + 60 s angehoben. Testdruck mit den aktuellen, noch ungespeicherten Einstellungen (`TestPageRenderer`). |
| Tasten | Tastenbelegung je Aktion (Foto auslösen, Admin öffnen, Abbrechen, Video starten, Video beenden), „Taste hinzufügen“ mit Aufnahmemodus („Drücken Sie jetzt die gewünschte Taste …“), Linke/Rechte Maustaste löst aus, „Standard wiederherstellen“. Auslösen und Admin behalten immer mindestens eine Taste. |
| Speicher | Zwei Karten. „Fotos speichern“: Speicherordner, Ordner öffnen, Standardordner, Tagesunterordner, Originalfotos zusätzlich, JPEG-Qualität, Dateipräfix mit Beispielname (`PhotoFileNameExample`, wie beim Speichern bereinigt). „Videos speichern“ (Panel `VideoStoragePanel`, bei ausgeschaltetem Videomodus ausgegraut): Speicherordner, Ordner öffnen, Standardordner, Tagesunterordner, Videoqualität, Dateipräfix mit Beispielname (`VideoFileNameExample`). Die Felder bleiben in `VideoSettings`; nur die Darstellung liegt im Tab „Speicher“. |
| Sicherheit & Daten | **Seit 2.5.1** so benannt. Drei Karten: „PIN“ (neue PIN, Wiederholung, „PIN ändern“, Status), „Einstellungen teilen“ (Exportieren …, Importieren …, Haken „PIN mit exportieren“), „Standardwerte“ („Standardwerte wiederherstellen …“). Inhalt siehe `einstellungen.md`. |
| Fun | Startseite mit vier Kacheln und je einer Unterseite (insgesamt fünf Seiten mit der Startseite), siehe unten. |

Tab-Wechsel: `OnTabSelectionChanged` bricht eine laufende Tastenaufnahme ab und setzt `IsFunTabSelected` (die Live-Erkennung der Fun-Vorschau läuft nur, solange der Tab Fun gewählt ist).

### Fun-Tab

Seiten (`FunPage`: `Start, Persons, Peace, MiddleFinger, Heart`, nur hinten erweitern) liegen als Geschwister-Grids im Tab (kein verschachteltes `TabControl`, das erbte den Seitenleisten-Stil), sichtbar je nach `AdminViewModel.FunPage`:

| Kachel | Beschreibung auf der Kachel | Seite |
|---|---|---|
| Personenerkennung | „Zielmarkierungen über erkannten Personen im Livebild.“ | Karten „Zielerfassung“ (an/aus), „Vorschau“ (Live, „Animation testen“), „Erkennung“ (Methode, Umfang, Empfindlichkeit, Mindestgröße, Höchstzahl, Verweildauer, Analysen/s), „Aussehen“ (Stil, Farbe, Animation, Dauer und Intensität der Einrast-Animation), „Text“ (Texte, Position, Stil, Anzeigedauer, Pause, Wiederholung, Personentausch), „Ton“ |
| Peaceerkennung | „Verstecktes Extra: Ein Peace-Zeichen löst eine Überraschung aus.“ (`PeaceSettingsView`) | Vorschau mit Handpunkten und „Peace erkannt“, „Selbstzerstörung testen“, „Bart testen“ (ohne Foto), „Gesichtspunkte anzeigen“ (nicht gespeichert, Standard aus), Aktionen (Selbstzerstörung, Foto, Schnurrbart; mindestens eine), Auslösung (Haltedauer, Fortschrittsring, Sperrzeit, Empfindlichkeit, Analysen/s), Detailkarten je Aktion, Bartformen „Beim Zufall verwenden“ (der letzte Haken ist gesperrt), Warnung bei fehlender Gesichtserkennung |
| Mittelfinger | „Hebt Bart und Selbstzerstörung auf.“ (`MiddleFingerSettingsView`, `AdminViewModel.MiddleFinger.cs`) | Vorschau („Mittelfinger erkannt“, „Aufheben testen“ = 20-s-Test-Bart), Erkennung (Haltedauer, Empfindlichkeit, **seit 2.5.1** auch „Analysen pro Sekunde“, derselbe Wert wie bei Peace – `PeaceSettings.AnalysesPerSecond`), Aktionen „Peace-Effekte aufheben“ und „Kisscam abbrechen“ (`ActionCancelKisscam`; die letzte gewählte lässt sich nicht abwählen), Warnung bei fehlender Gesichtserkennung sowie – nur wenn Peace **und** Herz ausgeschaltet sind – bei ausgeschalteter Erkennung (`HasMiddleFingerPeaceDisabledWarning`) |
| Herz | „Verstecktes Extra: Ein Herz aus zwei Händen startet die Kisscam.“ (`HeartSettingsView`, `AdminViewModel.Heart.cs`; Kachelstatus „An · Kisscam“) | Karten „Herzerkennung“ (an/aus), „Erkennung“ (Formen Klassisch/Dach/Umgedreht/Fingerherz mit mindestens einer Form, Haltedauer, Empfindlichkeit, Fortschrittsherz, „Analysen pro Sekunde“ gemeinsam mit Peace/Mittelfinger, ⓘ `AdminInfoTexts.HeartAnalysesPerSecond`), „Aktion“ (Wahlkarten aus dem Katalog `HeartActions.All`), „Kisscam“ (Timer, Gasttext mit `{Sekunden}` und Hinweis auf unbekannte Platzhalter, Banner-Text, Farbwahl mit Skizzen `KisscamFrameSketch`, Zoom samt Stärke, Dateipräfix mit Beispielname, Musikordner mit Songliste, Gesamtlautstärke), Vorschau mit Handpunkten, Haltefortschritt-Herz, Abzeichen „Herz erkannt ❤ (Form)“ und „Kisscam testen“ (siehe unten) |

Peace-, Mittelfinger- und Herz-Seite teilen sich dieselbe Kamera-Vorschau (`PeacePreview`; `PushPeacePreviewMode`). Auf der Herz-Seite bekommt sie einen Klon des Herz-Entwurfs mit `Enabled = true` (die Vorschau zeigt die Erkennung auch bei ausgeschaltetem Schalter) und `peaceGestures: false` (Peace-Gesten sind dort aus). `AffectsPeacePreview` kennt für Herz bewusst nur eine Liste erkennungsrelevanter Properties (Formen, Haltedauer, Empfindlichkeit, Aktion), kein Präfix.

**Kisscam-Test (Seite „Herz“):** Eine gehaltene Herzgeste in der Vorschau oder der Knopf „Kisscam testen“ startet den Test (nur auf der Herz-Seite; nie ein Foto, Druck oder Video). Ablauf mit 1-s-Takt über `TimeProvider`: Jumbotron-Rahmen (`KisscamOverlay`) mit formatiertem Gasttext, ab `SettingsLimits.KisscamFinalDigitsSeconds` (3 s) große Ziffern, bei 0 für 1,5 s das Abzeichen „Foto würde jetzt ausgelöst 📷“, dann Ende. Gewählte Musik: `Play` beim Start mit einem zufällig gewählten, angehakten und abspielbaren Song aus dem Stand der Bibliothek (ab dessen Startzeit, in Schleife; ohne Treffer Stille), `FadeOut(800 ms)` am Ende, `Stop` bei Abbruch (nur, wenn der Test sie gestartet hat; das Anspielen eines Songs endet beim Teststart). Abbruch durch den Knopf („Test abbrechen“), einen Mittelfinger (nur mit Aktion „Kisscam abbrechen“; der laufende Test zählt dafür als Ziel `MiddleFingerTargets.Kisscam`), Seiten-/Tabwechsel, Herz-Schalter aus, Entwurfswechsel (Zurücksetzen/Import) und Schließen. Die Properties `KisscamTest*` und `HeartPreview*` sind reiner Zustand (`IsHeartViewStateProperty`: keine Änderungsmeldung, keine Neukonfiguration der Erkennung). Der Zoom wird in der Vorschau nicht gezeigt.

Die Statuszeile ergänzt `PeacePreviewStatusFormatter.Format(…, middleFingerNow, heartNow)` um „Mittelfinger erkannt“ bzw. „Herz erkannt ❤“. Änderungen an `MiddleFingerPreview*`/`MiddleFingerDetected*` lösen keine Entwurfs-Änderungsmeldung aus. Die Fun-Texte und Algorithmen stehen in `fun-zielerfassung.md` und `fun-peace-mittelfinger.md`.

**Seite „Herz“ im Detail** (`fun-herz-kisscam.md`):

- **Musik (Ordner und Songliste):** Ordnerzeile mit dem Pfad (Standard `Musik\Fotobox+\Kisscam`) und den Knöpfen „Ordner wählen …“ (`ChooseFolderCore`/`IAdminUi.PickFolder`; zu lange Pfade oder Steuerzeichen werden mit Hinweis abgewiesen), „Standard“, „Ordner öffnen“ (nur ein vorhandener Ordner, mit `FolderProbeTimeout`) und „Aktualisieren“ (`IKisscamMusicLibrary.Refresh`). Der Ordner gehört zum Entwurf und ist gerätespezifisch (wird beim Teilen nicht übertragen). Der Admin lenkt die gemeinsame Bibliothek auf den Ordner des Entwurfs; **Schließen oder Abbrechen setzt sie immer auf den gespeicherten Ordner zurück** (`DisposeHeart`). Darunter die **Songliste** (`KisscamSongs`, eine `KisscamSongRowViewModel` je Datei; die Zeilen werden nach Dateinamen abgeglichen statt neu erzeugt, damit der Fokus erhalten bleibt): Häkchen, Startzeit `m:ss` (Textfeld und +/− in 1-s-Schritten, `KisscamTimeFormat`), Song-Lautstärke 10–100 % (`NumberStepper`), Songlänge, Warnungen „Startzeit liegt hinter dem Songende – es wird ab 0:00 gespielt“ und „Datei lässt sich nicht abspielen“, Knopf „Anspielen“/„Stopp“. Jede Änderung geht sofort in die Bibliothek und wird automatisch in `kisscam-musik.json` gespeichert – **unabhängig vom „Speichern“ des Admins** und ohne Wirkung auf „Änderungen verwerfen?“. Hinweise (`KisscamMusicHints`): leerer Ordner („Der Ordner ist leer – kopieren Sie Musikdateien (MP3, WAV, M4A, WMA) hinein.“), „Kein Song ausgewählt – die Kisscam läuft ohne Musik.“, „Kein abspielbarer Song ausgewählt …“, Ordner nicht erreichbar, Config defekt (mit Hinweis auf `kisscam-musik.json.defekt`), Speichern fehlgeschlagen.
- **Anspielen:** spielt den Song ab seiner Startzeit (in Schleife) für `KisscamSeconds` und blendet dann in 800 ms aus (`KisscamTestMusicFadeOut`); es läuft immer nur ein Anspielen. Es endet bei einem anderen Song, dem Kisscam-Test, einem Ordnerwechsel, dem Wechsel von Seite oder Tab, beim Ausschalten der Herzerkennung, beim Ersetzen des Entwurfs, wenn der Song aus dem Ordner verschwindet und beim Schließen (`StopKisscamMusicPreview`; nur, wenn diese Ansicht es gestartet hat – der Player gehört auch der Gastansicht). Während des Anspielens zeigt die Zeile die Position (`PositionChanged`) und „Position übernehmen“ setzt die zuletzt gemeldete Position als Startzeit. Die Gesamtlautstärke (`KisscamMusicVolumePercent`) gehört zum Entwurf; die Lautstärke gilt beim nächsten Anspielen.
- **Timer-Hinweis:** Bei höchstens `SettingsLimits.KisscamFinalDigitsSeconds` (3) Sekunden steht unter dem Timer „Bei 3 Sekunden erscheint nur der Countdown 3 – 2 – 1, kein Text.“ (`HasKisscamOnlyCountdownHint`, `KisscamOnlyCountdownHintText`; reiner Zustand in `IsHeartViewStateProperty`, keine Änderungsmeldung). Die Statuszeile der Vorschau zeigt gleiche Warnungen nur einmal (`PeacePreviewStatusFormatter` entfernt Duplikate, z. B. wenn Hand- und Gesichtsstufe denselben Grund nennen).
- **Farbauswahl:** fünf Wahlkarten (Pink-Rot, Rot, Gold, Lila, Schwarz-Weiß) mit gezeichneter Rahmen-Skizze in den Bildfarben aus `KisscamPalette` (Skizzen sind bewusst fest eingefärbt, keine Admin-Farben).
- **Validierung (`ValidateHeart`):** ohne Form bei eingeschalteter Erkennung, Bereichsprüfungen, Textlängen; Kisscam-spezifische Felder (Text, Banner, Präfix nie leer/ungültig, Pfadlänge) nur bei eingeschalteter Erkennung mit Aktion Kisscam.
- **Live-Vorschau und „Kisscam testen“:** Die Seite zeigt die Kamera-Vorschau mit der Herz-Erkennung (Form des erkannten Herzens). „Kisscam testen“ spielt die Kisscam mit den Entwurfswerten **simuliert** ab – Timer, Gasttext, Ziffern 3-2-1, ein Abzeichen „Foto würde jetzt ausgelöst“ statt Blitz und Musik – und löst nie ein Foto oder einen Druck aus. Abbruch per Knopf, per Mittelfinger und beim Seitenwechsel.

Die Live-Vorschauen nutzen die Entwurfswerte (noch nicht gespeichert), laufen nur bei sichtbarem Tab/gewählter Seite und lösen nie ein echtes Foto oder einen Druck aus (die Admin-Vorschau bekommt bewusst keinen `GuestMoustacheState`).

### Info-Symbole (ⓘ)

**Seit 2.5.1** erklären ⓘ-Badges per Tooltip (Style `InfoBadge` in `Views/Admin/AdminTheme.xaml`) einzelne Einstellungen. Die Texte stehen zentral in `src/Fotobox/ViewModels/Admin/AdminInfoTexts.cs` (Tooltip und `AutomationProperties.HelpText` aus einer Quelle; aktuell „Analysen pro Sekunde“ für Zielerfassung, Peace, Mittelfinger und Herz). Die Aussagen müssen zum Verhalten der Erkennungsdienste passen (Kommentare in der Datei nennen die Codestellen). Verwendet werden sie in `AdminWindow.xaml`, `PeaceSettingsView.xaml` und `MiddleFingerSettingsView.xaml`.

## Speichern

Fußleiste: links „Fotobox+ beenden“, rechts „Schließen · Übernehmen · Speichern & schließen“.

- Beide Speicherwege laufen über `AdminViewModel.SaveCoreAsync(close)`; das Flag `IsSaving` sperrt gleichzeitiges Speichern (Doppelklick, Übernehmen + Speichern) und ebenso laufenden Export/Import (`IsTransferring`) sowie ein bereits angefordertes Schließen.
- Ablauf: `ValidateAsync` (Eingaben, Schreibtest geänderter Ordner mit 10-s-Frist, Overlay-Datei …; Fehlermeldungen in `ValidationMessage`) → eine begonnene PIN-Änderung zuerst anwenden (`TryApplyPin`) → `Security` aus dem Store (bzw. importierte PIN, siehe `einstellungen.md`) in den Entwurf übernehmen → `ISettingsStore.Save` (löst `SettingsChanged` aus) → `SettingsSaved = true`, `ResetBaseline`.
- „Übernehmen“ lässt das Fenster offen, zeigt `SaveStatusMessage` „Einstellungen gespeichert.“ (verschwindet, sobald der Entwurf wieder abweicht), aktualisiert `_initialPhotoFolder`/`_initialVideoFolder` und lässt die Kamera dem Entwurf weiter folgen. „Speichern & schließen“ verwirft wartende Entwurfs-Neustarts der Kamera (`_cameraRestartVersion`), damit sie nicht noch mit Entwurfswerten startet, während das Hauptfenster sie neu startet.
- `SettingsSaved` heißt „mindestens einmal gespeichert“.
- `HasUnsavedChanges` vergleicht das JSON des normalisierten Entwurfs ohne `Security` mit der Basis (`SnapshotJson`/`_baselineJson`); gefüllte PIN-Felder und eine importierte, noch nicht gespeicherte PIN zählen als Änderung. Lässt sich der Entwurf nicht serialisieren, gilt „geändert“ (lieber einmal zu viel fragen).
- „Schließen“ und das X (`Closing` in `AdminWindow`) rufen `ConfirmDiscardChanges` auf: bei Änderungen Rückfrage „Es gibt ungespeicherte Änderungen. Möchten Sie sie verwerfen und den Admin schließen?“; scheitert die Rückfrage, wird nicht blockiert.
- „Fotobox+ beenden“ fragt separat nach (`RequestExit`) und setzt `ExitRequested`; ungespeicherte Änderungen gehen dann verloren.
- Sofort wirksam nach dem Speichern sind Tasten, Hinweise und Layout (`SettingsChanged`). Mikrofon/Ton und Monitorwahl wirken erst nach dem Schließen.

## Kamera-Entscheidung nach dem Schließen

`AdminLauncher.DecideCameraHandling(settingsSaved, exitRequested, cameraTouched, cameraBefore, videoBefore, lastApplied, saved, lastAppliedVideo)` entscheidet auf **genau einem** Weg, damit die Kamera nicht doppelt neu startet:

| Situation | Ergebnis |
|---|---|
| Nie gespeichert (nur Schließen), Admin hat die Kamera mit Entwurfswerten gestartet (`cameraTouched`) | `RestoreCamera`: die Admin-Ansicht stellt die Kamera selbst auf den gespeicherten Stand zurück (`AdminViewModel.RestoreCameraAsync`); das Hauptfenster startet nichts. |
| Mindestens einmal gespeichert | `CameraChanged` (Hauptfenster startet die Kamera mit den gespeicherten Einstellungen neu), wenn sich die gespeicherten Kameraeinstellungen gegenüber dem Öffnen ändern (nur Gerät oder Auflösung, `CameraSettings.RequiresRestartComparedTo`; Spiegeln, Regler, Profile, Flimmerschutz zählen nicht), oder die Kamera zuletzt mit etwas anderem gestartet wurde (`lastApplied` ≠ gespeichert; deckt eine nach „Übernehmen“ verstellte und beim Schließen verworfene Kamera ab), oder sich der Ton ändert (`VideoSettings.RequiresCameraRestartComparedTo`, Vergleich mit dem Stand beim Öffnen) |
| **Neu in 2.5.1:** Gespeichert, dann aber ein Ton-/Mikrofonstand verworfen, mit dem die Admin-Ansicht die Kamera gestartet hat | `CameraChanged` auch dann (`lastAppliedVideo.RequiresCameraRestartComparedTo(saved.Video)`), die Kamera läuft sonst mit dem verworfenen Ton weiter |
| Beenden der Anwendung | nichts |

Nach dem Schließen (außer beim Beenden) ruft der Launcher `AdminViewModel.RestoreAfterCloseAsync(saved, restoreCamera)` auf: gegebenenfalls zuerst `RevertCameraControlsAsync` (wenn die Wiederherstellung die Kamera wechselt), dann `RestoreCameraAsync`, dann das Zurücknehmen live verstellter Regler – verkettet, nicht parallel, höchstens 5 s (`ControlsRevertTimeout`). Das gilt auch ohne Kamera-Neustart und nach „Übernehmen“ (nur das seither Verstellte). Auch `SelectDeviceAsync` nimmt Regler vor dem Kamerawechsel zurück. `AdminResult` (`SettingsSaved`, `ExitRequested`, `CameraChanged`) geht ans Hauptfenster; dieses platziert die Gastansicht neu (`RefreshPlacementAsync`).

## Farbschema

- `AppSettings.Appearance.AdminTheme` (`AdminThemeMode`: System/Light/Dark, Standard System = Windows-App-Modus aus `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`, bei Lesefehler hell; unbekannte Werte → System). Gilt für Admin-Fenster, PIN-Abfrage und Erst-PIN-Dialog, nicht für die Gastansicht.
- Farben stehen nur in den Paletten `AdminColors.Light.xaml` und `AdminColors.Dark.xaml` (gleiche Schlüssel, ein Test prüft das). `AdminTheme.xaml` und alle Admin-XAMLs beziehen Farben ausschließlich per `DynamicResource`. Neue Admin-Farben immer als Schlüssel in beiden Paletten anlegen.
- `AdminThemeManager.Apply(window, mode)` tauscht die im Fenster gemergte Palette und setzt die Titelleiste per DWM (`DWMWA_USE_IMMERSIVE_DARK_MODE` = 20; vor der Fenstererzeugung verzögert in `SourceInitialized`).
- Der Tab „Darstellung“ wirkt sofort als Vorschau (das Fenster hört auf `AdminViewModel.AdminTheme`), gespeichert wird mit „Übernehmen“ bzw. „Speichern & schließen“. „Standardwerte wiederherstellen“ setzt auf System.
- Die ComboBox (inkl. Popup/Items) hat ein eigenes Template, weil das Windows-Template `Background` ignoriert. Skizzen (Layout-Karten, Schema-Karten, Hinweisstile) und die Gastansicht-Vorschau sind bewusst fest eingefärbt.
- Grenzen: `MessageBox` und Datei-/Ordnerdialoge bleiben systemgestaltet; Scrollleisten übernehmen nur den Hintergrund. Der Modus „System“ wird beim Öffnen gelesen, ein Wechsel in Windows bei offenem Admin gilt erst beim nächsten Öffnen.

## Größe der Einstellungen (Zoom)

- `AppSettings.Appearance.AdminZoomPercent`: 60–100 % in 5er-Schritten (`SettingsLimits.AdminZoomMin/Max/Step`), Standard 80 %. Der Normalizer klemmt und rundet auf das nächste Vielfache von 5. **Gerätespezifisch** (steht in `DeviceSpecificSettings.Paths`): wird nicht exportiert und beim Import nicht überschrieben, weil er vom Bildschirm abhängt.
- Der Regler steht im Tab „Darstellung“ unter dem Farbschema (`AdminViewModel.AdminZoomPercent`, Text `AdminZoomText`, Faktor `AdminContentScale` = Prozent / 100) und wirkt sofort live; gespeichert wird wie alles andere mit „Übernehmen“ bzw. „Speichern & schließen“. Verwerfen (Schließen ohne Speichern) lässt den gespeicherten Wert beim nächsten Öffnen gelten – es ist nur Layout des offenen Fensters, daher keine Rücksetzlogik wie beim Farbschema nötig.
- Umsetzung: Im `TabControl`-Template (`AdminTheme.xaml`, einziger Nutzer ist das Admin-`TabControl`) trägt der `ContentPresenter` `PART_SelectedContentHost` eine `ScaleTransform` als **`LayoutTransform`** (nicht `RenderTransform`: der Inhalt belegt dann den verkleinerten Platz, Scrollbereich und Umbrüche stimmen). Der Faktor kommt per Bindung (`DataContext.AdminContentScale` des `TabControl`, ohne Ansichtsmodell 1). Tab-Leiste links und Fußleiste bleiben bei 100 %.
- Das Fenster nutzt `TextOptions.TextFormattingMode="Display"` (pixelgenau); skaliert wird dieser Text unscharf. Der Inhaltsbereich steht deshalb bei Zoom ≠ 100 % auf `Ideal`, bei genau 100 % (DataTrigger) wie das übrige Fenster auf `Display`.
- Der Zoom löst keinen Kamera-Neustart und keine andere Folge aus: `AdminLauncher.DecideCameraHandling` vergleicht nur Kamera- und Videoeinstellungen.

## Proportionales Scrollen

`ProportionalWheelScroll` (`Views/Admin/ProportionalWheelScroll.cs`, Attached Property `IsEnabled`, per implizitem `ScrollViewer`-Style in `AdminTheme.xaml` und im ComboBox-Popup gesetzt) skaliert Mausrad und Präzisions-Trackpad nach dem Betrag des Deltas (`Core/WheelScrollMath.PixelsFor`). WPF wertet sonst nur das Vorzeichen aus, sodass jedes Trackpad-Ereignis eine volle Rastung wäre.

- Zuständig ist der innerste scrollbare `ScrollViewer` unter dem Zeiger; am Anschlag scrollt der äußere.
- Bei `CanContentScroll` (virtualisierte Listen) zählt ein `WheelDeltaAccumulator` ganze Rastungen (Rest bleibt, Richtungswechsel verwirft ihn).
- Umschalt bleibt beim WPF-Standard.
- Die Gastansicht ist unberührt (Layoutwahl per Mausrad bleibt rastend).
- Nur per Unit-Test geprüft; ein Praxistest steht aus.

## PIN-Dialoge

| Dialog | Zweck |
|---|---|
| `FirstRunPinDialog` | Erststart bzw. fehlende PIN: PIN festlegen (4–12 Ziffern, Wiederholung). Zeigt die tatsächliche Admin-Taste. Erscheint ohne Owner auf dem Hauptbildschirm; bricht der Nutzer ab, endet die App. |
| `PinDialog` | PIN-Abfrage vor dem Admin mit `PinPad`; `PinAttemptLimiter` zeigt Restversuche bzw. Restsperre. Prozessweit, die Sperre überdauert das Schließen des Dialogs. |

Beide folgen dem Farbschema. Regeln der PIN (Länge, Hash, Limiter): `einstellungen.md`. PIN ändern im Admin: Tab „Sicherheit & Daten“, Karte „PIN“; „PIN ändern“ gilt sofort, ausgefüllte Felder werden beim „Übernehmen“/„Speichern & schließen“ ebenfalls angewendet.

## Weitere Einzelheiten

- Admin-Auswahlen von Geräten (Kamera, Mikrofon, Monitor, Drucker, Papier) werden asynchron mit Frist geladen (siehe Tabelle in `architektur.md`); die Papiergröße ermittelt der Admin über `AdminViewModel.GetPageSpecAsync`, das die geteilte Abfrage des Controllers umgeht (bei hängendem Spooler bleibt je Aktion ein Thread hängen, siehe `bekannte-grenzen.md`).
- „Standardwerte wiederherstellen“ und Import bauen den UI-Zustand über `RebuildAfterDraftReplaced` neu auf (Details `einstellungen.md`).
- Neue Einstellungen im Admin: Bereich in `SettingsLimits` anlegen, Normalizer ergänzen, Eingabe an `SettingsLimits` binden (z. B. `NumberStepper` mit `Minimum`/`Maximum`), Verhalten beim Teilen einordnen (`SettingsSections.ExcludedProperties` oder `DeviceSpecificSettings.Paths`), Tests ergänzen.
