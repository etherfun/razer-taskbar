# 日志监听与设备选择

适用：改动 `src/RazerTaskbar.Core/Services/WatcherService.cs`、`Core/Services/HidWatcher.cs`、`Core/Models/DeviceModels.cs` 相关逻辑。

## 数据源

- V3：`%LOCALAPPDATA%\Razer\Synapse3\Log\Razer Synapse 3.log`
  - `_OnBatteryLevelChanged ... Name / Handle / level / state`；`_OnDeviceLoaded` / `_OnDeviceRemoved` 决定在线。
  - 规则：同 handle 取最后一次匹配；`connected = loaded_idx > removed_idx`；缺事件记 `-1`（纯电池记录视为离线，与 TS 一致）。
- V4：`%LOCALAPPDATA%\Razer\RazerAppEngine\User Data\Logs\systray_systrayv2*.log`
  - 文件选择：正则 `^systray_systrayv2(\d*)\.log$`，序号最大者胜（对应 TS `findLatestSynapseV4LogFile`）。
  - 行格式：`[timestamp] ... connectingDeviceData: [...]`；批次内按时间顺序重放，缺席最新快照的本源设备标离线（`_v4Known` 驱动的断连规则，等价旧全文件重放对每条记录重算在线的净效果）。
  - **增量读取（2026-09-11）**：日志 ~5MiB 轮转，每轮只读上次 offset 之后的新增字节；字节级 pending 缓冲持有未换行的残行（UTF-8 字符不被切片边界劈开），到齐才解析。路径变化/长度收缩（轮转/截断）→ 复位；读取失败不推进 offset，下轮重试同区域。
  - **首读走尾部（2026-09-21）**：复位后的第一次读取只取**末尾 256 KiB**（`V4TailBytes`），不再整文件读——连接集合只由**最后一条**快照决定，批次按 handle 末次胜出，所以尾部足够；整文件读是每次启动 4-5 MiB 的突发（真机实测单文件一次 3,988,907 B，任务管理器里那 0.1 MB/s 就是它）。切片起点落在行中间时**丢掉首个残段**（`V4CompleteLines(startsMidLine: true)`）：在 JSON 数组内部切断可能留下看起来像快照行的文本。
  - 串号→名称收割（`HarvestSerialNames`）与 BLE 身份（`HarvestBleIdentities`）同样读尾部，但**尾部只是快路径而非上界**：名字是历史事实（设备睡一小时就只在旧行里有，真机实测该映射距 EOF 475 KB，固定窗口会把键盘降级成 `Razer Keyboard` 占位名）。因此指定串号在尾部查不到时回退整文件读（`NeedsWholeFile`）；`HidWatcher` 另有更便宜的一层：先用历史库已持久化的设备名（`HistoryService.SavedName`，handle 即串号），占位名不算名字、不接受。
  - 在线集合：最新快照中所有 `serialNumber`/`deviceContainerId`；handle 取 `serialNumber` 为空则回退 `deviceContainerId`；`NOSERIALNUMBER` 在真序列号出现后去重删除。
  - 增量 guard：`lastV4Timestamp` 未变直接返回；末快照行 JSON 腐坏不推进 timestamp（完整但腐坏的行永不自愈，直接跳过等下一真实快照）。
- 版本选择（`synapse_version`）：`v3`/`v4` 强制；`auto`（默认）有 V4 候选即用 V4。

## 监听循环（`WatcherService` watcher 线程）

- 首次 `ParseOnce` 让挂件立即有内容；`FileSystemWatcher` 监听 V3 文件 + V4 目录，事件驱动解析：首个事件置脏，去抖 1s（整批写入只解析一次）后立即 `ParseOnce`。
- `polling_throttle_secs` 退化为无事件时的兜底轮询节奏（记录历史时取 `min(polling_throttle_secs, history_poll_interval_secs)`，ulong 域钳制防手编配置回绕），每轮**读一次** `ConfigService` 并贯穿 ParseOnce/ParseV3/ParseV4（2026-09-11 起不再一轮 2-3 次整读 settings.json；改动仍下轮生效）：设置页改轮询间隔/显示设备即时生效；下限钳制 2s。历史采样骑在每次解析上（事件驱动后过渡点更准时）。

## 显示选择（`DeviceSelector.PickDeviceToDisplay` + `DisplayModeResolver`）

- 候选：`is_connected && is_selected`（`shown_device_handle` 为空 = 全选，即 UI 中的"当前电量最低设备"自动项；菜单/切换后回写 `is_selected`）。
- 排序：`battery * (charging ? 100 : 1)` 升序取首个 → 非充电优先，电量低优先（与 TS `tray_manager.ts` 一致）。
- 显示模式（C# 版，`Core/Models/DisplayMode.cs` 的 `DisplayModeResolver`，`display_mode` 配置）：
  - `fixed`（默认）：上述规则原样。
  - `drop_swap`：基础 = 上述规则结果；任何在线设备电量严格下降（如 100→99）时临时替换显示该设备 `swap_display_secs` 秒（默认 30），窗口内再降重新计时，到期/断连切回；最新下降优先（同 tick 多台取最低电量），显示设备自身下降视为最新事件直接切回。设备电量基线存运行态，首见只建基线不触发，模式/参数变更时重置。
  - `rotate`：全部在线设备按名称（Ordinal）排序轮播，每台 `rotate_interval_secs` 秒（默认 30），推进按时间戳幂等（挂件绘制与托盘刷新共享一次推进）。
  - 托盘图标与悬停面板高亮跟随同一结果（`DisplayModeState.LastShownHandle`）；选择规则改动须同步 `tests/RazerTaskbar.Tests`（`BatteryTests` + `DisplayModeTests`）。
- 颜色（`color_for`）：0–19 红、20–39 橙、40–59 黄、60–79 浅绿、80+ 绿；离线灰。
- 字形（`fluent_battery_glyph`）：E850–E85A 按 10% 分档；充电且 ≥50% 用 EA93。

## 单测锚点（`tests/RazerTaskbar.Tests`）

- `WatcherV4Tests`：真实 V4 形状（含 camelCase + `null`）：字段映射、`null` 回退、空序列号回退容器 id；心跳收割；`V4CompleteLines` 的切片拼装（尾部读丢首残段、增量路径不丢、单行窗口全挂起）与 `NeedsWholeFile` 的整文件回退判据。
- `WatcherV3Tests`：V3 正则事件解析。
- 充电四态：`Charging=true`，`NoCharge_BatteryFull`/`off`/`""=false`。改解析必须保持这些用例全绿。
- HID 直读侧（`HidWatcher`/身份桥接）见 `docs/agent-hid.md`；历史预测见 `docs/agent-history.md`。
