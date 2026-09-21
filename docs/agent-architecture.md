# 核心宿主与功能挂载（core / features）

适用：改动 `src/RazerTaskbar/Host/`、`src/RazerTaskbar/Features/`、`Program.cs`、`App.xaml.cs`，
以及任何"新增一个 UI 表面或后台线程"的改动。

一句话：**进程归核心，表面归功能**。挂件窗口、设置/历史窗口都由功能持有，销毁/隐藏/重建由功能自己决定，
进程只在 `AppHost.RequestExit` 时结束。

## 结构

```
Program.cs                      入口：AppHost.Run(args)（12 行）
App.xaml(.cs)                   XAML Application 壳：安装异常钩子、把 dispatcher 交给宿主；不持有窗口
Host/
  AppHost.cs                    核心：探针 → 单实例 → Bootstrap → 服务 → 挂载功能 → Application.Start → 退出策略
  IAppFeature.cs                功能契约（Name/Required/State/Start/Stop）+ AppFeature 基类
  FeatureRegistry.cs            挂载（按顺序）/ 卸载（逆序）/ 故障上报，全部带日志
  UiThread.cs                   UI 线程投递；已在 UI 线程时**内联执行**（否则排队的关闭会落在退出之后）
  KeepAliveWindow.cs            保活窗口：永不显示/激活/用户关闭的 XAML 窗口，锚住 dispatcher 循环
  UiSelftest.cs                 环境变量驱动的无头 UI 自测（开关见 agent-build.md）
Features/
  Widget/WidgetFeature.cs       挂件层：覆盖层/嵌入窗 + 托盘 + 悬停 + UIA 重排（razer-widget STA 线程，Native/ 实现）
  Data/WatcherFeature.cs        数据层：日志解析 + HID/BLE 直读 + 历史采样（razer-watcher 线程，Core/Services 实现）
  ControlPanel/ControlPanelFeature.cs        控制面板功能：窗口按需新建、关闭=隐藏、可显式销毁
  ControlPanel/ControlPanelWindow.xaml(.cs)  窗口本体（原 MainWindow）
  ControlPanel/HistoryPage.xaml(.cs) / SettingsPage.xaml(.cs) / BatteryChart.cs
```

`Native/`（Win32/D2D/UIA interop 与原生表面）仍在原处，是挂件功能的实现库；原 `Views/`、`Controls/`
两个目录并入 `Features/ControlPanel/`（页面只服务于这一个窗口）。

## 生命周期契约

| 归属 | 内容 | 规则 |
|---|---|---|
| 核心 | 进程、Bootstrap、`ConfigService`/`HistoryService`/`I18n`/`AppState`、`Application.Exit` | 服务的初始化与关闭只在这里发生；功能**不得**释放核心服务 |
| 功能 | 线程、窗口、托盘图标、UIA 注册 | `Start()` 幂等可失败，`Stop()` 幂等；表面的显示/隐藏/销毁不得影响其他功能 |

- **必需 vs 可选**：`Required => true` 只给"没有它应用就没有可用表面"的功能——目前只有挂件线程（托盘
  图标也挂在它上面）。必需功能挂载失败或线程崩溃 → 核心记日志后结束进程；可选功能（控制面板）失败
  只记日志，其余照跑。
- **退出顺序**：`RequestExit(reason)`（任意线程，首个调用生效）→ UI 线程：
  `FeatureRegistry.UnmountAll`（逆序 control-panel → widget → watcher）→ `HistoryService.Close()` →
  保活窗口销毁 → `Application.Exit()` → `Application.Start` 返回 → Main 收尾返回 0。
- **降级模式**：Windows App SDK Runtime 缺失时不进入 XAML 循环，Main 阻塞在 `ExitGate` 上等退出请求
  （旧代码会把 Main 直接返回，把还在屏幕上的挂件一起带走）。托盘菜单的 Settings/History 置灰。

退出日志锚点（每次退出应全部可见）：
```
core: exit requested (widget: exit command)
feature widget: thread ended (message loop quit)
control-panel: window destroyed (heap=... private=...)
watcher: stopped
core: features unmounted (widget: exit command)
```

## 保活窗口（关键机制，勿删）

WinUI3 在**最后一个 XAML 窗口被销毁**时结束 `Application.Start` 的循环：Main 返回、进程退出、
挂件与监听线程一起消失。所以重构前 `MainWindow` 的 `Closing` 只能 `args.Cancel = true; Hide()`，
注释写明"销毁唯一的 XAML 窗口会结束进程"。

现在 `Host/KeepAliveWindow.cs` 在 `App` 构造时创建一个永不激活、永不显示的 XAML 窗口，它只是
dispatcher 循环的锚。控制面板因此与进程彻底解耦：**销毁它是允许的**（`Destroy()`，自测开关覆盖），
只是默认关闭行为不这么做（原因见下一节）。

红线：不要删保活窗口、不要给它加 `Hide()`/关闭路径、不要在它的 `Closing` 里取消关闭。

## 窗口重建会泄漏原生内存（2026-09-21 实测，决定"关闭=隐藏"）

反复"打开设置/历史窗口 → 关闭"会让私有字节与句柄**线性上涨**（用户实报）。逐层定位
（每轮 open→close 后强制 GC，取 6~8 轮，Win11 26340）：

| 内容 | 私有字节 | 句柄 | 结论 |
|---|---|---|---|
| 裸 `Window`（无内容，激活后关闭） | 113 → 116 MB 平 | 1381 → 1389 平 | 窗口本身不泄漏 |
| `Window` + 空 `Grid` | 111 → 115 MB | 1328 → 1357 | 基本平（+5/轮） |
| `Window` + `NavigationView`（空） | 112 → 125 MB | 1317 → 1431 | +19 句柄/轮 |
| `Window` + `HistoryPage` | 112 → 199 MB | 1318 → 1787 | **+77 句柄 / +15-25 MB 每轮** |
| `Window` + `SettingsPage` | 111 → 140 MB | 1316 → 1475 | +26 句柄 / +5 MB 每轮 |
| 完整面板（真实路径，含内容拆除 + 强制回收） | 112 → 220 MB | 1330 → 2126 | +30 句柄 / +6 MB 每轮，线性 |

关键点：

- 泄漏量与 **XAML 树的规模**成正比，托管堆每次都回到 ~1.9 MB（`GC.Collect(Forced)` +
  `WaitForPendingFinalizers` 之后），所以是 **WinRT/XAML 原生对端（native peer）没被释放**，
  不是我们的托管对象被持有：面板代码里没有静态可变状态，事件订阅都是元素自持（`+=` 的双方同一棵树）。
- `SystemBackdrop`（DesktopAcrylicBackdrop）**不是**元凶：去掉亚克力后同样泄漏（177/185/191/201）。
- 旧的收尾调用 `GC.Collect(2, GCCollectionMode.Optimized, blocking: false)` 是**提示**，
  GC 可以不理会——这正是首次上线时"关闭后内存不降、反复开关一路上涨"的直接原因；
  换成 `Forced`（非压缩、不阻塞）后托管侧确实回收了，但原生侧仍留 ~6 MB/轮。

因此策略定为：**默认关闭 = 隐藏**（`ControlPanelFeature.HideOnUiThread` → `AppWindow.Hide()`），
窗口与页面树常驻、不重建，实测 12 轮开关私有字节 181-186 MB、句柄 1835-1864、GDI 64、USER 86
全部走平；页面用 `NavigationCacheMode.Required` 缓存，切标签也不重建树。

用户关闭路径也被接管：`ControlPanelFeature.OnWindowClosing`（`AppWindow.Closing`）取消关闭并改为
`Hide()`，所以点 X / Alt+F4 与托盘关闭行为一致（外部连发 5 次 `WM_CLOSE` 实测：窗口 `visible=False`、
私有字节 140 MB 与句柄 1548 全程不动、进程存活）。`Destroy()` 用 `_destroying` 标志放行真正的关闭，
先 `Content = null` + `SystemBackdrop = null` 再 `Close()`，随后 `Forced` 回收（不加这两步会从 ~6 MB/轮
恶化到 15-25 MB/轮），代价是每轮 ~6 MB 原生残留——只在退出与自测里用。

### 若将来必须让"关闭=销毁"

先接受每轮 ~6 MB/30 句柄的残留（或等 WinUI 修复），并保留 `Content`/`SystemBackdrop` 拆除与
`Forced` 回收（去掉它们会恶化到 15-25 MB/轮）。判断有没有变好，用
`RAZER_TASKBAR_PANEL_DESTROY_CYCLE_SECS` 跑多轮看 `memprobe` 式的私有字节/句柄斜率。

## 实测记录（2026-09-21，Win11 26340）

- 销毁窗口不结束进程（结构保证，验收路径）：自测 `RAZER_TASKBAR_PANEL_DESTROY_CYCLE_SECS=6` →
  日志 `selftest: destroy panel (not hide)` → `control-panel: window destroyed` → 进程存活
  （无 `core: exit requested`）、挂件继续 `placement`；5s 后重建成功（`creating window (first open
  since last destroy)`）。向真实 HWND 发 `WM_CLOSE`（=点 X）早年版本同样验证过存活。
- 内存基线：空闲 111-113 MB / ~1320 句柄；首次打开面板 +70 MB / +560 句柄（WinUI 框架 + 页面树
  一次性预热，进程内拿不回来：强制阻塞 GC 只多回收 ~6 MB，第二次打开不再重复付费）。
- 隐藏式开关 12 轮：私有字节 181-186 MB、句柄 1835-1864 走平（见上节表格）。
- 退出路径（`RAZER_TASKBAR_EXIT_AFTER_SECS`，与托盘 Exit 同链）：功能逆序卸载、进程干净结束、
  无未捕获异常。

## 新增一个功能的 checklist

1. `Features/<Name>/<Name>Feature.cs` 继承 `AppFeature`：`Start()` 建线程/订阅事件，`Stop()` 幂等释放；
   线程体自己 `try/catch` 并调 `AppHost.OnFeatureFaulted(this, e)`（.NET 会因未捕获的线程异常杀进程）。
2. `Required` 只在"没有它就没有可用表面"时为 true；UI 类功能一律可选，窗口留到首次 `Show()` 再建。
3. **UI 表面遵循"建一次、别再拆"**：窗口/页面树创建昂贵且销毁会泄漏原生内存（本节实测），
   关闭就用隐藏，页面加 `NavigationCacheMode.Required`；确实需要销毁时走显式 `Destroy()` 并接受残留。
4. 在 `AppHost.Run` 里按依赖顺序 `Features.Mount(...)`（数据先于表面）。
5. 更新本文件 + `AGENTS.md` 目录表；行为可观测的部分加探针或 `UiSelftest` 开关（`docs/agent-build.md`）。

## 线程模型（与 agent-csharp.md 一致，归属已按功能划分）

| 线程 | 归属 | 职责 |
|---|---|---|
| 主线程 (STA) | 核心 + control-panel | `Application.Start`；保活窗口；设置/历史窗口按需新建、关闭隐藏 |
| razer-widget (STA) | widget | 覆盖层/嵌入窗、托盘、悬停、UIA 重排（Native/WidgetWindow.cs） |
| razer-watcher | watcher（数据） | 日志解析 + HID/BLE 直读 + 历史采样 |
| razer-uia-events (MTA) | widget | UIA 结构变化 → `PostMessage(WM_APP+2)` |

跨线程：UI → 挂件用 `WidgetThread.Post`（`WM_APP+3` + GCHandle 闭包）；挂件/自测 → UI 用 `UiThread.Post`。
配置权威副本仍在 `AppState`（挂件线程写 + 落盘，UI 经 Post 修改）。
