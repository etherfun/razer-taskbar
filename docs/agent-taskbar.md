# 任务栏挂载 / 共存避让 / UI 绘制

适用：改动 `src/taskbar.rs`、`src/window.rs`、`src/uia_events.rs`、`src/hover.rs`、`src/tray.rs`。

## 窗口模型（`window.rs`）

- 顶层 `WS_POPUP` + `WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE`，`HWND_TOPMOST`，`move_overlay` 定位（`SWP_NOACTIVATE | SWP_SHOWWINDOW`）。不是 `WS_CHILD`：子窗口会被 TranslucentTB 类 acrylic 盖住。
- 尺寸：96 DPI 下 144×48，按 `GetDpiForWindow` 缩放。
- `WM_NCHITTEST` 回 `HTTRANSPARENT`：覆盖层点击穿透，菜单只挂托盘图标（`WM_RBUTTONUP/DOWN` → `show_menu`）。

## 事件驱动布局（Taskbar-Lyrics 移植）

三路触发同一 `place_widget`，去抖/去重后执行：

1. **`TaskbarCreated` 广播**（`RegisterWindowMessageW` 注册，`handle_taskbar_created`）：explorer 重启后重新 `find_shell_tray` → `taskbar::reset_hold_state()` → `invalidate_widgets_cache()` → `tray::ensure_created` 重挂托盘图标 → 通知 UIA 线程 rebind → `place_widget(true)` 重新锚定。
2. **UIA 结构变化事件**（`uia_events.rs`）：后台 MTA 线程在任务栏 `Windows.UI.Input.InputSite.WindowClass` 子元素上注册 `IUIAutomationStructureChangedEventHandler`（`TreeScope_Descendants`，找不到 InputSite 时退回整个 tray 根），回调只做 `PostMessageW(WM_APP_LAYOUT)`。线程收到 rebind 信号或每 30s 检查任务栏 HWND 变化后拆旧注册重建（`ensureInitialized` 模式）。HWND 跨线程以裸地址（isize）传递。
3. **1s 定时器**（`TIMER_ID`）：兜底轮询 + `tray::refresh()` + 每秒失效重绘（电量变化不改矩形，靠这一步上屏；`paint` 按签名去重，未变化只 `BeginPaint/EndPaint`）。

- **去抖**：`request_layout` 限频 250ms（`LAYOUT_DEBOUNCE`），被合并的请求置 `layout_pending`，由一次性 `TIMER_LAYOUT` 兜尾。
- **去重**：`place_widget` 记录上次屏幕矩形（`last_layout`）与上次日志行（`last_log`），矩形不变则不 `InvalidateRect`（纯移动对分层窗口无需重绘）；日志仅在状态变化时输出。`paint` 以 `(label, level, charging, connected, w, h)` 签名去重，未变化时只 `BeginPaint/EndPaint` 验证更新区域。
- **UIA 查询缓存**（`widgets_button_rect`）：`(rect, notify_left, seen)` 缓存 30s，`TrayNotifyWnd` 左边沿变化或 `TaskbarCreated` 时失效；`TaskbarDa=0`（天气板关闭）时直接跳过 UIA。

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

## 共存避让状态机

- 枚举父窗口可见子窗口，跳过 `WHITELIST`（`Start`、`ReBarWindow32`、`MSTaskSwWClass`、`TrayNotifyWnd`、`DesktopWindowContentBridge` 等，XAML 覆盖层必须保留，否则每次都会"让位"）。
- 透明 padding 不挡路：`shrink_to_content` 屏幕采样收紧为 content rect（`block_rect`），比较与让位都用它（5s 缓存）。
- 身份：`OccupantKey(pid, class, 高度桶)`（不用裸 HWND，防复用误判）；`annotate_moves` 记 30s TTL、2px 抖动阈值，`moved` 粘性、`is_new` 首见。
- 让位节奏：`try_hold_position` —— 无碰撞 hold；新碰撞进 grace（静态 1s / 移动或新来 3s，`waiting=true`，按 `Instant` 计时，事件驱动下更频繁调用不改变语义）；同 key 过期才 yield。`avoid_overlap=false` 时清空第三方 occupant（仅钉死原始锚点，天气板仍避让）。
- 防弹射：`MAX_AVOID_JUMP_PX = 500`，非首轮、相对 live 位移超限则 stay + `capped=true`；首轮（`first=true`，窗口仍在 0,0）与 `TaskbarCreated` 后的重锚不设防。
- 退场回位：hold 与跳跃上限都是"防其他挂件"的手段——band 内不再有任何第三方 occupant 时（挤开我们的那个已关闭），两者一并解除，直接回自然锚点，不再滞留原地（`alone_in_band`）。

## 实验性嵌入模式（`embed_into_taskbar`，默认关）

- `taskbar::set_taskbar_child`：Lyricify 任务栏歌词同款——跨进程 `SetParent` 进任务栏带（Win11 是 `Shell_TrayWnd`，Classic 是 `ReBarWindow32`），`WS_POPUP→WS_CHILD`、去 `WS_EX_TOPMOST`、保留 `WS_EX_LAYERED|NOACTIVATE`，并 `HWND_TOP` 保持兄弟第 0 位（压在 XAML 桥上）。子窗口随任务栏生灭，z 序争夺战（`arm_z_burst`/`z_covered`）整体跳过，改为每秒 `reassert_child_top`。
- 坐标换算：定位数学输出带坐标，子窗口用父 CLIENT 坐标，差值即 `client_origin(parent)`。
- 失败回退：`SetParent` 被拒（安全软件拦截等）置 `embed_failed` 粘性回退覆盖模式；explorer 重启后父窗被拆，`place_widget` 每轮校验 `is_child_of`，丢父即重嵌或回退。
- 避让/occupant 枚举在嵌入下照常工作（枚举按 hwnd + 自身 pid 排除自己）；`embed_into_taskbar=false` 或删除该键即回到纯覆盖模式。

## 绘制与菜单

- `paint`：黑底整窗填充（colorkey 抠除）→ 只画字形/文字/阴影，TTB acrylic 可透出。字体 `Segoe Fluent Icons`（回退 `Segoe MDL2 Assets`）+ `Segoe UI Variable Text`（回退 `Segoe UI`），灰度抗锯齿；阴影 `0x202020`（纯黑会被抠掉）。
- 无设备/离线：灰色 + `--`。
- 设备类型图标（`icons.rs`，纯 GDI 矢量）：鼠标 / 耳机 / 键盘 / 其他（USB dongle）。挂件布局 `[类型图标][电池字形][百分比]` 居中；无设备时不画类型图标。判定：V4 日志 `category` 字段（MOUSE/KEYBOARD/HEADSET/…）优先，回退 `device_kind` 产品名关键词，兜底 Other（`battery.rs` 有单测）。图标宽度经 `width_for` 参与居中计算。
- 悬停设备面板（`hover.rs`）：挂件点击穿透收不到鼠标消息，`TIMER_HOVER`（120ms）轮询 `GetCursorPos`+`PtInRect` 检测悬停，静置 350ms 后显示。面板同为顶层分层 `NOACTIVATE` + `HTTRANSPARENT` 窗口（只读、不抢焦点、不挡点击）；光标在挂件或面板矩形外即隐藏。面板为圆角矩形（`RoundRect` 一笔填充+描边，直角落在 colorkey 黑上即透明），行布局 `[类型图标][电池字形][名称][充电闪电列（仅有个别行充电<50%时存在）][百分比]`，当前显示设备名称亮白。位置：底部任务栏向上弹出、顶部任务栏向下弹出，x 钳制进工作区；行内容/几何都有去重，无变化不重绘。菜单开关 `hover_devices`（默认开）。
- 菜单（`show_menu`，托盘图标右键，刻意精简）：All devices + 已连接按名排序（快速切换）→ Settings… → Battery history… → Exit。ID 段：`ID_SETTINGS=1011`、`ID_DEVICE_BASE=2000`。
- 设置窗口（`settings.rs`，"Settings…" 打开）：整合原菜单全部设置项，深色 Win11 风格同 `viewer.rs`（DWM 深色标题栏/圆角、卡片分区：挂件/电量记录/通用、owner-draw 左右侧 pill、DarkMode_Explorer 复选框与下拉）。单实例、跑在 UI 线程同一消息循环；所有改动即时应用 + 落盘（无 OK/Cancel），经 `window.rs` pub 辅助函数（`modify_config`/`set_shown_device`/`reposition_widget`/`config_snapshot`/`devices_arc`/`widget_hwnd`）改 `STATE.config` 并同步 UI 侧副作用（托盘重挂/隐藏、hover 隐藏、重排重绘、`i18n::set_setting` + tray/viewer/settings 三处 `sync_language`）。watcher 每轮重读 settings.json，改轮询/记录间隔无需重启。复选框/下拉的 `BM_SETCHECK`/`CB_SETCURSEL` 不回发 `BN_CLICKED`/`CBN_SELCHANGE`，刷新无回声问题。
- 托盘（`tray.rs`）：16×16 GDI 图标（轮廓+填充+闪电）+ tooltip，仅为菜单入口兜底；`refresh()` 按 (tooltip, 电量状态) 签名去重，`ensure_created` 重置签名（explorer 重启重挂后必须真正重画）。

## 禁区

- 不删白名单、不把天气板当普通 occupant、不动 `DesktopWindowContentBridge`。
- 不把 `SetLayeredWindowAttributes` 调两次（第二次会替换 colorkey 模式）。
- 定位改动必须保留 hold/grace/jump-cap 三件套行为，并在终端用 occupant 日志验证（见 `docs/agent-build.md`）。
- **UIA 缓存失效路径必须保留**：`TaskbarCreated` → `reset_hold_state` + `invalidate_widgets_cache`；`ensure_created` 必须重置 `TRAY_LAST` 签名，否则 explorer 重启后托盘图标不会重绘。
- 改 `uia_events.rs` 时保持回调零共享状态（只 `PostMessageW`）：UIA 事件在任意线程投递。
