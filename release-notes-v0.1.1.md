razer-taskbar v0.1.1

Unzip anywhere and run `razer-taskbar.exe` — no installer. Close a running instance before replacing its files (the process locks `razer-taskbar.dll`).

Requirements: Windows 10/11 x64, .NET 8 Desktop Runtime, and the Windows App SDK Runtime (without it the app still runs, but limited to widget-only mode).

---

# Changelog

## v0.1.1 — 2026-11-10

### Added

* **Tray icon styles** — the notification-area icon can now show the battery percentage as a number instead of the battery glyph. New setting **Tray icon style** (Icon / Number) under *Widget → Show tray icon*. The digits auto-fit the 16px tile (never truncating "100") and are centered on the digit ink, so they read at a glance; the number uses the DIN-style `Bahnschrift SemiCondensed` face for maximum height.
* **Multiple tray icons** — when two or more devices are known, a collapsible **Tray devices** list appears under *Widget → Show tray icon*: check a device and it gets its own tray icon with its own battery, tooltip and style. Unchecking a device removes only that icon; leaving the list empty restores the single icon that follows the widget.
* **Keep recorded devices** — new setting (on by default): devices recorded in the battery history stay visible after they go offline or after a restart, instead of reporting "No devices found". The tray tooltip reads `Name: 53% offline` (no time estimate), the icon is gray, and the hover panel lists the offline rows.

### Fixed

* **Widget no longer blanks itself on every data update** — in true-embed mode `UpdateLayeredWindow` was handed the widget's *screen* rectangle, but for a child of the taskbar band it expects the *parent client* rectangle. Every presented frame therefore moved the widget down by the taskbar origin (measured: +1552 px on each present, and a 5-tick fade drifted it off-screen), and the band-rebuild poke that follows each present snapshotted the already-off-screen window — the visible "completely cleared, then redrawn" flash. Measured before/after: widget blank frames 123/669 (18.4 %, up to 1.28 s) → 0/387; band-composite blanks → 0/250.
* **Widget survives a GPU driver reset** — after a TDR / `nvlddmkm` reset every paint threw `D2DERR_RECREATE_TARGET` (0x8899000C) at `BindDC`; the recovery branch tested the wrong constant (0x88990010) and the shared ink-scanner target was never rebuilt, so the widget stayed blank until a restart (5,076 + 1,507 logged failures, zero successful frames). It now recreates both render targets, logs one line per episode and recovers automatically; a failed paint no longer uploads a cleared frame.
* **Ghost Bluetooth identity archived to the 2.4G identity** — a device used over both Bluetooth and 2.4G could leave a stale `BLE:{mac}` row in `battery.db` that never merged into its serial identity. Three gaps closed: the startup heal now pairs fallback rows by canonical name (bare "Joro" == "Razer Joro", still refusing nested-but-different models), the HID bridge fold merges the history along with the store entry, and a fallback the database already pairs folds even when its counterpart is not answering this round. A real database was healed on the next start: 167 samples re-pointed, 2 same-second ties kept canonical, zero data loss.
* **Charging flag no longer lost across transports** — a reading without charging information (Bluetooth has none) used to discard the whole sample, so the last stretch of a charge vanished from the history; only the flag is held back now, the level still lands.
* **Swap detection calibrated** — the "battery replaced" threshold moved 30 → 20 points and a jump now has to *stick* (no fall-back within 15 minutes), so a real AA-cell swap is detected while a gauge re-anchor after a power cycle stays one cycle. The history chart's swap dots use the same rule.
* **Colored battery icon toggle now reaches the tray** — the tray glyph was always level-colored and the number mode always colorized; both styles now follow the setting (plain discharge uses the green→red palette when on, default white when off; charging / saver / offline state colors always show).
* **Toggle captions follow the app language** — on/off captions on the Settings page no longer fall back to the OS resource language.

### Notes

* Old `settings.json` files keep loading; every new key has a default.
* Windows may put newly registered tray icons in the hidden-icons overflow the first time; drag them out or enable them once in the taskbar settings.
* Tests: 278 passing.
