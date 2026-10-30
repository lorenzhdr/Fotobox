# Beschleunigung der KI-Erkennung (Windows ML)

Die vier ONNX-Modelle der Erkennung (Zielerfassung: YOLOX; Peace/Mittelfinger/Herz: YuNet, Palm, Hand-Landmark) laufen wahlweise auf der CPU oder – über **Windows ML** (`Microsoft.Windows.AI.MachineLearning`, bündelt ONNX Runtime und DirectML) – auf einer Grafikkarte (GPU) oder KI-Einheit (NPU). Fällt etwas aus, läuft die Erkennung auf der CPU weiter. Eine Umschaltung zur Laufzeit gibt es nicht: Einstellungen wirken nach einem Neustart der Fotobox. Der Admin-Tab „Leistung“ folgt in einem eigenen Schritt; die Dienste dafür stehen (siehe „API für die Oberfläche“).

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

1. `AcceleratorCatalog.DiscoverAsync`: `ExecutionProviderCatalog.GetDefault()?.RegisterCertifiedAsync()` (nur bereits vorhandene Ausführungsanbieter, **ohne Download**, normalerweise Millisekunden; Frist 10 s, danach `Cancel()`), dann `OrtEnv.GetEpDevices()`. Daraus entsteht die Geräteliste: die CPU immer, dazu GPUs und NPUs. Jeder Fehler oder Timeout ergibt nur die CPU plus Logwarnung.
2. Plan je Modell (`AccelerationPlanner.Decide`, reine Logik):
   - `Cpu` → alles CPU.
   - `Gpu` → Karte laut `GpuDeviceKey`, sonst die leistungsstärkste; fehlt sie → CPU + Hinweis.
   - `Npu` → erste NPU; fehlt sie → CPU + Hinweis.
   - `Auto` → siehe nächster Abschnitt.
3. Die Detektoren fragen ihren Plan über `IModelPlacement.GetDevice(model)`. Ist der Plan noch nicht fertig, warten sie auf dem Threadpool höchstens 3 s, sonst laden sie auf der CPU (und das erscheint im Status als Rückfall).

**Leistungsstärkste GPU** (`AcceleratorSelector.RankGpus`): diskrete vor integrierten, dann kleinerer `DxgiHighPerformanceIndex` (Windows-Ranking), dann mehr Videospeicher, dann der Hersteller-Ausführungsanbieter vor DirectML (gleicher Adapter; ein Hersteller-Gerät ohne eigene Adapter-Metadaten erbt sie vom DirectML-Gerät mit gleicher Hersteller-/Geräte-Id), dann der Schlüssel. Videospeicher kommt als Text mit Einheit („128 MB“) und wird in Megabyte gelesen (`AcceleratorCatalog.ParseMemoryMegabytes`).

## Modus Auto: Messung und Cache

- Cache-Datei `beschleunigung.json` im Konfigurationsordner (`ISettingsStore.ConfigDirectory`, also `%LOCALAPPDATA%\Fotobox`), atomar geschrieben (temporäre Datei + Ersetzen), fehlertolerant gelesen.
- **Fingerabdruck** (SHA-256, gekürzt): sortierte Geräteschlüssel + Anbieterversion, bereitgestellte Windows-ML-Ausführungsanbieter (Name + Paket-Id), App-Version, ONNX-Runtime-Version. Ändert sich etwas, gilt der Cache nicht mehr.
- **Treffer:** je Modell das gemessen schnellste Gerät, aber nur wenn es mindestens **15 %** schneller als die CPU ist (`AccelerationPlanner.MinimumSpeedup` = 1,15, d. h. Laufzeit ≤ CPU/1,15). Begründung: Die kleinen Modelle schwanken von Lauf zu Lauf um einige Prozent; für einen knappen Gewinn lohnen weder das Risiko eines Treiberfehlers noch die längere Ladezeit der Geräte-Session. Fehlt eine gültige CPU-Messung, zählt das schnellste gültige Gerät. Fehlgeschlagene Geräte zählen nicht.
- **Kein Treffer** (keine Datei, defekt, anderer Fingerabdruck): dieser Start läuft komplett auf der CPU. Nach 10 s startet im Hintergrund die Messung (`AcceleratorBenchmark`, Thread mit niedriger Priorität); das Ergebnis geht in den Cache und gilt ab dem nächsten Start (`AccelerationState.RestartRecommended`, wenn der neue Plan vom laufenden abweicht). Gibt es außer der CPU kein Gerät, wird nicht gemessen.
- **Messung** (`AcceleratorBenchmark`): je Gerät und Modell Session laden (Fabrik wie im Betrieb), ein Aufwärmlauf, dann 10 Läufe mit Nullpuffer in den echten Formen (YOLOX 416², YuNet [1,3,384,640], Palm 192², Landmark 224²). Ergebnis: Ladezeit, Ø Laufzeit, Fehler je Zeile. Frist 20 s je Gerät, 60 s gesamt; ein Gerät, das die Frist reißt, gilt als fehlgeschlagen (Zeile mit Grund), die Messung geht weiter. Ein hängender nativer Aufruf wird nicht abgewartet: der Thread läuft im Hintergrund aus, bricht zwischen zwei Läufen ab und gibt seine Session selbst frei (nie während eines `Run`). Die Messung läuft nicht im Threadpool, sondern auf eigenen Hintergrund-Threads (nur so ist die niedrige Priorität möglich); ONNX Runtimes eigene Rechen-Threads erben die Priorität nicht.
- Eine vom Admin ausgelöste Messung (`RunBenchmarkAsync`) ersetzt eine laufende Hintergrundmessung, aktualisiert den Cache und läuft auch in den Modi `Cpu`/`Gpu`/`Npu` (der Cache gilt dann, wenn man später auf `Auto` wechselt). Abgebrochene Messungen werden nicht gecacht.

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
| Messung je Gerät / gesamt | 20 s / 60 s | `BenchmarkPerDevice`, `BenchmarkTotal` |
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
| `Task<BenchmarkResult> RunBenchmarkAsync(IProgress<BenchmarkProgress>?, CancellationToken)` | Messung aller Geräte; Fortschritt von Hintergrund-Threads; Abbruch → `Cancelled`; wirft nie |
| `Task<ProviderInstallResult> InstallVendorProvidersAsync(IProgress<double>?, CancellationToken)` | Installation der Hersteller-Anbieter; Ergebnis mit Erfolg/Text |
| `IReadOnlyList<ProviderState> GetProviderStates()` | Anbieter des Katalogs (Name, Paket, `Ready`/`NotReady`/`NotPresent`); WinRT-Aufruf, nur auf dem Threadpool |
| `AccelerationState.Devices` | Geräteliste (`AcceleratorDevice`: `Key`, `Name`, `Kind`, `EpName`, `IsDiscrete`, `HighPerformanceIndex`, `VideoMemory` in MB, `Version`) |

Die Dienste tun beim Erzeugen nichts Natives (der DI-Test löst sie auf); Katalog, Registry und Messung starten erst in `App.OnStartup`.

## Log

Alle Zeilen beginnen mit `Beschleunigung:` bzw. `Grafikeinstellung:`. Startblock: `Modus …`, `Gerät <Name> (<Art>, <Anbieter>, …)`, `Ausführungsanbieter im Katalog: …`, `Messwerte aus dem Cache …` oder `Kein passender Messwert-Cache …`, `Plan Yolox=…, YuNet=…, Palm=…, Landmark=…`, `Hintergrundmessung startet in 10 s`. Messung: `Messung gestartet …`, je Gerät eine Zeile `Messung <Gerät>: Yolox 5,7 ms (Laden 377 ms); …`, `Messwerte gespeichert`. Rückfälle: `<Modell> konnte nicht auf „<Gerät>“ geladen werden (<Grund>) – Rückfall auf die CPU.` bzw. `… fehlgeschlagen (…)`. Gerätenamen erscheinen nur im Log zur Laufzeit, nie im Code, in Tests oder in der Doku.

## Tests

`AccelerationPlannerTests` (Schlüssel, Rangfolge, Modi, 15-%-Schwelle, Cache-Datei, Fingerabdruck), `AccelerationServiceTests` (Plan, Cache-Treffer/-Fehlschuss/-Defekt, Messung, Rückfallmeldung, späte Pläne, Installation, Einstellungsänderung, Dispose; Fake-Katalog und Fake-Messung), `GpuPreferenceRegistryTests` (Fake-Registry), `AcceleratedSessionTests` (CPU-Fabrik mit echten Modellen; Rückfallpfad über ein Gerät ohne ONNX-Runtime-Handle), `PerformanceSettingsTests`. Mit echter Hardware: `AccelerationHardwareTests` (`[Trait("Category","Hardware")]`).
