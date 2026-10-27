## Fotobox+ {{VERSION}}

> **Beta-Version:** Diese Vorabversion ist noch nicht für den produktiven Einsatz auf einer Veranstaltung freigegeben. Bitte vorher in Ruhe testen (Kamera mit Ton, Videoaufnahme, Druck) und Rückmeldungen als Issue melden.

### Neu in 2.0.0-beta

- **Videomodus:** eigene Start-/Stopptaste (Standard Eingabetaste), Clips bis 5 Minuten (einstellbar) mit Ton, gespeichert im eigenen Ordner `Videos\Fotobox+`.
- **Hinweiszeile oben** in der Gastansicht.
- **Neuer Name:** Die App heißt jetzt **Fotobox+** (Programmdatei, Einstellungen und Logs behalten ihre Namen, ein Update übernimmt alle Einstellungen).
- **Fotos** liegen standardmäßig in `Bilder\Fotobox+`. Wer keinen eigenen Fotoordner eingestellt hatte, findet ältere Fotos weiterhin in `Bilder\Fotobox`.

### Welche Datei passt zu meinem PC?

| Datei | Für welchen PC? |
| --- | --- |
| `FotoboxPlus-Setup-{{VERSION}}-x64.exe` | Installer für Windows-PCs mit **Intel- oder AMD-Prozessor** (x64) |
| `FotoboxPlus-Setup-{{VERSION}}-arm64.exe` | Installer für Windows-PCs mit **Snapdragon/ARM-Prozessor** (z. B. Copilot+ PCs) |
| `FotoboxPlus-{{VERSION}}-x64-portable.zip` | Portable Version ohne Installation, Intel/AMD (x64) |
| `FotoboxPlus-{{VERSION}}-arm64-portable.zip` | Portable Version ohne Installation, Snapdragon/ARM |

Unsicher? Unter *Einstellungen → System → Info* steht bei „Systemtyp“, ob es ein x64- oder ein ARM-basierter Prozessor ist.

### Hinweis zu Windows SmartScreen

Die Dateien sind nicht digital signiert. Windows zeigt deshalb beim ersten Start möglicherweise die Meldung „Der Computer wurde durch Windows geschützt“ an.
Klicke dort auf **Weitere Informationen → Trotzdem ausführen**.

### Hinweise

- Die Installation erfolgt standardmäßig nur für den aktuellen Benutzer und benötigt keine Administratorrechte.
- Einstellungen und Protokolle liegen in `%LOCALAPPDATA%\Fotobox`, Fotos standardmäßig unter `Bilder\Fotobox+`, Videos unter `Videos\Fotobox+`. Beides bleibt bei einer Deinstallation erhalten.
- Beim ersten Start wird eine Admin-PIN festgelegt.
- Standardtasten: **Leertaste** löst das Foto aus, **Esc** bricht den Countdown oder einen laufenden Druck ab, **Strg + Alt + A** öffnet die Admin-Ansicht. Alle Tasten lassen sich im Admin unter **Tasten** ändern.
