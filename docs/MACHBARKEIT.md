# Phase 1 – Technische Machbarkeitsanalyse

Stand: 30.10.2026 · Zielsysteme: Windows 10 (ab 1903) und Windows 11

## 1. Die Kernfrage

> Kann Windows einen einzigen physischen Monitor als mehrere echte Monitore betrachten?

**Nein – nicht direkt.** Windows (genauer: der Display-Stack aus DXGK-Grafiktreiber, WDDM und DWM)
behandelt jeden *Display-Ausgang* (Target) als genau einen Monitor. Es gibt **keine** Windows-API,
mit der man ein physisches Target in mehrere logische Monitore zerlegen kann:

| Schnittstelle | Was sie kann | Was sie **nicht** kann |
|---|---|---|
| Display Configuration API (`QueryDisplayConfig`/`SetDisplayConfig`) | Vorhandene Targets aktivieren, positionieren, Auflösung/Rotation setzen, Klonen/Erweitern | Neue Targets erzeugen oder ein Target aufteilen |
| `ChangeDisplaySettingsEx` | Alte API für Modus/Position | dito |
| Indirect Display Driver (IddCx) | **Neue, virtuelle Monitore** erzeugen, die Windows wie echte behandelt (Einstellungen → Anzeige, DPI, Taskleiste, Vollbild) | Das Bild landet beim Treiber, **nicht** auf einem physischen Monitor |
| Grafiktreiber-Features (NVIDIA Surround, AMD Eyefinity) | Mehrere Monitore zu einem verbinden | Genau das Gegenteil von dem, was wir brauchen |

Ein IDD erzeugt also echte virtuelle Monitore – aber deren Bildinhalt wird nicht automatisch
irgendwo angezeigt. Der Treiber bekommt jedes fertige Frame in einer Swapchain und muss selbst
entscheiden, was er damit macht (bei Remote-Desktop-Lösungen: komprimieren und verschicken).

## 2. Daraus folgende Architektur (einzig realistischer Software-Weg)

```
 ┌─────────────── Windows-Desktop ───────────────┐
 │  Virtueller Monitor 1   │  Virtueller Monitor 2 │   ← erzeugt vom IDD-Treiber
 │  (z. B. 1920×2160)      │  (z. B. 1920×2160)    │     Windows sieht 2 echte Monitore
 └───────────┬─────────────┴──────────┬────────────┘
             │ Frames                 │ Frames
             ▼                        ▼
      ┌──────────────── Compositor ────────────────┐
      │ Fängt die Bilder beider Monitore ab und     │
      │ zeichnet sie nebeneinander in ein           │
      │ randloses Vollbildfenster (D3D11)           │
      └───────────────────┬─────────────────────────┘
                          ▼
              Physischer 32"-Monitor (3840×2160)
```

1. **IDD-Treiber** stellt N virtuelle Monitore mit exakt den Auflösungen der Bereiche bereit.
2. **Topologie-Steuerung** (Display Configuration API) ordnet die virtuellen Monitore auf dem
   Windows-Desktop genau so an wie die Bereiche auf dem physischen Monitor und schiebt den
   physischen Monitor „aus dem Weg“.
3. **Compositor** holt sich die Bilder der virtuellen Monitore (Prototyp: Windows.Graphics.Capture;
   später direkt aus dem Treiber) und zeichnet sie in ein Vollbildfenster auf dem physischen Monitor.
4. **Maus**: Der Cursor wird mit `ClipCursor` auf die virtuellen Monitore beschränkt. Da deren
   Anordnung 1:1 der Bereichsaufteilung entspricht, bewegt sich die Maus für den Nutzer genau so,
   als wären die Bereiche echte Monitore. Der Cursor wird vom Compositor mitgezeichnet.
5. **Tastatur**: Kein Eingriff nötig – Fokus liegt immer beim Fenster auf dem virtuellen Monitor.

Das ist dasselbe Grundprinzip, das kommerzielle „virtual monitor“-Lösungen und Remote-Desktop-Tools
nutzen. Es ist technisch machbar, ohne Kernel-Treiber (IDDs laufen im User-Mode, UMDF).

### Verworfene Alternativen

| Ansatz | Grund für Verwerfung |
|---|---|
| Reine Fensterverwaltung (FancyZones-Prinzip) | Laut Anforderung ausdrücklich nicht ausreichend; Windows sieht nur einen Monitor |
| Kernel-Mode-Display-Filtertreiber | Extrem riskant (Bluescreens), praktisch nicht signierbar für Privatentwickler, keine dokumentierte API zum Aufteilen |
| Hook in DWM | Undokumentiert, bricht bei jedem Windows-Update, von Anti-Cheat als Manipulation gewertet |
| Eigener Kernel-Grafiktreiber (WDDM/DXGK) | Unrealistisch (GPU-herstellerspezifisch) |

### Die beste Alternative für Gaming: Hardware-PbP

Viele große Monitore (vor allem 32"–49") haben **Picture-by-Picture (PbP)**. Mit zwei Kabeln von
der Grafikkarte zeigt der Monitor zwei Eingänge nebeneinander an. Windows sieht dann zwei *echte*
Monitore – inkl. exklusivem Vollbild, G-Sync/FreeSync und null zusätzlicher Latenz. Die Software
unterstützt diesen Fall in der Programm-/Profilverwaltung genauso (Phase 3/4), braucht dann aber
keinen virtuellen Treiber. **Wenn der eigene Monitor PbP kann, ist das für Spiele immer die
bessere Wahl.** Das wird in der App ehrlich so angezeigt.

## 3. Treiber (Entscheidung vom 30.10.2026)

- MONIVOX liefert den **Virtual Display Driver** (VDD, Open Source, MIT) unverändert mit. Er ist ein
  IddCx-Treiber (User-Mode, UMDF) und über die SignPath Foundation **offiziell signiert**.
- Damit: **kein Testsignatur-Modus, kein selbst ausgestelltes Zertifikat, Secure Boot bleibt an.** Die Kernel-Teile
  (`WUDFRd.sys`, `IndirectKmd.sys`) stammen von Microsoft.
- Steuerung: Monitoranzahl, Auflösungen und Bildwiederholraten in `vdd_settings.xml`; Neuladen über die Pipe
  `\\.\pipe\MTTVirtualDisplayPipe` (`RELOAD_DRIVER`), Fallback Geräteneustart. Installation: Katalog-Zertifikat
  als vertrauenswürdiger Herausgeber eintragen, `pnputil /add-driver`, Software-Gerät `Root\MttVDD` anlegen.
- Version und SHA-256 des Treiberpakets sind in der CI festgenagelt; ein manipuliertes Paket bricht den Build ab.
- Ein eigener Treiber wurde zunächst gebaut (siehe Git-Historie) und verworfen: er hätte ein selbst ausgestelltes
  Stammzertifikat auf dem Nutzer-PC gebraucht – schlechter für Virenscanner und Vertrauen.

## 4. Gaming – ehrliche Bewertung

| Punkt | Bewertung |
|---|---|
| DirectX 9–12, Vulkan, OpenGL | Funktioniert – Spiele rendern auf der GPU, das IDD bekommt das fertige Bild. |
| Randloses Vollbild (Borderless) | **Funktioniert zuverlässig.** Empfohlener Modus. |
| Exklusives Vollbild | Wird auf IDD-Monitoren von Windows unterstützt (Modus-Wechsel), aber im Prototyp kann Windows.Graphics.Capture bei manchen Spielen schwarze Frames liefern. Bis das in Phase 5 getestet ist: Spiel auf Borderless stellen. |
| Bildwiederholrate | Virtuelle Monitore können z. B. 144 Hz melden. Sichtbar ist max. die Rate des physischen Monitors; der Compositor präsentiert synchron dazu. |
| G-Sync / FreeSync | **Nicht durchgereicht.** VRR gilt nur für das Compositor-Fenster, nicht für das Spiel. Dafür ist PbP nötig. |
| Latenz | +1 Frame (Capture + Komposition), bei 144 Hz ≈ 7 ms. Für Einzelspieler/Casual unkritisch, für kompetitive Shooter spürbar. |
| Maus/Eingabe | Maus bleibt per `ClipCursor` auf den virtuellen Monitoren; Spiele mit Raw Input funktionieren normal. Spiele, die selbst `ClipCursor` setzen, überschreiben unseren Clip kurz – der Compositor setzt ihn alle 250 ms neu. |
| Anti-Cheat | Kein Testsignatur-Modus nötig. MONIVOX greift nicht in Spiele ein (kein Injizieren, kein Speicherzugriff, kein eigener Kernel-Treiber); virtuelle Monitore + Bildschirmaufnahme nutzen auch OBS/Parsec/Sunshine. Bann-Fälle dafür sind nicht bekannt, eine Garantie gibt es nicht. Die Status-Seite warnt bei aktiver Testsignatur und zeigt laufende Anti-Cheats an. |
| HDR | Prototyp: SDR (BGRA8). HDR (FP16) ist ab Windows 11 im IDD möglich, später. |

**Fazit:** Desktop-Nutzung und Spiele im Borderless-Modus: gut machbar. Kompetitives Gaming mit
G-Sync und exklusivem Vollbild: nur mit Hardware-PbP wirklich gleichwertig. Die App wird nie
behaupten, dass jedes Spiel auf jedem virtuellen Monitor perfekt läuft.

## 5. Weitere Einschränkungen

- **DRM-geschützte Inhalte** (Netflix, Disney+, Prime Video …) werden von Windows aus jeder Bildschirmaufnahme
  herausgehalten und auf virtuellen Monitoren ohne HDCP teils gar nicht abgespielt. Das ist ein bewusster Schutz und
  wird nicht umgangen. Lösung: **Direktbereich** – ein Bereich des Monitors wird nicht virtualisiert, sondern zeigt den
  echten Monitor (DRM funktioniert, keine Zusatzlatenz). Erfordert eine Maus-Übergabe zwischen den Bereichen (Phase 5).
- **Absturzsicherheit:** Stürzt der Compositor ab, sieht man den „leeren“ physischen Monitor, während
  Fenster auf unsichtbaren virtuellen Monitoren liegen. Daher: Watchdog-Dienst startet den Compositor neu;
  Not-Tastenkombination `Strg+Alt+Shift+Q` beendet den Split und gibt die Maus frei; die App kann die
  virtuellen Monitore jederzeit abschalten.
- **Ressourcen:** Pro virtuellem Monitor eine GPU-Kopie pro Frame. Bei 4K gesamt ≈ 33 MB/Frame im
  VRAM-Bandbreite – für aktuelle GPUs erwartbar gering; die tatsächliche Last wird in Phase 5 gemessen und hier eingetragen.
- **Wechsel des physischen Monitors:** Der Compositor reagiert auf `WM_DISPLAYCHANGE` und baut sich neu auf.

## 6. Architekturentscheidung (ADR-001)

| Komponente | Technologie | Begründung |
|---|---|---|
| Virtueller Display-Treiber | Mitgelieferter Virtual Display Driver (IddCx, signiert) | Echte virtuelle Monitore ohne Testmodus |
| Compositor | C++20, Win32, Direct3D 11, C++/WinRT (Windows.Graphics.Capture) | Niedrigste Latenz, kein GC, direkter GPU-Zugriff |
| Kernlogik (Layouts, Profile) | C# / .NET 8, plattformneutral | Gut testbar (auch unter Linux-CI), wartbar |
| Windows-Integration (Topologie, Fenster) | C# / .NET 8 mit P/Invoke | Display Configuration API + Win32-Fenster-APIs |
| Oberfläche | WPF (.NET 8) mit Windows-11-Stil | Stabil, ausgereift, kein zusätzlicher Runtime-Installer wie bei WinUI 3; Mica/abgerundete Ecken über DWM-Attribute |
| Installer | WiX Toolset v5 (kostenlos) | MSI, kann Treiberinstallation per Custom Action |

**Entscheidung WPF statt WinUI 3:** WinUI 3 bräuchte das Windows App SDK als Zusatz-Runtime und ist bei
Tray-Icons, globalen Hotkeys und Ein-Datei-Bereitstellung umständlicher. WPF erreicht mit
Mica-Hintergrund und eigenen Styles optisch dasselbe Ergebnis.

## 7. Nachweisplan für Phase 2

1. Treiber über die App installieren (Secure Boot an, kein Testmodus) → in *Einstellungen → Anzeige* erscheinen 2 neue Monitore. ✅ Kriterium: Windows zeigt sie mit den konfigurierten Auflösungen.
2. `monivox apply --preset half` → virtuelle Monitore werden angeordnet, Compositor startet.
   ✅ Kriterium: linke/rechte Hälfte des physischen Monitors zeigen je einen virtuellen Monitor, Maus wechselt zwischen ihnen.
3. Browser auf Monitor 2 verschieben, Spiel (Borderless) auf Monitor 1 → beide gleichzeitig sichtbar und bedienbar.
5. Zwei physische Monitore: nur der gewählte wird aufgeteilt, der andere bleibt unverändert nutzbar.
