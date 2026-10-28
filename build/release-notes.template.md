## Fotobox+ {{VERSION}}

### Neu in 2.1.0

- **Admin: „Übernehmen“.** Die Fußleiste hat jetzt „Schließen“, „Übernehmen“ und „Speichern & schließen“. Mit „Übernehmen“ werden Einstellungen gespeichert, ohne die Admin-Ansicht zu verlassen. Beim Schließen mit ungespeicherten Änderungen fragt die App nach.
- **Mausradzeile einstellbar:** im Admin-Tab *Gastansicht* ein- und ausschaltbar, mit eigenem Text und fünf Darstellungen (passend zum Foto-Hinweis, kleine Pille, schlichter Text, Chip, Pfeile).
- **Sechs neue Hinweis-Stile** für Foto- und Video-Hinweis (jetzt neun): Neon, Sprechblase, Laufschrift, Glas, Zettel und Kino. Die Auswahl im Admin zeigt kleine Skizzen.

### Neu seit 1.4.1 (Fotobox+ 2.0)

- **Videomodus:** eigene Start-/Stopptaste (Standard Eingabetaste), Clips bis 5 Minuten (einstellbar) mit Ton, gespeichert im eigenen Ordner `Videos\Fotobox+`.
- **Anzeige während der Aufnahme** (Aufnahmepunkt, Laufzeit, Restzeit), Admin-Tab *Video* mit Mikrofonwahl und Countdown.
- **Hinweiszeile oben** in der Gastansicht mit eigenem Stil.
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
- Einstellungen und Protokolle liegen in `%LOCALAPPDATA%\Fotobox`, Fotos standardmäßig unter `Bilder\Fotobox+`, Videos unter `Videos\Fotobox+`. Alles bleibt bei einer Deinstallation erhalten.
- Beim ersten Start wird eine Admin-PIN festgelegt.
- Standardtasten: **Leertaste** löst das Foto aus, **Eingabetaste** startet und beendet ein Video, **Esc** bricht den Countdown oder einen laufenden Druck ab, **Strg + Alt + A** öffnet die Admin-Ansicht. Alle Tasten lassen sich im Admin unter **Tasten** ändern.
