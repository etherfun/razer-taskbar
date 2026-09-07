# C# + WinUI3 移植(分支 `refactor/csharp-winui3`,仓库根目录)

Rust 版的全量 C# 移植实验:挂件/托盘/悬停/日志监听/UIA 用 C# P/Invoke 重写(观感像素级对齐),
电量历史 + 设置页合并为一个 WinUI3 NavigationView 窗口。**Rust 版已从本分支移除**;
共享同一份 `%APPDATA%\razer-taskbar\settings.json` 与 `battery.db`(schema 兼容)。

## 构建与测试

```powershell
# 部署/探针一律用 Release x64(publish 直出仓库根 dist/,运行只认它):
dotnet build   src/RazerTaskbar/RazerTaskbar.csproj -c Release -p:Platform=x64
dotnet publish src/RazerTaskbar/RazerTaskbar.csproj -c Release -p:Platform=x64 -o dist
dotnet test    tests/RazerTaskbar.Tests/RazerTaskbar.Tests.csproj
# 常驻运行: dist/razer-taskbar.exe(publish 前先清掉旧 dist,防陈旧文件混留)
# 或直接用根目录脚本(封装:停进程→清 dist→publish→验证 dll→可选测试→重启):
powershell -ExecutionPolicy Bypass -File build.ps1 [-Test] [-Run] [-NoRun]
```

- **先停常驻进程再构建**:`razer-taskbar.exe` 运行时锁住 `razer-taskbar.dll`,
  MSBuild 的复制步骤会静默失败——Core.dll 刷新了而 app 产物仍是旧版,改完"没生效"多半是它。
- 普通构建不刷新 win-x64 RID 输出时加 `--no-incremental`。
- exe 是 apphost 壳,判断是否部署成功要看 **razer-taskbar.dll** 的时间戳。
- **不带 `-p:Platform=x64` 的构建会落到另一棵输出树 `bin/Release/.../win-x64/`**:
  那里的陈旧副本与规范路径互不覆盖,从旧路径手动启动就会跑旧版
  (2026-09-07 踩过:color-key 时代的 `bin/Release` 副本被启动,误判为渲染回退)。
  bin 树只用于构建,运行/自启动一律指向仓库根 `dist/razer-taskbar.exe`(publish 直出,
  `.gitignore` 已忽略 dist/)。
- **dotnet test 不要加 `--quiet`**(MSBuild 参数解析冲突);注意 `dotnet test` 只重建测试依赖链,
  不含 app csproj——探针参数(app 侧)改动后必须单独 build app 再跑探针。

## 独立诊断探针(不进 UI,单实例守卫之前)

| 命令 | 用途 |
|---|---|
| `razer-taskbar.exe --hid-probe` | HID 全枚举 + 电量/充电查询 + GATT 全 dump |
| `razer-taskbar.exe --hid-scan` | get 半区只读全段扫描(分钟级) |
| `razer-taskbar.exe --ble-vendor` | Razer BLE 厂商 GATT 通道重放(观测查询基线) |
| `… --ble-vendor --sweep` | 厂商通道 page 01/05 × id 0x80-0xFF 只读枚举 |
| `… --ble-vendor --raw=LEN:PAGE:ID:PARAM[:hex]` | 单发命令(LEN≠0=写,需 `--yes-i-know`;set 半区 id 拒绝) |
| `… --ble-vendor --power` | 生产路径 `BleVendor.TryReadPower` 自检(电量/充电/回退) |

- BLE 厂商通道探针要求设备在蓝牙模式;通道被驱动/服务层占用时 `--power` 报
  `characteristics missing` → null(回退路径,属预期)。协议细节见 `docs/agent-hid.md`。
- **STA 线程饿死 WinRT 事件泵**(探针踩坑):WinRT `ValueChanged` 回调在 STA 主线程同步阻塞时
  会被泵调度延迟数秒,响应帧全部错位。探针核心必须跑在 MTA 线程池
  (`Task.Run(...).GetAwaiter().GetResult()`);HidWatcher 的轮询线程本就是后台 MTA,无此问题。

## 运行时模型(对应要求)

- **不打包 WinUI3 依赖**:csproj 用 `WindowsPackageType=None` + `WindowsAppSDKSelfContained=false`
  + `SelfContained=false`,依赖系统安装的 Windows App SDK Runtime(框架依赖、unpackaged)。
  csproj 直接引用 WinUI/Foundation/Base/Runtime 子包而非整合包,避开 AI/ML/Widgets 负载。
- **Bootstrap 降级**:DISABLE_XAML_GENERATED_MAIN + 自定义 Main 自己调 `Bootstrap.TryInitialize`
  (Options.None 静默失败)。运行时缺失 → 挂件-only 降级,托盘菜单中 Settings/History 置灰,
  不退出进程(整合包自动初始化是 Environment.Exit,不可接受,故必须自定义 Main)。
- **跟随系统亮/暗**:不强制 RequestedTheme;图表配色走 ActualTheme + ThemeDictionaries。

## 线程模型(对应 Rust 三线程 + UIA 线程)

| 线程 | 职责 |
|---|---|
| 主线程 (STA) | WinUI3 `Application.Start`;MainWindow(NavigationView)按需惰性创建,空闲时不加载 XAML |
| razer-widget (STA) | 挂件覆盖层 + 悬停面板 + 托盘 + 菜单 + 4 定时器 + GetMessage 循环 |
| razer-watcher | 日志解析 + 历史采样(设置每轮重读,即时生效契约不变) |
| razer-uia-events (MTA) | UIA 结构变化监听,回调仅 PostMessage(WM_APP+2);TaskbarCreated 重绑 + 30s 自检 |

跨线程:UI → 挂件线程用 `WidgetThread.Post`(WM_APP+3 + GCHandle 闭包);挂件 → UI 用
`DispatcherQueue.TryEnqueue`。配置权威副本在 `AppState`(挂件线程写 + 落盘;UI 经 Post 修改)。

## 模块映射

| Rust | C# | 说明 |
|---|---|---|
| main.rs | Program.cs + App.xaml.cs | 单实例 FindWindow + EnumChildWindows 兜底;Bootstrap 降级 |
| window.rs | Native/WidgetWindow.cs | 类名/样式/colorkey/PaintSig 去重/墨迹居中/定时器 1,2,3,4/z-burst/菜单 ID 全保留 |
| taskbar.rs | Native/TaskbarLocator.cs | Win11 判定、右锚 notify.left−w+2、WidgetsButton UIA 30s 缓存、TaskbarDa 门控、embed 模式 |
| hover.rs | Native/HoverPanel.cs | 120ms 轮询 + 350ms dwell、黑 key 圆角面板 |
| tray.rs | Native/TrayIcon.cs | VERSION_4、每秒 NIM_MODIFY 去重、32×32 DIB 2x 软采样 16×16 HICON |
| icons.rs | Native/DeviceIcons.cs | 字形墨迹扫描 + ICON_SIZES 吸附(码点在 Core 共享给 FontIcon) |
| uia_events.rs | Native/UiaEvents.cs + Interop/Uia.cs | 手写 COM interop,IID/vtable 对齐官方 Win32 元数据(与 windows 0.58 crate 同源) |
| watcher.rs | Core/Services/WatcherService.cs | V3/V4 正则逐字保留;V4 camelCase + 显式 null→默认;FileSystemWatcher + 1s 去抖 |
| battery.rs | Core/Models + DeviceSelector | 选择规则/字形/五段色 |
| history.rs | Core/Services/HistoryService.cs | 同 schema/WAL;span 切分/instant 兜底逐条移植;预测为 C# 侧扩展(无 Rust 对应):EWMA 周期权重(30d 半衰期/180d 截断)+ 当前会话融合 + 逐级迁移剖面非线性外推(部分会话也计入,缺失档用速率填充)+ 充电速率健康度/寿命估算(History 页) |
| config.rs | Core/Services/ConfigService.cs | 同一路径/字段/默认值;Run 键自启 |
| i18n.rs | Core/Services/I18n.cs | 英文 key→zh 表 + LanguageChanged 事件热切换 |
| viewer.rs | MainWindow + Views/HistoryPage + Controls/BatteryChart | NavigationView 合并窗口;图表 = WinUI Shapes(网格/色带/面积/分段折线/换电点/5 刻度) |
| settings.rs | Views/SettingsPage | Win11 设置规范行布局;即时生效经 Post 回投挂件线程 |

## 冒烟验证记录(2026-09-06,Win11 26340)

- 单实例:Rust 版运行时启动 C# 版 → 1.1s 干净退出 ✓
- 挂件:`overlay kind=Win11 pos=(2,1556) size=144x40 embed=False`、FindWindowW 命中 ✓
- UIA:`UIA structure listener registered` ✓(注意:.NET `SetApartmentState(MTA)` 已初始化 COM,
  `CoInitializeEx` 返回 S_FALSE=1 属成功,必须按 `<0` 判失败)
- 菜单/悬停/两页面、亮暗切换:待人工验证(托盘右键 → Settings…/Battery history…)

## 已知差异 / 注意

- **UIA 矩形互操作修复(避让失效根因)**:`IUIAutomationElement.GetCurrentBoundingRectangle`
  返回的是 Windows RECT(4×int32 left/top/right/bottom),最初误声明为 4×double 的 UiaRect ——
  调用方写 16 字节、按 32 字节解读,boardLeft 永远是假 0,右侧避让(天气按钮实测
  (2039,1552)-(2191,1600),紧邻托盘)整体失效。修正后避让恢复:right 侧锚在 x=1895,
  天气按钮左侧。保留"board 仅在右半区才截断右锚"的防护(防未来按钮移到左缘)。
- **与 Rust 版的有意偏差(任务栏右侧锚定)**
:Win11 的 widgets 板按钮在新系统(26340 实测)
  位于任务栏**左缘**(UIA 返回 boardLeft=0)。Rust 的"右侧锚点必须停在 board 左侧"规则无条件生效,
  会把 right 侧挂件钳到 x=2。C# 版改为:**仅当 board 位于任务栏右半区(与托盘相邻)时**才用它截断
  右侧锚点;左缘的板由左侧 min_x 的 160px 禁区覆盖。实测 right→x=2068(紧贴托盘)、left→x=162。


- C# 版空闲内存约 100MB+ 量级(Rust 约 30MB):.NET 运行时 + WinUI 投影程序集;
  窗口惰性创建使 XAML 在首次打开前不加载。
- 启动竞态三处已按 Rust 语义处理:WndProc 在 `_state` 赋值前到达(WM_NCCREATE)→ DefWindowProc;
  `App.RequestExit` 在 Application.Start 构造 App 前到达 → Environment.Exit;
  UIA 线程 CoInitializeEx S_FALSE → 视为成功。
- 移植保真关键点(勿"顺手改"):单次 SetLayeredWindowAttributes(COLORKEY only);黑 key 非洋红;
  阴影 0x202020 非纯黑;顶层 WS_POPUP 永不 WS_CHILD(overlay 模式);embed 失败 sticky;
  z-burst 运行中不重排定时器;DrawTextW 空缓冲短路;V4 末行损坏不推进时间戳。
