# Build und Release

Stand: 2.5.1. Pfade relativ zum Repo-Root. Tests: [tests.md](tests.md).

## Befehle

```
dotnet build Fotobox.sln
dotnet test --filter "Category!=Hardware"
pwsh build/publish.ps1 -Version 1.2.3 [-Arch x64|arm64] [-RequireVcRuntime]
pwsh build/smoke-test.ps1 -PublishDir artifacts/publish/x64
```

- Ziel: `net10.0-windows10.0.19041.0`, x64 und ARM64, self-contained. Die Version steht in `Directory.Build.props` (`<Version>`); sie ist die Standardversion von `publish.ps1`. Lokal reicht ein installiertes .NET-10-SDK (auch eine Vorabversion); die CI nutzt das finale .NET 10.
- `publish.ps1` (PowerShell 7 und Windows PowerShell 5.1, Exit-Code ≠ 0 bei Fehlern) erzeugt je Architektur (Standard beide):
  1. `dotnet publish` (Release, self-contained) nach `artifacts/publish/<arch>`,
  2. app-lokale VC++-Runtime (siehe unten),
  3. statische Abhängigkeitsprüfung,
  4. `artifacts/FotoboxPlus-<Version>-<arch>-portable.zip`. Der Dateiname ist ohne „+“, weil ungeklärt ist, ob GitHub das Zeichen in Asset-Namen behält.
- Seit 2.5.0-beta gibt es **nur noch portable ZIPs** (kein Installer). Ältere Releases 1.4.1/2.2.1 behalten ihre Setup-Dateien.
- Die ONNX-Modelle und ONNX Runtime machen den Publish je Architektur deutlich größer (Zielerfassung ca. 21 MB, Peace ca. 15,6 MB).
- `Fotobox.exe` ist **nicht signiert** (SmartScreen-Hinweis steht im Release-Template).

## VC++-Runtime app-lokal

`onnxruntime.dll` importiert `MSVCP140`, `MSVCP140_1`, `VCRUNTIME140` (x64 zusätzlich `VCRUNTIME140_1`); self-contained .NET liefert sie nicht mit. Ohne sie lädt auf PCs ohne installierte VC++ Redistributable das KI-Modell nicht (die Zielerfassung fällt auf die Gesichtserkennung zurück; ab 2.2.1 ist die Runtime im Paket).

- Nach `dotnet publish` kopiert `publish.ps1` alle DLLs der Zielarchitektur aus `VC\Redist\MSVC\<Version>\<arch>\Microsoft.VC14*.CRT` (gefunden per `vswhere`) **flach neben `Fotobox.exe`**. Windows löst die Importe von `onnxruntime.dll` aus **deren** Ordner auf (nicht aus dem Exe-Ordner), deshalb muss der Publish flach bleiben (CRT neben `onnxruntime.dll`).
- Versionsprüfung: `msvcp140.dll` mindestens 14.40 **und** mindestens die Linker-Version von `onnxruntime.dll` (derzeit 14.44).
- Die arm64-Redist enthält ARM64EC-DLLs mit x64-Kopf (z. B. `vcruntime140_1.dll`) für emulierten x64-Code; sie werden **nicht** kopiert.
- **`-RequireVcRuntime`** (CI/Release): Fehlt die Runtime oder ist sie zu alt → Abbruch. Ohne den Schalter nur Warnung, es wird nichts kopiert, und das Paket enthält keine Runtime. **Lokal gebaute Pakete ohne Runtime dürfen nicht weitergegeben werden**; ausgelieferte Pakete kommen aus der CI. Wer lokal nur eine zu alte x64-CRT bzw. keine arm64-CRT hat, baut lokal ohne `-RequireVcRuntime`.
- `THIRD-PARTY-NOTICES.txt` im Repo-Root (wird mit ausgeliefert) nennt die Microsoft Visual C++ Runtime und die übrigen Komponenten.

**Statische Abhängigkeitsprüfung** (`Get-PeInfo` = eigener PE-Leser, `Test-NativeDependencies`): Native Dateien müssen die Zielarchitektur haben; jeder Import muss im Publish-Ordner, als API-Set oder in System32 liegen; CRT-DLLs (`msvcp|vcruntime|concrt|vccorlib`) zwingend im Publish-Ordner (ohne kopierte Runtime nur Warnung).

## Smoke-Test

`build/smoke-test.ps1 -PublishDir <Ordner> [-Seconds 20]` startet `Fotobox.exe` 20 s mit **isoliertem `USERPROFILE`/`LOCALAPPDATA`** (Temp-Profil; echte Einstellungen und Logs bleiben unberührt). Er schlägt fehl bei vorzeitigem Ende, fehlender Logdatei im isolierten Ordner oder Log-Meldungen „Unbehandelte Ausnahme“, „Nicht beobachtete Task-Ausnahme“, „Die Fotobox konnte nicht gestartet werden“, „Einzelinstanz-Sperre“. Läuft ohne Kamera.

## CI-Workflows (`.github/workflows`)

| Workflow | Auslöser | Schritte |
|---|---|---|
| `build.yml` | Push auf jeden Branch, Pull Request (Nebenläufigkeit je Ref, laufende Läufe werden abgebrochen) | `windows-latest`; Build Release; Tests ohne Hardware (`--filter "Category!=Hardware" --blame-hang-timeout 5m --blame-hang-dump-type none`); x64-Publish mit `-RequireVcRuntime`; Smoke-Test x64 |
| `release.yml` | Tag `v*`; `workflow_dispatch` (nur Artefakte, Standardversion `0.0.0-test`) | Version bestimmen (Tag ohne `v`, Format geprüft); Tests (ebenfalls mit Hang-Timeout); Publish beider Architekturen mit `-RequireVcRuntime`; Smoke-Test x64; ZIPs als Artefakt; nur bei Tag-Push: Release-Notes aus Template, `gh release create` |

Die `--blame-hang-timeout 5m` bricht Hänger mit Testnamen ab (Hintergrund: [tests.md](tests.md#wpf-tests-und-die-collection-pflicht)). Der Release-Lauf scheiterte einmal an einem zeitabhängigen Test und lief nach Neustart durch; der Test ist inzwischen robust.

## Release-Ablauf

1. Neue Arbeit auf einem **Branch** (Release-Vorbereitung z. B. `release/x.y.z`), Version in `Directory.Build.props` anheben.
2. Push → `build.yml` prüft (Build, Tests, Publish, Smoke-Test).
3. Nach `main` mergen.
4. **Tag `v*` pushen** (Tag auf dem `main`-Stand des Releases) → `release.yml` läuft.
5. `release.yml`: Tests, Publish, Smoke-Test, Release-Text aus `build/release-notes.template.md` (`{{VERSION}}` wird ersetzt, UTF-8 ohne BOM), `gh release create <Tag> --title "Fotobox+ <Version>" --notes-file … --verify-tag` mit den ZIPs. **Versionen mit `-` (vor einem etwaigen `+`) werden als Prerelease markiert.**
6. Assets: `FotoboxPlus-<Version>-x64-portable.zip` und `FotoboxPlus-<Version>-arm64-portable.zip`.
7. Danach werden die gemergten Arbeitsbranches gelöscht; auf GitHub gibt es dauerhaft nur `main`.

**Release-Texte:**

- Das Template enthält „Neu in …“, die Tabelle „Welche Datei passt zu meinem PC?“, den SmartScreen-Hinweis und Hinweise (Entpacken statt Installation; Einstellungen in `%LOCALAPPDATA%\Fotobox`; Standardtasten) sowie das Archiv.
- **Archiv „Ältere Versionen“:** Auf GitHub sind nur die neuesten Releases einzeln sichtbar. Ältere Releases werden **gelöscht (die Tags bleiben)**; ihre Texte stehen im aufklappbaren Abschnitt „Ältere Versionen“ (`<details>`) des **neuesten** Release.
- Beim Voranstellen von „Neu in x.y.z“ an einen bestehenden Release-Text diesen **als Datei mit echten Zeilenumbrüchen** schreiben und per `gh release edit --notes-file` setzen; sonst zerfällt die Tabelle.

**Commits:** Nutzen die GitHub-noreply-Adresse des Repo-Besitzers (repo-lokale `user.email`), keine private Mail. Das Repo ist öffentlich: keine persönlichen oder gerätespezifischen Daten (Benutzernamen, lokale Pfade, Hostnamen, Drucker-/Kameramodelle, Portnamen) in Code, Tests, Docs oder Commits.

**Offene Punkte rund um Releases:** ungeprüft, ob GitHub das „+“ in Asset-Namen behält (deshalb `FotoboxPlus-…`); `Fotobox.exe` unsigniert (Code-Signing braucht ein Zertifikat); x64 nicht auf echter Intel/AMD-Hardware geprüft, nur CI und Emulation auf ARM64. Siehe [bekannte-grenzen.md](bekannte-grenzen.md).
