# razer-taskbar — Agent 协作规范

Windows 任务栏电池挂件（C# / WinUI 3 + Win32 P/Invoke，挂件层 Direct2D+DirectWrite/ULW 原生绘制，无图片资源）。
Rust 原版已从本分支移除（git 历史可考），当前实现即 C# 全量版。

- 运行环境：Windows 10 / 11；Razer Synapse 3/4 可选（回退电量源 + 蓝牙身份桥）；.NET 8 SDK 构建，
  Windows App SDK Runtime 缺失时降级为挂件-only（Bootstrap 静默失败，不退出进程）。
- 构建产物：`dist/razer-taskbar.exe`（`dotnet publish` 直出，无安装程序；运行/自启动只认 dist）。
- 入口：`src/RazerTaskbar/Program.cs`（12 行，全部转交 `Host/AppHost.cs`）。核心宿主做单实例检查 →
  Bootstrap.TryInitialize → 初始化共享服务 → **挂载功能** → `Application.Start`，并独占退出策略；
  四个线程按功能划分：主线程 STA（核心 + 控制面板）、razer-widget STA（挂件/托盘/悬停）、razer-watcher
  （日志解析+历史采样）、razer-uia-events MTA（UIA 监听）。结构与新增功能清单见 `docs/agent-architecture.md`。
- **进程归核心、表面归功能**：挂件窗口与设置/历史窗口都由功能持有，关掉任何一个都不结束应用
  （`Host/KeepAliveWindow.cs` 永不显示的 XAML 窗口是 dispatcher 循环的锚 —— 勿删、勿隐藏）。
  但**默认关闭 = 隐藏**：反复创建/销毁 XAML 树会按树规模泄漏原生内存与句柄（实测 +6 MB/30 句柄每轮，
  见 `docs/agent-architecture.md`），所以窗口建一次、页面用 `NavigationCacheMode.Required` 缓存；
  销毁能力保留（`Destroy()` + 自测开关），只在退出时用。
- 任务栏布局事件驱动（Taskbar-Lyrics 式）：`TaskbarCreated` 广播 + UIA 结构变化事件触发去抖重排，
  1s 定时器兜底；详见 `docs/agent-taskbar.md`。

## 目录结构

| 路径 | 说明 |
|---|---|
| `src/RazerTaskbar/` | WinUI3 主应用（exe，`WindowsPackageType=None` 框架依赖 unpackaged） |
| `Program.cs` / `App.xaml(.cs)` | 入口与 XAML Application 壳（单实例/Bootstrap/线程划分离到 Host/） |
| `Host/` | **核心宿主**：`AppHost`（启动、功能挂载、退出策略）、`FeatureRegistry`、`IAppFeature`、`UiThread`、`KeepAliveWindow`、`UiSelftest` |
| `Features/Widget/` `Features/Data/` | 功能：挂件线程（含托盘/悬停/UIA）与数据采集线程的挂载与生命周期 |
| `Features/ControlPanel/` | 功能：设置/历史窗口（原 `MainWindow` + `Views/` + `Controls/`，关闭即销毁）|
| `Native/WidgetWindow.cs` | 挂件覆盖层窗口、ULW/D2D 绘制、菜单、事件驱动布局、embed v2、交叉淡化 |
| `Native/TaskbarLocator.cs` | 任务栏发现、Win10/11 定位、widgets 板 UIA 查询与避让 |
| `Native/HoverPanel.cs` | 悬停设备列表面板（光标轮询，非交互只读） |
| `Native/TrayIcon.cs` | 托盘兜底图标（菜单入口，explorer 重启后重挂） |
| `Native/DeviceIcons.cs` / `Native/D2d.cs` | 设备字形常量、吸附字号 / 共享 D2D 渲染上下文（工厂/字体回退/格式缓存/墨迹扫描）|
| `Native/UiaEvents.cs` + `Native/Interop/Uia.cs` | UIA 结构变化监听（手写 COM interop） |
| `Native/Interop/Win32.cs` | Win32 P/Invoke 声明集中地 |
| `Native/AppState.cs` / `SingleInstance.cs` | 配置权威副本（挂件线程写+落盘）/ 单实例守卫 |
| `src/RazerTaskbar.Core/` | 无 UI 类库（exe 与测试共享） |
| `Core/Hid/`（RazerReport / RazerPidTable / BleBattery / BleVendor） | USB HID 90 字节 vendor report 与 BLE 厂商 GATT 通道 |
| `Core/Interop/HidApi.cs` | hid.dll P/Invoke |
| `Core/Models/`（DeviceModels / DisplayMode / Glyphs / HistoryModels） | 设备模型、`DeviceSelector`+`DisplayModeResolver` 选择决策器、字形/颜色 |
| `Core/Services/WatcherService.cs` | Synapse V3/V4 日志监听与解析（+HidWatcher 直读、身份桥接） |
| `Core/Services/HistoryService.cs` | 电量历史：SQLite 采样、周期切分、三层预测；见 `docs/agent-history.md` |
| `Core/Services/SpikeFilter.cs` / `ReboundFilter.cs` | 瞬时跳变剔除（写时）/ 弛豫回弹剔除（读时） |
| `Core/Services/ConfigService.cs` | `%APPDATA%\razer-taskbar\settings.json` 读写、自启动同步 |
| `Core/Services/I18n.cs` / `Log.cs` / `ExportService.cs` | 国际化 / 日志（文件+stderr 镜像）/ CSV 导出 |
| `tests/RazerTaskbar.Tests/` | xUnit 测试（Rust 侧单测的移植 + C# 扩展） |
| `build.ps1` | 停进程→清 dist→publish→验证 dll→(测试)→重启 |
| `RazerTaskbar.sln` | 3 项目：RazerTaskbar / RazerTaskbar.Core / RazerTaskbar.Tests |

## 常用命令

```powershell
dotnet publish src/RazerTaskbar/RazerTaskbar.csproj -c Release -p:Platform=x64 -o dist   # 部署（先停常驻进程）
dotnet test    tests/RazerTaskbar.Tests/RazerTaskbar.Tests.csproj                        # 单元测试（勿加 --quiet）
powershell -ExecutionPolicy Bypass -File build.ps1 [-Test] [-Run] [-NoRun]               # 一键脚本
```

- `-p:Platform=x64` 必须带，否则落另一棵输出树产生互不覆盖的陈旧副本。
- exe 运行时锁 `razer-taskbar.dll`（MSBuild 复制静默失败）：构建前先停进程（build.ps1 已封装）。
- 部署成功看 **razer-taskbar.dll** 时间戳（exe 只是 apphost 壳）。

详情见 `docs/agent-build.md`。

## 配置速览

- 路径：`%APPDATA%\razer-taskbar\settings.json`，缺失键由 `ConfigService` 默认值回填，旧配置文件始终可加载。
- 关键字段：`polling_throttle_secs`、`shown_device_handle`、`display_mode`（fixed/drop_swap/rotate，默认 fixed；drop_swap=其他设备电量下降（如 100→99）时临时替换显示 `swap_display_secs` 秒（默认 30），rotate=全部在线设备按名称轮播、每台 `rotate_interval_secs` 秒（默认 30）；决策器 `Core/Models/DisplayMode.cs`，模式/参数变更需重置运行态）、`synapse_version`（auto/v3/v4）、`battery_source`（auto/hid/log，默认 auto：**按设备**沿 有线 USB = 2.4G 接收器 > 蓝牙 > Synapse 日志 取值——USB 直读，蓝牙设备走 Razer 厂商 GATT 通道（电量+充电，被占用时回退 BAS），本轮没被直接读到的设备才由日志解析补齐；被直接读到的设备日志不覆盖也不判离线；胜出链路记入 `RazerDevice.Transport` 并持久化到 `devices.source`，见 `docs/agent-hid.md`）、`widget_side`（left/right）、`embed_into_widgets_space`（默认 false；开启后挂件嵌入任务栏小组件按钮内部空位并忽略 widget_side，旧值 widget_side=widgets 载入时自动归一为本开关，同时整块拦截鼠标——挂件区域吞掉点击不再触发小组件面板，WM_NCHITTEST + alpha 底板双闸）、`embed_into_taskbar`（默认 false；嵌入 v2：销毁重建为任务栏带真正子窗口，ULW+重建 poke 呈现、锚窗口承载线程绑定，可与 widgets_space 组合，见 `docs/agent-embed.md`）、`avoid_overlap_with_widgets`、`show_tray_icon`、`show_widget`、`hover_devices`、`window_offset_*`、`taskbar_*_space_win11`、`record_battery_history`（默认 true）、`device_battery_types`（每台设备的电池类型覆盖，handle → `rechargeable`/`replaceable`，缺省按型号自动判定——openrazer 的 AA/AAA 名单；可更换电池从不充电，其读数跃升按换电而非充电会话处理，设置入口在历史页设备选择旁，见 `docs/agent-battery-type.md`）、`show_estimated_time`（默认 true，挂件第二行显示预计时间）、`color_battery_icon`（默认 false，充电/省电/离线状态色常显，开启后普通模式电量按绿→红渐变，双层字形渲染）、`fade_transition`、`history_poll_interval_secs`（默认 5）、`language`（auto/en/zh）。
- 电量历史库：`%APPDATA%\razer-taskbar\battery.db`（SQLite/WAL，永久保留，表 `samples`/`devices`）。
- 自启动：`HKCU\...\Run\RazerTaskbar`，由设置页开关同步。

## Agent 工作守则

- Windows-only 项目：不引入跨平台抽象，不假设 Linux/macOS 可编译。
- Win32/COM interop 集中在 `Native/Interop/`、`Core/Interop/`（手写 COM interop 的 IID/vtable 对齐官方
  Win32 元数据）；Win32 调用失败路径只记日志（`Log`），消息循环/回调内禁止未捕获异常。
- 日志解析改动必须同步更新 `tests/RazerTaskbar.Tests`（`WatcherV3Tests`/`WatcherV4Tests`）；显示选择改动
  必须覆盖 `BatteryTests`/`DisplayModeTests`；协议改动同步 `HidTests`；历史/过滤改动同步
  `HistoryTests`/`ReboundFilterTests` 等；电池类型名单/判定改动同步 `BatteryTypeTests`（见各 agent 文档"测试锚点"节）。
- 任务栏定位改动先读 `docs/agent-taskbar.md`：小组件板（天气）硬保留、UIA 缓存失效路径必须维持；不重新
  引入反应式第三方避让（设计决定见该文档）。
- 配置新增字段必须带默认值（`ConfigService`），保持旧配置文件可加载。
- 常驻内存红线：**不要删 `WidgetWindow.MemWatchdog`**（1s 定时器里堆过 8 MiB 就做一次后台回收）。
  运行时的 GC 预算按物理内存推算，本应用每秒几 KB 的瞬时分配永远够不到它——实测十分钟 `gc0=0`、
  私有字节 +0.9 MB/分钟无上限（用户看到的"内存一直涨"），强制回收后堆 8 MB→2 MB 存活且走平。
  新增绘制/轮询代码时避免每帧/每轮分配大数组（>85 KB 即落 LOH，例：墨迹扫描曾每次 `Marshal.Copy`
  一个 294,912 B 数组）；缓存要么有界（`InkCacheMax`），要么键空间固定。
- 功能生命周期红线：**不要删 `Host/KeepAliveWindow`**（保活窗口 = `Application.Start` 循环的锚，
  删掉后关闭设置/历史窗口会让整个应用退出，这正是重构前的老毛病）；不要把控制面板改回 hide-on-close；
  功能线程体必须自带 `try/catch` 并上报 `AppHost.OnFeatureFaulted`（.NET 会因未捕获的线程异常杀进程）；
  功能不得释放核心服务（如 `HistoryService.Close()` 只允许出现在 `AppHost.Shutdown`）。
  详见 `docs/agent-architecture.md`。
- 渲染保真红线：任何模式**不**调 `SetLayeredWindowAttributes` COLORKEY（黑 key 有 AA 暗边）；阴影
  `0x202020` 非纯黑；覆盖层保持 `WS_POPUP`、嵌入保持出生即 `WS_CHILD`，模式切换走销毁重建**永不**
  SetParent（`docs/agent-embed.md`）；z-burst 运行中不重排定时器；空文本 DrawText 短路（`D2d.DrawInkText`/`TextWidth` 内置）；V4 末行损坏
  不推进时间戳；**尺寸与帧同一次 Paint 提交**——量出新自然尺寸后必须按新尺寸重渲染、`SetWindowPos`、
  ULW 一气呵成（`WidgetWindow.Paint` 的 resize 分支），先呈现后等 1s 轮询缩放会让旧尺寸表面被拉伸进
  新矩形，被带重建冻结成可见闪帧。

# 分文档索引

- 构建/测试/调试/诊断探针/内存与磁盘 I/O 测量：`docs/agent-build.md`
- 编码规范与检查：`docs/agent-conventions.md`
- 日志解析与设备选择：`docs/agent-watcher.md`
- 任务栏挂载/共存避让/UI 绘制：`docs/agent-taskbar.md`
- 电量历史与预测（采样/切分/三层预测/防伪过滤）：`docs/agent-history.md`
- 电池类型（AA/AAA vs 内置充电）：`docs/agent-battery-type.md`（含 HID 实测结论与自动检测路径）
- HID 直读电量（协议/Windows 坑/探针）：`docs/agent-hid.md`
- 核心宿主与功能挂载（core/features 结构、窗口生命周期、保活窗口、新增功能清单）：`docs/agent-architecture.md`
- C# 实现总览（线程模型/模块映射/冒烟记录/已知差异）：`docs/agent-csharp.md`
- 任务栏真嵌入设计（embed v2/26340 实测机制）：`docs/agent-embed.md`
