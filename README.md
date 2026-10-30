# MONIVOX

Teilt einen Monitor frei in mehrere **echte Windows-Bildschirme** auf. Windows, Programme und Spiele sehen jeden
Bereich als eigenen Monitor (Einstellungen → Anzeige, „Auf Bildschirm 2 verschieben“, Vollbild pro Bereich).
Nur der gewählte Monitor wird aufgeteilt – alle anderen bleiben völlig unverändert.

> **Status: Prototyp.** Alle Teile werden per CI gebaut und getestet, der Nachweis auf echter Hardware steht noch aus.
> Fortschritt und offene Punkte: [ROADMAP](ROADMAP.md) · Technischer Hintergrund: [docs/MACHBARKEIT.md](docs/MACHBARKEIT.md)

## Funktionen

- **Freier Editor**: bis zu 24 Bereiche selbst aufziehen, verschieben, an Kanten ziehen, pixelgenau eingeben, Lücken erlaubt.
  Auch schmale Streifen (z. B. Telemetrie bei Rennspielen). Taskleiste pro Bereich ausblendbar.
- **Programme fest zuweisen**: „Discord öffnet sich → Bereich 2“, „Teams → Bereich 3“ – automatisch, auch nach einem Neustart des Programms; optional mit dem Profil mitstarten.
- **Profile** (Gaming, Arbeit, Alltag …), Export/Import, Wechsel per Rechtsklick aufs Tray-Symbol.
- **Infobereich**: läuft unten rechts in der Taskleiste, startet mit Windows (ohne UAC-Abfrage), Bildschirme entstehen beim Start automatisch.
  Doppelklick öffnet die Einstellungen, Rechtsklick zeigt Schnellbefehle.
- **Automatische Updates** aus den GitHub-Releases – nie mitten in einer laufenden Aufteilung.
- **Farbige Rahmen** mit Name, Größe und Art jedes Bereichs – ein-/ausblendbar (Tray, Einstellungen, Strg+Alt+Shift+F),
  nie in Aufnahmen/Streams sichtbar und ausgeblendet ohne jeden Einfluss auf Spiele.
- **Anti-Cheat-Check**: zeigt laufende Anti-Cheat-Systeme und warnt, falls Windows im Testmodus läuft.

## Ehrliche Einschränkungen

| | |
|---|---|
| ✅ Andere Monitore | unverändert: keine zusätzliche Latenz, G-Sync/FreeSync wie gewohnt |
| ✅ Kein Testmodus | mitgelieferter, offiziell signierter Anzeigetreiber; Secure Boot bleibt an |
| ✅ Anti-Cheat | MONIVOX greift nicht in Spiele ein. Probleme sind nicht bekannt, aber nicht ausgeschlossen |
| ✅ **Direktbereich** (grün ★) | ein Bereich pro Monitor zeigt den echten Monitor: keine Zusatzlatenz, G-Sync/FreeSync, **DRM-Videos funktionieren** |
| ⚠️ Spiele im Direktbereich | im **Fenstermodus** in Größe des Bereichs starten; MONIVOX entfernt den Rahmen (Regel „Randlos“). Exklusives Vollbild würde den ganzen Monitor belegen |
| ⚠️ Spiele auf virtuellen Bereichen | +~1 Bild Latenz, kein G-Sync/FreeSync; randloses Vollbild empfohlen |
| ❌ DRM auf virtuellen Bereichen | schwarz – Windows/Streamingdienste verhindern das bewusst; dafür gibt es den Direktbereich |

## Installation (Prototyp)

1. Unter **Releases** die neueste `Monivox-win-x64.zip` laden und z. B. nach `C:\Programme\MONIVOX` entpacken.
   Voraussetzung: [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
2. `MONIVOX.exe` starten → **Status & Treiber → Treiber installieren**.
3. **Bildschirme**: Monitor wählen, Bereiche einteilen → **Speichern & anwenden**.
4. **Einstellungen → Mit Windows starten** anhaken.

Not-Aus: **Strg + Alt + Shift + Q** beendet die Aufteilung sofort und stellt alles wieder her.
Für Tests gibt es zusätzlich `monivox-cli.exe` (`list`, `apply`, `stop`, `check`, …).

## Aufbau

```
src/Monivox.App/       WPF-Oberfläche, Tray, Editor, Programmregeln
src/Monivox.Core/      Layouts, Profile, Treiber-Konfiguration (plattformneutral, getestet)
src/Monivox.Windows/   Monitore anordnen, Treiber steuern, Fensterregeln, Updates, Anti-Cheat-Check
src/Monivox.Cli/       monivox-cli für Tests
native/                Compositor (D3D11 + Windows.Graphics.Capture) und Geräte-Werkzeug
tests/                 Unit-Tests
```

## Lizenz

MIT. Mitgelieferte Komponenten: siehe [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
