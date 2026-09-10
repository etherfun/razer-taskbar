# 电量历史与预测（battery.db / HistoryService）

适用：改动 `src/RazerTaskbar.Core/Services/HistoryService.cs`、`Services/SpikeFilter.cs`、`Services/ReboundFilter.cs`、`Models/HistoryModels.cs`。

## 采样

- SQLite `%APPDATA%\razer-taskbar\battery.db`（WAL，永久保留，表 `samples`/`devices`；schema 与已移除的 Rust 版字节兼容，两者不可同时运行）。
- 采样挂在 watcher 线程每次解析之后（`Record`）：`(connected, charging, level)` 任一变化即写点，静止时 15 分钟心跳（`HeartbeatSecs`，必须远小于 `GapBreakSecs`，否则长静默会被误判为离线）。
- `record_battery_history` 的判定收敛在 `Record` 一处（`Tick` 无条件调用、一次读盘）：关闭时挂件/托盘/悬停不残留冻结预测（历史 bug，2026-09-09 修复）。记录间隔设置只影响 watcher 的轮询节奏；事件驱动下过渡点即时入库。

## 并发模型（2026-09-11 定稿）

- 单一 `SqliteConnection` 被多线程共用（watcher 写、UI 读），而 Microsoft.Data.Sqlite 每连接只允许一条打开的命令：**所有 SQL 必须在 `DbLock` 内执行**（`Record` 写入、`SamplesInRange` 读取、`MergeAlias`/Scrub 的 DB 半边）。锁序恒为 `Sync → DbLock`（`Sync` 护内存结构，`DbLock` 护连接），任何路径不得反序或嵌套反转。
- 估计缓存**增量刷新**：预测是序列的纯函数，`Record` 每轮只重算本轮长出了样本的序列（`dirty`），尾部断连的条目剪除，首次出现的在线尾部补算；全量清空重算会让每秒绘制/托盘刷新与 120ms 悬停 tick 的 `EstimateFor` 每轮都等一次 O(全历史) 计算。`MergeAlias` 合并后重算 dst（序列形状变了）。
- `ConfigService.Save` 原子落盘（tmp + `File.Replace`）：写一半崩溃不再产生半文件被 `Load` 回落为默认值、再被下次 Save 固化覆盖用户配置的链路。`Log` 按 5MiB 轮转到 `.old`。

## 周期切分（`ComputeSpans`）

- 放电/充电会话按 `(level, charging, connected)` 切分；**跨省电关机延续**：断连静默 >30 分钟（`GapBreakSecs`）不计活跃时间但**不结束**周期（图表用同一阈值画暗带，排除出统计）。
- 放电中上升 ≥30 点（`SwapJumpPct`）= 换电，当前周期结束、从新电平另起；跳后立即回落的是报告毛刺，整段丢弃（防伪见下）。

## 三层数据防伪

1. **SpikeFilter（写时）**：瞬时跳变（真机案例 65%→100% 共 20s）先扣住 60s 不入库；窗口内回到跳前电平附近 → 整段丢弃，到期 → 作为真换电提交；跨跳变震荡段（重连形态：跳前锚是陈旧断连心跳）同样丢弃。
2. **Scrub（启动时，`CommittedGlitchTs`）**：把同样的回溯规则应用于已加载序列——60s 内回落的可疑跳变从 battery.db 与内存中删除，治愈写时守卫存在之前（或经其漏洞）入库的坏行。
3. **ReboundFilter（读时）**：无线设备静置后电芯弛豫，唤醒报高读数（真机案例 61%→65%→61%）；放电序列维持包络 env，1~4 点的未充电回升立即钳到 env，回落 ≤env 确认伪影；抬升**在线持续 ≥2h**（`ReboundAcceptSecs`，静置/断连不计时钟）才接受为真实再校准；≥5 点回升（SpikeFilter 已确认的真换电）与充电样本直通并重置 env。**预测/周期统计/健康/显示锚点跑在包络（Deflate）序列上，图表与显示百分比保持原始读数**。契约：公共入口（`Predict`/`CycleStatsOf`/`HealthStatsOf`）收**原始**序列且各只过一遍（非幂等，被接受的次阈值回升二次扫描会被重钳）。

## 三层预测器（`Predict` → `PredictDeflated`）

1. **逐级迁移剖面**（`DischargeTransitProfile`/`ChargeTransitProfile`/`ChargeTimeToFullProfile`/`DischargeTimeToEmptyProfile`）：每个会话贡献相邻高低水位之间观测到的 秒/百分点，EWMA 新近权重（`HalfLifeDays`=30 半衰期）；端点估计逐级求和，未观测档用第 2 层速率填充（`LookupProfile`）。只充到 80%、不满就拔的习惯也计数（只是覆盖更少的档位）；CC-CV 充电尾段与快耗尾段的非线性形状来自真实数据。
2. **混合速率**（`BlendedRate` + `WeightedHoursPerPct`）：历史周期 pooled hours-per-percent，EWMA 新近权重（30 天半衰期，>180 天丢弃——`MaxAgeDays`：小电芯日历老化与习惯漂移按墙钟时间累积，权重按天龄而非周期数）与当前会话已观测速率（`CurrentSessionProgress`）融合，会话权重随已移动百分点增长（`BlendKPct`=8：移动 8 点占半权重，新习惯快速显现而早期噪声被抑制）。
3. **即时速率**：最近 30 分钟（`ReboundFilterTests` 之外的兜底层，新装无历史时可用）。

## 显示锚点（伪倒计时）

`Predict` 在 watcher 线程算好并缓存（`_estimates` 存 `(Estimate, Anchor)`，锚点 `LevelAnchorSecs`）；显示时 `PseudoAdjust` 按锚点以来流逝的时间实时收缩——电平不变倒计时也在走。

## 电池健康（History 页，`HealthStatsOf`）

充电速率是干净的容量衰减代理：充电电流由充电座决定，与使用习惯无关（放电速率则随负载波动）。SOH = 近期 EWMA 充电速率相对最早记录会话的比值；最小二乘衰减趋势外推到行业 80% 寿命终点。

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
- 改动切分/加权/过滤规则必须保持对应用例全绿；ReboundFilter 的"原始序列恰好一次"契约勿破坏。
