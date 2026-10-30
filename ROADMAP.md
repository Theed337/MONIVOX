# Roadmap & Fortschritt

Legende: ✅ erledigt · 🔨 Code fertig, CI grün, auf Hardware ungetestet · ⬜ offen

## Phase 1 – Machbarkeit ✅
- ✅ Schnittstellen geprüft, Architektur: virtueller Anzeigetreiber + Anordnung + Compositor → [docs/MACHBARKEIT.md](docs/MACHBARKEIT.md)
- ✅ Treiber: mitgelieferter, offiziell signierter Virtual Display Driver (kein Testmodus, keine eigene Signatur nötig)

## Phase 2 – Prototyp 🔨
- 🔨 Treiber-Steuerung: Monitoranzahl/Auflösungen per vdd_settings.xml, Neuladen per Pipe, Fallback Geräteneustart
- 🔨 Compositor: D3D11 + Windows.Graphics.Capture, Not-Aus, Maus bleibt aus dem verdeckten Bereich
- 🔨 Nur der gewählte Monitor wird aufgeteilt; Originalanordnung wird gesichert und wiederhergestellt
- 🔨 Kleine Bereiche (< 640×480) über hochskalierte virtuelle Monitore im gleichen Seitenverhältnis
- ⬜ **Nachweis auf echter Hardware** – nächster Schritt
- ⬜ Messwerte: Latenz, GPU-/CPU-Last

## Phase 3 – Oberfläche 🔨
- 🔨 WPF-App im Windows-11-Stil (hell/dunkel), Infobereich mit Doppelklick/Rechtsklick-Menü
- 🔨 Freier Editor (Aufziehen, Verschieben, Kanten, Einrasten, pixelgenaue Eingabe), Vorlagen optional
- 🔨 Profile: anlegen, umbenennen, löschen, Export/Import, Wechsel aus dem Tray
- 🔨 Status-Seite: Treiber installieren, Anti-Cheat-Check, Protokoll
- 🔨 Autostart über geplante Aufgabe (keine UAC-Abfrage, auch im Akkubetrieb)

## Phase 4 – Fensterverwaltung 🔨
- 🔨 Programme Bereichen zuweisen (Fenster wird beim Öffnen platziert: maximiert/Fenster/randlos/minimiert)
- 🔨 Programme mit Profil starten (ohne Adminrechte über den Explorer)
- ⬜ Tastenkombinationen zum Profilwechsel

## Phase 5 – Gaming, DRM & Stabilität ⬜
- 🔨 **Direktbereich** (echter Monitor, keine Latenz, DRM) mit Maus-Brücke (Raw Input + SetCursorPos, keine Hooks)
  und angepasstem Arbeitsbereich (Maximieren füllt genau den Direktbereich)
- 🔨 Farbige, ein-/ausblendbare Rahmen mit Beschriftung (nicht in Aufnahmen sichtbar)
- ⬜ Taskleiste: liegt sie unten auf dem aufgeteilten Monitor und ist der untere Teil virtuell, ist sie verdeckt
- ⬜ Mehrere Monitore gleichzeitig aufteilen
- ⬜ `MinUpdateInterval` (Windows 11 24H2) für > 60 fps in virtuellen Bereichen
- ⬜ Spieletests (DX11/DX12/Vulkan, Borderless/Exklusiv), Ergebnisliste
- ⬜ Energiesparen/Standby, Monitor ab- und anstecken im laufenden Betrieb testen

## Phase 6 – Fertige Anwendung ⬜
- ⬜ Installer (WiX) inkl. .NET-Laufzeit-Prüfung, Startmenü, Deinstallation (Treiber + Autostart entfernen)
- ⬜ Einrichtungsassistent beim ersten Start
- ⬜ Codesignatur für MONIVOX selbst (SignPath Foundation – jetzt möglich, da das Repo öffentlich ist; Antrag nötig)

## Hinweise
- ✅ Repository ist öffentlich – Auto-Update über GitHub Releases funktioniert ohne Zugangsdaten.
