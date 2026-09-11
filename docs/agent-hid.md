# HID 直读电量（免 Synapse 数据源）

`battery_source`（默认 `"auto"`）：绕过 Synapse 日志，直接通过 USB HID 报文查询设备电量。
`auto` = 每轮先直读 HID，查到至少一台电量设备就用；查不到才回退 Synapse 日志解析。
`hid` = 仅直读；`log` = 仅日志（旧行为）。设置页暂未暴露，改 `%APPDATA%\razer-taskbar\settings.json`。

## 协议速查（OpenRazer 逆向成果，多实现交叉验证）

90 字节 vendor feature report；hid.dll 缓冲区多 1 字节 report ID（0x00），共 91 字节。

| 偏移(载荷) | 字段 | 查电量取值 |
|---|---|---|
| 0 | status | 0x00（请求） |
| 1 | transaction_id | 按 PID 查表，见下 |
| 2..3 | remaining_packets | 0（大端） |
| 4 | protocol_type | 0 |
| 5 | data_size | 0x02 |
| 6 | command_class | 0x07（BATTERY） |
| 7 | command_id | 0x80 电量 / 0x84 充电 |
| 8..87 | arguments | 0 |
| 88 | crc | XOR 载荷字节 2..87（无常量；tx=0x1F 查电量为 0x85） |
| 89 | reserved | 0 |

- 响应校验：status=0x02 成功（先于 echo 校验，忙/无响应的应答头可能是旧命令的）；
  echo（tx/class/id）+ CRC；`arguments[1]`（整包 byte 10）= 电量原始值 / 充电标志。
- 电量缩放：按 PID 显式表项（`RazerPidTable`，`BatteryScale` 枚举）：**鼠标世代 = Scaled255，raw × 100 / 255**
  （0..255；已在 Viper V3 HyperSpeed 真机确认：raw 161 = 63%，与 Synapse 一致）；直读百分比机型 = Direct100；
  未知 PID 用 `BatteryScale.Auto` 启发式（≤100 视为直读百分比，>100 按 0..255 缩放）。
- status：0x01 忙 / 0x04 无响应 → 重发；0x03 失败 / 0x05 不支持 → 放弃。
- 无线链路空闲会休眠：空闲后第一次查询往往 NoResponse，靠重发唤醒（重试间隔递增 100/200/350/500ms，
  最多 5 次；xzeldon 用 10×500ms）。

## 危险命令清单（扫描/实验时的禁区）

全部已知破坏性命令位于 **set 半区（id 0x00-0x7F）**：配对 `0x00/0x41 + 0x46`、
**解除配对 `0x00/0x42`**（会把子设备踢出接收器）、0x00/0x40（配对族邻近写命令）、
充电控制 `0x07/0x10`、出厂测试模式 `0x02/0x00`、LED 矩阵大块写 `0x03/0x0B+0x0C`。
get 半区的 0x00/0xC1、0xC2、0xC6 是配对命令的未文档化镜像，实验时一并跳过。

## 多设备合并接收器（combo dongle）

鼠标+键盘共用一只接收器时（如 Viper V3 HS + Joro 套装，PnP 里只有 0x00B8 一个 USB 设备），
**同一厂商 feature 通道按 transaction id 路由到不同子设备**（真机 2026-09 实测）：

- `tx=0x1F`（以及 0x3F/0x0F/0x2F）→ 鼠标（serial 632516H31000044，raw 156 = 61%）
- `tx=0x9F`（以及 0xFF）→ 键盘（serial SI2522F18701637，raw 247 = 97%，×100/255 缩放；
  与蓝牙厂商通道 05/81 同值——跨传输电量一致）
- **充电查询按槽应答**（0x07/0x84，2026-09-07 补测）：0x1F/0x9F 均 Success flag=0（未插线）；
  "只插线不切模式"时 flag 是否翻 1 待测
- arguments 寻址无效（0x9F args0=0x01 → NotSupported）；键盘槽用短重试预算（2 次，通常缺席时不拖慢轮询）
- 串号按 tx 缓存于会话；**查询失败不缓存**（风暴期打开会话时拿不到串号，若缓存会导致
  `HID:{pid}` 假身份落库），未解析时每轮重试
- 键盘槽的显示名：产品字符串是鼠标名，且**厂商协议没有名称命令**（INFO 类 0x00 实测只有
  0x81 固件 / 0x82 串号 / 0x84 模式；OpenRazer 的设备名也是内核驱动按 PID 硬编码的
  `device_type` switch，daemon 只读 sysfs）。名称来源：优先从 Synapse V4 日志收割
  `serialNumber → name.en`（`WatcherService.HarvestSerialNames`，Synapse 曾运行过即可，
  10 分钟重试一次），兜底 "Razer Keyboard"；Kind=Keyboard 由槽位角色保证。
  键盘自有 dongle（产品名即键盘名）保留产品名；Joro 有线见下节。

## 有线（线缆）模式（2026-09-07 真机实测，Razer Joro）

Joro 机身开关切到线缆模式后枚举为独立 USB 复合设备 **PID 0x02CD**（产品字符串
"Razer Joro"，无线链路断开，dongle 键盘槽 NoResponse）：

- **厂商 feature 通道在 MI_03**（usagePage 0x0001/consumer control，feature=91）——
  不总是鼠标 TLC；MI_00/MI_01 全部 `SetFeature err=1`，按既有黑名单机制跳过。
- **tx=0x1F**（键盘用鼠标代际的 tx）：串号查询返回 `SI2522F18701637`，**与 dongle 键盘槽
  完全一致** → 有线/无线共用一个 `HandleFor` 身份，切换模式历史无缝衔接（devices 表
  不产生 `HID:02CD` 伪行）。电量 raw 255 = 100%（Scaled255），**充电中 0x07/0x84 = 1**
  ——充电状态只有有线（或充电座）能观察到，是线缆模式的核心收益；充满 100% 后 flag
  自动回落 0（真机 18:09 充电中=1 → 18:14 满电=0），非充电=0、充电中=1 语义干净。
  满电停充时设备实际由 USB 供电，故挂件显示规则：**有线 PID + 100% → 视为充电**
  （`RazerPidTable.IsWiredDevice`，仅设备本体枚举的 PID；dongle 槽位/BLE 满电时在电池上，
  绝不套用）。
- 表项：`RazerPidTable.ExplicitSlots[0x02CD]` 单 Keyboard 槽（tx 0x1F、Scaled255）；
  Known 表同 PID 条目保证探针/缩放查询直接命中。
- 仅插线不切模式 = 只充电（**已实测**）：0x02CD 不出现、2.4GHz 链路保持、电量经 dongle
  键盘槽实时上涨（247→255，拔线回落 249 = 真实满格非占位值），但 **dongle 键盘槽
  0x07/0x84 恒 0 不报告充电**——dongle+坞充的充电图标只能靠 Synapse 心跳
  （`chargingStatus="Charging"` 正常出现）。
- Joro 蓝牙模式见下节。

## 蓝牙（BLE）模式（2026-09-07 真机实测，Razer Joro）

Joro 切蓝牙并配对后走 **BTHLE**（HID-over-GATT，服务 UUID `{00001812-…}`），
形态与 USB 完全不同：

- **VID/PID 都变**：接口路径形如 `…_dev_vid&02068e_pid&02ce_rev&0001_<BLE MAC>&colNN`——
  `02` 是蓝牙 SIG 的 id-source 前缀，真实 **VID 0x068E**（Razer 的 BLE VID，Synapse 的
  `RZCONTROL\VID_068E&PID_02CE` 印证），**PID 0x02CE**（≠ 有线 0x02CD）。经典蓝牙路径
  （BTHENUM，7 位 VID `vid&0001532`）未在本机出现——Joro 是纯 BLE。解析规则统一取
  VID 段**后 4 位**（`0001532`→0x1532，`02068e`→0x068E）。
- **厂商 feature 通道不存在**：BLE report map 只有标准 HID collection，91 字节厂商
  缓冲被拒（键盘 TLC 报 `err=87` ERROR_INVALID_PARAMETER，其余 `err=1`）——`err=87`
  与 `err=1` 同为确定性失败，进黑名单。GATT 全表只有标准服务（0x1800/0x180A/0x180F），
  **无 Razer 厂商服务**。
- **电量走 GATT Battery Service（0x180F/0x2A19，0-100% 直读）**——Windows 设置同源。
  `BleBattery`（`src/RazerTaskbar.Core/Hid/BleBattery.cs`）每轮 uncached 读一次，
  设备不可达时按缺席计数判离线。
- **身份分裂已由日志桥解决（2026-09-07 二期）**：0x180A 没有 0x2A25 串号特征，蓝牙上拿不到
  厂商串号。但 **Synapse 心跳设备数组**（V4 日志 `info: Device  [{…}, …]` 行，约 1 分钟一条）
  给所有配对设备记录**规范串号**而不分传输——`useBle:true` 标记 BLE 设备（真机验证：蓝牙 Joro
  仍记 `serialNumber SI2522F18701637` + USB productId 717）。`WatcherService.HarvestBleIdentities`
  读日志尾部最后一条心跳（256KB tail），解析 BLE 电量设备（串号/名称/类别/充电状态，
  心跳 >10 分钟旧则充电位不采信），`MatchBleIdentity` 按设备类别匹配（唯一候选即使类别
  不符也接受——蓝牙名可能很简短；两个同类别候选保持歧义→回退 MAC）。命中后蓝牙身份
  升级为 `SI2522F18701637`，**三种模式一个身份，历史无缝**。充电位注意：心跳
  `chargingStatus` 枚举为 `Charging`/`NoCharge_BatteryFull`/`off`（中间是默认非充电态，
  61% 未插线也报），且 **BLE 条目无 powerStatus 字段**——蓝牙充电位实际来自厂商通道
  (05,85)，厂商通道被 razerwdl 占用时蓝牙充电状态为未知。Synapse 未运行/无心跳 → 退回
  `BLE:<MAC>` 身份（`BleBattery` 缓存 DIS 串号，Joro 无 → 恒走 MAC）。
- **对照 OpenRazer**（PR #2683，2026-02，进行中）：Joro 1532:02CD 用 tx 0x1F + report_index
  0x03（与我们探针一致）；**该 PR 无电量方法**（仅灯光/宏），OpenRazer 也**不支持蓝牙设备**
  （BT 不识别为 USB 设备）——GATT 路线无先例可抄。

### 厂商定制通道（BLE 规范留口处，2026-09-07 真机 + 已逆向）

规范留给厂商的定制点只有两处，Joro 上各有一份：

1. **128-bit 厂商 GATT 服务 `52401523-f97c-7f90-0e7f-6c6f4e36db1c`**（Razer 私有，
   **2026-09-07 三期已逆向出命令协议**，Linux bluetoothctl 输出可见同族 UUID，无其他公开先例）：

   | 特征 | 属性 | 用途 |
   |---|---|---|
   | `52401524-…` | Write | 命令通道（Write Request） |
   | `52401525-…` | Read+Notify | 应答寄存器（20 字节快照，Notify 推送） |
   | `52401526-…` | Read+Notify | 8 字节 token `6EA77DCFC5DD2D85`（疑似配对 token，未用） |

   **命令格式**（8 字节 Write Request）：`[seq][payload_len] 00 00 [page][id][param:2]`；
   `payload_len=0` 为查询，非 0 为写入（命令帧后紧跟等长 payload 写，如节能配置写
   `05 0a` + `00 2c 01 14`）。**应答**（52401525 的 20 字节寄存器快照）：
   头帧 `[echo_seq][len] 00 00 00 00 00 [tag]`（tag `0x02`=成功 / `0x05`=未知命令），
   `len` 个载荷字节按 20 字节快照流式续传（串号 22 字节分 2 帧），快照中超出本次载荷的
   字节是**寄存器残留**（典型为串号尾 "8701637"）——禁止解读超出声明的长度。
   另有 CCCD：写 `0100` 到 52401525 的 CCCD 句柄开启通知。

   **已验证命令表**（Joro，差分实验确认）：

   | page/id/param | 语义 | 观测 |
   |---|---|---|
   | 05/81/0001 | **电量，Scaled255**（raw×100/255） | F7=97%（与 BAS 一致；插线 1 分钟涨到 F9=98%） |
   | 05/85/0001 | **充电标志** 0/1 | 插线 0→1，拔线 →0（两次翻转） |
   | 01/83/0000 | 完整串号（22 字节） | "SI2522F18701637"——蓝牙原生身份，不依赖心跳 |
   | 01/86/0000 | 3 字节，恒 `01 00 00` | 未知（状态？） |
   | 01/82/0000 | 2 字节，恒 `03 00` | 未知 |
   | 01/A0/0000 | 1 字节，恒 `02` | 未知 |
   | 05/80/0001 | 1 字节，恒 `01` | 未知 |
   | 05/84/0000 | 2 字节，恒 900 (0x0384) | 疑似容量/阈值类常量（Synapse 与 87 成对轮询） |
   | 05/87/0001 | 1 字节，恒 `00` | 未知（插线时仍 0；怀疑"充电完成"——满电插线可验证） |
   | 05/8A/0001 | 4 字节 `[n][秒][分]` | 节能配置 `[01][300][20]`；Synapse 经 05/0a 写入（`[idx][LE16 值][?]`，180→300 生效回读确认） |
   | 05/8D/0001 | 1 字节，恒 `00` | 未知 |
   | (page)/80/0000 | 该页支持的 id 列表 | page01→`40 81 C2 C3 C6`，page05→`40 41 C2 C3 C4 45 C6 C7 CA`（0x4x 是 set 半区镜像，勿碰） |
   | 10/05/0100 + payload 00 | Synapse 设置写入（亮度类） | seq 1b/1c/1d 三连发，对应操作亮度 |
   | 06/02/0008、07/0B/0000 | Synapse 设置写入 | 同上（节能相关），payload 1 字节 |

   **通道占用语义**（真机实测，重要）：占用者 = **驱动层的无线设备加载器
   `razerwdl.exe`**（用户手动启动驱动栈时出现，随即特征枚举失败"characteristics
   missing"——服务可见但特征打不开，不是 AccessDenied；razerwdl 退出后立即恢复）。
   **RazerAppEngine（Synapse UI）本身不占用**——它空闲时通道共享可读。挂件策略：
   `BleVendor.TryReadPower` 先试厂商通道（电量+充电一次拿全），特征枚举失败/订阅失败
   自动回退 BAS + 心跳充电位（`HidWatcher` BLE 分支）。

   **心跳枚举语义**（V4 日志实测）：`powerStatus.chargingStatus` 只有三个观测值——
   `"Charging"`（真在充，真机 13:11-13:41 充电段）、`"NoCharge_BatteryFull"`
   （**默认非充电态**，命名有误导：鼠标 61% 未插线也报它）、`"off"`（关机/离线）。
   且 **BLE 心跳条目（useBle:true）根本没有 powerStatus 字段**——蓝牙模式的充电位
   只能靠厂商通道（05,85），心跳补充电位只对 dongle/有线条目成立。
   另：FN+ESC 切换节能纯固件行为，BLE 寄存器零变化（主机不可见）。

   **抓包方法**（复现用）：`logman start trace -ets BthCap -p {8A1F9517-3A8C-4A9E-A018-4F17A200F277}`
   `0xC000000000000000 0x04 -o cap.etl -nb 128 256`（Microsoft-Windows-BTH-BTHPORT，HCI 关键字
   0x4000…+HCIRAW 0x8000…）→ 停止后用 BTP 包的 `BTETLParse -cfa cap.btsnoop cap.etl` 转 btsnoop，
   Wireshark/tshark 直接读。抓 Synapse 会话：杀 RazerAppEngine → 开抓包 → 起 Synapse → 打开设备页
   操作（它只在需要时才开厂商 GATT 会话）。**探针**：`razer-taskbar.exe --ble-vendor`（重放）/
   `--sweep`（get 半区只读枚举）/ `--raw=LEN:PAGE:ID:PARAM[:hex]`（单发，LEN≠0 需 `--yes-i-know`）/
   `--power`（生产 TryReadPower 自检）。

   **键盘本地开关对主机不可见**：FN+ESC（节能开关）连按两次，通道所有寄存器零变化——
   纯固件行为，别指望从主机读它。
2. **HID Report Map 的厂商 usage page（0xFF00+）**：无。preparsed caps 显示 BLE collections
   只有标准页（键盘 0x06/鼠标 0x02/consumer 0x0C + 页 0x80、0x00 两段杂项，feature 最长
   3 字节）——**没有 90 字节厂商 feature，也没有标准化 Battery Strength（0x06 页 usage 0x20）**。
   0x1812 HID 服务的特征（含 Report Map 0x2A4B）被 Windows HID 栈独占，GATT 直读恒
   AccessDenied，这是 HOGP 的设计行为（描述符只能走 HidD_GetPreparsedData）。
   另观测：HOGP 内某特征句柄（0x1B）周期性发**空通知**（5-8 连发、间隔 100-300ms，
   约 2-3 分钟一波），内容恒空、含义未明（疑似状态广播），不影响本挂件。

探针 BLE 段（`--hid-probe`）现 dump 全部 GATT 服务/特征（含属性位与值；Uncached 失败
回退 Cached）。标准服务一览：0x1800（名"Joro"/外观 0x03C1/连接参数）、0x1801（GATT）、
0x180A（Manufacturer="Razer"+PnP ID `028E06CE02…`）、0x180F（BAS，Read+Notify）。
- **充电标志现状**：厂商通道可用时充电位来自 (05,85)；通道被占回退 BAS（无充电）+
  心跳 `chargingStatus`。`Commit` 仍保留有线读数的充电状态不被 BLE 读数覆盖。
- 蓝牙模式同时 dongle 键盘槽会持续 NoResponse（2 次预算后缺席计数）——缺席 2 轮且静默
  ≥30s 后判离线（2026-09-11 起判离线需墙钟静默，见"设备身份 / 数据流"）。

### 蓝牙模式实机复测（2026-09-11，Joro USB→BT 热切换）

- **传输切换无缝**：USB 拔线到 BLE 读数接上仅 ~14s（同一串号条目先判离线再由 BLE 复联，
  30s 墙钟下限正好吸收交接窗，历史无离线样本）。身份升级有 ~1 分钟滞后（心跳节奏）：
  首轮以厂商通道原始名落地。
- **BLE:{mac} 假身份残留（已修）**：身份升级那一轮只写串号条目，旧 MAC 条目成为孤儿——
  原代码只回收 `HID:{pid}` 形态，BLE 回退"靠名字折叠"，但解析后不再有回退轮，且首尾
  名字不同（厂商通道 "Joro" vs 心跳 "Razer Joro"），启动自愈 `AliasPairs`（精确同名）
  也配不上对。修复：Commit 实时折叠——本轮解析出真串号时，回收**同 pid**（pid 钉死型号，
  包含匹配不跨型号）且名字相等/互相包含的 `:` 形态回退行（`_fallbackPid` 记录根落时的
  pid），走 `MergeAlias` 历史无缝。两个同型号单元不会误折叠：心跳匹配歧义时两边都解析
  不出来，pid 形态回退在 pid 已解析时根本不落根。
- `razerwdl` 占用厂商通道期间 BLE 电量走 BAS 回退 + 心跳充电位，实测显示正常（96%，未充电）。

## get 半区全段扫描（2026-09-07 真机，`--hid-scan`，结果 hid-scan.log）

只读扫描（id 0x80-0xFF × class 0x00-0xFF，set 半区与配对镜像 id 已排除，见
`HidProbe.GetHalfScan` 的安全注释）。可读命令地图：

- **class 0x00（INFO，两槽共 24 个 id）**：0x81 固件、0x82 串号、0x84 设备模式、0x83/0x85-0x87
  小状态、0x8D/0x8E、0x91/0x93、0x95/0x97/0xA4（疑似 RGB/标定数据）、0xA2-0xA4、
  0xB3/0xB8/0xBA/0xBC、0xBF/0xC5（内含 dongle PID 0x00B8）、0xC0/0xC3、0xD0(仅键盘)。
  **0x92 = 配对设备串号**（键盘槽上返回鼠标的串号，鼠标槽返回自身）。
  **没有名称命令**——命名靠 Synapse 日志收割（见下）。
- **class 0x07（BATTERY，鼠标槽 8 个）**：0x80 电量(raw 161)、0x84 充电、**0x81 低电阈值(raw 77)、
  0x82 未知(raw 85)、0x83 空闲时间(0x0384=900s)** ——后三个 get 有望用于挂件增强。
- **class 0x02（鼠标槽）**：0x82 `081C081C`、0x84 `08 01 02 03 04 05 60 09`（疑似 DPI 段表，
  与 Synapse 5 段 DPI 吻合）、0x98 `19`。
- **class 0x05**（两槽）：0x80/0x81/0x8A 小状态；**class 0x06（仅键盘）**：0x86/0x8E 12-14 字节
  含 0x64(100)（疑似亮度/电量相关）；**class 0x0B（仅鼠标）** 4 个 id。
- class 0x04（键盘槽）与 0x0A（键盘槽）对**任意 id 回 Success+全零**——假回显类，无信息量。
- 扫描方法教训：`BuildQuery(tx, class, id, size)` 参数序曾传错导致全图误判，sanity 检查
  （已知可用的 0x07/0x80 判死）当场暴露。

## transaction_id 表（OpenRazer 驱动 switch 的精选移植，`RazerPidTable.cs`）

- **0x1F**（新一代鼠标）：Viper V3 HyperSpeed(0x00B8)、Viper V2/V3 Pro、DeathAdder V3/V4 Pro、
  Basilisk V3 Pro 系、Naga Pro/V2 Pro、Orochi V2、Cobra Pro、Pro Click 系、HyperPolling dongle(0x00B3)
- **0x3F**：DeathAdder V2 Pro(0x007C/0x007D)、Mamba Wireless(0x0072/0x0073)
- **0xFF**：Viper Ultimate(0x007A/0x007B)
- **键盘**：0x9F 是无线键盘标准 tx（combo dongle 的键盘槽同款）；BlackWidow V3 Pro 有线
  0x3F / 无线 0x9F；BW V3 Mini HS 有线 0x1F / 无线 0x9F；Joro 有线 0x1F（实测），
  BLE 无厂商通道（电量走 GATT）。OpenRazer 尚不支持 Joro（#2540）
- 未知 PID：探测序列 0x1F → 0x9F → 0x3F → 0xFF（echo+CRC 校验通过即用）
- Razer VID 固定 0x1532。耳机是另一套协议，暂不支持。

## Windows 实现要点（真机踩坑记录，2026-09）

1. **feature 报告在鼠标 TLC 上**：Viper V3 HS 接收器(PID 0x00B8)的 90 字节 feature report 声明在
   MI_00 根鼠标 collection（feature=91）。其他 collection（MI_01 vendor 页 col05-08 等）
   `SetFeature` 一律 `ERROR_INVALID_FUNCTION(1)`——确定性失败，按路径黑名单跳过。
2. **打开方式**：鼠标/键盘 TLC 对 `GENERIC_READ|GENERIC_WRITE` 返回 err=5（OS 独占），
   但 **GENERIC_WRITE-only 能打开**，且 W 句柄上 `HidD_SetFeature/HidD_GetFeature` 完全正常
   （feature IOCTL 是 FILE_ANY_ACCESS）。`HidApi.OpenForFeature` 按 RW→W→R→0 阶梯尝试。
3. **句柄跨轮复用**（HidSession）：每轮重开句柄在空闲后首次打开会间歇失败（表现为空轮抖动）；
   句柄保持打开，仅当设备路径消失（拔出）或连续 6 轮无应答时重开。
4. 声明的 `FeatureReportByteLength` 可能是 0，不能作为过滤条件，只当缓冲下限。
5. 蓝牙路径的 VID/PID 形如 `_vid&02068e_pid&02ce`（BLE）或 `_vid&0001532_pid&02cd`
   （经典 BT），与 USB 的 `vid_1532&pid_00b8` 不同；VID 段按**后 4 位**解析（BLE 前缀
   `02` 是蓝牙 SIG 的 id-source），枚举正则两种都要匹配，VID 白名单含 0x1532 + 0x068E。
6. 接收器 HID 序列号字符串是全 0（如 `000000000000`），不是设备身份。真实序列号通过厂商命令
   `class 0x00 / id 0x82`（22 字节 ASCII，OpenRazer `razer_chroma_standard_get_serial`）查询，
   在鼠标 TLC 上可用——Viper V3 HS 真机返回 `632516H31000044`，与 Synapse 日志的 `serialNumber`
   完全一致，两个数据源共用同一身份。查询失败（设备忙/不支持，如 Joro）才退化为 `HID:{pid:X4}`。
   **不做按名合并**：同名≠同设备（用户更换全新同型号鼠标即反例），历史库出现分裂身份时由
   展示层用序列号后缀区分（`DeviceLabels`：历史页/hover/托盘/设置页显示 `名称(序列号尾8位)`）。
7. 与 Synapse 并存无冲突（只读 feature 交换）；`Razer Control Device`(RZCONTROL) 是 Synapse
   自有驱动通道，与我们无关。

## 设备身份 / 数据流

- `HidWatcher.Poll` 由 `WatcherService.ParseOnce` 在 watcher 线程调用（单写者），
  写入 `DeviceStore` 的条目与日志源同构（`IsSelected` 盖章、`DeviceClassifier` 分类），
  下游挂件/hover/托盘/历史零改动。历史采样仍由 `Tick` 统一挂载。
- 身份：优先厂商序列号查询，其次 HID 序列号字符串，最后 `HID:{pid:X4}`（见第 6 点）。
- 连接判定（2026-09-11 修订）：本轮查到 → connected；缺席计数 ≥2 轮 **且** 距最后应答
  静默 ≥30s（`DisconnectAfterMs`）→ disconnected。墙钟下限吸收无线链路 wake 风暴
  （2-4 轮快速 miss 不闪烁离线）；转换各记一条 INFO。两个坑（真机日志定位）：
  ① 原 `_owned = seen` 首轮空读即清空归属集，miss 永远到不了 2——判离线实际从未生效，
   深睡设备恒显示最后电量"在线"，已改为归属持久累积；
  ② auto 回退日志源期间日志源会重写同一条目——HID miss 循环按"最后写入者引用比对"
   跳过非本源写入的条目，不越权翻转日志源的连接判定。
- **句柄重开判据（2026-09-11 修订）**：重开（stale-handle failsafe）只在连续空轮内存在
  **交换故障证据**（setFeature/getFeature 失败、BadEcho/BadCrc/BadLength，`_faultInRound`）
  满 6 轮时触发；纯 NoResponse/Busy = 设备深睡而句柄健在，重开无意义且有"空闲后重开
  间歇失败"风险（实测 4h：3964 次重开 vs 2 次 API 故障）。纯静默仅保留 120 轮深保底。
- **status=0x05（不支持）日志限频**：空接收器槽/AA 设备对电量查询回 0x05 属预期，按
  (pid, slot) 10 分钟限频，消息带 pid+tx（`QueryCommand` 返回 (value, unsupported)）。
- `hid poll` 签名行附 `(charging)`，可从日志直接核验充电位。
- **auto 模式的粒度是整体而非按设备**：HID 查到 ≥1 台后日志解析不再运行。HID 不支持
  `0x07` 电量命令的设备（耳机是另一套协议）会停留在最后一次日志值并保持冻结——此类设备
  为主时建议 `battery_source=log`，或等待按设备合并的后续改进。
- 探针：`razer-taskbar.exe --hid-probe`（绕过单实例），枚举全部 Razer collection、
  dump 收发 hex 与错误码，输出到控制台与 `%APPDATA%\razer-taskbar\hid-probe.log`。
  新增未知设备时先跑探针确认 tx/缩放。

## 测试

`tests/RazerTaskbar.Tests/HidTests.cs`：CRC 向量、报文布局、响应解析各分支、缩放规则、
PID 表、HID→RazerDevice 映射与 `IsSelected` 盖章。协议解析改动必须同步此文件。
