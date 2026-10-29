# Druck, Layouts und Speichern

Stand: 2.5.1. Pfade relativ zum Repo-Root. Überblick: [ueberblick.md](ueberblick.md); Zustandsmaschine: [architektur.md](architektur.md).

## Dateien

| Datei | Inhalt |
|---|---|
| `src/Fotobox/Services/Printing/PrintService.cs` | `PrintService` (System.Printing/XPS), `PageSpecCache` |
| `src/Fotobox/Services/Printing/PrintJobStateEvaluator.cs` | reine Auswertung des Auftragszustands |
| `src/Fotobox/Services/Printing/PrintLayoutResolver.cs` | Papier, Ausrichtung, Geometrie (ohne Spooler) |
| `src/Fotobox/Services/Layout/LayoutComposer.cs`, `LayoutMath.cs` | Komposition der Druckseite |
| `src/Fotobox/Services/Storage/PhotoStorage.cs` | JPEG-Speicherung |
| `src/Fotobox/Core/BoothController.cs` | Ablauf, `PrintGuardedAsync`, geteilte Seitengrößen-Abfrage |

## PrintService

- Druck über `System.Printing` mit XPS-Writer (`PrintQueue.CreateXpsDocumentWriter(queue).Write(visual, ticket)`). `PrintQueue`-Objekte sind thread-affin: **jede Operation läuft auf einem eigenen STA-Thread** (`RunSta`, danach `Dispatcher.InvokeShutdown`).
- **Ticket (`BuildTicket`):** Papier über `PrintLayoutResolver.ResolvePaper` (Name aus den Treibergrößen → `PaperWidthMm/HeightMm` → Standardgröße des Treibers). Findet sich der Name nicht, sucht `FindPaperByDimensions` ein Treiberpapier **per Maß (±1 mm, beide Ausrichtungen)**, weil viele Treiber eine eigene namenlose Größe still durch ihre Standardgröße ersetzen. Kopien auf 1–20 geklemmt. Randlos (`PageBorderless.Borderless`), falls der Treiber es anbietet. Der Inhalt wird immer in der **nativen Medienausrichtung** gesendet (`Portrait`); das Bild dreht der Dienst selbst. Das Ticket wird per `MergeAndValidatePrintTicket` validiert; die Maße des validierten Tickets gelten (der Treiber darf ersetzen).
- **Seitenverhältnis:** Weicht die Seite um mehr als 1 % vom Medium ab (`AspectWarnTolerance`), wird nur **geloggt** (`WarnOnAspectMismatch`); das Druckbild bleibt unverändert (crop-to-fill: beschnitten, nie gestreckt, nie Rand; `ComputeFillRect`).
- **PORTPROMPT:** Drucker mit Port `PORTPROMPT` (fragt nach einem Dateinamen, z. B. Print to PDF) werden abgelehnt (`FailedFilePrompt`), weil ein modaler Dialog hinter der Vollbild-Gastansicht hängen bliebe. Lässt sich der Port nicht lesen, wird der Drucker angenommen (herstellerneutral).
- Jobname kulturunabhängig `<Anzeigename> yyyyMMdd-HHmmss-fff`; der Auftrag wird per Name (bzw. gemerkter ID) in der Queue gesucht, Fallback: genau ein neuer, unbekannter Job.
- Druckerliste und Papiergrößen (Admin): Frist `QueryTimeout` 10 s, bei Timeout leere Liste und Log.

## Überwachung (`PrintJobStateEvaluator`)

Die Schleife in `PrintCore` fragt alle 500 ms (`PollInterval`) Job- und Queue-Flags ab und lässt `Evaluate` entscheiden (reine Funktion, keine Uhr):

| Situation | Ergebnis |
|---|---|
| `IsPrinted` | fertig (zählt vor allem anderen) |
| `IsDeleted`/`IsDeleting` | fertig, wenn `IsCompleted`, sonst Fehler „Der Druckauftrag wurde abgebrochen.“ |
| `IsCompleted` (heißt nur „an den Drucker gesendet“, IPP/WSD druckt dann oft noch) | fertig erst bei `IsPrinted` oder wenn der Job nach dem Sehen aus der Queue verschwindet; nur bei aufbewahrten Jobs (`IsRetained`) genügt `IsCompleted` |
| Job nie gesehen | nach `NeverSeenCompletedAfter` 10 s gilt er als abgeschlossen |
| Konkrete Ursache | Status „Papierstau“, „Papier leer – bitte nachlegen“, „Drucker offline“, „Drucker benötigt Aufmerksamkeit“; hat Vorrang vor einem generischen Fehler |
| Fehlerflag ohne Ursache | Fehler erst nach `GenericErrorGrace` 8 s Dauer („Der Drucker meldet einen Fehler.“) |

Bei Fehlschlag wird der Auftrag storniert (sonst käme er später doch heraus, doppelter Druck). Ein aus der Queue verschwundener Job gilt als erledigt, auch wenn ihn jemand gelöscht hat (bekannte Grenze).

**Frist:** Die Gesamtfrist (`Math.Max(PrintTimeoutMin, TimeoutSeconds)`) läuft ab **Beginn** von `PrintCore` (inkl. Ticket und Schreiben). Läuft sie ab und wurde der Auftrag vollständig gesendet, aber kein Druckende gemeldet (manche IPP/WSD-Treiber), gilt der Druck als **fertig** (`Completed`, Log). Sonst `TimedOut`: „Der Druck wurde nicht rechtzeitig fertig … Der Auftrag bleibt in der Warteschlange.“

## Fristen

| Wert | Frist |
|---|---|
| Druck-Timeout (`PrinterSettings.TimeoutSeconds`) | Standard 300 s, `PrintTimeoutMin/Max` 10–900 s |
| Anhebung beim Normalisieren | mindestens `ExpectedPrintSeconds` + 60 s (`PrintTimeoutReserveSeconds`), danach auf Min/Max geklemmt (`SettingsNormalizer`; der Admin zeigt einen Hinweis) |
| Erwartete Druckdauer (Fortschrittsbalken) | `FlowSettings.ExpectedPrintSeconds`, Standard 240 s, 30–600 |
| Hartes Limit des Controllers | Druck-Timeout + 30 s (`PrintHardLimitReserve`); der Dienst-Timeout läuft ab `PrintCore` und greift davor |
| Seitengröße im Durchgang | 5 s (`PageSpecTimeout`), danach letzter guter Wert bzw. `PageSpec.Default` |
| Seitengröße im Cache | `PageSpecCache.DefaultLifetime` 10 min „frisch“, danach nur noch „letzter guter Wert“ |
| Speichern | 30 s (`SaveTimeout`) |
| Bart im Foto | 3 s (`PhotoDecorateTimeout`) |
| „Fertig“-Anzeige | 5 s (`PrintedDisplay`) |
| „Druck abgebrochen“-Anzeige | 3 s (`PrintCancelledDisplay`) |
| Warten auf Abbruchbestätigung | 10 s (`PrintCancelTimeout`) |
| Auftragssuche nach Abbruchwunsch | bis 5 s (`UserCancelFindWindow`) |

## Seitengröße: `PageSpecCache` und geteilte Abfrage

- `PrintService.GetPageSpec(printer, layout)` liefert zuerst einen frischen Cache-Eintrag (`PageSpecKey` = Drucker, Papiername, Maße, Ausrichtung, Layoutart). Nur **echte Treiberabfragen** werden gespeichert; ein Rückfall (Drucker offline/nicht gefunden) wird nicht für Minuten festgeschrieben, stattdessen gilt der zuletzt gute Wert (`TryGetLastGood`), sonst die Auflösung ohne Papier → `PageSpec.Default` (10 × 15 cm = 101,6 × 152,4 mm, 300 dpi).
- **Geteilte Abfrage:** Vorabruf (`StartPageSpecPrefetch`) und Durchgang teilen sich je Schlüssel eine laufende Abfrage (`BoothController.GetPageSpecQuery`). Die Treiberabfrage selbst ist nicht abbrechbar; Aufrufer begrenzen nur ihr Warten. Dadurch stapeln sich bei hängendem Spooler keine Threads. Der Admin umgeht die geteilte Abfrage (`AdminViewModel.GetPageSpecAsync`, bekannte Grenze: je Admin-Aktion bleibt ein Thread hängen).

## Ausrichtung

`PrintLayoutResolver.ResolveLandscape`: `PageOrientation.Portrait` → hoch, `Landscape` → quer, **`Auto`: Streifen (`LayoutKind.Strip`) hoch, alle anderen Layouts quer.** Das Papier wird als Hochformat normiert (kurze × lange Seite) und per `PageSpec.ToOrientation` gedreht.

## Druckabbruch

Taste „Abbrechen“ (Standard Esc, JSON `CancelCountdownKeys`) bricht den Countdown still ab und zusätzlich einen laufenden Druck (in `Preview`, sobald der Druck läuft, und in `Printing`; in Aufnahme/Verarbeitung ignoriert). Bewusst kein Hinweis „Esc zum Abbrechen“ in der Gastansicht.

- `IPrintService.PrintAsync(page, printer, status, ct, userCancel)` trennt zwei Abbrüche: **`ct`** beendet nur das Warten (App-Ende, hartes Limit) und löscht den Spooler-Job **nicht**; **`userCancel`** (Wunsch des Gastes) **storniert** ihn. Wurde vor dem Senden abgebrochen, wird nichts gesendet. Ein Abbruchwunsch während `Write` greift erst danach.
- `PrintOutcome`: `Completed`, `Failed`, `TimedOut`, `Cancelled` (nicht mehr warten, Auftrag unberührt), `UserCancelled`, `UserCancelFailed`.
- Nach dem Abbruchwunsch wertet der Dienst erst den regulären Zustand aus (ein inzwischen fertiger/fehlgeschlagener Auftrag behält sein Ergebnis); ist der Auftrag noch unbekannt, wird bis 5 s weiter gesucht (`DecideUserCancel`: `CancelJob`, `KeepSearching`, `GiveUp`).
- Der Controller wartet nach dem Wunsch höchstens `PrintCancelTimeout` (10 s); ohne Bestätigung `UserCancelFailed`. Anzeige in `PrintCancelled` 3 s: „Druck abgebrochen / Dein Foto ist trotzdem gespeichert.“ bzw. bei Fehlschlag „Der Druck ließ sich nicht mehr abbrechen – dein Foto kommt gleich.“ Das Foto ist in jedem Fall gespeichert.
- Der Druck läuft parallel zur Vorschau. Die Gastansicht zeigt beim Drucken einen Fortschrittsbalken über `ExpectedPrintSeconds`. Rechte bei Netzwerk-/IPP-/WSD-Druckern sind ungeprüft, ein bereits beim Drucker liegender Auftrag wird evtl. physisch nicht gestoppt.

## Layouts

`LayoutComposer.Compose(photos, layout, page, overlayDirectory, timestamp)` malt per `DrawingVisual` in Pixelkoordinaten (Seite = `PageSpec` bei 300 dpi), rendert in ein `RenderTargetBitmap` (Pbgra32) und wandelt in ein eingefrorenes Bgra32. `LayoutKind`:

| Art | Beschreibung |
|---|---|
| `SingleBorderless` | ein Foto crop-to-fill über die ganze Seite; Bildunterschrift/Datum als dunkles Verlaufsband unten (weißer Text) |
| `Strip` mit `StripStyle.DoubleCut` | zwei identische Streifen (hoch nebeneinander, quer untereinander), je 2–4 Fotos (`StripPhotoCount`, Standard 3), gestrichelte Schnittlinie |
| `Strip` mit `StripStyle.Single` | ein Streifen mit **ganzen** Fotos untereinander (Seitenverhältnis des Fotos, nicht beschnitten), gleich breit, zentriert; im Querformat entsteht eine schmalere Spalte |
| `Overlay` | Foto crop-to-fill, darüber ein eigenes PNG, dann das Textband |

- **Kisscam-Foto (Rahmen):** `Compose(…, PhotoFrame? frame)` (Überladung in `ILayoutComposer`, Standard ignoriert den Rahmen): Mit Rahmen füllt das erste Foto die **ganze Seite** (crop-to-fill, schwarzer Grund, unabhängig von der gewählten Layout-Art), darüber liegt nur der Jumbotron-Rahmen (`KisscamFrameRenderer`, 96-dpi-Pixelkoordinaten der Seite). **Keine** Bildunterschrift, kein Datum, kein Overlay-PNG, kein Streifen; Seitengröße und dpi bleiben. Der Durchgang ist immer ein Einzelfoto mit eigenem Dateipräfix (`Kisscam` bzw. der gewählte), Speichern und Druck laufen wie üblich (Druckschalter, Kopien). Die Originale (`SaveOriginals`) sind die ungerahmten, ggf. zugeschnittenen Kamerafotos. Der Rahmen reicht bis zum Seitenrand: Wird randlos mit Beschnitt gedruckt (Overscan, Schnitttoleranz), kann der äußere Rahmen dünner wirken; die 5-mm-Sicherheitszone von `LayoutMath` gilt hier nicht. Maße: `fun-herz-kisscam.md`.
- Aufnahmen je Durchgang: Streifen = `StripPhotoCount`, sonst 1.
- **Overlay:** Datei aus `OverlayDirectory` (nur `Path.GetFileName`), gecacht nach Pfad und Änderungsdatum. Fehlt sie oder ist nichts konfiguriert, entsteht die Seite ohne Overlay (Warnung im Log). Das PNG wird ohne Rücksicht auf sein Seitenverhältnis auf die ganze Seite gezogen (bekannte Grenze: Verzerrung).
- **Text:** Bildunterschrift bis **2 Zeilen**, erst danach schrittweise verkleinert (bis 40 %, `MinTextScale`) und mit „…“ gekürzt. Datum `dd.MM.yyyy` (`ShowDate`). Schrift wählbar (`CaptionFontFamily`, Standard Segoe UI, Rückfall Segoe UI). Bei winzigen Seiten (Fußbereich < 4 px) kein Text. `IsCaptionTruncated` rechnet für den Admin mit denselben Maßen.
- **`LayoutMath`:** `SafeBottomMm` = **5 mm** Sicherheitsabstand zur Unterkante gegen Overscan beim randlosen Druck und Schnitttoleranz (Text nie näher); `SafeSideMm` 3 mm; Bildunterschrift-Block 11 mm, Datum 4,5 mm, Textabstand 1 mm; Streifenrand 4 % der kurzen Streifenseite. `CropToFill` (zentrierter Zuschnitt), `ComputeStrips` (Doppelstreifen), `ComputeSingleStrip` (Einzelstreifen).
- `Compose` erzeugt mehrere Vollseiten-Puffer (ungemessene Performance-Option, siehe [bekannte-grenzen.md](bekannte-grenzen.md)).
- Gast-Layoutwahl per Mausrad: siehe [einstellungen.md](einstellungen.md).

## PhotoStorage

- Speichert Layout und (optional, `SaveOriginals`) die Originale als JPEG (`JpegQuality` 50–100, Standard 92). Alles wird zuerst als `.<guid>.tmp` geschrieben (Layout zuerst), dann unter `NamingGate` umbenannt; bei Fehler werden schon verschobene Dateien wieder gelöscht, Temporärdateien immer entfernt.
- **Ordner:** konfigurierter Ordner (`PhotoFolder`, optional Unterordner `yyyy-MM-dd`), sonst `AppInfo.DefaultPhotoFolder` = `Bilder\Fotobox+` (immer absolut; liefert Windows keinen Known Folder: `Benutzerprofil\Pictures`, sonst `LocalAppData\Fotobox\Fotos`). Ist der konfigurierte Ordner nicht beschreibbar oder nicht vorhanden, weicht `PhotoStorage` **einmal** auf den Standardordner aus; die Pfade im Ergebnis nennen den tatsächlich verwendeten Ordner. Alte Fotos bleiben in `Bilder\Fotobox` (kein Verschieben).
- **Namen:** `{Präfix}_{yyyyMMdd_HHmmss}.jpg` (Standardpräfix `Fotobox+`, kulturunabhängig), Originale `{Name}_orig{i}.jpg`. **Kollisionen:** Suffix `_2`, `_3` … bis Layout- und alle Original-Namen frei sind. Präfixlänge `FilePrefixMaxLength` 40.
- Speichern auf einer hängenden Netzfreigabe: Der Ausweichordner greift erst nach einer Exception; der Controller bricht nach 30 s ab (Fehler, kein Druck). Liegengebliebene `*.tmp` werden nie aufgeräumt.
