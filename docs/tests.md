# Tests

Stand: 2.5.1. Pfade relativ zum Repo-Root. Build und CI: [build-release.md](build-release.md).

## Überblick

- Projekt `tests/Fotobox.Tests`, **xUnit** 2.9 mit `Xunit.StaFact` (WPF/STA: `[StaFact]`, `[StaTheory]`) und `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`).
- Aufruf: `dotnet test --filter "Category!=Hardware"`. Aktuell **3097** Tests ohne Hardware (Ergebnis von `dotnet test --filter "Category!=Hardware"` auf dem Branch `feature/herz-kisscam`; die Zahl wächst, „über 3000“ genügt als Größenordnung).
- Laufzeit ca. **55 s**. Kritischer Pfad ist die serielle WPF-Collection (s. u.); `GuestHintStyleTests` brauchen ca. 25 s wegen fester Wartezeiten (`Thread.Sleep(40)` in `Settle`, damit WPF-Animationsuhren ticken).

## Kategorien (`[Trait("Category", …)]`)

| Kategorie | Bedeutung | CI |
|---|---|---|
| `Hardware` | echte Kamera/echter Drucker/echte Audioausgabe (z. B. Kamera-Neustart, Reglertests in `CameraTests`, `VideoRecordingHardwareTests`, Peace-/Detection-Kamera-Tests, ein Wiedergabetest in `MusicPlayerTests`) | läuft **nicht** (`Category!=Hardware`), nur lokal |
| `Visual` | rendert PNGs zur Sichtprüfung | läuft mit, ist aber ein No-op ohne Umgebungsvariable |

Tests mit echter Kamera/echtem Drucker immer als `Hardware` markieren.

### Umgebungsvariablen

| Variable | Wirkung |
|---|---|
| `FOTOBOX_RENDER_DIR` | Zielordner für gerenderte PNGs (`HintVisualRenderTests`, `FunVisualRenderTests`, `PeaceVisualRenderTests`, `AdminPeaceVisualRenderTests`, `AdminVisualRenderTests`, `AdminCameraControlsTests`, `KisscamVisualRenderTests` (Rahmen in allen Farben, Layout mit Rahmen) …); ohne sie No-op. `RenderTargetBitmap` zeichnet keine `DropShadowEffect`s (Schatten fehlen in den PNGs) |
| `FOTOBOX_DETECTION_IMAGE` | Bild für `DetectionVisualTests` (Zielerfassung); im Repo liegt bewusst kein Personenfoto |
| `FOTOBOX_PEACE_IMAGE` | Bild für `DetectionPeaceVisualTests` |
| `FOTOBOX_DETECTION_EXPECTED` | erwartete Personenzahl dort |
| `FOTOBOX_SAMPLE_DIR` | Ausgabeordner für Beispielseiten in `LayoutComposerTests` |

## WPF-Tests und die Collection-Pflicht

Alle Testklassen, die WPF-Fenster, XAML oder Rendering nutzen, tragen `[Collection(WpfCollection.Name)]` (`tests/Fotobox.Tests/WpfCollection.cs`) und laufen dadurch **nacheinander**. **Neue WPF-Testklassen brauchen dieses Attribut.**

Grund: Parallel laufende StaFact-Collections laden gleichzeitig BAML und deadlocken an der prozessweiten `WpfSharedBamlSchemaContext`-Sperre; der Testhost blieb sporadisch (etwa jeder 12. Lauf, auch in der CI) für immer hängen. Im echten Programm gibt es nur einen UI-Thread, dort tritt es nicht auf. Die Workflows brechen Hänger zusätzlich per `--blame-hang-timeout 5m` mit Testnamen ab.

`WpfCollection.EnsurePackUris()` registriert das `pack://`-Schema (statischer Konstruktor von `System.Windows.Application`), ohne eine `Application` anzulegen. Sonst scheitert ein Pack-URI mit „The URI prefix is not recognized“, wenn der Test **allein** läuft und noch kein anderer Test WPF initialisiert hat.

Zusatztasten (Strg/Alt/Umschalt/Win) liest die App nur über `ModifierState.Current` (`src/Fotobox/Views/ModifierState.cs`). Im Testlauf setzt `FixedModifierState` (Modul-Initialisierer) sie fest auf „keine“. Simulierte Tastendrücke und Mausradereignisse hängen dadurch nicht von Tasten ab, die gerade am Rechner gedrückt sind; vorher schlug z. B. `BoothWindow_HeldVideoKey_RepeatDoesNotToggle` sporadisch fehl. Neuer Code liest `Keyboard.Modifiers` nicht direkt.

## Muster

- **`FakeTimeProvider`:** Zeit läuft im Code über `TimeProvider` (Controller, Engines, Tracker, Watchdog …). Tests treiben sie mit `Time.Advance(…)`. Beispiel `BoothControllerTests`: Ein `Harness` mit Fake-Kamera/-Composer/-Drucker/-Storage und einem eigenen `PumpContext` (`SynchronizationContext`), damit der Testthread der „UI-Thread“ des Controllers ist und Fortsetzungen deterministisch laufen; ein Flag verhindert, dass die Fake-Zeit vor dem `Task.Run`-Sprung der Seitengrößen-Abfrage weiterläuft.
- Hardware-Abhängigkeiten sind Interfaces (`Core/Interfaces.cs`) und in Tests durch Fakes ersetzbar; Regler laufen über `ICameraControlTarget` (`CameraControlAccessTests`).
- Struktur-Wächter: `SettingsSectionsTests` erzwingt, dass jedes neue Feld in `SettingsSections.ExcludedProperties` oder `DeviceSpecificSettings.Paths` eingeordnet wird; ein Test prüft, dass `AdminColors.Light/Dark.xaml` dieselben Schlüssel haben; `NativeStructSizeTests` prüft die Größen der P/Invoke-Strukturen (`Win32DisplayService.Native` ist dafür `internal`).

## Hinweise

- **Die Tests schreiben ins echte App-Log** (`%LOCALAPPDATA%\Fotobox\logs`) statt in ein isoliertes Verzeichnis.
- **Tastentests lesen den echten Modifier-Zustand** (`AdminWindow.xaml.cs` nutzt `Keyboard.Modifiers`): Hält man lokal während des Laufs Strg/Alt gedrückt, schlagen die `AdminWindow_KeyCapture_*`-Tests fehl (CI unbetroffen). Ein Fix bräuchte eine injizierbare Modifier-Quelle.
- Die manuelle E2E-Prüfung (Erststart, Alt+F4-Sperre, Foto speichern, PIN-Sperre, Kamerawechsel) erfolgte mit einem isolierten Benutzerprofil (`USERPROFILE`/`LOCALAPPDATA` umbiegen) per UI Automation. Es gab bewusst keinen Testdruck auf einem echten Drucker; der Druck muss mit beliebigen Druckern funktionieren (herstellerneutral).
- **Beschleunigung** (Details `beschleunigung.md`): `AccelerationPlannerTests` (Geräteschlüssel, GPU-Rangfolge, Modi, 15-%-Schwelle, Cache-Datei, Fingerabdruck), `OutOfProcessBenchmarkTests`/`MeasurementProcessIntegrationTests` (Messprozess, Absturzschutz; die Integrationstests starten die gebaute `Fotobox.exe` aus dem Testordner), `AccelerationServiceTests` (Fake-Katalog/-Messung aus `AccelerationTestDoubles`), `GpuPreferenceRegistryTests` (Fake-Registry), `AcceleratedSessionTests` (echte CPU-Sessions; der Rückfallpfad über ein Gerät ohne ONNX-Runtime-Handle), `PerformanceSettingsTests`, `AdminPerformanceTests`/`AdminPerformanceViewTests` (Admin-Tab „Leistung“ mit `FakeAccelerationService`; Render-Test `Render_AdminPerformanceTab` mit `FOTOBOX_RENDER_DIR`). `AccelerationHardwareTests` (echter Katalog, echte Messung, `Hardware`) laufen nur lokal.
- Peace- und Mittelfinger-Regeln sind nur mit synthetischen Händen getestet (`PeaceHandFixtures`); die Herzregeln zusätzlich mit gemessenen Handpunkten einer Person mit zwei Händen (`HeartHandFixtures.RealHeart…`/`RealNonHeart…`, nur Zahlen).
- **Herz und Kisscam** (Details `fun-herz-kisscam.md`): `HeartGestureClassifierTests` (+ `HeartHandFixtures`: synthetische Hände je Form und echte Aufnahmen), `HeartEngineTests` (Halten, Aussetzer, Wiederscharf nach beobachteter Ruhe), `HeartPipelineTests` (`PairedHands`), `HeartActionsTests` (Enum ↔ Katalog), `HeartSettingsTests`, `KisscamFrameGeometryTests`, `KisscamZoomTests`, `KisscamTextFormatterTests` (reine Rechnung, ohne WPF); `BoothControllerKisscamTests` (Klemmung 3–60 s, Einzelfoto, Präfix, Zuschnitt vor Spiegeln, Rahmen), `BoothViewModelKisscamTests` (Anzeige-Properties, Zufallswahl des Songs, Mittelfinger-Abbruch), `KisscamMusicConfigCodecTests`, `KisscamTimeFormatTests`, `KisscamSongPickerTests` (reine Logik, ohne WPF), `KisscamMusicLibraryTests` (mit `FakeMusicFileSystem`, `FsGate` für hängende Zugriffe und `FakeTimeProvider`: Auflisten, Bereinigen nur nach Erfolg, Fristen, defekte Config, Entprellung, Rücksetzen bei Admin-Abbruch, schnelles Dispose), `KisscamMusicFileSystemTests` (echtes Dateisystem im Temp-Ordner: Endungen, nicht rekursiv, atomares Schreiben, Watcher), `PeaceOverlayViewModelHeartTests`, `LayoutComposerKisscamTests`, `MusicPlayerTests` (Generationen, Fristen, `ResolveStart`, Pfad im `PlaybackFailed`; die Wiedergabetests mit Startzeit, Schleife und Positionsmeldung sind `Hardware`), `AdminHeartTabTests` (Admin-Seite, Musikordner, Songliste, Anspielen; mit `FakeMusicLibrary`/`FixedRandom` aus `FakeMusicLibrary.cs`), `HeartViewsTests`; WPF-Klassen davon laufen in der `WpfCollection`.
- Trackpad-Scrollen ist nur per Unit-Test (`WheelScrollMathTests`, `ProportionalWheelScrollTests`) geprüft.
