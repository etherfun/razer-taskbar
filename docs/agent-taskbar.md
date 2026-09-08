# 任务栏挂载 / 共存避让 / UI 绘制

适用：改动 `src/taskbar.rs`、`src/window.rs`、`src/uia_events.rs`、`src/hover.rs`、`src/tray.rs`。

## 窗口模型（`window.rs`）

- 顶层 `WS_POPUP` + `WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE`，`HWND_TOPMOST`，`move_overlay` 定位（`SWP_NOACTIVATE | SWP_SHOWWINDOW`）。不是 `WS_CHILD`：子窗口会被 TranslucentTB 类 acrylic 盖住。
- 尺寸：96 DPI 下 144×40（双层内容压缩高度，任务栏带 48 内上下各留 4px，placement 自动居中），按 `GetDpiForWindow` 缩放。
- `WM_NCHITTEST` 回 `HTTRANSPARENT`：覆盖层点击穿透，菜单只挂托盘图标（`WM_RBUTTONUP/DOWN` → `show_menu`）。

## 事件驱动布局（Taskbar-Lyrics 移植）

三路触发同一 `place_widget`，去抖/去重后执行：

1. **`TaskbarCreated` 广播**（`RegisterWindowMessageW` 注册，`handle_taskbar_created`）：explorer 重启后重新 `find_shell_tray` → `taskbar::reset_hold_state()` → `invalidate_widgets_cache()` → `tray::ensure_created` 重挂托盘图标 → 通知 UIA 线程 rebind → `place_widget(true)` 重新锚定。
2. **UIA 结构变化事件**（`uia_events.rs`）：后台 MTA 线程在任务栏 `Windows.UI.Input.InputSite.WindowClass` 子元素上注册 `IUIAutomationStructureChangedEventHandler`（`TreeScope_Descendants`，找不到 InputSite 时退回整个 tray 根），回调只做 `PostMessageW(WM_APP_LAYOUT)`。线程收到 rebind 信号或每 30s 检查任务栏 HWND 变化后拆旧注册重建（`ensureInitialized` 模式）。HWND 跨线程以裸地址（isize）传递。
3. **1s 定时器**（`TIMER_ID`）：兜底轮询 + `tray::refresh()` + 每秒失效重绘（电量变化不改矩形，靠这一步上屏；`paint` 按签名去重，未变化只 `BeginPaint/EndPaint`）。

- **去抖**：`request_layout` 限频 250ms（`LAYOUT_DEBOUNCE`），被合并的请求置 `layout_pending`，由一次性 `TIMER_LAYOUT` 兜尾。
- **去重**：`place_widget` 记录上次屏幕矩形（`last_layout`）与上次日志行（`last_log`），矩形不变则不 `InvalidateRect`（纯移动对分层窗口无需重绘）；日志仅在状态变化时输出。`paint` 以 `(label, level, charging, connected, w, h)` 签名去重，未变化时只 `BeginPaint/EndPaint` 验证更新区域。
- **UIA 查询缓存**（`widgets_button_rect`）：`(rect, notify_left, seen)` 缓存 30s，`TrayNotifyWnd` 左边沿变化或 `TaskbarCreated` 时失效；`TaskbarDa=0`（天气板关闭）时直接跳过 UIA。查询用 `FindFirst(TreeScope_Descendants, AutomationId="WidgetsButton")` 引擎侧搜索——旧版手工 ControlViewWalker 限深 DFS 在 26340 上从任务栏根节点走不动（板避让静默失效），Descendants 搜索不受影响。

## Z 序维护（防任务栏覆盖）

挂件与 `Shell_TrayWnd` 同在 TOPMOST 带，后抬升者在上；shell 会在托盘图标增减、任务栏动画、设置变更时反复抬高任务栏，单靠每秒一次 `move_overlay` 重顶会输掉竞争（表现为挂件被吞）。

- **检测**：`taskbar::tray_above(widget, tray)` —— 从挂件沿 `GW_HWNDPREV` 限 16 跳找 tray，找到即"被覆盖"（几次系统调用的开销，适合 1s tick）。
- **修复**：`TIMER_ID` tick 先检测，命中则 `arm_z_burst`：`TIMER_Z_BURST` 以 150ms×12 次连续 `place_widget`（每轮以 `move_overlay` 重申 `HWND_TOPMOST`）赢回 shell 的连续抬升；覆盖日志 30s 去重（`COVERED_LOG_EVERY`）。
- **采样不再自我隐藏**：`shrink_to_content` 仅在 occupant rect 与挂件 live rect 真实重叠时才 `SW_HIDE/SW_SHOWNA`（正常无碰撞稳态零隐藏——旧的整窗隐藏每 5s 缓存过期闪一次，观感即"被盖住"）。

## Win11 / Win10 定位（`compute_placement`）

- 判定：`Shell_TrayWnd` 下存在 `DesktopWindowContentBridge` 即 Win11（TrafficMonitor 一致）。
- 右锚（默认）：`x = TrayNotifyWnd.left - w + 2`；左锚：`Start` 右侧起排；`y` 按 `Start` 高度居中；叠加 `window_offset_*` 后钳制进任务栏带。
- 天气挂件板是 XAML 内容，`EnumChildWindows` 看不见：经缓存后的 UIA `WidgetsButton` 取 rect，失败回退注册表 `TaskbarDa` 估计（`widgets_zone_width`）。**无论 `avoid_overlap` 开关一律避让**；右锚可用带止于 `min(board_left, notify_left)`。
- **坐标系**：返回的 `x/y` 相对 `Placement.parent`——Win11 是 `Shell_TrayWnd`，Classic 是 `ReBarWindow32` 带（回退 `WorkerW`）。`place_widget` 必须用 `pl.parent` 的原点换算屏幕坐标，用 tray 原点换算 Classic 会偏移 Start 按钮宽度。

## 无第三方避让（设计决定）

- 挂件位置 = 纯锚点计算（右锚：`TrayNotifyWnd` 左缘 − 宽 + 2，小组件板左侧硬保留），每个放置 pass 结果确定、无状态。与第三方挂件（Lyricify、TrafficMonitor 等）重叠时接受：嵌入模式我们是 band 兄弟第 0 位、覆盖模式是 TOPMOST，始终绘制在对方上层。
- 曾经实现过完整的 occupant 避让状态机（枚举子窗口、屏幕采样收紧 content rect、grace/hold/跳跃上限），但实测歌词窗内容秒级变化，反应式避让永远慢半拍且来回抖动；Lyricify 自身又无避让逻辑，僵局无解——2026-09 经用户确认整体移除。若未来要恢复，参考 git 历史（`2111fea` 及之前）。
- 仍保留的边界：`TrayNotifyWnd`/`Start` 锚点约束，小组件板（天气）硬保留（XAML 内容 HWND 枚举不可见，经 UIA `WidgetsButton` 查询 + 注册表 `TaskbarDa` 门控，30s 缓存）。

## 嵌入模式（`embed_into_taskbar`，默认关；embed v2 已在 C# 版实现）

- **归因修正（2026-09 实测，详见 `docs/agent-embed.md`）**：26340 上旧实现的失效**不是**"DWM 不合成带内分层子窗口"，而是两个死点叠加——(1) 带（`Shell_TrayWnd`）对子窗口按"出生即子窗口"快照合成，**`SetParent` 迁移进来的窗口被完全忽略**；(2) **ULW（`UpdateLayeredWindow`）帧在两次带重建之间被冻结**（句柄/几何/Z 序全部正常、ULW 返回成功，但只有子窗口创建/销毁触发的带重建才会把当时表面快照上屏）。旧实现两条都踩中。可用配方：**出生即 `WS_CHILD` + 出帧后 poke 一次带重建**（GDI 直绘亦可实时，但 AA 在 colorkey 下有暗毛边，弃用）。
- **C# 版 embed v2 实现**：切换模式时**销毁重建**显示窗口（进嵌入态 = 销毁顶层覆盖层、在带上直接创建 `WS_CHILD`）；渲染与覆盖层共用 ULW 管线，`AlphaPresent` 末尾 poke 带重建使新帧上屏。`WS_CHILD` 随任务栏生灭 → 线程级绑定（定时器/`WmInvoke`/UIA 事件/`TaskbarCreated`）全部迁到永生的隐形**锚窗口**；explorer 重启后锚窗口发现显示窗口已死即重建重嵌。
- 坐标换算：定位数学输出带坐标，子窗口用父 CLIENT 坐标，差值即 `client_origin(parent)`；每秒 `reassert_child_top` 保持兄弟第 0 位（实测 z-touch 不触发重建）。
- 幽灵协议：带会永久保留被销毁子窗口的最后一帧快照（explorer 重启才清）——销毁/迁出前先呈现一帧全透明 ULW（含 poke）再等 120ms。强杀进程仍会留残影（已知限制）。
- 失败回退：子窗口创建失败置 `embed_failed` 粘性回退覆盖模式；Classic（Win10）任务栏不支持嵌入（无 XAML 桥，自动走覆盖）。
- `embed_into_taskbar=false` 或删除该键即回到纯覆盖模式。

## 绘制与菜单

- `paint`：黑底整窗填充（colorkey 抠除）→ 只画字形/文字/阴影，TTB acrylic 可透出。**双层布局（v3）**：设备类型图标（14px）独立于两行、全高垂直居中在左侧；其右为两行列：**图标列**（电池字形与状态图标在 `max(图标宽, 状态图标宽)` 的列内互相水平居中，共享一条垂直中心轴）+ **文本列**（两行文字同一 x 起笔）——上行 `[电池字形][百分比]`（字形 snap 20px、文字 14px semibold），下行 `[状态图标][预计时间]`（E823 时钟=放电、F607=充电；纯时长无 ~/+ 前缀 `format_estimate_plain`；文字 14px 常规，放电暗灰/充电白色）；无预测数据（功能关、无历史、离线）时回退单行全高居中（类型图标回到单行组内）。高度 40 / 宽度固定 144 基准，均不加宽。**竖向居中按墨迹而非行框**：`draw_shadowed_text` 内用 `GetGlyphOutlineW(GGO_METRICS)` 求字符串联合墨迹盒，算出行框不对称下伸留白造成的偏移量再平移矩形（`ink_center_delta`）——行框居中会让图标偏高、文字偏低。字体 `Segoe Fluent Icons`（回退 `Segoe MDL2 Assets`）+ `Segoe UI Variable Text`（回退 `Segoe UI`），灰度抗锯齿；阴影 `0x202020`（纯黑会被抠掉）。字体进程级缓存（`icons::icon_font` / `window::cached_text_font`，HFONT 以 isize 存储绕过非 Send/Sync），重绘零 CreateFont/DeleteObject。
- 无设备/离线：灰色 + `--`。
- 设备类型图标（`icons.rs`，纯 GDI 矢量）：鼠标 / 耳机 / 键盘 / 其他（USB dongle）。单行模式下挂件布局 `[类型图标][电池字形][百分比]` 居中；无设备时不画类型图标。判定：V4 日志 `category` 字段（MOUSE/KEYBOARD/HEADSET/…）优先，回退 `device_kind` 产品名关键词，兜底 Other（`battery.rs` 有单测）。图标宽度经 `width_for` 参与居中计算。注意：`draw`/`scan_ink_box` 必须 `DT_NOCLIP`——snap 后的字体行框常高于布局框（14px 槽位装 16px 字体），否则图标上下墨迹被 DrawTextW 裁掉。
- 悬停设备面板（`hover.rs`）：挂件点击穿透收不到鼠标消息，`TIMER_HOVER`（120ms）轮询 `GetCursorPos`+`PtInRect` 检测悬停，静置 350ms 后显示。面板同为顶层分层 `NOACTIVATE` + `HTTRANSPARENT` 窗口（只读、不抢焦点、不挡点击）；光标在挂件或面板矩形外即隐藏。面板为圆角矩形（`RoundRect` 一笔填充+描边，直角落在 colorkey 黑上即透明），行布局 `[类型图标][电池字形][名称][充电闪电列（仅有个别行充电<50%时存在）][百分比]`，当前显示设备名称亮白。位置：底部任务栏向上弹出、顶部任务栏向下弹出，x 钳制进工作区；行内容/几何都有去重，无变化不重绘。菜单开关 `hover_devices`（默认开）。
- 菜单（`show_menu`，托盘图标右键，刻意精简）：All devices + 已连接按名排序（快速切换）→ Settings… → Battery history… → Exit。ID 段：`ID_SETTINGS=1011`、`ID_DEVICE_BASE=2000`。
- 设置窗口（`settings.rs`，"Settings…" 打开）：整合原菜单全部设置项，深色 Win11 风格同 `viewer.rs`（DWM 深色标题栏/圆角、卡片分区：挂件/电量记录/通用、owner-draw 左右侧 pill、DarkMode_Explorer 复选框与下拉）。单实例、跑在 UI 线程同一消息循环；所有改动即时应用 + 落盘（无 OK/Cancel），经 `window.rs` pub 辅助函数（`modify_config`/`set_shown_device`/`reposition_widget`/`config_snapshot`/`devices_arc`/`widget_hwnd`）改 `STATE.config` 并同步 UI 侧副作用（托盘重挂/隐藏、hover 隐藏、重排重绘、`i18n::set_setting` + tray/viewer/settings 三处 `sync_language`）。watcher 每轮重读 settings.json，改轮询/记录间隔无需重启。复选框/下拉的 `BM_SETCHECK`/`CB_SETCURSEL` 不回发 `BN_CLICKED`/`CBN_SELCHANGE`，刷新无回声问题。
- 托盘（`tray.rs`）：16×16 GDI 图标（轮廓+填充+闪电）+ tooltip，仅为菜单入口兜底；`refresh()` 按 (tooltip, 电量状态) 签名去重，`ensure_created` 重置签名（explorer 重启重挂后必须真正重画）。

## 禁区

- 不动 `DesktopWindowContentBridge`；小组件板（天气）硬保留必须维持。
- 不把 `SetLayeredWindowAttributes` 调两次（第二次会替换 colorkey 模式）。
- **UIA 缓存失效路径必须保留**：`TaskbarCreated` → `invalidate_widgets_cache`；`ensure_created` 必须重置 `TRAY_LAST` 签名，否则 explorer 重启后托盘图标不会重绘。
- 改 `uia_events.rs` 时保持回调零共享状态（只 `PostMessageW`）：UIA 事件在任意线程投递。
