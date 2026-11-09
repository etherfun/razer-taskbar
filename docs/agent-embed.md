# 任务栏真嵌入（embed_into_taskbar v2）设计

> **状态：已在 C# 版实现并实测通过（2026-09-08，冒烟记录见 `docs/agent-csharp.md`）。**
> 最终方案与本文 §2 的差异：渲染**不走 GDI+colorkey**（AA 灰边与 0x202020 阴影在浅色
> 任务栏上形成可见暗色毛边，用户实报"字体渲染劣化"），而是**沿用覆盖层的 ULW 管线**，
> 每次出帧后 poke 一次带重建（§4 机制的应用）——画质与覆盖层逐像素一致。
> 锚窗口承载线程级绑定；退出协议经 `ExitWidget()`/`WM_CLOSE` 触发。

> 实证环境：Windows 11 家庭中文版 Insider Preview，Build 26340.9233（ge_release），
> 2560×1600 100% DPI。全部结论来自 2026-09-08 在该机器上的窗口树枚举 + 探针矩阵 +
> 挂件本尊 A/B 实测（截图比对），非文档推测。

## 0. 一句话结论

26340 的任务栏带对传统 HWND 子窗口的合成是**快照式**的：
**"出生即子窗口 + GDI 直绘 + SLWA colorkey"实时上屏且支持实时重绘；
ULW（UpdateLayeredWindow）呈现被完全忽略；SetParent 迁移进来的窗口被忽略**。
旧实现（Rust 与 C# 移植）用 SetParent + 保留 ULW 呈现，两条都踩在死点上，
这就是 `embed_into_taskbar` 在此构建上"子窗口存在但整窗不可见"的真正原因——
与 `docs/agent-taskbar.md` 中"DWM 不再合成带内分层子窗口"的旧记录相比，
归因需要修正：**不是分层子窗口整体失效，而是 ULW 呈现路径 + SetParent 迁移路径失效**。

## 1. 实测机制（探针矩阵）

`Shell_TrayWnd` 带内子窗口呈现矩阵（全部为直接创建的 `WS_CHILD|WS_VISIBLE|WS_CLIPSIBLINGS`
+ `WS_EX_NOACTIVATE`，创建后 `SetWindowPos(HWND_TOP)` 提到兄弟第 0 位）：

| 呈现方式 | 上屏 | 实时重绘 | 备注 |
|---|---|---|---|
| 普通 GDI（WM_PAINT 直画窗口 DC） | ✓ | ✓ | 品红→绿重绘实时可见（F1/F1B） |
| GDI + SLWA `LWA_COLORKEY`（黑 key） | ✓ | ✓ | key 色透出背景，白字浮层，F5A→F5B 实时 |
| GDI + SLWA `LWA_ALPHA`(255) | ✓ | 未测 | 首帧可见 |
| **ULW per-pixel alpha** | **✗** | **✗** | 句柄/几何/Z 序/vis/cloak 全正常、ULW 返回成功，但像素永远不显示 |
| SetParent 迁移（顶层 popup → SetParent + WS_POPUP→WS_CHILD） | ✗ | — | GDI 内容从不出现（P6） |

补充规则：

- **Z 序**：创建后必须显式 `SetWindowPos(HWND_TOP)`；不提位则被
  `DesktopWindowContentBridge`（不透明、占满整带）盖住，什么都不显示。
- **带重建快照**：任一带内子窗口**创建/销毁**会触发一次带级重建，重建时所有
  子窗口（含 ULW 呈现者）按**当时表面**被快照一次。实测：探针销毁后，挂件的
  ULW 帧被"烤"进带里突然可见，随后再次冻结。
- **幽灵泄漏**：子窗口销毁后，其**最后一帧永久留在带上**（P1 品红框存活 30+ 分钟，
  窗口枚举已无此窗口），任务栏内容重排（托盘图标增减）也不会清除，
  仅 explorer 重启清除。
- **移动/隐藏/显示/改尺寸不触发重快照**：对 ULW 子窗口 nudge 1px、SW_HIDE/SW_SHOW、
  高度 ±2 均不能使其上屏（nudge 实验）。
- **Lyricify Syllable 参照**：其歌词窗口是 `Shell_TrayWnd` 直接子窗口（Z 序第 0 位，
  `WS_CHILD|WS_EX_LAYERED|WS_EX_NOACTIVATE`，跨进程宿主），闲置时表面为空。
  推断配方 = 不透明呈现面（WPF DComp，实时）+ SLWA 黑色 colorkey；
  与我们可选的"GDI + colorkey"路线在 DWM 层面等价。

## 2. 嵌入 v2 方案（已实现，渲染=ULW+重建 poke）

### 2.1 窗口生命周期（核心变更）

**废弃 SetParent 迁移，改为"窗口重建"**：

```
进入嵌入态：DestroyWindow(顶层覆盖层) → CreateWindowExW(WS_CHILD, parent=Shell_TrayWnd)
退出嵌入态：DestroyWindow(band child)  → CreateWindowExW(WS_POPUP 顶层覆盖层)
explorer 重启（TaskbarCreated）：IsChildOf 失败 → 销毁重建子窗口；失败回退覆盖层
```

- 新 HWND 直接以带为父出生，绕开"迁移窗口被忽略"的死点。
- 窗口类/尺寸/DPI 逻辑不变；`SetTimer` 的 4 个定时器对新 hwnd 重挂；
  `WidgetState` 与 hwnd 解耦的部分（配置快照、PaintSig、LastLayout）原样保留。
- 单实例检测已有 `EnumChildWindows(tray)` 类名兜底（`Program.cs`），无需改。

### 2.2 渲染（最终实现：ULW + 重建 poke）

~~GDI 直绘 + 黑 colorkey~~（首版实现，AA 灰边与阴影在浅色任务栏上形成暗色毛边，
弃用）。**最终方案：嵌入态与覆盖层共用同一条 ULW 管线**（`EnsureMemSurface` →
`PaintBody` → `AlphaPresent`），仅有的差别是 `AlphaPresent` 成功后若处于嵌入态
则 `PokeBandRebuild()`：

- poke = 在带上创建一个 1×1 丢弃子窗口（SLWA alpha=0 保证其泄漏快照不可见）
  并立即销毁——子窗口的创建/销毁触发带重建（§1"带重建快照"），重建会把所有
  band 子窗口按**当时表面**重新快照，我们的新帧由此上屏。
- 挂件的绘制频率极低（PaintSig 去重，电量/设备变化才出帧），poke 的资源消耗
  可忽略；期间其它工具（Lyricify 等）的帧也被顺带刷新，无害。
- 淡入淡出（FadeTransition）因共用 `AlphaPresent` 而在嵌入态自动可用。
- **ULW 的 `pptDst` 语义（2026-11-09 实测）**：对带内 `WS_CHILD`，ULW 按**父客户区
  坐标**定位，不是屏幕坐标——`AlphaPresent` 必须传父客户区矩形，否则每次出帧都会
  把窗口再平移一个任务栏原点、poke 随即快照到出屏的挂件（详见 §6.1）。

**可选优化（未采用）**：key 色采样任务栏底色——需要解决 PrintWindow/CopyFromScreen
的自反馈（我们的窗口就盖在采样点上）与底色渐变问题；ULW+poke 无这些妥协。

### 2.3 定位与 Z 序

- 坐标：`ComputePlacement` 输出的带内坐标（Win11 锚 `Shell_TrayWnd`）即父客户区
  坐标，沿用现有 `ClientOrigin` 换算即可；`embed_into_widgets_space` 的按钮空闲区
  数学（UIA `WidgetsButton` + 文字右缘）**可直接组合**——
  `embed_into_taskbar=true && embed_into_widgets_space=true` 就是最终形态：
  真嵌入 + 长在小组件按钮空闲位里。
- Z 序：创建后 `SetWindowPos(HWND_TOP)`；保留每秒 `ReassertChildTop`（实测
  z-touch 不触发重建，不产生额外快照）。
- 点击穿透：`WM_NCHITTEST → HTTRANSPARENT` 保持，命中落到 XAML 桥（= 任务栏本体）。

### 2.4 幽灵规避（销毁协议）

利用"幽灵 = 最后一帧快照"：**销毁/迁出前先画一帧不可见内容，等一拍，再销毁**：

- 嵌入态（ULW）：DIB 整帧清零（alpha=0）→ `AlphaPresent`（内含 poke）→ 等 120ms；
- 覆盖层窗口：无泄漏问题，无需处理。

异常退出（崩溃/强杀）仍会留残影，explorer 重启清除——写进已知限制。

### 2.5 失败回退链

1. 子窗口创建失败 → 粘性 `EmbedFailed`（现有字段）→ 本会话停留覆盖层；
2. explorer 重启拆父 → `IsChildOf` 检测 → 销毁重建；重建失败 → 覆盖层；
3. Classic（Win10）任务栏无 XAML 桥，`wantEmbed` 直接按覆盖层处理。

## 3. 实施改动点（按文件）

| 文件 | 改动 |
|---|---|
| `Native/WidgetWindow.cs` | 窗口生命周期：`RecreateAsBandChild()/RecreateAsOverlay()` 替代 embed 迁移；`Paint` 按 `st.Embedded` 分叉 `PaintToWindowDc`（GDI 直画，跳过 `AlphaPresent`）；销毁协议 `PrepareSilentExit()`；定时器重挂 |
| `Native/TaskbarLocator.cs` | 删除 `SetTaskbarChild`（语义被窗口重建替代）；保留/简化 `IsChildOf`（`GetParent(hwnd)==tray`）；保留 `ClientOrigin`、`ReassertChildTop` |
| `Native/AppState.cs` | `WidgetState` 与 hwnd 解绑项审计（MemDIB 嵌入态可不分配） |
| `Features/ControlPanel/SettingsPage.xaml(.cs)`（当时为 `Views/SettingsPage`） | 暴露"嵌入任务栏"开关（`embed_into_taskbar` 键已存在，默认 false），标注实验属性 |
| `docs/agent-taskbar.md` | 修正 26340 失效归因（ULW/SetParent 路径失效，非"分层子窗口整体失效"），指向本文档 |
| `docs/agent-csharp.md` | 冒烟记录追加嵌入态清单 |

## 4. 验证清单（实施后冒烟）

- [ ] `embed_into_taskbar=true`（可叠加 `embed_into_widgets_space=true`）→ 挂件出现在
      小组件按钮空闲区，重启 explorer 自动恢复，全屏应用下内容不被盖。
- [ ] 电量变化实时上屏（插拔充电、drop_swap 触发）。
- [ ] 开关关闭 → 平滑回覆盖层，任务栏无残影（销毁协议生效）。
- [ ] 强杀进程 → 残影出现且 explorer 重启清除（已知限制，文档标注）。
- [ ] 100%/125%/150% DPI 与多显示器下几何正确。

## 5. 本轮实验残留

- 实验产生的带内幽灵快照（1450–1910 区域品红/绿/黄框）已通过重启 explorer 清除。
- `%APPDATA%\razer-taskbar\settings.json.bak-embedtest` 为实验前的配置备份，可删。
- 全部截图证据在 `~/.zcode/cli/artifacts/`（`probe*.png`、`f1..f5*.png`、`n*.png` 等）。

## 6. 2026-11-09 实测：数据更新时整块闪空的根因与修复

> 现场：`embed_into_taskbar=true` + `embed_into_widgets_space=true` +
> `fade_transition=true`，`display_mode=drop_swap`，Windows 11，2560×1600 100%，
> 任务栏高 48px。用户报告"挂件在更新数据时完全清除再绘制"。
> 全部结论来自 PrintWindow 高频采集 + 临时诊断插桩（实测后已移除），不是推测。

### 6.1 机制 A：ULW 在带内子窗口上按父客户区坐标定位（闪空主因）

`AlphaPresent` 原先把 `GetWindowRect` 的**屏幕坐标**直接交给 `UpdateLayeredWindow`
的 `pptDst`。实测：对 `Shell_TrayWnd` 的 `WS_CHILD`，ULW 把 `pptDst` 解释为**父客户区
坐标**，于是每次呈现都把窗口再向下平移"任务栏屏幕原点（本机 1552px）"：

| 时刻 | present 前窗口矩形 | ULW pptDst | present 后窗口矩形 |
|---|---|---|---|
| 普通出帧 | (1976,1559) | (1976,1559)（屏幕坐标） | **(1976,3111)** |
| 淡入 tick 2 | (1976,3111) | (1976,3111) | (1976,4663) |
| tick 3 | (1976,4663) | … | (1976,6215) |
| tick 4/tick 5 | … | … | (1976,7774) → (1976,9326) |

链条：一次数据更新 → `PaintBody` 出帧 → 淡入每 50ms 一个 tick，每个 tick 都
`AlphaPresent` → ULW 每 tick 把窗口再推低 1552px（5 个 tick 后完全出屏）→ 紧随其后的
`PokeBandRebuild` 触发带重建，快照的是**已出屏的窗口** → 带上的挂件整块变空
（= "完全清除"）。1s 兜底定位会把窗口搬回原位，但**带快照不会因此刷新**；要等下一次
自身 poke 或任务栏其它重建才会重新拍到帧，而下一次 present 又会先把窗口推出屏，
于是表现为清空、偶发重绘。

同一 25fps PrintWindow 采集（rotate 模式每 10s 一次设备切换，强制走更新路径）：

| 指标 | 修复前 | 修复后 |
|---|---|---|
| 挂件表面空帧（`PrintWindow(PW_RENDERFULLCONTENT)`） | 123 / 669 = **18.4%**，每 10s 一段，最长 1.28s | **0 / 387** |
| 带合成里挂件区域空帧（`PrintWindow(Shell_TrayWnd)` 裁切） | 与上段同步的约 2s 交替空白 | **0 / 250** |
| present 前后窗口矩形 | 每次 +1552px 漂移 | 完全一致 |

修复（`WidgetWindow.AlphaPresent`，`Native/WidgetWindow.cs:1349`）：嵌入态先把
`GetWindowRect` 的屏幕矩形换算成父客户区坐标（`TaskbarLocator.ClientOrigin(GetAncestor(hwnd, GA_PARENT))`）
再交给 ULW，窗口不再被 ULW 移动；覆盖层是顶层窗口，仍传屏幕坐标、行为不变。
尺寸与帧依旧在同一次 Paint 里提交（`PlaceWidgetCore` 的 SetWindowPos 先于
`AlphaPresent`），淡入淡出照旧（采集到的淡入逐帧可见 53%↔87% 双影）。

### 6.2 机制 B：D2D 设备丢失后永不恢复（同日 20:15 的实况叠加）

本机 20:14:58 / 20:15:12 `nvlddmkm` 报 ID 14/153（GPU 驱动复位/TDR）。此后
`csharp-debug.log` 每秒一条（`.old` 5,076 条 + 当前 1,507 条，全部 0x8899000C、零成功出帧）：

```
[ERROR] paint body failed: COMException ... (0x8899000C)
  at ID2D1DCRenderTarget.BindDC  ... D2d.cs:312
  at D2d.ScanInk                 ... PaintBody
```

三处缺口：

1. `PaintBody` 的 EndDraw 分支比较的是 `0x88990010`（其实是
   D2DERR_UNSUPPORTED_VERSION），**不是** `D2DERR_RECREATE_TARGET = 0x8899000C`，
   所以设备丢失后表面永不重建；
2. `D2d.ScanInkUncached` 的进程级 scratch 目标（`_scanRt`/`_scanBrush`）同样随设备
   死亡、也不重建——即使主目标重建，下一次未缓存的墨迹扫描仍会在
   `rt.BindDC(_scanDc, …)` 抛出；
3. `PaintBody` 先 `rt.Clear(transparent)` 再测文本；异常被内部 catch 后 `Paint` 仍
   调用 `AlphaPresent`，把**已清空的整帧**上传并 poke——每次重试再清一次，
   这正是"完全清除后不再回来"。

修复：

- `D2d`：新增 `D2DERR_RECREATE_TARGET` / `IsDeviceLost(Exception|int)` /
  `OnDeviceLost()`（`Native/D2d.cs:259`）；`ScanInkUncached` 在 `BindDC`/`BeginDraw`
  异常或 EndDraw 失败时销毁 scratch 目标并返回 null（调用方回退行盒度量），
  失败的扫描不再写缓存，恢复后可重新量到真值。
- `WidgetWindow.PaintBody`（`Native/WidgetWindow.cs:1403`）改为两阶段：
  **Phase 1 量测**（只碰共享扫描目标，不写 `MemBits`）→ **Phase 2 清屏+绘制**；
  任何阶段失败都返回 false，`Paint` 在 false 时**不** AlphaPresent、不 poke，
  已上屏的旧帧原样保留（`Native/WidgetWindow.cs:1080`）。
- 设备丢失统一走 `RecoverFromDeviceLoss`（`Native/WidgetWindow.cs:1633`）：
  销毁挂件表面 + `D2d.OnDeviceLost()`，下一次 Paint 在恢复后的设备上重建；
  同一次丢失只记一行日志（现场是 1/s 刷屏），`first paint` 也改为每窗口一次。
- `SilentFrame`（退出幽灵协议）改用 `PatBlt(BLACKNESS)` 清帧：D2D 设备丢失时
  teardown 也能走完。

**验证（故障注入）**：diag 构建让前 6 次出帧在 Clear 之后抛 0x8899000C（模拟设备
丢失窗口）——带合成空帧 **0/200**、挂件表面仅 1 帧启动期空帧，日志恰好一行
`D2D device lost`，注入结束后自动恢复出帧；对照修复前同一路径（真实日志）：
每次失败都 AlphaPresent 清空帧。

### 6.3 复测口径

采集 Harness 在 `%TEMP%\razer-diag\`（`pwcapture.ps1` / `bandcapture.ps1`）：
全屏游戏盖住任务栏时 `CopyFromScreen` 只能拿到游戏画面，改用
`PrintWindow(PW_RENDERFULLCONTENT)`——对挂件窗口得到其 ULW 表面（呈现帧），
对 `Shell_TrayWnd` 得到带合成（含挂件）的像素。证据目录：
`pwcapture\`、`bandcapture\`、`fadeseries\`（淡入逐帧）。
