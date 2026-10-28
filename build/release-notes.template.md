## Fotobox+ {{VERSION}}

### Neu in 2.2.0

- **Fun-Modus „Zielerfassung“:** Die Fotobox erkennt Personen im Livebild und markiert jede wie im Film mit einer Zielbox „TARGET 1“, „TARGET 2“ … Neue Ziele rasten animiert ein, danach erscheint „Hey du, nimm doch ein Foto oder Video mit mir auf!“ bzw. bei mehreren Personen „Hey ihr, nehmt doch …“. Das passiert nur in der Gastansicht, solange niemand fotografiert oder filmt, und landet nie im Foto oder Video. Standardmäßig eingeschaltet.
- **Neuer Admin-Tab „Fun“** (ganz unten): ein- und ausschalten, Erkennung per KI-Modell (ganzer Körper, auch seitlich oder von hinten) oder sparsamer per Windows-Gesichtserkennung, Box-Umfang (Kopf, Oberkörper, ganze Person), drei Box-Stile, fünf Farben, drei Einrast-Animationen, eigene Texte mit Platzhalter `{Anzahl}`, Position und Stil des Texts (Terminal-Look mit Tipp-Effekt oder einer der Hinweis-Stile), Anzeigedauer, Pause, Empfindlichkeit und optionale Soundeffekte. Eine Live-Vorschau mit Kamerabild und der Knopf „Animation testen“ zeigen die Wirkung sofort.
- **Alles lokal:** Die Erkennung läuft komplett auf dem PC, es werden keine Bilder gespeichert oder verschickt. Der Download ist dadurch etwa 21 MB größer (KI-Modell YOLOX-Nano und ONNX Runtime, Lizenzhinweise in `THIRD-PARTY-NOTICES.txt`).

### Neu in 2.1.0

- **Admin: „Übernehmen“** speichert, ohne die Admin-Ansicht zu verlassen; beim Schließen mit ungespeicherten Änderungen fragt die App nach.
- **Mausradzeile einstellbar** (ein/aus, eigener Text, fünf Darstellungen) und **sechs neue Hinweis-Stile** (Neon, Sprechblase, Laufschrift, Glas, Zettel, Kino).

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
