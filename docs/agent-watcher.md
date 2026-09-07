# 日志监听与设备选择

适用：改动 `src/watcher.rs`、`src/battery.rs`、`src/config.rs` 相关逻辑。

## 数据源

- V3：`%LOCALAPPDATA%\Razer\Synapse3\Log\Razer Synapse 3.log`
  - `_OnBatteryLevelChanged ... Name / Handle / level / state`；`_OnDeviceLoaded` / `_OnDeviceRemoved` 决定在线。
  - 规则：同 handle 取最后一次匹配；`connected = loaded_idx > removed_idx`；缺事件记 `-1`（纯电池记录视为离线，与 TS 一致）。
- V4：`%LOCALAPPDATA%\Razer\RazerAppEngine\User Data\Logs\systray_systrayv2*.log`
  - 文件选择：正则 `^systray_systrayv2(\d*)\.log$`，序号最大者胜（对应 TS `findLatestSynapseV4LogFile`）。
  - 行格式：`[timestamp] ... connectingDeviceData: [...]`；全文件按时间重放，缺席最新快照的设备标离线。
  - 在线集合：最新快照中所有 `serialNumber`/`deviceContainerId`；handle 取 `serialNumber` 为空则回退 `deviceContainerId`；`NOSERIALNUMBER` 在真序列号出现后去重删除。
  - 增量 guard：`last_v4_timestamp` 未变直接返回；最新行 JSON 腐坏不推进 timestamp，下轮重试（避免全员冻结离线）。
- 版本选择（`synapse_version`）：`v3`/`v4` 强制；`auto`（默认）有 V4 候选即用 V4。

## 监听循环（`RazerWatcher::run`）

- 首次 `parse_once` 让挂件立即有内容；`notify` 监听 V3 文件 + V4 目录，事件驱动解析：首个事件置脏，去抖 1s（`EVENT_DEBOUNCE`，整批写入只解析一次）后立即 `parse_once`。
- `polling_throttle_secs` 退化为无事件时的兜底轮询节奏（记录历史时取 `min(polling_throttle_secs, history_poll_interval_secs)`），每轮重读 `config::load()`：菜单改轮询间隔/显示设备即时生效；下限钳制 2s。历史采样骑在每次解析上（事件驱动后过渡点更准时）。

## 显示选择（`battery::pick_device_to_display`）

- 候选：`is_connected && is_selected`（`shown_device_handle` 为空 = 全选，即 UI 中的"当前电量最低设备"自动项；菜单/切换后回写 `is_selected`）。
- 排序：`battery * (charging ? 100 : 1)` 升序取首个 → 非充电优先，电量低优先（与 TS `tray_manager.ts` 一致）。
- 显示模式（C# 版，`Core/Models/DisplayMode.cs` 的 `DisplayModeResolver`，`display_mode` 配置）：
  - `fixed`（默认）：上述规则原样。
  - `drop_swap`：基础 = 上述规则结果；任何在线设备电量严格下降（如 100→99）时临时替换显示该设备 `swap_display_secs` 秒（默认 30），窗口内再降重新计时，到期/断连切回；最新下降优先（同 tick 多台取最低电量），显示设备自身下降视为最新事件直接切回。设备电量基线存运行态，首见只建基线不触发，模式/参数变更时重置。
  - `rotate`：全部在线设备按名称（Ordinal）排序轮播，每台 `rotate_interval_secs` 秒（默认 30），推进按时间戳幂等（挂件绘制与托盘刷新共享一次推进）。
  - 托盘图标与悬停面板高亮跟随同一结果（`DisplayModeState.LastShownHandle`）；选择规则改动须同步 `tests/RazerTaskbar.Tests`（`BatteryTests` + `DisplayModeTests`）。
- 颜色（`color_for`）：0–19 红、20–39 橙、40–59 黄、60–79 浅绿、80+ 绿；离线灰。
- 字形（`fluent_battery_glyph`）：E850–E85A 按 10% 分档；充电且 ≥50% 用 EA93。

## 单测锚点（`watcher.rs #[cfg(test)]`）

- `SAMPLE` 真实 V4 形状（含 camelCase + `null`）：字段映射、`null` 回退、空序列号回退容器 id。
- 充电四态：`Charging=true`，`NoCharge_BatteryFull`/`off`/`""=false`。改解析必须保持这三组用例全绿。
