# C# + WinUI3 实现总览(分支 `refactor/csharp-winui3`,仓库根目录)

Rust 版的全量 C# 实现(本分支唯一实现,不再是实验):挂件/托盘/悬停/日志监听/UIA 用 C# P/Invoke 重写
(观感像素级对齐),电量历史 + 设置页合并为一个 WinUI3 NavigationView 窗口。
**Rust 版已从本分支移除**;共享同一份 `%APPDATA%\razer-taskbar\settings.json` 与 `battery.db`
(schema 兼容,不可同时运行)。

## 构建与测试

命令、部署坑（停进程/平台标志/dist 树/dll 时间戳）、测试覆盖面与**独立诊断探针**（`--hid-probe`/
`--hid-scan`/`--ble-vendor` 及 STA 饿死 WinRT 事件泵踩坑）统一维护在 **`docs/agent-build.md`**，
此处不再重复。

## 运行时模型(对应要求)

- **不打包 WinUI3 依赖**:csproj 用 `WindowsPackageType=None` + `WindowsAppSDKSelfContained=false`
  + `SelfContained=false`,依赖系统安装的 Windows App SDK Runtime(框架依赖、unpackaged)。
  csproj 直接引用 WinUI/Foundation/Base/Runtime 子包而非整合包,避开 AI/ML/Widgets 负载。
- **Bootstrap 降级**:DISABLE_XAML_GENERATED_MAIN + 自定义 Main 自己调 `Bootstrap.TryInitialize`
  (Options.None 静默失败)。运行时缺失 → 挂件-only 降级,托盘菜单中 Settings/History 置灰,
  不退出进程(整合包自动初始化是 Environment.Exit,不可接受,故必须自定义 Main)。
- **跟随系统亮/暗**:不强制 RequestedTheme;图表配色走 ActualTheme + ThemeDictionaries。

## 线程模型(对应 Rust 三线程 + UIA 线程)

每个线程都由一个"功能"（`Features/`）持有，进程本身归核心宿主（`Host/AppHost.cs`）——见
`docs/agent-architecture.md`：**进程归核心、表面归功能**，关掉设置/历史窗口不再结束应用。

| 线程 | 职责 |
|---|---|
| 主线程 (STA) | WinUI3 `Application.Start`;保活窗口(`Host/KeepAliveWindow.cs`)锚住循环;控制面板窗口按需惰性创建、关闭即销毁 |
| razer-widget (STA) | 挂件覆盖层 + 悬停面板 + 托盘 + 菜单 + 4 定时器 + GetMessage 循环 |
| razer-watcher | 日志解析 + 历史采样(设置每轮重读,即时生效契约不变) |
| razer-uia-events (MTA) | UIA 结构变化监听,回调仅 PostMessage(WM_APP+2);TaskbarCreated 重绑 + 30s 自检 |

跨线程:UI → 挂件线程用 `WidgetThread.Post`(WM_APP+3 + GCHandle 闭包);挂件 → UI 用
`Host/UiThread.Post`(已在 UI 线程时内联执行)。配置权威副本在 `AppState`(挂件线程写 + 落盘;UI 经 Post 修改)。

## 模块映射

| Rust | C# | 说明 |
|---|---|---|
| main.rs | Host/AppHost.cs + Program.cs + App.xaml.cs | 单实例 FindWindow + EnumChildWindows 兜底;Bootstrap 降级;功能挂载与退出策略(2026-09-21 拆分,见 agent-architecture.md) |
| (新增) | Host/IAppFeature.cs + FeatureRegistry.cs + UiThread.cs + KeepAliveWindow.cs | 功能契约/挂载表/UI 线程投递/保活窗口 |
| (新增) | Features/Widget、Features/Data、Features/ControlPanel | 三个功能:挂件线程、数据采集线程、设置/历史窗口(各自持有生命周期) |
| window.rs | Features/Widget/WidgetFeature.cs → Native/WidgetWindow.cs | 类名/样式/colorkey/PaintSig 去重/墨迹居中/定时器 1,2,3,4,5/z-burst/菜单 ID 全保留;C# 扩展(无 Rust 对应):设备切换交叉淡化(fade_transition,预乘帧 CPU 插值,TimerFade 16ms/300ms smoothstep,仅 overlay ULW 路径,embed colorkey 无动画)、show_widget 门控(2026-09-09:`SetWidgetEnabled` 只销毁/重建显示窗口,锚窗口/定时器/UIA/托盘常驻,`TrayHostHwnd()` 在挂件关闭时让锚窗口承载托盘回调,`PlaceWidgetCore` 对 !WidgetOn 短路防 1s 轮询/UIA 复活窗口) |
| taskbar.rs | Native/TaskbarLocator.cs | Win11 判定、右锚 notify.left−w+2、WidgetsButton UIA 30s 缓存、TaskbarDa 门控、embed 模式 |
| hover.rs | Native/HoverPanel.cs | 120ms 轮询 + 350ms dwell、黑 key 圆角面板 |
| tray.rs | Native/TrayIcon.cs | legacy 回调(2026-09-09 移除 VERSION_4:V4 抑制标准 szTip tooltip 需 NIF_SHOWTIP,且回调改派 WM_CONTEXTMENU/NIN_SELECT,与 WM_TRAY 分发的 WM_RBUTTONUP/WM_LBUTTONDBLCLK 永不匹配——tooltip 与托盘右键/双击一并失效)、每秒 NIM_MODIFY 去重、32×32 DIB 2x 软采样 16×16 HICON |
| icons.rs | Native/DeviceIcons.cs | 字形常量 + D2d 墨迹宽度 + ICON_SIZES 吸附(码点在 Core 共享给 FontIcon) |
| (新增) | Native/D2d.cs | 共享 D2D/DWrite 上下文：工厂/字体回退探测/格式缓存/墨迹扫描(渲染像素为准) |
| uia_events.rs | Native/UiaEvents.cs + Interop/Uia.cs | 手写 COM interop,IID/vtable 对齐官方 Win32 元数据(与 windows 0.58 crate 同源) |
| watcher.rs | Core/Services/WatcherService.cs | V3/V4 正则逐字保留;V4 camelCase + 显式 null→默认;FileSystemWatcher + 1s 去抖;V4 首读走尾部(agent-watcher.md);按设备的日志兜底(agent-hid.md);V3 解析单测 WatcherV3Tests |
| battery.rs | Core/Models + DeviceSelector + DisplayModeResolver | 选择规则/字形/五段色;链路来源 `BatteryTransport`(Wired/Receiver/Ble/Log)+优先级(见 agent-hid.md);显示模式扩展(无 Rust 对应):fixed/drop_swap(电量下降临时替换 30s)/rotate(30s 名称轮播),测试 DisplayModeTests |
| (新增) | Core/Models/BatteryType.cs | 电池类型(内置充电/可更换 AA-AAA):按 openrazer `charge_status` 的 "Use AA batteries" 名单**按型号**判定,可由 `device_battery_types` 覆盖;历史页设备选择旁设置,可更换电池的序列清充电旗(见 agent-history.md) |
| history.rs | Core/Services/HistoryService.cs | 同 schema/WAL;span 切分/instant 兜底逐条移植;预测为 C# 侧扩展(无 Rust 对应):EWMA 周期权重(30d 半衰期/180d 截断)+ 当前会话融合 + 逐级迁移剖面非线性外推(部分会话也计入,缺失档用速率填充)+ 充电速率健康度/寿命估算(History 页)+ ReboundFilter 弛豫回弹剔除(读路径包络,见"已知差异") |
| config.rs | Core/Services/ConfigService.cs | 同一路径/字段/默认值;Run 键自启 |
| i18n.rs | Core/Services/I18n.cs | 英文 key→zh 表 + LanguageChanged 事件热切换 |
| viewer.rs | Features/ControlPanel/ControlPanelWindow + HistoryPage + BatteryChart | NavigationView 合并窗口;图表 = WinUI Shapes(网格/色带/面积/分段折线/换电点/5 刻度);窗口关闭即销毁、下次打开重建 |
| settings.rs | Features/ControlPanel/SettingsPage | Win11 设置规范行布局;即时生效经 Post 回投挂件线程 |

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

## 冒烟验证记录(show_widget + 托盘 tooltip,2026-09-09,Win11 26340)

- **托盘 tooltip 修复**:移除 `EnsureCreated` 的 `NIM_SETVERSION`/`NOTIFYICON_VERSION_4`
  (V4 抑制标准 szTip tooltip 需 NIF_SHOWTIP,且回调改派 WM_CONTEXTMENU/NIN_SELECT,
  与 `WM_TRAY` 分发的 WM_RBUTTONUP/WM_LBUTTONDBLCLK 永不匹配——tooltip 与托盘
  右键/双击一并失效,Rust 版同样带病)。恢复 legacy 后 tooltip/右键菜单/双击历史
  共用一条已验证路径;悬停人工复核。
- **show_widget=false 启动**:停进程 → settings.json 置 false → 启动 → 日志只有
  bootstrap/watcher,**无 first paint / placement**(显示窗口未创建),进程稳定 ✓;
  恢复 true 重启 → first paint/placement 回归 ✓(配置键随后持久化)。
- **WriteBlend 尺寸竞态修复**(启动实报,非本次功能引入):设备在布局收敛期
  (w=41→70)连上时 fade 已武装,表面 resize 后 `FadeTick` 用旧尺寸快照越界
  (WndProc 保护拦住,6 条 ERROR/次)。`FadeTick` 增加快照长度守卫后,同场景
  (首绘 none→设备连上→两次 resize)连续两轮启动零 ERROR ✓。
- 设置页运行时开关(SwitchWidget)与悬停 tooltip 为人工复核项。

## 已知差异 / 注意

- **移植补漏(2026-09-09 全项目审查)**:① watcher 轮询间隔把秒值直接当毫秒
  用(Rust `Duration::from_secs` 的 ×1000 在移植时丢失),实际 15ms/5ms 空转
  约 3000 倍——已修正并在 ulong 域钳制,防手编 settings.json 的
  ulong→long 回绕把间隔塌到 2ms 忙循环;② ReboundFilter 三个公共入口
  (Predict/CycleStatsOf/HealthStatsOf)统一收**原始**序列,Predict 拆出已
  Deflate 内核 `PredictDeflated` 供 Record 复用,消除"Predict 收已
  Deflate / 其余收原始"的相反契约(双重 Deflate 会重钳已接受的再校准);
  ③ RecordBatteryHistory 判定收敛到 `HistoryService.Record`(Tick 无条件
  调用、一次读盘),修复关闭记录后挂件/托盘/悬停残留冻结预测的 bug;
  ④ TrayIcon.BuildIcon 补齐 GDI 判零(全库唯一不设防点)与选中态
  DeleteObject 泄漏,顺修 hdc 提前 ReleaseDC 的 use-after-release;
  ⑤ 字体回退改 `GetTextFaceW` 验证式(当时在 `GdiText.FaceResolved`,2026-09 迁到 `D2d.ProbeFace`;DeviceIcons/
  HoverPanel 的 `font == 0` 是永假分支,Win10 上字形会落 SimSun 替换);
  ⑥ Native 层 24 处诊断日志从 Console.Error 双轨统一进 Log(WinExe 下
  stderr 无去处,Log 落文件且镜像 stderr);⑦ I18n 表 switch 转 Dictionary
  并新增 I18nTests 调用点↔映射表双向对照;⑧ WatcherService.Record 空
  catch 删除、BLE 序列号失败不再永久缓存、HistoryService 断连样本不再用
  0% 兜底伪造。

- **墨迹居中/测量走渲染实况(2026-09-09,用户实报"换字形后竖向居中不可用")**:图标字体的
  声明度量(GGO_METRICS)与实际光栅不符——E850 电池字形声明 8px、实际渲染 10px@20px
  (旧 EBA0 同样 12 vs 14),且图标字体行盒为纯 ascent(desc=0);行高收紧(窗口贴墨迹)后
  GGO 居中的 1~2px 偏差变得可见。墨迹测量改为"渲染后扫像素"(当时在 `GdiText`:调用方 DC 的
  字体渲染进内存 DIB,单字符按 (字号,字符) 缓存、多字符标签直算,GGO 仅作 InkHeight 兜底;
  2026-09 随 D2D 迁移搬到 `D2d.InkCenterDelta`/`InkHeight`,改经 D2D 渲染进 scratch 预乘 DIB
  扫 alpha,缓存键 (格式,文本));`GetCurrentObject/OBJ_FONT` 为当时新增的 P/Invoke。类型图标盒高
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
  (被接受的次阈值回升二次扫描会被重钳,非幂等);测试 `ReboundFilterTests`(11 条用例
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
  深度≤3/每层兄弟≤12)取天气文字右缘,`x = (textRight + boardRight)/2 - w/2 + bias`,
  居中 + 自适应右偏 `clamp((w-freeWidth)/4, 0, 16)`:墨迹放得下时 bias=0 精确居中;
  预计时间较长(如 "156h59m")时墨迹超过空位宽度,向按钮右缘偏移(留白/箭头侧容忍透明
  悬出,"多云"文字侧不容许遮挡;2026-09-09 恢复——同日早些时候曾因窗口贴墨迹后空位
  通常够宽而移除,但长时间文本会让墨迹重新超出空位);文字扫描失败回退
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
  `AppHost.RequestExit` 在 Application.Start 构造 App 前到达 → Environment.Exit;
  UIA 线程 CoInitializeEx S_FALSE → 视为成功。
- 移植保真关键点(勿"顺手改"):任何模式都**不**调用 SLWA COLORKEY(黑 key 有 AA 暗边,
  用户实报"字体劣化";嵌入与覆盖层共用 ULW 管线,嵌入靠 `AlphaPresent` 末尾的
  `PokeBandRebuild` 出帧);阴影 0x202020 非纯黑;覆盖层保持 WS_POPUP、嵌入保持
  出生即 WS_CHILD——模式切换走 `RecreateWindow` 销毁重建,**永不** SetParent+样式翻转
  (带忽略迁移窗口并冻结 ULW 帧,docs/agent-embed.md);embed 失败 sticky;
  z-burst 运行中不重排定时器;DrawTextW 空缓冲短路;V4 末行损坏不推进时间戳。

## 冒烟验证记录(性能/正确性迭代,2026-09-11,Win11 26340)

无人值守自我迭代 8 个提交(ea42a93..bf23435),构建 0 警告 0 错误,单测 156/156:

- **Core**:HistoryService 单连接全 SQL 收敛 `DbLock`(原 Record 写与 SamplesInRange 读跨线程
  裸并发,Microsoft.Data.Sqlite 一连接一命令);估计缓存增量重算(原每轮全量清空重算阻塞
  EstimateFor);ConfigService 原子写;Log 5MiB 轮转。
- **Native**:TrayIcon HICON 按 (level,charging) 缓存(原 tooltip 每分钟一换、每天漏 ~1400 句柄);
  UIA 遍历 null 子元素判空(InputSite 预算不再耗在异常上);TaskbarDa 注册表读 5s TTL;
  AppState 配置快照改"变更时发布、读取免锁"(原 WM_NCHITTEST 每条鼠标消息 Clone 26 字段)。
- **Watcher**:每轮一次配置读贯穿传递;HidWatcher 的 MergeAlias(SQLite)移出 DeviceStore 锁;
  V4 日志改增量尾读(~5MiB 文件原每 5s 整读+全正则),字节级残行缓冲,轮转复位,
  断连规则复现全文件重放净效果。
- **UI**:HistoryPage 取数/统计后台化(代次守卫),一次 Deflate+ComputeSpans 喂统计/健康/列表
  (原 UI 线程 3 次全量重算);BatteryChart 色带合并相邻同类区间、轴查找二分、折线/面积
  亚像素抽稀;HoverPanel 字体按高度缓存+行集不变跳过重测。
- 实机:重启后 bootstrap/首绘/UIA 注册/放置/V4 解析(devices=2, connected=1)全部正常,
  日志零 ERROR,进程稳定(~150MB)。

## 冒烟验证记录(数据获取迭代,2026-09-11,Win11 26340)

实机键盘 Joro 已切 USB(PID 0x02CD),鼠标 Viper V3 HyperSpeed 2.4G(0x00B8)。日志 4h 统计
暴露三处采集质量问题并修复(提交见 git,单测 160/160):

- **判离线从未生效(真 bug)**:`Commit` 尾部 `_owned = seen` 在首轮空读后清空归属集合,
  miss 计数永远到不了 2——深睡/关机设备一直显示最后电量"在线"。修复:归属集合并入 seen
  持久累积;同时加 30s 墙钟静默下限(wake 风暴 2-4 轮 miss 不再闪烁离线)与"最后写入者
  拥有连接权"守卫(auto 回退日志源重写的条目,HID miss 循环按引用比对跳过,不越权翻转)。
  离线/上线转换各记一条 INFO。
- **reopen 风暴**:4h 3964 次 `no answer → reopening`,其中 API 级失败仅 2 次——99.9% 是对
  深睡设备的无意义重开(重开有"空闲后间歇失败"风险)。修复:重开须在连续空轮内观察到
  交换故障证据(setFeature/getFeature 失败、BadEcho/BadCrc/BadLength,6 轮触发);
  纯静默深睡只保留 120 轮深保底重开。日志带 pid。
- **0x05 刷屏**:键盘离开 dongle 后空键盘槽偶发以 status=0x05 应答电量查询(4h 2685 条)。
  修复:按 (pid, slot) 10 分钟限频,消息带 pid+tx 便于定位;`QueryCommand` 返回
  (value, unsupported) 区分"不支持"与"无应答"。
- 其他:hid poll 签名行附 `(charging)`,可从日志直接验证充电位(Joro USB 线充实测 99% 在充)。

## 冒烟验证记录(蓝牙模式,2026-09-11,Win11 26340)

用户把键盘 Joro 切到蓝牙、鼠标睡眠阈值改 1 分钟后实机复测(单测 162/162):

- **睡眠→离线→唤醒循环**(b9bb238 判离线逻辑):末次应答后 35s 判离线(2 轮 miss + 30s
  墙钟),唤醒单轮回连、电量无跳变;单轮 wake 抖动被墙钟下限吸收;battery.db 样本实测
  connected 1→0→1 翻转与日志时刻一致。长睡期每 ~4 分钟一次"6 轮故障证据"重开(旧逻辑
  42s 一次)。
- **USB→蓝牙热切换无缝**:拔线到 BLE 读数接上 ~14s,同串号条目先离线再复联,历史无离线
  样本;身份升级 ~1 分钟滞后(心跳节奏),升级后名称从厂商通道原始名("Joro")变为规范名。
- **发现并修复 BLE:{mac} 假身份残留**:身份升级轮不回收旧 MAC 条目(原只回收 HID:{pid}
  形态,且首尾名字不同连启动自愈的精确同名配对也失败)。修复:Commit 实时折叠同 pid +
  名字相等/互相包含的回退行(`_fallbackPid` + `NamesMatch`,见 agent-hid.md 蓝牙节);
  存量 DB 分裂序列已手术合并(2 样本 + devices 行)。部署后首轮即以规范名出现,零回退根。
- **发现并修复组合接收器跨子设备串台(2026-09-18)**:`HID:{pid}` 只命名得了一个子设备,
  Viper V3 HS 接收器(0x00B8)的键盘槽(Joro,80%)与鼠标槽(36%)共用它 → 同一条历史序列;
  任一侧解析出串号时,`Commit` 的退休折叠(只判 `map.ContainsKey(rooted)`)把另一侧的行整批
  `MergeAlias` 进该序列——直接 INSERT 绕过 SpikeFilter,当日 8 次合并 → 库里 8 组 36%↔80%
  ±44 尖峰(启动 scrub 认这个形状可清,但一次遍历漏掉每组平台最后一行——重锚定行自身仍是
  80%;已改为跑不动点,见 agent-history.md 第 2 点)。修复:第二子设备(槽位角色
  ≠ pid 首槽角色)合成身份带键 `HID:{pid}:K`(`RazerPidTable.SecondaryKey`),`HandleFor`/
  `DedupRound`/退休折叠/孤儿折叠统一按键判定;第二个子设备也不再继承共享的接口串号。
  同批副作用(顺带修掉):键盘槽不再因同 pid 有串号应答而在当轮被丢弃——此前它每轮被判缺席,
  在 hover/挂件里闪断。回归测试 `DeviceIdentityTests.SerialResolutionNeverRetiresTheSiblingSlotsFallback`。
- **发现并修复更新闪帧(2026-09-19)**:ETA 文本宽度每分钟变化 → `PaintBody` 量出新自然尺寸后
  按旧矩形先呈现一帧,1s 轮询才 `SetWindowPos` 到新矩形——旧尺寸表面被拉伸进新矩形,而嵌入态
  每次呈现都 poke 带重建,重建恰好落在"已改几何、未上新帧"的窗口期时把拉伸帧冻结上屏(用户
  报告"更新时闪一下")。修复:`Paint` 内量得尺寸变化就地按新尺寸重渲染 → `PlaceWidgetCore`
  立即应用几何 → `AlphaPresent`,DWM 只可能合成最终帧;启动不再有 144→42→71→89 的多级 churn
  (首帧直接落最终尺寸)。红线已记入 AGENTS.md"尺寸与帧同一次 Paint 提交"。
- **内存节奏：托管堆看门狗（2026-09-21）**：空闲挂件的私有字节会**持续上涨**（实测 +0.9 MB/分钟，
  45 分钟从 155 MB 涨到 243 MB）。归因：`GC.GetTotalMemory(false)` 同步上涨而 `gc0=0`——十分钟
  **一次 GC 都没发生**。workstation GC 的预算按物理内存推算，本机远大于应用每秒几 KB 的瞬时分配
  （每轮重读 settings.json、V4 日志目录枚举 152 个文件 ×2、30s 一次 UIA 刷新、每分钟一次 ETA 文本
  变化），于是预算永远够不到，垃圾只堆不收。**强制一次回收把堆从 8 MB 打到 2 MB 存活**，且此后私有
  字节在窗口内走平——证明是"垃圾未回收"而非"被持有"。
  修复：① `System.GC.ConserveMemory=5`（csproj → runtimeconfig）；② `WidgetWindow.MemWatchdog`
  ——1s 定时器里，堆超过 `HeapCeilingBytes`(8 MiB，存活集 1-2 MiB) 就 `GC.Collect(2, Optimized,
  blocking:false, compacting:false)`（后台回收、不压缩、无 UI 停顿），并有 `WatchdogMinIntervalMs`
  (60s) 下限，避免存活集高于阈值时变成回收循环。用**上限而非硬限**（`GCHeapHardLimit`）：历史页
  加载 "All" 范围会瞬时分配几十 MB，硬限会 OOM。
  实测：空闲私有字节 93.4 → 92.5 MB 走平（此前 +0.9 MB/分钟）；主窗口打开后稳定在 ~183 MB。
  同批小优化：`D2d.ScanInkUncached` 改为**就地扫描 DIB**（原先每次未命中 `Marshal.Copy` 一个
  294,912 B 的托管数组，超过 LOH 阈值 → 每分钟一次大对象分配）；`InkCache` 加上界
  （键含文本，ETA 字符串每分钟变一次，原为无界增长）。
  遗留（低影响，未修）：进程**内核句柄**以 +12.5/分钟缓慢漂移（GDI/USER 对象是平的 20/35，说明不是
  GDI 泄漏；形态为锯齿——HID 轮询阶段 +5 后又释放，净值上漂）。18k/天远低于进程上限，内核池约
  2-4 MB/天，不是 143 MB 的成因。定位受限于：进程级 `HandleCount` 无法区分线程，且本机无
  handle.exe/ProcMon/`dotnet-counters`（安装需联网）。
- **设置/历史窗口的内存（2026-09-21 实测，同日完成结构重构）**：空闲 111 MB / 1320 句柄 → 首次打开
  `185 MB / 1880` → 关闭后 177 MB（销毁归还 ~9 MB）。那笔 ~70 MB 是 **WinUI 框架一次性预热 + 页面树**，
  进程内无法归还（强制阻塞 GC 只多回收 ~6 MB），旧文档"留个保活窗口就能把 98 MB 还回去"的假设不成立。
  重构的部分：窗口由 `Features/ControlPanel/ControlPanelFeature` 持有、`Host/KeepAliveWindow`（永不显示的
  XAML 窗口）锚住 dispatcher，因此**销毁窗口不再结束进程**（旧注释所述限制解除，`Destroy()` + 自测开关覆盖）。
  但默认关闭行为是**隐藏**而非销毁：逐层实测表明反复创建/销毁 XAML 树会按树规模泄漏原生内存与句柄
  （裸窗口不漏；+空 Grid 基本不漏；+NavigationView 漏 +19 句柄/轮；+HistoryPage 漏 +77 句柄/轮；
  去掉 acrylic 同样漏），换 `GC.Collect(Forced)` 后托管堆每轮回到 1.9 MB、原生侧仍留 ~6 MB/轮。
  隐藏式 12 轮开关私有字节/句柄/GDI/USER 全部走平。数据、红线与验收步骤见 `docs/agent-architecture.md`。
