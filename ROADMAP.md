# Roadmap & Fortschritt

Legende: ✅ erledigt · 🔨 Code vorhanden, auf Hardware ungetestet · ⬜ offen

## Phase 1 – Technische Machbarkeit ✅
- ✅ Windows-Schnittstellen geprüft (Display Configuration API, IddCx, WGC) → [docs/MACHBARKEIT.md](docs/MACHBARKEIT.md)
- ✅ Architektur festgelegt: IDD + Topologie-Steuerung + Compositor (ADR-001)
- ✅ Treiberanforderungen geklärt: UMDF-Treiber braucht keine Testsignatur (Authenticode + vertrautes Zertifikat)
- ✅ Gaming-Einschränkungen dokumentiert

## Phase 2 – Technischer Prototyp 🔨
- 🔨 IddCx-Treiber: N Monitore, Modi aus Registry, eindeutige EDIDs (`driver/`)
- 🔨 Software-Gerät anlegen/entfernen (`MonivoxDevice.exe`)
- 🔨 Compositor: D3D11 + Windows.Graphics.Capture, skalierter Blit pro Bereich, ClipCursor, Not-Aus
- 🔨 Anordnung der virtuellen Monitore, physischer Monitor geparkt (`SplitSession`)
- 🔨 `monivox list / preview / apply / stop / check / update`
- 🔨 Nur der gewählte Monitor wird aufgeteilt, andere bleiben unverändert; Originalanordnung wird gesichert/wiederhergestellt
- 🔨 Anti-Cheat-Check (Testsignatur, laufende Anti-Cheats)
- 🔨 Auto-Update über GitHub Releases (CI veröffentlicht bei jedem Push auf main)
- ✅ Kernlogik mit Unit-Tests (Layouts, Rundung, Anordnung, Konfigformate, Profile)
- ⬜ **Nachweis auf echter Hardware** (Kriterien in MACHBARKEIT.md §7) – *nächster Schritt*
- ⬜ Messwerte: Latenz, GPU-Last, CPU-Last eintragen

## Phase 3 – Desktop-Anwendung ⬜
- ⬜ WPF-App im Windows-11-Stil (Mica, hell/dunkel)
- ⬜ Bildschirmübersicht + visueller Editor (Bereiche ziehen, Trennlinien verschieben, einrasten)
- ⬜ Profilverwaltung (Datenmodell ✅ in Core)
- ⬜ Programmzuweisung (Datenmodell ✅ in Core)
- ⬜ Treiber- und Statusseite (Gerät, Zertifikat, Compositor-Status, Anti-Cheat-Hinweise)

## Phase 4 – Fensterverwaltung ⬜
- ⬜ Dienst: neue Fenster per `SetWinEventHook(EVENT_OBJECT_SHOW)` erkennen
- ⬜ Fenster auf zugewiesenen virtuellen Monitor verschieben, Zustand setzen
- ⬜ Programme beim Profilladen starten, Positionen merken
- ⬜ Hotkeys zum Profilwechsel, Tray-Icon, Autostart

## Phase 5 – Gaming & Stabilität ⬜
- ⬜ Bildübergabe direkt aus dem Treiber (Shared Texture) – behebt Exklusiv-Vollbild-Probleme, senkt Latenz
- ⬜ `GraphicsCaptureSession.MinUpdateInterval` (Win11 24H2) für > 60 fps Aufnahme
- ⬜ Tests mit Spielen (DX11/DX12/Vulkan, Borderless/Exklusiv), Ergebnisliste veröffentlichen
- ⬜ Watchdog-Dienst, Wiederherstellung nach Absturz
- ⬜ HDR (FP16) ab Windows 11

## Phase 6 – Fertige Anwendung ⬜
- ⬜ WiX-Installer inkl. Treiberinstallation/-deinstallation
- ⬜ Einrichtungsassistent (PbP-Hinweis, Backend-Wahl)
- ⬜ Signatur über SignPath Foundation (kostenlos, braucht öffentliches Repo) oder gekauftes Zertifikat statt Einweg-Zertifikat

## Bekannte offene Punkte
- Treiber liest die Konfiguration nur beim Gerätestart → Layoutwechsel = kurzer Geräteneustart (~1–2 s)
- Monitorzuordnung beim Fremdtreiber erfolgt nach Gerätenummer, nicht garantiert stabil
- Positionierung nutzt `ChangeDisplaySettingsEx`; Umstieg auf `SetDisplayConfig` geplant (atomarer, Pfad-genau)
