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
| `Views/SettingsPage.xaml(.cs)` | 暴露"嵌入任务栏"开关（`embed_into_taskbar` 键已存在，默认 false），标注实验属性 |
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
