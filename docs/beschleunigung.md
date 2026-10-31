# Beschleunigung der KI-Erkennung (Windows ML)

Die vier ONNX-Modelle der Erkennung (Zielerfassung: YOLOX; Peace/Mittelfinger/Herz: YuNet, Palm, Hand-Landmark) laufen wahlweise auf der CPU oder – über **Windows ML** (`Microsoft.Windows.AI.MachineLearning`, bündelt ONNX Runtime und DirectML) – auf einer Grafikkarte (GPU) oder KI-Einheit (NPU). Fällt etwas aus, läuft die Erkennung auf der CPU weiter. Jede Messung und Prüfung eines Geräts läuft in einem **eigenen Prozess** (siehe „Messprozess“), damit ein Absturz im Grafiktreiber die Fotobox nicht mitreißt. Eine Umschaltung zur Laufzeit gibt es nicht: Einstellungen wirken nach einem Neustart der Fotobox. Bedient wird das im Admin-Tab „Leistung“ (siehe „Admin-Tab „Leistung““); die Dienste dafür stehen unter „API für die Oberfläche“.

Code: `src/Fotobox/Services/Detection/Acceleration/`.

## Einstellungen (`AppSettings.Performance`)

Alle Felder sind **gerätespezifisch** (`DeviceSpecificSettings`; nie exportiert, beim Import nicht überschrieben).

| Feld | Standard | Bedeutung |
|---|---|---|
| `Accelerator` (`AcceleratorMode`) | `Auto` | `Auto`: per Messung das schnellste Gerät je Modell. `Cpu`: alles auf dem Prozessor. `Gpu`: gewählte bzw. leistungsstärkste Grafikkarte. `Npu`: erste KI-Einheit. Fehlt das Gerät oder scheitert es, läuft die CPU (Hinweis im Status, Logzeile). Enum nur hinten erweitern. |
| `GpuDeviceKey` | null | Stabiler Schlüssel der gewählten Grafikkarte (`AcceleratorDevice.Key`); null = leistungsstärkste. Wirkt nur bei `Gpu`. ≤ `SettingsLimits.GpuDeviceKeyMaxLength` (200), sonst verworfen. |
| `PreferHighPerformanceGpu` | an | Setzt die Windows-Grafikeinstellung „Hohe Leistung“ für die Fotobox (siehe unten). |

**Geräteschlüssel:** `AcceleratorSelector.BuildKey` = Ausführungsanbieter + Hersteller-Id + Geräte-Id + Beschreibung, normiert (klein, nur einfache Zeichen). Bewusst ohne LUID und Adapterindex (ändern sich beim Neustart). Zwei völlig gleiche Karten bekommen `#2`, `#3`, … in der Reihenfolge der Adapternummer.

## Ablauf beim Start

`App.OnStartup` startet den `AccelerationService` **vor** der PIN-Abfrage im Hintergrund (`StartAsync`, Threadpool, blockiert nie). War die `settings.json` nicht lesbar (`ISettingsStore.LoadFailed`), geschieht das erst nach erfolgreicher PIN-Abfrage bzw. Wiederholung, damit nicht die Standardwerte statt der echten Einstellungen gelten:

1. `AcceleratorCatalog.DiscoverAsync`: `ExecutionProviderCatalog.GetDefault()?.RegisterCertifiedAsync()` (nur bereits vorhandene Ausführungsanbieter, **ohne Download**, normalerweise Millisekunden; Frist 10 s, danach `Cancel()`), dann `OrtEnv.GetEpDevices()`. Daraus entsteht die Geräteliste: die CPU immer, dazu GPUs und NPUs. **Software-Grafikadapter** (Windows „Microsoft Basic Render Driver“ = WARP, Hersteller-Id 0x1414 mit Geräte-Id 0x8C oder „Basic Render Driver“ in der Beschreibung; `AcceleratorSelector.IsSoftwareAdapter`) kommen nie in die Liste (Logzeile „Software-Grafikadapter ignoriert“): DirectML läuft darauf auf der CPU, ist langsam und stürzte auf einem CI-Runner ab. Alle Felder von Katalog und ONNX Runtime werden null-sicher gelesen (`FindAllProviders()` kann null liefern). Jeder Fehler oder Timeout ergibt nur die CPU plus Logwarnung.
2. Plan je Modell (`AccelerationPlanner.Decide`, reine Logik):
   - `Cpu` → alles CPU.
   - `Gpu` → Karte laut `GpuDeviceKey`, sonst die leistungsstärkste; fehlt sie → CPU + Hinweis.
   - `Npu` → erste NPU; fehlt sie → CPU + Hinweis.
   - Bei `Gpu`/`Npu` wird das Gerät im Hauptprozess nur geladen, wenn der Messwert-Cache (gleicher Fingerabdruck) das jeweilige Modell darauf **erfolgreich** (im Messprozess) ausweist; siehe „Modus Gpu/Npu: erst prüfen“. Eine Mindestgeschwindigkeit gibt es dort nicht (ausdrücklich gewählt).
   - `Auto` → siehe nächster Abschnitt.
3. Die Detektoren fragen ihren Plan über `IModelPlacement.GetDevice(model)`. Ist der Plan noch nicht fertig, warten sie auf dem Threadpool höchstens 3 s, sonst laden sie auf der CPU (und das erscheint im Status als Rückfall).

**Leistungsstärkste GPU** (`AcceleratorSelector.RankGpus`): diskrete vor integrierten, dann kleinerer `DxgiHighPerformanceIndex` (Windows-Ranking), dann mehr Videospeicher, dann der Hersteller-Ausführungsanbieter vor DirectML (gleicher Adapter; ein Hersteller-Gerät ohne eigene Adapter-Metadaten erbt sie vom DirectML-Gerät mit gleicher Hersteller-/Geräte-Id), dann der Schlüssel. Videospeicher kommt als Text mit Einheit („128 MB“) und wird in Megabyte gelesen (`AcceleratorCatalog.ParseMemoryMegabytes`).

## Modus Auto: Messung und Cache

- Cache-Datei `beschleunigung.json` im Konfigurationsordner (`ISettingsStore.ConfigDirectory`, also `%LOCALAPPDATA%\Fotobox`), atomar geschrieben (temporäre Datei + Ersetzen), fehlertolerant gelesen.
- **Fingerabdruck** (SHA-256, gekürzt): sortierte Geräteschlüssel + Anbieterversion, bereitgestellte Windows-ML-Ausführungsanbieter (Name + Paket-Id), App-Version, ONNX-Runtime-Version. Ändert sich etwas, gilt der Cache nicht mehr.
- **Treffer:** je Modell das gemessen schnellste Gerät, aber nur wenn es mindestens **15 %** schneller als die CPU ist (`AccelerationPlanner.MinimumSpeedup` = 1,15, d. h. Laufzeit ≤ CPU/1,15). Begründung: Die kleinen Modelle schwanken von Lauf zu Lauf um einige Prozent; für einen knappen Gewinn lohnen weder das Risiko eines Treiberfehlers noch die längere Ladezeit der Geräte-Session. Fehlt eine gültige CPU-Messung, zählt das schnellste gültige Gerät. Fehlgeschlagene Geräte zählen nicht.
- **Kein Treffer** (keine Datei, defekt, anderer Fingerabdruck, Cache-Format < 2): dieser Start läuft komplett auf der CPU. Nach 10 s startet im Hintergrund die Messung (`OutOfProcessBenchmark`, Messprozess mit niedriger Priorität); das Ergebnis geht in den Cache und gilt ab dem nächsten Start (`AccelerationState.RestartRecommended`, wenn der neue Plan vom laufenden abweicht). Gibt es außer der CPU kein Gerät, wird nicht gemessen. Der Cache trägt das Format 2: ältere, noch im Hauptprozess gemessene Dateien gelten nicht als „im Messprozess geprüft“.
- **Messung** (`AcceleratorBenchmark`, läuft im Messprozess): je Gerät und Modell Session laden (Fabrik wie im Betrieb), ein Aufwärmlauf, dann 10 Läufe mit Nullpuffer in den echten Formen (YOLOX 416², YuNet [1,3,384,640], Palm 192², Landmark 224²). Ergebnis: Ladezeit, Ø Laufzeit, Fehler je Zeile. Frist 20 s je Gerät, 60 s gesamt; ein Gerät, das die Frist reißt, gilt als fehlgeschlagen (Zeile mit Grund), die Messung geht weiter. Ein hängender nativer Aufruf wird nicht abgewartet: der Thread läuft im Hintergrund aus, bricht zwischen zwei Läufen ab und gibt seine Session selbst frei (nie während eines `Run`). Die Messung läuft nicht im Threadpool, sondern auf eigenen Hintergrund-Threads; die niedrige Priorität gilt für den ganzen Messprozess (`PriorityClass` BelowNormal), also auch für die Rechen-Threads von ONNX Runtime.
- Eine vom Admin ausgelöste Messung (`RunBenchmarkAsync`) ersetzt eine laufende Hintergrundmessung, aktualisiert den Cache und läuft auch in den Modi `Cpu`/`Gpu`/`Npu` (der Cache gilt dann bei `Gpu`/`Npu` als Prüfung des Geräts und, wenn man später auf `Auto` wechselt, für die Zuordnung). Abgebrochene Messungen werden nicht gecacht.

## Messprozess (Absturzschutz)

Ein Absturz im Grafiktreiber oder in DirectML (nativ, z. B. Exit-Code 0xC0000409) ist in .NET nicht abfangbar. Deshalb misst `OutOfProcessBenchmark` (die `IAcceleratorBenchmark`-Umsetzung im Betrieb) **alle** Geräte, auch die CPU (derselbe Code und dieselben Optionen, bessere Vergleichbarkeit), in einem getrennten Prozess und wertet nur dessen Meldungen aus. Das gilt für die Hintergrundmessung und für „Jetzt messen“.

- **Start:** dieselbe `Fotobox.exe` (`Environment.ProcessPath`) mit `--beschleunigung-messen <Auftragsdatei>`, `UseShellExecute=false`, ohne Fenster, Ausgabe umgeleitet (stdout zeilenweise gelesen, stderr leergelesen und bei einem Absturz mit angehängt). `Program.Main` (Startobjekt) zweigt den Schalter ab, **bevor** `App` entsteht: kein Fenster, keine Einstellungen/PIN/Kamera/Registry, keine Einzelinstanz-Sperre, kein Protokollschreiben (`AppLog.Sink` leitet die Logzeilen als Nachrichten an den Hauptprozess, der sie mit „Beschleunigung (Messprozess): …“ ins Log schreibt; nie zwei Prozesse in einer Logdatei). Im Prozess: `SetErrorMode` (kein Windows-Absturzdialog auf dem Gäste-Bildschirm), niedrige Priorität, Selbstbegrenzung (`MaxRuntime` 120 s, danach `Environment.Exit`).
- **Auftrag** (JSON im Temp-Ordner, vom Hauptprozess danach gelöscht): Geräteschlüssel in Messreihenfolge (CPU zuerst), Ergebnisdatei, Priorität. Der Messprozess ermittelt die Geräte selbst (`AcceleratorCatalog.DiscoverAsync`, gleiche Schlüssel); ein unbekannter Schlüssel ergibt Einträge „Gerät im Messprozess nicht gefunden“.
- **Ausgabe** (JSON je Zeile, `MeasureMessage`): `start` (Gerät beginnt), `progress`, `entry` (ein Messwert), `deviceDone`, `log`, `done`. Am Ende schreibt er `ergebnis.json` atomar (temporäre Datei + Ersetzen); maßgeblich sind die gestreamten Einträge. Ein Gerät zählt erst bei `deviceDone`; Einträge eines Geräts, auf dem der Prozess abstürzt, werden verworfen.
- **Absturz oder Ende ohne `done`:** Das Gerät, das gerade lief (`start` ohne `deviceDone`), gilt mit allen Modellen als fehlgeschlagen („Messprozess abgestürzt (Exit-Code 0x…)“) und ist im Plan nie schneller als die CPU; fertige Geräte bleiben, die übrigen misst ein **neuer Messprozess** (höchstens so viele Neustarts wie Geräte, Gesamtfrist gilt). Stürzt der Prozess vor dem ersten Gerät ab (Start, Geräteliste), gelten alle offenen Geräte als fehlgeschlagen (kein Neustart-Kreis). Meldet er `done` und endet dann mit Exit ≠ 0, werden die Werte übernommen (Logwarnung).
- **Fristen:** je Gerät 20 s (im Messprozess) plus Wachhund im Hauptprozess (20 s + 10 s Reserve ohne Meldung, bis zum ersten Gerät 30 s, zwischen zwei Geräten 10 s): danach Kill, das Gerät gilt als Zeitüberschreitung, der Rest wird neu gestartet; gesamt 60 s (`BenchmarkTotal`, danach Kill, der Rest „Nicht gemessen“); nach `done` höchstens 3 s Wartezeit auf das Prozessende, dann Kill. Kill immer mit `entireProcessTree`. Abbruch (Admin „Abbrechen“, Beenden) beendet den Messprozess.
- **Keine Waisen:** Jeder Messprozess wird einem Windows-Job-Objekt mit „beim Schließen alle beenden“ zugeordnet (`MeasureJobObject`, Handle lebt bis zum Prozessende). Verschwindet die Fotobox (Absturz, Smoke-Test beendet sie hart), beendet Windows auch den Messprozess; er sperrt keine Dateien des Programmordners. Scheitert das Job-Objekt, bleibt nur das Beenden durch den Hauptprozess und die Selbstbegrenzung.
- **Test-Schalter:** Die Umgebungsvariable `FOTOBOX_MESSPROZESS_TESTABSTURZ` (Komma-getrennte Teilstrings von Geräteschlüsseln, z. B. `cpu` oder `dmlexecutionprovider`) lässt den Messprozess vor dem passenden Gerät per `Environment.FailFast` abstürzen. Sie wirkt nur im Messprozess und dient dem Test des Absturzpfads.

## Modus Gpu/Npu: erst prüfen

Eine ausdrücklich gewählte Grafikkarte/NPU wird im Hauptprozess erst geladen, wenn sie im Messprozess bestanden hat:

- **Nie gemessen** (kein Eintrag im Cache zum Gerät): dieser Start läuft auf der CPU, im Hintergrund prüft der Messprozess (wie der Auto-Cache-Fehlschuss, nach 10 s). Hinweis: „Die Grafikkarte wird zuerst geprüft und ab dem nächsten Start genutzt.“ (bei der NPU „Die KI-Einheit (NPU) …“). Nach der Messung `RestartRecommended` und der Hinweis „Die Prüfung ist abgeschlossen – ab dem nächsten Start nutzt die Erkennung „<Gerät>“.“ (Admin: „Neue Messwerte liegen vor – sie wirken nach einem Neustart der Fotobox.“); bei einem Fehlschlag steht stattdessen der Misserfolgs-Hinweis.
- **Bestanden** (alle Modelle erfolgreich): das Gerät wird geladen, ohne Hinweis.
- **Gescheitert oder abgestürzt:** die betroffenen Modelle laufen auf der CPU, der Grund steht als Rückfall im Status (`AccelerationState.Fallbacks`), Hinweis „… hat die Prüfung nicht bestanden – … Mit „Jetzt messen“ können Sie die Prüfung wiederholen.“ Es wird nicht bei jedem Start neu gemessen (ein abstürzender Treiber würde sonst jedes Mal getestet).
- Der Rückfall zur Laufzeit (`AcceleratedSession`) bleibt als zweites Netz bestehen.

## Rückfälle (`AcceleratedSession`, `IModelPlacement.ReportFallback`)

`OnnxSessionFactory` ist die **einzige** Stelle, die `SessionOptions` und `InferenceSession` baut (CPU-Optionen unverändert: 2 intra/1 inter Threads, `ORT_ENABLE_ALL`, kein Spinning; Gerät zusätzlich mit angehängtem Ausführungsanbieter, bei DirectML `ORT_SEQUENTIAL` und ohne Memory-Pattern). Alle drei Detektoren laden über `AcceleratedSession`:

- **Beim Laden:** Scheitert Session-Erstellung oder Aufwärmlauf auf dem Gerät (Ausnahme, oder Frist 10 s je Detektor für alle seine Modelle), wird einmal auf der CPU nachgeladen. Der abgebrochene Ladeversuch gibt seine Session nach seinem Ende selbst frei. Logzeile mit Grund, Meldung an den `AccelerationService`.
- **Zur Laufzeit:** Wirft ein `Run` auf dem Gerät eine `OnnxRuntimeException` (z. B. Grafiktreiber zurückgesetzt), wird einmal auf eine CPU-Session gewechselt und der Aufruf wiederholt; die alte Session wird innerhalb der Sperre `_runLock` freigegeben (dort läuft kein anderer `Run`). Dekodierfehler und Abbrüche lösen keinen Rückfall aus. Das zählt **nicht** zu den `FailureLimit`-Fehlschlägen von Peace/Zielerfassung. Danach bleibt das Modell bis zum Neustart auf der CPU. Hinweis: Der Wechsel (CPU-Session bauen, Lauf wiederholen) geschieht innerhalb der 2-s-Analysefrist; die CPU-Sessions sind in unter einer Sekunde da.
- Jedes Modell fällt einzeln zurück (z. B. Palm auf der GPU, Landmark auf der CPU).

## Fristen

| Bereich | Frist | Konstante |
|---|---|---|
| Katalog registrieren + Geräteliste | 10 s (äußere Frist 12 s) | `AccelerationTimeouts.Discovery` |
| Detektor wartet auf den Plan | 3 s | `PlanWait` |
| Laden auf dem Gerät (Session + Aufwärmlauf), je Detektor | 10 s | `DeviceLoad` |
| Laden auf der CPU | 15 s | `CpuLoad` |
| Detektor gesamt (Plan + Gerät + CPU-Rückfall) | 28 s | `DetectorInit`, `…Detector.InitTimeout` |
| Erkennungsdienst wartet auf einen Detektor | 35 s | `ServiceInit`, `PersonDetectionService.InitTimeout`, `PeaceDetectionService.InitTimeout` |
| Messung je Gerät / gesamt | 20 s / 60 s (Messprozess, danach Kill) | `BenchmarkPerDevice`, `BenchmarkTotal` |
| Wachhund des Messprozesses | bis zum ersten Gerät 30 s, je Gerät 20 s + 10 s, zwischen Geräten 10 s, nach „fertig“ 3 s | `BenchmarkChildStartup`, `…DeviceReserve`, `…BetweenDevices`, `…Exit` |
| Hintergrundmessung startet nach | 10 s | `BackgroundBenchmarkDelay` |
| Installation der Hersteller-Anbieter | 10 min | `Install` |

Die Dienstfrist muss größer als die Detektorfrist sein, sonst schaltet `PersonDetectionService` bei einem langsamen Gerät-Laden dauerhaft auf die Gesichtserkennung um (ein Test prüft die Beziehung).

## Windows-Grafikeinstellung „Hohe Leistung“

`GpuPreferenceRegistry` (Registry-Zugriff hinter `IGpuPreferenceStore`, testbar) schreibt beim Start gemäß `PreferHighPerformanceGpu` unter `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` den Wert `GpuPreference=2;` mit dem vollen Pfad der laufenden `Fotobox.exe` als Namen (bei Hybridgrafik nutzt Windows dann die starke Karte). Ausgeschaltet entfernt sie **nur** genau diesen Wert; ein anderer, vom Benutzer gesetzter Wert wird nie überschrieben oder entfernt (Logzeile). Fehler (Registry, fehlender Pfad) werden nur geloggt. Auch bei `SettingsChanged` angewendet; Windows liest den Wert erst beim nächsten Programmstart. Der Pfad steht nie im Log. Hinweis: Lokale Tests des veröffentlichten Ordners (`smoke-test.ps1`) setzen diesen Wert für die dortige `Fotobox.exe` im echten Benutzer-Hive (der Smoke-Test biegt nur Profil-Variablen um, nicht die Registry).

## Hersteller-Ausführungsanbieter (Internet nötig)

`IAcceleratorCatalog.InstallVendorProvidersAsync` ruft `EnsureAndRegisterCertifiedAsync` auf (Download, Installation, Registrierung; Fortschritt 0–1, Abbruch über `Cancel()`, Frist 10 min). Das braucht Internet und liefert je nach Gerät zusätzliche Anbieter (z. B. für NPUs und Grafikkarten bestimmter Hersteller); diese Hersteller-Ausführungsanbieter setzen Windows 11 24H2 oder neuer voraus. Nach erfolgreicher Installation gilt ein Neustart als nötig (`AccelerationState.RestartRequired`), neue Geräte erscheinen in der Liste, ein geänderter Fingerabdruck löst im Modus Auto eine neue Messung aus. DirectML (GPU) funktioniert ohne Installation und ohne Internet.

## API für die Oberfläche

`IAccelerationService` (Singleton, in DI auch als `IModelPlacement`):

| Mitglied | Bedeutung |
|---|---|
| `AccelerationState State` | unveränderliche Momentaufnahme: `Mode`, `PlanReady`, `Plan` (je Modell `ModelPlacement`: Gerätename, Art, Schlüssel, `FellBack`), `Fallbacks` (Modell, Gerät, Grund, Laufzeit?, Zeit), `Devices`, `BenchmarkRunning`/`BenchmarkIsBackground`, `LastBenchmark`, `Notice`, `PlanFromCache`, `RestartRequired`, `RestartRecommended` |
| `event EventHandler? StateChanged` | bei jeder Änderung; **kann von Hintergrund-Threads kommen** (die Oberfläche wechselt selbst auf den UI-Thread); Ausnahmen der Beobachter werden geschluckt |
| `Task StartAsync()` | einmalig (idempotent), wirft nie |
| `Task<BenchmarkResult> RunBenchmarkAsync(IProgress<BenchmarkProgress>?, CancellationToken)` | Messung aller Geräte (im Messprozess); Fortschritt von Hintergrund-Threads; Abbruch → `Cancelled`; wirft nie |
| `Task<ProviderInstallResult> InstallVendorProvidersAsync(IProgress<double>?, CancellationToken)` | Installation der Hersteller-Anbieter; Ergebnis mit Erfolg/Text |
| `IReadOnlyList<ProviderState> GetProviderStates()` | Anbieter des Katalogs (Name, Paket, `Ready`/`NotReady`/`NotPresent`); WinRT-Aufruf, nur auf dem Threadpool |
| `AccelerationState.Devices` | Geräteliste (`AcceleratorDevice`: `Key`, `Name`, `Kind`, `EpName`, `IsDiscrete`, `HighPerformanceIndex`, `VideoMemory` in MB, `Version`) |

Die Dienste tun beim Erzeugen nichts Natives (der DI-Test löst sie auf); Katalog, Registry und Messung starten erst in `App.OnStartup`.

## Admin-Tab „Leistung“

Code: `AdminWindow.xaml` (Tab vor „Sicherheit & Daten“, Icon `&#xEC4A;`, Stil `ChoiceRadio` in `AdminTheme.xaml`), `AdminViewModel.Performance.cs`, Anzeigetexte in `AccelerationDisplay.cs`, Erklärtext `AdminInfoTexts.Acceleration`. Der `AdminViewModel` bekommt den `IAccelerationService` als letzten, optionalen Konstruktorparameter (`AdminLauncher` reicht ihn aus dem Container durch; ohne Dienst, etwa in Tests, zeigt der Tab „nicht verfügbar“, die Auswahl bleibt im Entwurf bedienbar). Alle Werte lesen und schreiben direkt `_draft.Performance` (der Abschnitt wird von „Standardwerte“ ersetzt, deshalb nie als Referenz gemerkt; `RebuildAfterDraftReplaced` ruft `RebuildPerformanceFromDraft`).

Fünf Karten (die fünfte, „Leistungsanzeige“, steht im Abschnitt „Leistungsanzeige“ weiter unten):

- **Hardwarebeschleunigung** (mit ⓘ): Wahlzeilen „Automatisch (empfohlen)“, „Nur Prozessor (CPU)“, „Grafikkarte (GPU)“ (darunter die Auswahl „Leistungsstärkste Grafikkarte“ = `GpuDeviceKey` null plus alle GPUs, leistungsstärkste zuerst, mit „dediziert/integriert“, Videospeicher und Anbieter), „KI-Beschleuniger (NPU)“ (ausgegraut mit Erklärung, wenn die Geräteliste keine NPU enthält). Ein gespeicherter `GpuDeviceKey`, der nicht in der Liste steht, bleibt als „Nicht gefunden: gespeicherte Karte“ gewählt und wird nie überschrieben (vor dem fertigen Plan neutral „Gespeicherte Grafikkarte“, weil dann nur die CPU bekannt ist; dasselbe gilt für die NPU-Erklärung). Darunter der **Betriebsstand**: vor `PlanReady` „Wird ermittelt …“, danach je Modell eine Zeile („Personenerkennung (Zielerfassung)“, „Gesichtserkennung“, „Handflächen“, „Handpunkte“: Gerät und Art), Rückfälle orange mit Grund (beim Laden bzw. während des Betriebs), der `Notice` des Dienstes und die Herkunft der Zuordnung („letzte Messung“, „Hintergrundmessung läuft“, „noch nicht gemessen“, nur im Modus Auto; bei Grafikkarte/NPU „Das gewählte Gerät wird im Hintergrund geprüft …“).
- **Messung:** „Jetzt messen“ (`RunBenchmarkAsync`, Fortschrittsbalken mit „Messe <Gerät> (n/m)“, „Abbrechen“), danach die Tabelle (Zeilen = Modelle, Spalten = Geräte aus der Messung, „x,x ms“ immer deutsch formatiert, schnellster Wert je Zeile hervorgehoben, aber nur bei mindestens zwei gültigen Werten; Fehler als „–“ mit Grund im Tooltip). Beim Öffnen steht `State.LastBenchmark` schon da (der Dienst füllt es beim Cache-Treffer).
- **Zusätzliche Beschleuniger:** Erklärtext (Internet, mehrere Hundert MB, Windows 11 24H2 für Hersteller-Beschleuniger, vorab erledigen), Anbieterliste aus `GetProviderStates()` (nur per `Task.Run`, Frist 5 s `ProviderStatesTimeout`, Fehler/Frist → „Status unbekannt.“), „Beschleuniger installieren“ (`InstallVendorProvidersAsync`, Fortschritt in Prozent, „Abbrechen“, Ergebnistext des Dienstes; danach Anbieterliste neu).
- **Windows-Grafikeinstellung:** Schalter `PreferHighPerformanceGpu`; Text: setzt „Hohe Leistung“ in den Windows-Grafikeinstellungen, betrifft Darstellung und Kamera, wirkt ab dem nächsten Start, ein von Hand gesetzter anderer Wert bleibt unangetastet.

**Neustart-Hinweis** (`PerformanceRestartHint`, orange unter der Auswahl): „Änderungen wirken nach einem Neustart der Fotobox.“ erscheint, sobald der Entwurf vom laufenden Stand abweicht – `Accelerator` ≠ `State.Mode` (erst ab `PlanReady`; vorher ist `State.Mode` ein Initialwert), `Accelerator` ≠ gespeichertem Modus, bei Wahl GPU ein anderer Schlüssel als gespeichert – oder der Dienst `RestartRequired` meldet (gespeicherte Änderung, Installation). `RestartRequired` vergleicht die **gespeicherten** Einstellungen (`SettingsChanged`), nicht den Entwurf; deshalb rechnet die Oberfläche den Entwurf selbst dazu. Nach einer Installation steht „Neue Beschleuniger wurden installiert – sie werden nach einem Neustart der Fotobox genutzt.“, im Modus Auto, Grafikkarte und NPU bei `RestartRecommended` „Neue Messwerte liegen vor – sie wirken nach einem Neustart der Fotobox.“ `PreferHighPerformanceGpu` löst keinen Hinweis aus (eigener Text in seiner Karte).

**Threading und Schließen:** Der Handler von `StateChanged` (beliebiger Thread, auch aus dem Run-Lock eines Detektors) tut nur `SynchronizationContext.Post` auf den UI-Kontext des Ansichtsmodells und liest `State` erst dort; Fortschritt (`UiProgress<T>`) wird ebenso gepostet. Ohne UI-Kontext (Tests) läuft die Aktion direkt. Nach `Dispose` wird nichts mehr angefasst. `Dispose` meldet den Handler ab (`-=`, ein .NET-Ereignis) und bricht eine laufende Messung und Installation ab (verknüpft mit `_lifetime`); der Dienst wirft nie und es wird nie gewartet. Messung und Installation sperren sich gegenseitig (auch gegen die Hintergrundmessung bei der Installation; „Jetzt messen“ ersetzt dagegen eine laufende Hintergrundmessung).

## Leistungsanzeige (seit 2.10.1)

Einblendung oben rechts in der Gastansicht (`Views/Booth/PerformanceOverlay.xaml`, `PerformanceOverlayViewModel`, Zeilenaufbau rein in `PerformanceOverlayRows`). Einstellungen: `AppSettings.PerformanceOverlay` (übertragbar, Felder `einstellungen.md`); Taste `Input.PerformanceOverlayKeys` (Standard `Ctrl+Alt+Y`, Vorrang nach allen anderen, nur Gastansicht, jeder Zustand, siehe `architektur.md`). Ein/Aus wird nie gespeichert, nach dem Start ist die Anzeige aus. Im Log nur „Leistungsanzeige ein“ bzw. „aus“ (keine Messwerte).

- **Darstellung:** Im Fenster der Gastansicht, außerhalb des Zoom-Hosts, über allem außer dem Blitz, ohne Eingaben (`IsHitTestVisible=False`) und nie in Foto oder Video. Zeilen in fester Reihenfolge (CPU, CPU-Takt, CPU-Temperatur, GPU, GPU-Takt, GPU-Temperatur, GPU-Speicher, NPU, RAM, Kamera, Anzeige, Zielerfassung/Gesten als Analysen/s und Rechenzeit, Gerät je Modell); Darstellung Text oder Balken (Balken nur bei Auslastung und Speicher); Warnfarben gelb ab 70 %, rot ab 90 %. „–“ = noch kein Wert oder Funktion inaktiv, „nicht verfügbar“ = die Messung lief, das Gerät liefert den Wert nicht.
- **Messung (`ISystemMetricsSource`, `WindowsSystemMetricsSource`):** nur bei sichtbarer Anzeige, ohne Treiber und ohne Zusatzpaket: Auslastung, CPU-Takt und -Temperatur, GPU-Auslastung und -Speicher über Leistungsindikatoren (PDH, englische Zählernamen, `PdhNative`); GPU-Name, -Takt und -Temperatur über D3DKMT (`gdi32.dll`, `D3dkmtNative`); RAM über `GlobalMemoryStatusEx`. Jede Teilquelle ist einzeln abgesichert (Fehler → nur ihre Werte fehlen, höchstens eine Logwarnung je Quelle, ohne Messwerte). Die Raten brauchen zwei Messungen; die erste Messung zeigt „–“. Je Takt (0,5–5 s) baut ein UI-Timer die Zeilen aus dem zuletzt gemessenen Stand und stößt höchstens eine neue Messung auf dem Threadpool an; Frist 3 s (`SampleTimeout`), ein hängender Lauf wird verworfen, ein zweiter startet erst nach dessen Ende. Die native Abfrage hängt am `DeferredReleaseGate` (nie während einer Messung freigegeben).
- **App-Werte (passiv, keine eigene Erkennungs-Anforderung):** Kamera-FPS aus `ICameraFrameStats.FramesReceived` (Differenz je Takt), Anzeige-FPS aus `DisplayFrameCounter` (zählt die tatsächlich gezeigten Livebilder der Vorschau), Rate und Rechenzeit der Zielerfassung und Gesten aus deren Status (älter als 5 s = `StatusStaleAfter` → „–“), Gerät je Modell aus dem Plan des `IAccelerationService` (Rückfall als „CPU (Rückfall)“; läuft die Zielerfassung auf dem Windows-Gesichtsdetektor, steht dort „Windows-Gesichtserkennung“).
- **GPU-Zuordnung (`SystemMetricsLogic.SelectGpu`):** Welche Grafikkarte gemessen wird, bestimmt der Plan (Gerät des ersten Modells auf einer Grafikkarte, `SelectionFromPlan`). Software-Adapter (WARP) und reine Rechen-Adapter (NPUs) kommen nie in Frage. Drei Stufen: 1. gleiche Hersteller-/Geräte-Id (bei mehreren gleichen der mit gleichem Namen), 2. sonst Adapter mit passendem Namen (normalisiert, einer enthält den anderen; nötig, weil ONNX Runtime und D3DKMT auf manchen Geräten verschiedene Id-Räume melden), 3. sonst – wie ohne Plan – die erste echte Grafikkarte mit Render und Anzeige. Die NPU ist der erste Rechen-Adapter, der kein Software-Adapter ist.
- **GPU-Speicher:** Mit eigenem Videospeicher (dediziert ≥ 512 MiB, diskrete Karte) „Dedicated Usage“ von der dedizierten Größe, sonst (integrierte Grafik) Dedicated + Shared von lokalem + nicht lokalem Speicher.
- **Grenzen:** `bekannte-grenzen.md`.

## Log

Alle Zeilen beginnen mit `Beschleunigung:` bzw. `Grafikeinstellung:`. Startblock: `Modus …`, `Gerät <Name> (<Art>, <Anbieter>, …)`, `Ausführungsanbieter im Katalog: …`, `Messwerte aus dem Cache …` oder `Kein passender Messwert-Cache …`, `Plan Yolox=…, YuNet=…, Palm=…, Landmark=…`, `Hintergrundmessung startet in 10 s`. Messung: `Messung gestartet …`, `Messprozess gestartet (PID …)`, Zeilen des Messprozesses als `Beschleunigung (Messprozess): …`, `Messprozess beendet …` bzw. `… ist abgestürzt (Exit-Code 0x…)`, je Gerät eine Zeile `Messung <Gerät>: Yolox 5,7 ms (Laden 377 ms); …`, `Messwerte gespeichert`. Rückfälle: `<Modell> konnte nicht auf „<Gerät>“ geladen werden (<Grund>) – Rückfall auf die CPU.` bzw. `… fehlgeschlagen (…)`. Gerätenamen erscheinen nur im Log zur Laufzeit, nie im Code, in Tests oder in der Doku.

## Tests

Tests der Leistungsanzeige: `SystemMetricsTests`, `SystemMetricsGpuSelectionTests`, `PerformanceOverlayRowsTests`, `PerformanceOverlayViewModelTests`, `BoothViewModelPerformanceOverlayTests`, `AdminPerformanceOverlayTests` (siehe `tests.md`).

`AccelerationPlannerTests` (Schlüssel, Rangfolge, Modi, 15-%-Schwelle, Cache-Datei, Fingerabdruck), `AccelerationServiceTests` (Plan, Cache-Treffer/-Fehlschuss/-Defekt, Messung, Rückfallmeldung, späte Pläne, Installation, Einstellungsänderung, Dispose; Fake-Katalog und Fake-Messung), `GpuPreferenceRegistryTests` (Fake-Registry), `AcceleratedSessionTests` (CPU-Fabrik mit echten Modellen; Rückfallpfad über ein Gerät ohne ONNX-Runtime-Handle), `PerformanceSettingsTests`, `AdminPerformanceTests` (Ansichtsmodell des Tabs mit `FakeAccelerationService`: Auswahl, GPU-Liste, NPU, Betriebsstand, Neustart-Hinweis, Messung, Installation, Threading, Schließen), `AdminPerformanceViewTests` (Tab, Layout in beiden Farbschemata, Tabelle). `OutOfProcessBenchmarkTests` (Hauptprozess mit skriptbarem Fake-Messprozess: Ablauf, Absturz → Gerät fehlgeschlagen und Rest im neuen Prozess, Wachhund, Gesamtfrist, Abbruch, Startfehler, Neustart-Grenze), `MeasurementProcessIntegrationTests` (echte `Fotobox.exe` als Messprozess: CPU messen; Absturz per Test-Schalter ohne GPU), `AccelerationRobustnessTests` (Software-Adapter, null-sichere Anbieterliste). Mit echter Hardware: `AccelerationHardwareTests` (`[Trait("Category","Hardware")]`).
