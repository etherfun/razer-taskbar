# 电量历史与预测（battery.db / HistoryService）

适用：改动 `src/RazerTaskbar.Core/Services/HistoryService.cs`、`Services/SpikeFilter.cs`、`Services/ReboundFilter.cs`、`Models/HistoryModels.cs`。

## 采样

- SQLite `%APPDATA%\razer-taskbar\battery.db`（WAL，永久保留，表 `samples`/`devices`；schema 与已移除的 Rust 版字节兼容，两者不可同时运行）。
- 采样挂在 watcher 线程每次解析之后（`Record`）：`(connected, charging, level)` 任一变化即写点，静止时 15 分钟心跳（`HeartbeatSecs`，必须远小于 `GapBreakSecs`，否则长静默会被误判为离线）。
- `record_battery_history` 的判定收敛在 `Record` 一处（`Tick` 无条件调用、一次读盘）：关闭时挂件/托盘/悬停不残留冻结预测（历史 bug，2026-09-09 修复）。记录间隔设置只影响 watcher 的轮询节奏；事件驱动下过渡点即时入库。
- **WAL 尺寸恒定 ≠ 没在写**：SQLite checkpoint 之后**原位复用** WAL 空间，所以 `battery.db-wal`
  长期停在 ~4.13 MB（1000 页自动 checkpoint 阈值附近）而 mtime 每 5 秒更新一次——判"在不在写"要看
  mtime 与进程写入计数器，不能看文件尺寸。实测一次真实 checkpoint 只回写本代有效帧（几十 KB），
  不是整个 4 MB，所以无需为 WAL 大小额外调优。
- `devices.source` 记每台设备**上次读数是从哪条链路来的**（`BatteryTransport`：Wired/Receiver/Ble/Log，写入见 `UpsertDevice`）。旧库由 `EnsureTables` 自动补列（`pragma_table_info` 探测 + `ALTER TABLE ... DEFAULT ''`），旧行读回按 `Log`（`ParseSavedSource`）。读取入口 `HistoryService.SavedTransport(handle)`；实时值在 `RazerDevice.Transport` 上，优先级链见 `docs/agent-hid.md`。

## 并发模型（2026-09-11 定稿）

- 单一 `SqliteConnection` 被多线程共用（watcher 写、UI 读），而 Microsoft.Data.Sqlite 每连接只允许一条打开的命令：**所有 SQL 必须在 `DbLock` 内执行**（`Record` 写入、`SamplesInRange` 读取、`MergeAlias`/Scrub 的 DB 半边）。锁序恒为 `Sync → DbLock`（`Sync` 护内存结构，`DbLock` 护连接），任何路径不得反序或嵌套反转。
- 估计缓存**增量刷新**：预测是序列的纯函数，`Record` 每轮只重算本轮长出了样本的序列（`dirty`），尾部断连的条目剪除，首次出现的在线尾部补算；全量清空重算会让每秒绘制/托盘刷新与 120ms 悬停 tick 的 `EstimateFor` 每轮都等一次 O(全历史) 计算。`MergeAlias` 合并后重算 dst（序列形状变了）。
- `ConfigService.Save` 原子落盘（tmp + `File.Replace`）：写一半崩溃不再产生半文件被 `Load` 回落为默认值、再被下次 Save 固化覆盖用户配置的链路。`Log` 按 5MiB 轮转到 `.old`。

## 周期切分（`ComputeSpans`）

- 放电/充电会话按 `(level, charging, connected)` 切分；**跨省电关机延续**：断连静默 >30 分钟（`GapBreakSecs`）不计活跃时间但**不结束**周期（图表用同一阈值画暗带，排除出统计）。
- 放电中上升 ≥30 点（`SwapJumpPct`）= 换电，当前周期结束、从新电平另起（新周期带 `Span.SwapStart`，列表按"换电"而非"充电"展示）；跳后立即回落的是报告毛刺，整段丢弃（防伪见下）。
- **电池类型**（`Core/Models/BatteryType.cs`，完整交接见 `docs/agent-battery-type.md`）：openrazer 在 `razermouse_driver.c: razer_attr_read_charge_status` 里用一张 **PID 名单**把 Atheris/Orochi/HyperSpeed 等机型直接短路成 `charge_status = 0`（注释"Use AA batteries"）——即**按机型**判电池类型，而不是读设备上的某个值。同样的"按型号判"名单移植在 `BatteryTypes.Detect`（型号名不区分大小写包含匹配；HyperSpeed 后缀写全，避免误伤同为 HyperSpeed 的键盘与 V3 Pro / Naga V2 Pro 等内置电池机型），`Resolve` 先看用户设置。可更换电池的读数只有**换电池**时才会跃升，且根本没有充电硬件：`BatteryTypes.AsReplaceable` 把序列的充电旗强制清零后再进 `Deflate`/`ComputeSpans`，否则一个假充电旗 + 换电跃升会被当成一次真正的充电会话（`ChargeHoursPerPct`、容量衰减都会被污染）。**作用范围仅限历史页读路径**（`HistoryPage.Reload` 及其对比序列）：HID/日志采集与挂件/托盘显示仍按设备上报原样工作——若要连显示一起纠正，入口在 `HidWatcher.ToRazerDevice`。**HID 直读电池类型已实测证伪**（设备固件只把它用在百分比换算上，无寄存器承载），可行的自动判定路径是 Synapse 的 `products_*.log`，见 `docs/agent-battery-type.md`。
- History 页"充放电循环"计数是**等效满充放循环**（行业口径，`CycleStatsOfSpans`）：合格放电段（`Qualifies`）的 `MovedPct` 累计 ÷ 100——一次完整"充满→放空→再充满"计为 1 次循环，不完整会话按比例累计（每天 5 次 20% 的外出 = 1 而非 5）；低于 5 点/60s 门槛的 flicker 段不计。`CycleStats.Cycles` 为 double，UI 按 1~2 位小数显示。

## 三层数据防伪

1. **SpikeFilter（写时）**：瞬时跳变（真机案例 65%→100% 共 20s）先扣住 60s 不入库；窗口内回到跳前电平附近 → 整段丢弃，到期 → 作为真换电提交；跨跳变震荡段（重连形态：跳前锚是陈旧断连心跳）同样丢弃。`Record` 的配套规则 `CoalesceSample`：**断连行一律记序列可信电量**（`hist[^1]`）、充电旗清零——离线设备无法上报活电平，商店里复读的可能正是毛刺值（真机案例 2026-09-10 05:04：58% 在线 → 断电毛刺 100% → 5.5h 陈旧 100% 断连心跳被 60s 到期当"真换电"整段提交），写可信电平让扣留看到真实回落、按既有规则丢段。
2. **Scrub（启动时，`CommittedGlitchTs`）**：把同样的回溯规则应用于已加载序列——60s 内回落的可疑跳变从 battery.db 与内存中删除，治愈写时守卫存在之前（或经其漏洞）入库的坏行。**跨断连形态**：跳变行之后（或跳变行本身即）断连 = 电平从未被在线确认，陈旧平台行不携带信息；沿断连行继续扫描，下一个**在线**读数（无论电平/充电旗，无论多久之后）重锚定序列，跳变行+平台整段删除；断连行若本身带锚点容差内的可信电平（诚实回现行）则按容差就地确认。真换电不受影响：新电平在线到达并在宽限后保持（`KeepsSwapBridgedByOffThenSteadyNewLevel`）。**规则跑到不动点**（`CommittedGlitchTs` → `GlitchPass` 多轮）：一遍扫描在"重锚定行自身仍是毛刺值"时会漏掉平台最后一行（真机案例 2026-09-18 11:15：合并注入的 80% 平台以一行 **在线 80%** 收段，真实 36% 在 9s 后到达，残留的 80 夹在两条 36 之间，图上就是一根孤峰）。判定的行从序列里去掉后再判一轮，直到某轮无产出——等价于写时扣留在首次回落时丢掉整段待定行的行为。收敛单调（每轮至少少一行），无循环风险。
3. **ReboundFilter（读时）**：无线设备静置后电芯弛豫，唤醒报高读数（真机案例 61%→65%→61%）；放电序列维持包络 env，1~4 点的未充电回升立即钳到 env，回落 ≤env 确认伪影；抬升**在线持续 ≥2h**（`ReboundAcceptSecs`，静置/断连不计时钟）才接受为真实再校准；≥5 点回升（SpikeFilter 已确认的真换电）与充电样本直通并重置 env。**预测/周期统计/健康/显示锚点跑在包络（Deflate）序列上，图表与显示百分比保持原始读数**。契约：公共入口（`Predict`/`CycleStatsOf`/`HealthStatsOf`）收**原始**序列且各只过一遍（非幂等，被接受的次阈值回升二次扫描会被重钳）。
4. **BridgeDropouts（历史页读路径，`Reload`）**：枚举抖动不是关机——真机 DB 实测 2026-09-23：有界断连行 1327 条中 467 条跨度 ≤5 min（14 s ~ 5 min，绝大多数单行），每条都会把曲线切出口子、把会话统计切碎。规则：断连行连续 ≤3 行、两侧均在线、总跨度 ≤5 min → 就地改标在线并携带所属运行段的充电旗（图表连线、离线暗带、会话切分因此一致地视设备为持续在场）；跨度更长或序列首尾无锚的断连保持原样（真实离开，暗带与会话切分不动）。主/对比序列在 Deflate 之前各过一遍。

## 三层预测器（`Predict` → `PredictDeflated`）

1. **逐级迁移剖面**（`DischargeTransitProfile`/`ChargeTransitProfile`/`ChargeTimeToFullProfile`/`DischargeTimeToEmptyProfile`）：每个会话贡献相邻高低水位之间观测到的 秒/百分点，EWMA 新近权重（`HalfLifeDays`=30 半衰期）；端点估计逐级求和，未观测档用第 2 层速率填充（`LookupProfile`）。只充到 80%、不满就拔的习惯也计数（只是覆盖更少的档位）；CC-CV 充电尾段与快耗尾段的非线性形状来自真实数据。
2. **混合速率**（`BlendedRate` + `WeightedHoursPerPct`）：历史周期 pooled hours-per-percent，EWMA 新近权重（30 天半衰期，>180 天丢弃——`MaxAgeDays`：小电芯日历老化与习惯漂移按墙钟时间累积，权重按天龄而非周期数）与当前会话已观测速率（`CurrentSessionProgress`）融合，会话权重随已移动百分点增长（`BlendKPct`=8：移动 8 点占半权重，新习惯快速显现而早期噪声被抑制）。
3. **即时速率**：最近 30 分钟（`ReboundFilterTests` 之外的兜底层，新装无历史时可用）。

## 显示锚点（伪倒计时）

`Predict` 在 watcher 线程算好并缓存（`_estimates` 存 `(Estimate, Anchor)`，锚点 `LevelAnchorSecs`）；显示时 `PseudoAdjust` 按锚点以来流逝的时间实时收缩——电平不变倒计时也在走。

## 电池健康（History 页，`HealthStatsOf`）

充电速率是干净的容量衰减代理：充电电流由充电座决定，与使用习惯无关（放电速率则随负载波动）。SOH = 近期 EWMA 充电速率相对最早记录会话的比值；最小二乘衰减趋势外推到行业 80% 寿命终点。

**可更换电池（AA/AAA）不适用**：它没有充电会话，充电速率这个代理不存在（且换电池跃升不是充电）。判定为 `Replaceable` 时历史页直接让健康卡片显示"不适用"，`HealthStatsOfSpans` 拿到空充电列表自然返回 null（两条路一致，不靠"数据不足"朦胧处理）；同一台设备的"当前剩余"也改走 `Predict`（`chargingNow = false`）而不是缓存里那份带着噪声充电旗的估计。

## 电池类型设置（历史页）

设备选择右侧的"电池类型"下拉：自动（型号名单）/ 内置充电电池 / 可更换电池（AA/AAA），选择写入 `settings.json` 的 `device_battery_types`（handle → `rechargeable`|`replaceable`，选"自动"则删键）。下拉旁的灰色小字回显**实际生效**的类型，避免"自动"变成黑箱。落盘走 `AppState.PostModifyConfig`（配置权威副本在挂件线程）；`Load` 会丢弃拼写无法识别的值（`Parse` 会读回 auto，留着只会掩盖真实设置），并兼容 `"device_battery_types": null` 的手改文件。对比设备各自按自己的类型处理（图表充电色块不会给 AA 设备涂绿）。

## 关键常量

| 常量 | 值 | 语义 |
|---|---|---|
| `HeartbeatSecs` | 15 min | 静止心跳间隔，必须远小于 `GapBreakSecs` |
| `GapBreakSecs` | 30 min | 静默阈值：不计活跃时间但不结束周期；图表同阈值画暗带 |
| `SwapJumpPct` | 30 | 放电中上升 ≥此值 = 换电 |
| `HalfLifeDays` | 30 | EWMA 半衰期（日历老化/习惯漂移按墙钟累积） |
| `MaxAgeDays` | 180 | 周期数据截断龄（老化膝点后由新近权重尾巴跟踪） |
| `BlendKPct` | 8 | 当前会话混合常数（已移动 8 点 = 半总权重） |

## 测试锚点

- `HistoryTests`：span 切分、加权速率、预测。
- `ReboundFilterTests`：11 条用例含真实案例基准（61→65→61）。
- `HistoryScrubTests`：启动回溯清洗。
- `HistoryAliasTests`：合成句柄（`HID:{pid}`/`BLE:{mac}`）别名合并。
- `TransportPriorityTests`：`devices.source` 的 upsert/读回与旧库补列（`EnsureTables` 的迁移分支）。
- `BatteryTypeTests`：型号名单（AA/AAA 机型 vs HyperSpeed 键盘/V3 Pro 等内置电池机型）、设置优先级、配置拼写往返、`AsReplaceable` 只清充电旗。
- `ConfigTests.BatteryTypeOverridesLoadAndFilterUnknownSpellings`：`device_battery_types` 的缺键/`null`/未知拼写与往返。
- 改动切分/加权/过滤规则必须保持对应用例全绿；ReboundFilter 的"原始序列恰好一次"契约勿破坏。
