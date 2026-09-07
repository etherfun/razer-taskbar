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
- 电量缩放：**鼠标 = raw × 100 / 255**（0..255；已在 Viper V3 HyperSpeed 真机确认：raw 161 = 63%，
  与 Synapse 一致；新旧世代均如此）。键盘未实测，`BatteryScale.Auto` 启发式（≤100 视为直读百分比）。
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

- `tx=0x1F`（以及 0x3F/0x0F/0x2F）→ 鼠标（serial 632516H31000044，raw 161 = 63%）
- `tx=0x9F`（以及 0xFF）→ 键盘（serial SI2522F18701637，raw 255 = 100%，×100/255 缩放）
- arguments 寻址无效；键盘槽用短重试预算（2 次，通常缺席时不拖慢轮询）
- 串号按 tx 缓存于会话；**查询失败不缓存**（风暴期打开会话时拿不到串号，若缓存会导致
  `HID:{pid}` 假身份落库），未解析时每轮重试
- 键盘槽的显示名：产品字符串是鼠标名，且**厂商协议没有名称命令**（INFO 类 0x00 实测只有
  0x81 固件 / 0x82 串号 / 0x84 模式；OpenRazer 的设备名也是内核驱动按 PID 硬编码的
  `device_type` switch，daemon 只读 sysfs）。名称来源：优先从 Synapse V4 日志收割
  `serialNumber → name.en`（`RazerWatcher.HarvestSerialNames`，Synapse 曾运行过即可，
  10 分钟重试一次），兜底 "Razer Keyboard"；Kind=Keyboard 由槽位角色保证。
  键盘自有 dongle（产品名即键盘名）保留产品名；Joro 有线（PID 0x02CD）走自有 USB 设备

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
  0x3F / 无线 0x9F；BW V3 Mini HS 有线 0x1F / 无线 0x9F。OpenRazer 尚不支持 Joro（#2540）
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
5. 蓝牙路径的 VID/PID 形如 `_vid&0001532_pid&02cd`，与 USB 的 `vid_1532&pid_00b8` 不同，
   枚举正则两种都要匹配。
6. 接收器 HID 序列号字符串是全 0（如 `000000000000`），不是设备身份。真实序列号通过厂商命令
   `class 0x00 / id 0x82`（22 字节 ASCII，OpenRazer `razer_chroma_standard_get_serial`）查询，
   在鼠标 TLC 上可用——Viper V3 HS 真机返回 `632516H31000044`，与 Synapse 日志的 `serialNumber`
   完全一致，两个数据源共用同一身份。查询失败（设备忙/不支持，如 Joro）才退化为 `HID:{pid:X4}`。
   **不做按名合并**：同名≠同设备（用户更换全新同型号鼠标即反例），历史库出现分裂身份时由
   展示层用序列号后缀区分（`DeviceLabels`：历史页/hover/托盘/设置页显示 `名称(序列号尾8位)`）。
7. 与 Synapse 并存无冲突（只读 feature 交换）；`Razer Control Device`(RZCONTROL) 是 Synapse
   自有驱动通道，与我们无关。

## 设备身份 / 数据流

- `HidWatcher.Poll` 由 `RazerWatcher.ParseOnce` 在 watcher 线程调用（单写者），
  写入 `DeviceStore` 的条目与日志源同构（`IsSelected` 盖章、`DeviceClassifier` 分类），
  下游挂件/hover/托盘/历史零改动。历史采样仍由 `Tick` 统一挂载。
- 身份：优先厂商序列号查询，其次 HID 序列号字符串，最后 `HID:{pid:X4}`（见第 6 点）。
- 连接判定：本轮查到 → connected；连续 2 轮查不到（接收器在、设备关机/离开）→ disconnected。
- **auto 模式的粒度是整体而非按设备**：HID 查到 ≥1 台后日志解析不再运行。HID 不支持
  `0x07` 电量命令的设备（实测 Razer Joro 无应答；耳机是另一套协议）会停留在最后一次
  日志值并保持冻结——此类设备为主时建议 `battery_source=log`，或等待按设备合并的后续改进。
- 探针：`razer-taskbar.exe --hid-probe`（绕过单实例），枚举全部 Razer collection、
  dump 收发 hex 与错误码，输出到控制台与 `%APPDATA%\razer-taskbar\hid-probe.log`。
  新增未知设备时先跑探针确认 tx/缩放。

## 测试

`tests/RazerTaskbar.Tests/HidTests.cs`：CRC 向量、报文布局、响应解析各分支、缩放规则、
PID 表、HID→RazerDevice 映射与 `IsSelected` 盖章。协议解析改动必须同步此文件。
