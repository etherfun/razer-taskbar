# 电池类型（AA/AAA 可更换电池 vs 内置充电电池）— 交接文档

适用：接手「电池类型」这条线的 agent。包含**已落地的实现**、**本轮 HID 实测结论**、
**发现的日志读取路径**与**未完成事项**。相关背景见 `docs/agent-history.md`（历史/统计口径）、
`docs/agent-hid.md`（HID 协议与探针）、`docs/agent-build.md`（构建/探针）。

> 状态：功能**已实现且测试全绿**（219 用例）；HID 直读**已证伪**；自动检测**已找到可行路径但未实现**。

---

## 1. 为什么需要这个功能

可更换电池（AA/AAA）与内置锂电在数据上有一个致命差别：**前者只有换电池时电量才会跃升**，
而后者跃升必然是一次充电会话。如果混为一谈：

- 一次换电被记成一次"充电会话" → `CycleStats.ChargeHoursPerPct`（单次充电可用）被污染；
- 电池健康卡片用**充电速度**估算容量衰减（`HealthStatsOfSpans`）→ 换电跃升直接毒化衰减趋势。

参考实现：**openrazer 是按机型判定的**——`driver/razermouse_driver.c: razer_attr_read_charge_status`
用一张 PID 名单把 Atheris / Orochi / HyperSpeed 等机型直接短路成 `charge_status = 0`，注释写着
`// Use AA batteries`。它读的不是设备上的某个值，而是"这个型号用 AA 电池"这个事实。

---

## 2. 已落地的实现（勿重复实现）

| 文件 | 内容 |
|---|---|
| `Core/Models/BatteryType.cs`（新增） | `BatteryType{Auto,Rechargeable,Replaceable}`；`Detect(name)` 型号名单；`Resolve(设置, 名称)`；`Parse`/`ToConfig`（`auto`/`rechargeable`/`replaceable`）；`AsReplaceable(samples)` |
| `Core/Services/ConfigService.cs` | 新字段 `device_battery_types`（handle → 拼写）；`Load` 里补 `null` 兜底 + **丢弃无法识别的拼写** |
| `Native/AppState.cs` | `ConfigExt.Clone` 深拷贝该字典（唯一引用类型成员，浅拷贝会让发布快照与权威副本共享可变字典） |
| `Core/Models/HistoryModels.cs` | `Span` 增 `SwapStart`（该循环由换电开启） |
| `Core/Services/HistoryService.cs` | `ComputeSpans` 标记 `SwapStart`（`OpenRun.Swapped`，仅放电段） |
| `Features/ControlPanel/HistoryPage.xaml(.cs)` | 设备选择右侧「电池类型」下拉（自动/内置充电/可更换）+ 灰字回显**实际生效**类型 + 悬停说明；判为可更换时：清充电旗后再切分、健康卡片显示"不适用"、当前剩余改走 `Predict(..., chargingNow:false)`、周期列表换电起点打「换电」黄标；对比设备各按自己的类型处理 |
| `Core/Services/I18n.cs` | 6 条 zh 文案（`Battery type`/`Built-in rechargeable`/`Replaceable battery (AA/AAA)`/`SWAP`/`n/a (replaceable battery)`/说明长句） |
| `tests/.../BatteryTypeTests.cs`（新增） | 名单判定（含反例）、设置优先级、拼写往返、`AsReplaceable` 只清充电旗 |
| `tests/.../HistoryTests.cs` | `BatterySwapJumpEndsCycle` 补 `SwapStart` 断言；新增 `ReplaceableCellNeverProducesChargeSessions` |
| `tests/.../ConfigTests.cs` | `BatteryTypeOverridesLoadAndFilterUnknownSpellings`（缺键/`null`/未知拼写/往返） |

型号名单（`BatteryTypes.ReplaceableModels`，不区分大小写包含匹配）：`atheris`、`orochi`、
`basilisk x hyperspeed`、`basilisk v3 x hyperspeed`、`basilisk mobile`、`deathadder v2 x`、
`naga v2 hyperspeed`、`viper v3 hyperspeed`、`pro click mini`。
HyperSpeed 后缀**写全**，否则会误伤同为 HyperSpeed 的键盘（内置电池）与 V3 Pro / Naga V2 Pro 等机型。

---

## 3. 本轮 HID 实测（Viper V3 HS PID 0x00B8 + Joro 0x02CD combo）

方法：新增只读探针 `--hid-power`，扫描已知可读 class 的 get 半区（0x00/0x02/0x05/0x07/0x0B ×
id 0x80-0xFF，每项 3 轮判"稳定/变化"），在 Synapse 里切换电池类型前后各跑一次做 A/B/A 对比。

### 3.1 结论一：设置**确实写进了设备**，但改变的是百分比换算

鼠标槽 `tx=0x1F`（串号 632516H31000044）的 `cls=0x07 id=0x80`（电量原始值）：

| 设置 | 时间 | raw | ×100/255 | `0x85` byte2 |
|---|---|---|---|---|
| 可充电镍氢 | 01:55 | **161** | 63% | 134 |
| 碱性 | 01:59 | **118** | 46% | 125 |
| 碱性（复测） | 02:02 | **118** | 46% | 136 |
| 镍氢（回切） | 02:08 | **165** | 65% | 117 |

- 电量随设置**来回跳变**（161↔118，回切后 165 ≈ 161 的同一档），而 `0x85` byte2 在**同一设置内**
  也变（125 vs 136）→ 它是活读数（电压类代理），**不是**设置的载体。
- 说明：设备固件按电池类型换了一条 电压→百分比 曲线；Synapse 只是把选择写下去。

### 3.2 结论二：**没有任何可读寄存器承载电池类型**

切换前后逐条比对，**完全一致**的寄存器（两槽都查了）：

- `cls=0x07`：`0x81`（低电阈值 77/22）、`0x82`（**鼠标 85 / 键盘 255，恒定**）、`0x83`（空闲 900s）、
  `0x84`（充电标志）、`0x8D`（`00 00 28 01 9C 01 4F 05`）
- `cls=0x00`：`0x81`、`0x83`、`0x84`、`0x85`、`0x86`、`0x87`、`0x8D`、`0x8E`、`0x91`、`0x92`、`0x93`、
  `0x95`、`0x97`、`0x9F`、`0xA4`、`0xB3`、`0xB8`、`0xBA`、`0xBC`、`0xBF`、`0xC3`、`0xC5`
- `cls=0x02`：`0x82`、`0x84`、`0x98`；`cls=0x05`：`0x80`、`0x81`、`0x8A`；`cls=0x0B`：`0x80`、`0x85`、`0x8B`、`0x8E`

曾把 `cls=0x07 id=0x82` 当作主要嫌疑（紧邻低电阈值，set 半区有天然配对 `0x07/0x02`），
**实测排除**。→ **HID 层读不到电池类型，只能读到被它换算过的百分比。**

### 3.3 附带修正的旧记录

`docs/agent-hid.md` 原写"class 0x07 鼠标槽 8 个 id"，实测**键盘槽同样能读 class 0x07**
（`0x80/0x81/0x82/0x83/0x84/0x87/0x88/0x89/0x8A/0x8B`），且 `tx=0x1F`/`0x3F` 同指鼠标、
`0x9F`/`0xFF` 同指键盘，与既有"tx 路由子设备"结论一致。

---

## 4. 突破：Synapse 自己的日志里**有**电池类型（推荐后续实现）

顺着"设置被写进设备"这条线去翻 Synapse 落盘，在**我们已经在监听的同一个目录**里找到了明文记录。

### 4.1 位置与格式

目录：`%LOCALAPPDATA%\Razer\RazerAppEngine\User Data\Logs`
（= `WatcherService` 已监听的 V4 目录，但**文件名不同**：现有的是 `systray_systrayv2*.log`）

文件名：`products_<productId十进制>_<mw|ui> <containerId><轮转序号>.log`

- `184` = 0xB8 = Viper V3 HyperSpeed；`717` = 0x02CD = Razer Joro
- `containerId` 是设备容器 GUID；`<序号>` 随轮转递增（实测见到 …2/…3/…4/…5.log）

**关键行**（两条，都在 `mw` 文件里）：

```
info: [updateUI] MW_SET_BATTERY_TYPE_TO_UI 1
info: [onDeviceChangeEventReceived] receive {"type":"ON_SET_BATTERY_TYPE","payload":{"batteryType":0}}
```

- `MW_SET_BATTERY_TYPE_TO_UI N`：**设备加载 / Synapse 启动时就会打**
  （实测 2026-09-20 15:24:22/23/26 连续出现）→ **不需要用户改设置也能读到当前值**。
- `ON_SET_BATTERY_TYPE`：用户改动时打（本轮的 01:59:34 → `0`、02:08:01 → `1`）。

同文件还有身份信息可直接配用：
`info: [updateUI] VALID_DEVICES [{"serialNumber":"632516H31000044",...,"productId":184,...}]`
以及 `RzMappingEngine.callMappingEngineAction() … {"action":"localStorageSetItem","payload":{"key":"synapse_184_{9E502CF7-…}"…`。

### 4.2 取值映射（A/B 实测）

| 值 | 含义 | 依据 |
|---|---|---|
| `0` | 碱性（Alkaline） | 01:59 设成碱性时记录 |
| `1` | 可充电镍氢（NiMH） | 02:08 设回镍氢时记录；09-20/09-21 长期为 1，与用户"原本是镍氢"一致 |
| `2` | 锂电（Lithium）**未验证** | 推测，见 §5 P4 |

### 4.3 为什么这条路径有价值

**"某个 productId 出现 `MW_SET_BATTERY_TYPE_TO_UI`"这件事本身，就等价于 openrazer 的
"Use AA batteries" 名单**——Synapse 只为可更换电池的设备提供这个设置。
所以它比我们现在的型号名表更权威，而且**与化学无关**：

- 我们的 `BatteryType` 只需要 `Replaceable`（碱/镍氢/锂电三者**都是**可更换），
  所以直接判"有设置 = Replaceable"即可，无需关心 `0/1/2` 的具体含义；
- 具体化学值可作为将来在历史页展示的附加信息（可选，见 P3）。

---

## 5. 未完成事项（按优先级）

**P1 — 用 products 日志做自动判定（推荐，替代/加固型号名表）**
1. `WatcherService` 增加对 `products_*.log` 的监听（同一目录，新增一个 `FileSystemWatcher` 模式 +
   `ResolveLatest` 轮转解析，参照现有 `systray_systrayv2*.log` 的实现）；
2. 正则抓 `MW_SET_BATTERY_TYPE_TO_UI\s+(?<n>\d+)` 与 `ON_SET_BATTERY_TYPE.*"batteryType":(?<n>\d+)`，
   **从文件名解析 productId**（`^products_(?<pid>\d+)_`）→ 得到 "pid 用可更换电池" 的事实；
3. **持久化学到的 pid**（否则 Synapse 不跑就丢）：建议存 battery.db 的 `devices` 表新列，
   或 settings.json 的一个只读回填键；`BatteryTypes.Detect` 的型号名表保留为离线兜底；
4. 注意：pid 只钉型号不钉个体，而 handle 是串号——接线时用**现有 HID 层的 pid**（`RazerPidTable`
   已经按 pid 组织）或 `VALID_DEVICES` 里的 `serialNumber`↔`productId` 配对。

**P2 — 决定 `--hid-power` 探针的去留**
`docs/agent-build.md` 有一条约定"探针一律测完即删"。当前它**仍在树里**，并已按 `--hid-probe`/
`--hid-scan` 的同等待遇写进了探针表（它是这类"哪条寄存器随设置变化"问题的通用 A/B 工具，
留着复用价值高）。**要么保留并在提交信息里说明，要么按约定摘掉**——别让它悬着。

> **2026-09-22 续挖注记**：本轮把 HID 剩余的两个未开垦维度也扫完了——transaction_id 全空间
> （`--hid-tx`，发现 tx 位 7 选子设备槽，无第三槽/无隐藏路由）与全部可读寄存器的 90 字节全载荷
> （`--hid-deep`，载荷深部恒为零，无第二数据面）。**结论不变：HID 层没有承载电池类型的寄存器**，
> P1 的 `products_*.log` 路径仍是唯一可行方案。细节见 `docs/agent-hid.md` "transaction_id 全空间
> 扫描 + 全载荷深挖"节；顺带解码了 DPI 阶段表与节能配置元组（与电池类型无关）。

**P3 —（可选）展示电池化学**：拿到 `0/1/2` 后可在历史页类型旁显示"碱性/镍氢/锂电"。
注意 I18n 双向守卫（新 `Tr()` 字面量必须进 `ZhMap`，且 `ZhMap` 每个键都必须在 src 里被引用）。

**P4 — 验证 `2` = 锂电**：在 Synapse 里选"锂电"跑一次 `--hid-power --label=lithium`，
确认 `ON_SET_BATTERY_TYPE` 的值为 `2`（只需看日志，不必看寄存器）。

**P5 — 让类型影响显示路径**：目前电池类型**只作用于历史页读路径**（`HistoryPage.Reload`）。
若希望挂件/托盘也不再显示"充电中"，入口在 `HidWatcher.ToRazerDevice`（那里能拿到每轮配置，
可以按 pid/handle 查 `device_battery_types` 后清 `charging`）。注意 §3.1：设备自己已经按类型
换算过百分比，**不要**在显示侧再做任何换算。

---

## 6. 约束与红线

- **只读 HID**：set 半区（id 0x00-0x7F）一律不碰。`0x07/0x02` 是"设置电池类型"的合理配对命令，
  但**写入设备超出本项目范围且风险未知**——不要为了省事去写它。危险命令清单见 `docs/agent-hid.md`。
- **不要在显示侧重算百分比**：类型换算由设备固件完成（§3.1 已证）。
- **`SwapStart` 只对放电段有意义**（`CloseSpan` 里 `swapStart && !charging`）；充电会话永不带此标记。
- **ReboundFilter 契约**：公共入口收**原始**序列且各只过一遍（非幂等）。可更换电池的清旗
  必须在 `Deflate` **之前**做（现在的实现即是），否则会二次处理。
- **I18n 双向守卫**：`I18nTests.TrCallSitesMatchZhMapBothWays` 会同时检查"Tr 字面量必须是键"
  和"键必须在 src 里被引用"。
- 型号名表的**反例保护**：改名单时务必保留 `BatteryTypeTests` 里 HyperSpeed 键盘、
  V3 Pro / Naga V2 Pro 那些"看起来像但其实是内置电池"的反例。

---

## 7. 命令

```powershell
# 构建/测试（测试勿加 --quiet）
dotnet test tests/RazerTaskbar.Tests/RazerTaskbar.Tests.csproj -p:Platform=x64

# 部署（会先停常驻进程；探针改动必须重新 publish 才生效）
powershell -ExecutionPolicy Bypass -File build.ps1 [-Test] [-Run] [-NoRun]

# HID 电源寄存器 A/B（只读；挂件在跑也能用，绕过单实例）
dist\razer-taskbar.exe --hid-power --label=<名字>
#   → %APPDATA%\razer-taskbar\hid-power-<名字>.log

# 其他探针：--hid-probe（全枚举+dump）、--hid-scan（get 半区全段，分钟级）、--ble-vendor
```

读 Synapse 的电池类型记录：

```powershell
$dir = "$env:LOCALAPPDATA\Razer\RazerAppEngine\User Data\Logs"
Get-ChildItem $dir -Filter 'products_*' | Sort-Object LastWriteTime -Descending |
  Select-Object -First 1 |
  ForEach-Object { Select-String -Path $_.FullName -Pattern 'BATTERY_TYPE' | Select-Object -Last 5 }
```

---

## 8. 测试锚点

- `BatteryTypeTests`：名单判定（正例 + 反例）、设置优先级、拼写往返、`AsReplaceable`。
- `HistoryTests.BatterySwapJumpEndsCycle`：`SwapStart` 标记。
- `HistoryTests.ReplaceableCellNeverProducesChargeSessions`：清旗后无充电会话、无健康估算。
- `ConfigTests.BatteryTypeOverridesLoadAndFilterUnknownSpellings`：配置兼容与过滤。
- 改名单 → 同步 `BatteryTypeTests`；改切分/清旗 → 同步 `HistoryTests`；改配置 → 同步 `ConfigTests`。

---

## 9. 本轮实测原始数据（可复查）

日志留档在 `%APPDATA%\razer-taskbar\`：

| 文件 | 内容 |
|---|---|
| `hid-power-nimh.log` | 01:55 镍氢（首轮，仅 class 0x07）→ 电量 raw 161 |
| `hid-power-alkaline.log` | 01:59 碱性 → raw 118 |
| `hid-power-alkaline2.log` | 02:02 碱性（扩展探针，全 class 基线）→ raw 118 |
| `hid-power-nimh2.log` | 02:08 回切镍氢 → raw 165 |
| `hid-probe.log` | 常规探针（含 `raw=118 percent(scaled)=46`、`not charging`） |

Synapse 侧证据：`products_184_mw {9E502CF7-160A-51EA-8250-14BD19EB4A4A}5.log` 中
`ON_SET_BATTERY_TYPE` 两条（01:59:34 → 0、02:08:01 → 1）与历史 `MW_SET_BATTERY_TYPE_TO_UI 1`。
