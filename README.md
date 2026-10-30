# MONIVOX

Teilt einen großen physischen Monitor in mehrere **echte virtuelle Windows-Monitore** auf.
Windows, Programme und Spiele sehen die Bereiche als eigenständige Displays
(Einstellungen → System → Anzeige, eigene Taskleiste, „Auf Monitor 2 verschieben“, Vollbild pro Bereich).

> **Status: Prototyp (Phase 2).** Machbarkeit ist analysiert, Treiber, Compositor und
> Kommandozeile sind implementiert und werden per CI gebaut. Die grafische Oberfläche folgt erst,
> wenn die Kernfunktion auf echter Hardware nachgewiesen ist. Siehe [ROADMAP](ROADMAP.md).

## Wie es funktioniert

Windows kann einen physischen Monitor **nicht** selbst aufteilen – dafür gibt es keine API.
Monivox kombiniert deshalb drei Bausteine (Details: [docs/MACHBARKEIT.md](docs/MACHBARKEIT.md)):

1. **Virtueller Display-Treiber** (IddCx, User-Mode) erzeugt pro Bereich einen virtuellen Monitor
   mit genau der passenden Auflösung (z. B. 2× 1920×2160 bei einem 4K-Monitor).
2. **Anordnung**: Die virtuellen Monitore werden auf dem Windows-Desktop exakt wie die Bereiche
   angeordnet; der physische Monitor wird „geparkt“, die Maus bleibt auf den virtuellen Monitoren.
3. **Compositor** holt die Bilder der virtuellen Monitore per GPU und zeigt sie nebeneinander
   randlos auf dem physischen Monitor an.

## Ehrliche Einschränkungen (Kurzfassung)

| | |
|---|---|
| ✅ Desktop-Programme, Browser, Discord, Office | voll unterstützt |
| ✅ Spiele im **randlosen Vollbild** | unterstützt (+~1 Frame Latenz) |
| ⚠️ Exklusives Vollbild | auf manchen Spielen schwarzes Bild im Prototyp → Borderless verwenden |
| ❌ G-Sync / FreeSync für das Spiel | wird nicht durchgereicht |
| ✅ Kein Testsignatur-Modus | Secure Boot bleibt an; MONIVOX greift nicht in Spiele ein. Anti-Cheat-Probleme sind nicht bekannt, aber nicht ausgeschlossen → `monivox check`, im Zweifel vor dem Spiel `monivox stop` |
| ✅ Mehrere Monitore | nur der gewählte Monitor wird aufgeteilt, alle anderen bleiben unverändert |
| ❌ DRM-Videos (Netflix in Edge) | werden schwarz dargestellt |

**Tipp:** Hat dein Monitor **Picture-by-Picture (PbP)**, ist das für kompetitives Gaming die bessere
Lösung (zwei Kabel, echte Monitore, G-Sync, keine Latenz). Monivox-Profile/Programmzuweisungen
sollen auch damit funktionieren.

## Projektstruktur

```
driver/MonivoxIdd/     C++  IddCx-Treiber (virtuelle Monitore, Modi aus Registry)
native/src/compositor.cpp   C++  D3D11 + Windows.Graphics.Capture – zeigt die Bereiche an
native/src/devicetool.cpp   C++  legt das Software-Gerät für den Treiber an/entfernt es
src/Monivox.Core/      C#   Layouts, Vorlagen, Profile, Konfigurationsformate (plattformneutral, getestet)
src/Monivox.Windows/   C#   Monitore abfragen/anordnen, Treiber steuern, Compositor überwachen
src/Monivox.Cli/       C#   monivox – Kommandozeile für den Nachweis in Phase 2
tests/                      C#   xUnit-Tests der Kernlogik
scripts/                    PS   Treiber installieren/deinstallieren
```

## Ausprobieren (Prototyp)

1. Unter **Releases** die neueste `Monivox-win-x64.zip` herunterladen und z. B. nach `C:\Programme\MONIVOX` entpacken.
   Voraussetzung: [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Treiber einmalig installieren (PowerShell **als Administrator**, kein Testsignatur-Modus, Secure Boot bleibt an):
   ```powershell
   cd "C:\Programme\MONIVOX\scripts"
   .\install-driver.ps1
   ```
3. Aufteilen:
   ```powershell
   cd ..
   .\monivox list                         # Monitore anzeigen, z. B. DISPLAY1, DISPLAY2
   .\monivox check                        # Anti-Cheat-Verträglichkeit prüfen
   .\monivox apply half --monitor DISPLAY2 # nur DISPLAY2 in linke/rechte Hälfte teilen
   ```
   Strg+C oder **Strg+Alt+Shift+Q** beendet die Aufteilung.
4. Zurück zum Normalzustand: `monivox stop`. Komplett entfernen: `scripts\uninstall-driver.ps1`.

**Alternative ohne eigenen Treiber:** den signierten
[Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) installieren, dort so viele
Monitore/Auflösungen einrichten wie `monivox preview half 3840x2160` ausgibt, dann `monivox apply half --external`.

## Automatische Updates

Jeder Push auf `main` baut per GitHub Actions ein neues Release (`v0.2.<Buildnummer>`). MONIVOX prüft bei jedem
`monivox apply` (oder manuell mit `monivox update`), ob ein neueres Release existiert, lädt es, prüft die
SHA-256-Prüfsumme und ersetzt die Programmdateien. Ändert sich der Treiber, wird er automatisch neu installiert.
Abschalten pro Start mit `--no-update`.

> Voraussetzung: Die Releases müssen ohne Anmeldung abrufbar sein, d. h. das Repository muss **öffentlich** sein.

Vorlagen: `full`, `half`, `half-h`, `thirds`, `main-side`, `main-stack`, `grid`, eigene Spalten mit
`monivox apply custom --columns 0.6`.

## Notfall

- **Strg+Alt+Shift+Q** beendet den Compositor und gibt die Maus frei.
- `monivox stop` stellt die ursprüngliche Monitoranordnung wieder her und schaltet die virtuellen Monitore ab.
- Strg+Alt+Entf hebt eine hängende Mausbegrenzung immer auf.

## Bauen

- Treiber: Visual Studio 2022 + WDK, `driver/MonivoxDriver.sln`
- Native: `cmake -S native -B build/native -A x64 && cmake --build build/native --config Release`
- .NET: `dotnet test tests/Monivox.Core.Tests` und `dotnet publish src/Monivox.Cli -r win-x64`

## Lizenz

MIT. Der Treiberaufbau orientiert sich am Microsoft-Beispiel *IddSampleDriver* (MIT).
