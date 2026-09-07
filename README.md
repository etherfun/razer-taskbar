# razer-taskbar

Display the battery state of Razer products as a floating widget on the
Windows taskbar — a click-through overlay, no PNG assets (a tray icon exists
purely as a fallback menu entry point). C# / WinUI 3 implementation; the
original Rust version lives in the git history.

Battery data is read **directly from the hardware** (`battery_source=auto`):
USB HID vendor feature reports on the 2.4G dongle / cable, Razer's private
vendor GATT channel on Bluetooth — so Razer Synapse is *optional*: it is only
the fallback data source (devices the direct queries can't reach, e.g.
headsets) and the identity/charging bridge when the Bluetooth channel is
busy.

Inspired by [Tekk-Know/RazerBatteryTaskbar](https://github.com/Tekk-Know/RazerBatteryTaskbar),
the top-level-overlay embedding of
[TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)
and the event-driven layout of
[Taskbar-Lyrics](https://github.com/mo-jinran/Taskbar-Lyrics).

## Requirements

* Windows 10 or 11
* .NET 8 SDK (build time only)
* Windows App SDK Runtime — optional: without it the app degrades to
  widget-only mode (no settings/history windows)
* Razer Synapse 3 or 4 — optional: fallback battery source and the Bluetooth
  identity/charging bridge

## Run / build

```powershell
dotnet build src/RazerTaskbar/RazerTaskbar.csproj -c Release -p:Platform=x64
# -> src/RazerTaskbar/bin/x64/Release/net8.0-windows10.0.22621.0/win-x64/razer-taskbar.exe
dotnet test  tests/RazerTaskbar.Tests/RazerTaskbar.Tests.csproj
```

No installer: the exe (plus its DLLs) can be copied anywhere and run. Enable
*Run at startup* in the Settings window (writes `HKCU\...\Run\RazerTaskbar`).

Diagnostic probes (same exe): `--hid-probe` (HID enumeration + battery/charging
queries + GATT dump), `--hid-scan` (read-only vendor command sweep),
`--ble-vendor` (`--power` / `--sweep` / `--raw=…`, see `docs/agent-csharp.md`).

## How it works

Battery polling (`battery_source=auto`) tries, in order:

1. **Direct HID** (dongle / cable) — 90-byte vendor feature reports, below.
2. **Razer vendor GATT channel** (Bluetooth) — battery *and* charging from
   the device itself, below.
3. **Synapse logs** — for devices the direct queries can't reach (e.g.
   headsets), ported from the old Electron `src/watcher/*`:
   * `%LOCALAPPDATA%\Razer\Synapse3\Log\Razer Synapse 3.log` (V3:
     `_OnBatteryLevelChanged` / `_OnDeviceLoaded` / `_OnDeviceRemoved`)
   * `%LOCALAPPDATA%\Razer\RazerAppEngine\User Data\Logs\systray_systrayv2*.log`
     (V4: `connectingDeviceData: [...]` JSON, whole history replayed, last snapshot decides connection)

The V4 log's periodic heartbeat device array doubles as the **identity
bridge**: it logs every paired device with its canonical serial regardless of
transport, so a device seen under the dongle PID, the cable PID and the BLE
MAC collapses into one identity — the battery history follows the physical
device across all three modes.

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
* Tested with Razer Blackshark V2 Pro (2023), Razer Joro (keyboard, all three
  connection modes) and Razer Viper V3 HyperSpeed (mouse, dongle + cable).

### What each connection mode provides

Battery data comes from direct device queries (`battery_source=auto`, no
Synapse needed) with Synapse logs as fallback. Verified on the Joro keyboard
and Viper V3 HyperSpeed mouse; the raw protocol notes live in
`docs/agent-hid.md`.

| | 2.4G dongle | Wired (cable mode) | Bluetooth (BLE) |
|---|---|---|---|
| Battery level | ✅ direct HID | ✅ direct HID | ✅ Razer vendor GATT channel (Scaled255) → GATT Battery Service |
| Charging state | ⚠️ mouse slot verified, keyboard slot untested | ✅ direct HID | ✅ Razer vendor GATT channel → Synapse heartbeat |
| Serial identity | ✅ direct HID | ✅ direct HID (same serial as dongle) | ✅ Razer vendor GATT channel (full serial) → Synapse heartbeat → `BLE:<MAC>` |
| Device name & type | ✅ product string (combo-dongle keyboard slot named via Synapse log) | ✅ product string | ✅ GAP name + Synapse log category |
| Works without Synapse running | ✅ | ✅ | ✅ — when Razer's services actively hold the vendor channel, level falls back to the Battery Service and identity/charging degrade as above |
| Predicted usable / time-to-full (history) | ✅ | ✅ | ✅ |

The three modes share one device identity (the same serial across dongle,
cable and Bluetooth), so the battery history follows the physical device, not
the transport.

#### USB HID (dongle / cable) — 90-byte vendor feature report

Ported from the reverse-engineered OpenRazer kernel driver (`razer_report`,
`static_assert(sizeof == 90)`; hid.dll feature buffers carry a leading
report-ID byte, so wire buffers are 91 bytes):

* request `[status=00][tx][remaining=0,2B][protocol=0][data_size][class][id][args…][crc][00]`
  — battery = class `0x07` id `0x80`, charging = `0x07` `0x84`, serial =
  `0x00` `0x82` (22-byte ASCII argument)
* response validated in order: status (`0x02` success; `0x01` busy /
  `0x04` no-response → retry, the wireless link sleeps and the first query
  after idle usually no-responses; `0x03`/`0x05` final), echo of
  tx+class+id, CRC = XOR of payload bytes 2..87 (no constant). The value is
  `arguments[1]`
* the transaction id is per PID (`RazerPidTable.cs`: mice `0x1F`, keyboards
  `0x9F`, unknown PIDs probe `0x1F/0x9F/0x3F/0xFF`); combo dongles host
  several sub-devices behind one receiver, each with its own slot ids
* battery scale per firmware generation (`BatteryScale`): legacy `0..255`
  (Scaled255, ×100/255) vs direct `0..100`, with an above-100-means-255
  heuristic for unknown PIDs

#### Bluetooth LE — Razer's private vendor GATT channel

Service `52401523-f97c-7f90-0e7f-6c6f4e36db1c` (undocumented; reverse
engineered from HCI captures of Synapse 4 — method and full command table in
`docs/agent-hid.md`):

* `52401524` (Write) = command channel: 8-byte frame
  `[seq][payload_len]00 00[page][id][param:2]`; writes append `payload_len`
  bytes in a second Write Request
* `52401525` (Read+Notify) = response: 20-byte register snapshots, header
  `[echo_seq][len]00 00 00 00 00[tag]` with tag `0x02` = ok / `0x05` =
  unknown command; `len` payload bytes follow in further snapshots and every
  byte past the declared length is stale register content — never
  interpreted
* verified commands: battery = page `05` id `81` (Scaled255), charging =
  `05`/`85` (`0`/`1`), serial = `01`/`83` (22-byte ASCII)
* when Razer's services actively hold the channel (characteristics enumerate
  but fail to open until their session ends) the app degrades: level from
  the standard Battery Service `0x180F`/`0x2A19` (no charging flag), serial
  from the Synapse heartbeat, else `BLE:<MAC>`
* keyboard-local shortcuts (e.g. FN+ESC power saving) are firmware-internal
  and invisible to the host

#### Synapse log fallback

V3 regex events carry level + charging state directly. V4 replays the
`connectingDeviceData` JSON arrays and harvests the ~1/min heartbeat device
array (canonical serial, category, `chargingStatus`); a heartbeat older than
10 minutes no longer supplies the charging flag.

## Attributions

* RazerBatteryTaskbar: <https://github.com/Tekk-Know/RazerBatteryTaskbar>
* TrafficMonitor taskbar embedding: <https://github.com/zhongyang219/TrafficMonitor>
* Taskbar-Lyrics overlay + event-driven layout: <https://github.com/mo-jinran/Taskbar-Lyrics>
