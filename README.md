# razer-taskbar (Rust)

Display the battery state of Razer products using log messages from Razer Synapse —
floating on the Windows taskbar as a click-through overlay, no PNG assets
(a tray icon exists purely as a fallback menu entry point).

Inspired by [Tekk-Know/RazerBatteryTaskbar](https://github.com/Tekk-Know/RazerBatteryTaskbar),
the top-level-overlay embedding of
[TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)
and the event-driven layout of
[Taskbar-Lyrics](https://github.com/mo-jinran/Taskbar-Lyrics).
Instead of USB communication this app uses Razer Synapse logs, so it supports
more devices (headsets, mice, keyboards) without extra configuration, but
requires Razer Synapse 3 or 4 running in the background.

## Requirements

* Windows 10 or 11
* `Razer Synapse 3` or `Razer Synapse 4` running in the background
* Rust toolchain (build time only)

## Run / build

```powershell
cargo run --release
cargo build --release   # -> target/release/razer-taskbar.exe (~1.3 MB)
```

No installer: copy the single exe anywhere and run it. Enable *Run at
startup* in the Settings window (writes `HKCU\...\Run\RazerTaskbar`).

## How it works

The app monitors the Synapse logs (ported from the old Electron `src/watcher/*`):

* `%LOCALAPPDATA%\Razer\Synapse3\Log\Razer Synapse 3.log` (V3:
  `_OnBatteryLevelChanged` / `_OnDeviceLoaded` / `_OnDeviceRemoved`)
* `%LOCALAPPDATA%\Razer\RazerAppEngine\User Data\Logs\systray_systrayv2*.log`
  (V4: `connectingDeviceData: [...]` JSON, whole history replayed, last snapshot decides connection)

Display pick: the menu-selected device first, else the lowest battery
preferring non-charging devices.

### Taskbar hook

The widget is a top-level `WS_POPUP | WS_EX_LAYERED` overlay window at
`HWND_TOPMOST`, NOT a `WS_CHILD` of the taskbar — a child would be composited
under taskbar-wide effects like TranslucentTB acrylic, while the layered
overlay composites above. The overlay is click-through (`WM_NCHITTEST` →
`HTTRANSPARENT`), so it never steals taskbar clicks.

Layout is event-driven (Taskbar-Lyrics approach):

* The `TaskbarCreated` broadcast (explorer restart) re-binds the taskbar
  handle, resets the hold state, re-adds the tray icon and re-anchors.
* A background UIA `IUIAutomationStructureChangedEventHandler` on the taskbar's
  XAML input site posts a coalesced message on every taskbar layout change
  (tray icons appearing/disappearing, widgets board toggling, …).
* A 1s timer stays as the fallback poll and drives the tray refresh.
  Placement passes dedupe: nothing moves, repaints or logs while the state
  is unchanged, and the Win11 widgets-board UIA query is cached.

Anchoring: Win11 (detected via the `DesktopWindowContentBridge` child) sits at
`TrayNotifyWnd.left - width + 2`, vertically centered; Win10/classic anchors
on the `ReBarWindow32` band (fallback `WorkerW`).

### Hover device list

Resting the cursor on the widget (350ms) opens a small rounded panel above it
listing **every** known device — type icon, battery glyph, name, charging bolt,
level-colored percentage — with the device the widget currently shows
highlighted. Since the overlay is click-through and receives no mouse
input, hover is detected by polling the cursor on a 120ms timer; the panel
itself is topmost, never takes focus and is click-through too. Toggle via
*Show devices on hover* in the Settings window (`hover_devices` in
settings.json, default on).

### Battery history & predicted usage time

Every parse pass also samples the devices into `%APPDATA%\razer-taskbar\battery.db`
(SQLite, retained forever): a point is written when (connected, charging,
level) changes, plus a 15-min heartbeat; the *Record interval* setting
controls how fast the watcher polls for this (1–30s, default 5s).

From that history the widget predicts per device:

* **Usable time left** while discharging — the weighted mean of past
  discharge cycles ("active hours per %", newest 10 cycles full weight,
  next 90 half, older ignored) times the current level. Cycles span across
  power-save shutdowns: disconnected stretches (idle auto-off) count no
  time but do not end a cycle, and a large level jump while discharging is
  treated as a battery swap (a fresh cycle starts at the new level).
* **Time to full** while charging — same weighting over charge sessions,
  tracked separately.

The prediction shows in the hover panel, the tray tooltip, and optionally
on the widget itself (*Show time remaining on widget*; the widget widens to
fit). *Battery history…* opens a viewer window in a dark Win11 style (DWM
rounded corners + dark title bar, WinUI palette, stat cards) with a
level-over-time chart (green = charging, dark bands = off/unknown time
excluded from stats), cycle stats cards and the cycle/session list with
USE/CHARGE badges. Toggle recording via *Record battery history*
(`record_battery_history`, default on).

### Coexistence with other hook tools

Every placement enumerates the taskbar's visible children, skips the OS
whitelist (`Start`, `ReBarWindow32`, `MSTaskSwWClass`, `TrayNotifyWnd`,
`DesktopWindowContentBridge`, …) and shifts past any remaining occupant
(e.g. TrafficMonitor), logging `exe (class)` to stderr (only when the state
changes). Right side shifts left, left side shifts right; position is clamped
into the taskbar band. A hold/grace/jump-cap state machine prevents
leapfrogging with other widgets that have their own avoidance logic, and a
500px jump cap refuses far yields. The Win11 widgets board (weather) is
XAML content invisible to `EnumChildWindows`, so it is avoided via a cached
UIA rect of the `WidgetsButton`, always, regardless of the overlap switch.

### Native icons

The battery is drawn in `WM_PAINT` with plain GDI — rounded outline,
proportional fill (red/orange/yellow/green by level), charging-bolt polygon,
percentage text. No `assets/*.png`, DPI-aware via `GetDpiForWindow`.
Each device's type (headset / mouse / keyboard / other) shows as a small
GDI vector icon ahead of the battery glyph, both on the widget and in the
hover panel. The type comes from the Synapse V4 log's `category` field
(MOUSE / KEYBOARD / HEADSET / …), falling back to product-name keywords for
V3 logs and unknown categories.

## Menu (right-click the tray icon) & Settings window

The overlay itself is click-through, so the tray icon carries the menu. It
stays deliberately small — quick device switching plus entry points:

* Device list (radio, incl. *All devices*)
* Settings… (opens the settings window)
* Battery history… (viewer window)
* Exit

Everything else lives in the **Settings** window (dark Win11-style page,
same chrome as the history viewer), organized into three sections:

* **Widget** — shown device, widget side (left/right), show time remaining,
  avoid overlap, show tray icon, show devices on hover
* **History** — poll interval (5/10/15/30/60s), record battery history,
  record interval (1/2/5/10/30s)
* **General** — language (Auto / English / 中文), run at startup

Every change applies and persists immediately (no OK/Cancel); the watcher
re-reads settings.json each cycle, so nothing needs a restart.

The UI ships in English and Chinese; **Auto** (default) follows the Windows
UI language, switches apply live (`language` in settings.json). Compact
durations (`3h25m`) stay locale-neutral.

Config lives in `%APPDATA%\razer-taskbar\settings.json`.

## Supported hardware

* Potentially any wireless Razer device compatible with Razer Synapse 3 or 4.
* Tested with Razer Blackshark V2 Pro (2023).

## Attributions

* RazerBatteryTaskbar: <https://github.com/Tekk-Know/RazerBatteryTaskbar>
* TrafficMonitor taskbar embedding: <https://github.com/zhongyang219/TrafficMonitor>
* Taskbar-Lyrics overlay + event-driven layout: <https://github.com/mo-jinran/Taskbar-Lyrics>
