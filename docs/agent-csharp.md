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
| window.rs | Native/WidgetWindow.cs | 类名/样式/colorkey/PaintSig 去重/墨迹居中/定时器 1,2,3,4,5/z-burst/菜单 ID 全保留;C# 扩展(无 Rust 对应):设备切换交叉淡化(fade_transition,预乘帧 CPU 插值,TimerFade 16ms/300ms smoothstep,仅 overlay ULW 路径,embed colorkey 无动画) |
| taskbar.rs | Native/TaskbarLocator.cs | Win11 判定、右锚 notify.left−w+2、WidgetsButton UIA 30s 缓存、TaskbarDa 门控、embed 模式 |
| hover.rs | Native/HoverPanel.cs | 120ms 轮询 + 350ms dwell、黑 key 圆角面板 |
| tray.rs | Native/TrayIcon.cs | VERSION_4、每秒 NIM_MODIFY 去重、32×32 DIB 2x 软采样 16×16 HICON |
| icons.rs | Native/DeviceIcons.cs | 字形墨迹扫描 + ICON_SIZES 吸附(码点在 Core 共享给 FontIcon) |
| uia_events.rs | Native/UiaEvents.cs + Interop/Uia.cs | 手写 COM interop,IID/vtable 对齐官方 Win32 元数据(与 windows 0.58 crate 同源) |
| watcher.rs | Core/Services/WatcherService.cs | V3/V4 正则逐字保留;V4 camelCase + 显式 null→默认;FileSystemWatcher + 1s 去抖 |
| battery.rs | Core/Models + DeviceSelector + DisplayModeResolver | 选择规则/字形/五段色;显示模式扩展(无 Rust 对应):fixed/drop_swap(电量下降临时替换 30s)/rotate(30s 名称轮播),测试 DisplayModeTests |
| history.rs | Core/Services/HistoryService.cs | 同 schema/WAL;span 切分/instant 兜底逐条移植;预测为 C# 侧扩展(无 Rust 对应):EWMA 周期权重(30d 半衰期/180d 截断)+ 当前会话融合 + 逐级迁移剖面非线性外推(部分会话也计入,缺失档用速率填充)+ 充电速率健康度/寿命估算(History 页)+ ReboundFilter 弛豫回弹剔除(读路径包络,见"已知差异") |
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

## 冒烟验证记录(embed v2,2026-09-08,Win11 26340.9233)

- 覆盖层回归:`embed_into_taskbar=false` 启动 → `pos=(2060,1556) embed=False`,widgets-space 定位正常 ✓
- 嵌入上屏:`embed_into_taskbar=true`(+widgets_space)重启 → band 兄弟第 0 位
  `parent=Shell_TrayWnd`,**内容可见**且画质与覆盖层逐像素一致(ULW+重建 poke;
  首版 GDI+colorkey 因 AA 灰边/阴影在浅色任务栏上形成暗色毛边被弃用) ✓
- 实时重绘:启动时 device=none 画 `--`,HID 轮询发现设备后**不重建窗口**直接重绘
  (poke 使新帧上屏);预计时间 13h54m→13h52m 自动刷新 ✓
- explorer 重启:band 子窗口随任务栏销毁 → 锚窗口经 `TaskbarCreated` 重建重嵌,
  重新出现在兄弟第 0 位并正常绘制 ✓
- 优雅退出(WM_CLOSE→`ExitWidget`):隐身末帧(ULW 零帧+poke)→ 进程退出 →
  **带内零残影** ✓
- 设置页:新增"嵌入任务栏"开关(`SwitchEmbedTaskbar`);覆盖层↔嵌入的实时切换走
  同一 `RecreateWindow` 路径(启动/重建已验证;开关触发的切换未单独自动化验证)

## 已知差异 / 注意

- **墨迹居中/测量走渲染实况(2026-09-09,用户实报"换字形后竖向居中不可用")**:图标字体的
  声明度量(GGO_METRICS)与实际光栅不符——E850 电池字形声明 8px、实际渲染 10px@20px
  (旧 EBA0 同样 12 vs 14),且图标字体行盒为纯 ascent(desc=0);行高收紧(窗口贴墨迹)后
  GGO 居中的 1~2px 偏差变得可见。`GdiText.InkCenterDelta`/`InkHeight` 改为把文本用调用方
  DC 的字体渲染进内存 DIB 后扫描实际墨迹行(单字符按 (字号,字符) 缓存,多字符标签直算,
  GGO 仅作 InkHeight 兜底);`GetCurrentObject/OBJ_FONT` 为本次新增 P/Invoke。类型图标盒高
  `kindH` 也走 `SnapSize` 吸附(微软图标字体推荐字号 16/20/24/32/40/48/64,偏离会模糊;
  96 DPI 下 14→16),电池 20px、状态/预计图标 16px 均已吸附。

- **ReboundFilter(弛豫回弹剔除,2026-09-08 用户实报案例)**:无线设备静置后电芯电压弛豫
  (端电压向 OCV 回升),电压式电量计唤醒时报高读数——实测鼠标 03:32 使用中 61% →
  06:56 静置后 65% → 07:48 恢复使用跌回 61%(BU-903:电压式 SoC 需数小时静置才准)。
  这组"假回升+回落"会把 levelNow 抬高、把回落当真实消耗(~4%/52min 的假速率按
  BlendKPct 混入 `BlendedRate`,预计使用时间被拉短)、并让伪倒计时在回落中途重锚。
  `Core/Services/ReboundFilter.cs`(读路径纯函数,单遍扫描):放电序列维持包络 env,
  1~4 点的未充电回升立即钳到 env;回落到 ≤env 确认为弛豫伪影;抬升**在线持续 ≥2h**
  (`ReboundAcceptSecs`,静置/断连不计时钟)才接受为真实再校准;≥5 点回升(SpikeFilter
  写时已确认的真换电)直通并重置 env;充电样本直通并重置 env(也覆盖拔出后先跌后弹的
  镜像伪影)。接入点:`Record` 的预测+锚点缓存(`_estimates` 存 (Estimate, Anchor),
  锚点在 watcher 线程算好)、`CycleStatsOf`、`HealthStatsOf`、HistoryPage 周期列表。
  **图表与显示百分比保持原始读数**,仅估计用包络。注意:必须在原始序列上恰好调用一次
  (被接受的次阈值回升二次扫描会被重钳,非幂等);测试 `ReboundFilterTests`(15 条用例
  含真实案例基准)。

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


- **HID↔日志身份桥接(同一设备只出现一次)**:dongle 厂商序列号查询未应答时,HID 源以合成句柄
  (`HID:{pid}`/`BLE:{mac}`,`HandleFor`)入库,与 Synapse 日志源的同一物理设备(真实序列号句柄)
  形成两个条目——悬停面板出两行、历史记两条序列(2026-09-08 用户实报"鼠标出现两个":
  Viper V3 HyperSpeed 同时有 `31000D44` 与 `HID:0088` 两行)。修复:`HidWatcher.ResolveIdentity`
  (纯函数,有单测)把合成句柄读数并入同名真实序列号条目(HID 电量值优先,对应
  battery_source=auto 的直读偏好)并删除陈旧合成条目;序列号正常应答或 Synapse 未运行时行为不变。
  接受的边界:同型号两台设备且两台都解析不出序列号时会并成一行(序列号解析是常态,合成句柄是例外)。
- **`embed_into_widgets_space`(C# 扩展,嵌入小组件按钮内部,设置页开关"嵌入小组件空余空间")**:
  26340 实测 `WidgetsButton`
  (UIA 矩形 2007-2159,紧邻 TrayNotifyWnd)内部天气文字(最右 Text 子元素右缘 x≈2075)到按钮右缘
  之间有一段 OS 留空的内部区域(用户截图红框)。widgets 模式把挂件**居中**在该空位上:
  UIA 查询除按钮矩形外还做按钮子树的有界 DFS(`ScanInnerTextRight`,ControlType==Text,
  深度≤3/每层兄弟≤12)取天气文字右缘,`x = (textRight + boardRight)/2 - w/2`,
  精确居中,无额外偏置(旧版曾向按钮右缘偏 `clamp((w-freeWidth)/4, 0, 16)` 防墨迹压住
  "多云"文字;窗口贴墨迹后用不上,且复查确认当年悬出源于任务栏侧布局而非墨迹宽度,
  2026-09-09 移除);文字扫描失败回退
  按钮整体居中,板矩形缺失(TaskbarDa=0/UIA miss)回退右锚。挂件仍是 TOPMOST 覆盖层,
  不改 Windows 小组件本身。开关关闭时回到普通左/右锚点(小组件板恢复为禁区)。曾短暂以
  `widget_side=widgets` 形式存在,Load() 时自动归一为本开关。板信息与避让共用同一 30s 缓存
  (`WidgetsBoardInfo`,含 TextRight;`WidgetsButtonRect` 变为其 Rect 投影),`TaskbarCreated`
  失效路径不变。**点击盾(2026-09-09,用户要求)**:该模式下挂件整块拦截鼠标,点击挂件区域
  不再触发小组件面板——双闸缺一不可:`WM_NCHITTEST` 回 `DefWindowProc`(HTCLIENT,后续
  鼠标消息由 wndproc 吞掉),同时 `Premultiply`/`AlphaPresent` 把空像素 alpha 从 0 提到 1
  (ULW 命中测试逐像素,alpha=0 会无视 NCHITTEST 直穿到按钮;alpha=1 预乘后是 0.4% 黑,
  视觉不可见)。盾状态翻转经 `Paint` 的 `ClickShield` 跟踪强制重绘(1s tick 内生效);
  ghost 帧(`SilentFrame`)带 `hitFloor:false` 保持 alpha=0(隐身且穿透);关闭开关即恢复穿透。


- C# 版空闲内存约 100MB+ 量级(Rust 约 30MB):.NET 运行时 + WinUI 投影程序集;
  窗口惰性创建使 XAML 在首次打开前不加载。
- 启动竞态三处已按 Rust 语义处理:WndProc 在 `_state` 赋值前到达(WM_NCCREATE)→ DefWindowProc;
  `App.RequestExit` 在 Application.Start 构造 App 前到达 → Environment.Exit;
  UIA 线程 CoInitializeEx S_FALSE → 视为成功。
- 移植保真关键点(勿"顺手改"):任何模式都**不**调用 SLWA COLORKEY(黑 key 有 AA 暗边,
  用户实报"字体劣化";嵌入与覆盖层共用 ULW 管线,嵌入靠 `AlphaPresent` 末尾的
  `PokeBandRebuild` 出帧);阴影 0x202020 非纯黑;覆盖层保持 WS_POPUP、嵌入保持
  出生即 WS_CHILD——模式切换走 `RecreateWindow` 销毁重建,**永不** SetParent+样式翻转
  (带忽略迁移窗口并冻结 ULW 帧,docs/agent-embed.md);embed 失败 sticky;
  z-burst 运行中不重排定时器;DrawTextW 空缓冲短路;V4 末行损坏不推进时间戳。
