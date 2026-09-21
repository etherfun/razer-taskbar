# 构建 / 测试 / 调试

适用：`dotnet build/publish`、`dotnet test`、诊断探针、本地运行排错。

## 构建

```powershell
# 部署/探针一律用 Release x64（publish 直出仓库根 dist/，运行只认它）：
dotnet build   src/RazerTaskbar/RazerTaskbar.csproj -c Release -p:Platform=x64
dotnet publish src/RazerTaskbar/RazerTaskbar.csproj -c Release -p:Platform=x64 -o dist
# 或用根目录脚本（封装：停进程→清 dist→publish→验证 dll→可选测试→重启）：
powershell -ExecutionPolicy Bypass -File build.ps1 [-Test] [-Run] [-NoRun]
```

- **先停常驻进程再构建**：`razer-taskbar.exe` 运行时锁住 `razer-taskbar.dll`，MSBuild 的复制步骤会
  静默失败——Core.dll 刷新了而 app 产物仍是旧版，改完"没生效"多半是它。
- 普通构建不刷新 win-x64 RID 输出时加 `--no-incremental`。
- exe 是 apphost 壳，判断是否部署成功要看 **razer-taskbar.dll** 的时间戳。
  **但改动只落在 Core 时这个时间戳不动**（MSBuild 判定 app 项目输入未变，不重链）——此时要看
  `dist/RazerTaskbar.Core.dll`（2026-09-21 踩过：Core 已是新版而 `razer-taskbar.dll` 仍是旧时间戳，
  一度以为发布没生效；逻辑全在 Core，Core 新即代码新）。
- **不带 `-p:Platform=x64` 的构建会落到另一棵输出树 `bin/Release/.../win-x64/`**：那里的陈旧副本与
  规范路径互不覆盖，从旧路径手动启动就会跑旧版（2026-09-07 踩过：color-key 时代的 `bin/Release`
  副本被启动，误判为渲染回退）。bin 树只用于构建，运行/自启动一律指向仓库根 `dist/razer-taskbar.exe`
  （publish 直出，`.gitignore` 已忽略 dist/）。
- 运行时模型（unpackaged、框架依赖、Windows App SDK 子包引用、Bootstrap 降级）见 `docs/agent-csharp.md`。

## 测试

```powershell
dotnet test tests/RazerTaskbar.Tests/RazerTaskbar.Tests.csproj   # 勿加 --quiet（MSBuild 参数解析冲突）
```

- `dotnet test` 只重建测试依赖链，不含 app csproj——探针参数（app 侧）改动后必须单独 build app 再跑探针。
- 测试覆盖面：`WatcherV3Tests`/`WatcherV4Tests`（日志解析）、`BatteryTests`/`DisplayModeTests`
  （选择规则/显示模式）、`HidTests`（HID 协议）、`BleVendorTests`、`HistoryTests`/`ReboundFilterTests`/
  `HistoryScrubTests`/`HistoryAliasTests`（历史与防伪过滤）、`DeviceIdentityTests`、`I18nTests`、
  `ExportServiceTests`。解析/选择/协议改动必须同步增补用例，全绿才算完成。

## 诊断探针（不进 UI，单实例守卫之前）

| 命令 | 用途 |
|---|---|
| `razer-taskbar.exe --hid-probe` | HID 全枚举 + 电量/充电查询 + GATT 全 dump |
| `razer-taskbar.exe --hid-scan` | get 半区只读全段扫描（分钟级） |
| `razer-taskbar.exe --ble-vendor` | Razer BLE 厂商 GATT 通道重放（观测查询基线） |
| `… --ble-vendor --sweep` | 厂商通道 page 01/05 × id 0x80-0xFF 只读枚举 |
| `… --ble-vendor --raw=LEN:PAGE:ID:PARAM[:hex]` | 单发命令（LEN≠0=写，需 `--yes-i-know`；set 半区 id 拒绝） |
| `… --ble-vendor --power` | 生产路径 `BleVendor.TryReadPower` 自检（电量/充电/回退） |

- BLE 厂商通道探针要求设备在蓝牙模式；通道被驱动/服务层占用时 `--power` 报
  `characteristics missing` → null（回退路径，属预期）。协议细节见 `docs/agent-hid.md`。
- **STA 线程饿死 WinRT 事件泵**（探针踩坑）：WinRT `ValueChanged` 回调在 STA 主线程同步阻塞时会被泵
  调度延迟数秒，响应帧全部错位。探针核心必须跑在 MTA 线程池
  （`Task.Run(...).GetAwaiter().GetResult()`）；HidWatcher 的轮询线程本就是后台 MTA，无此问题。
- 探针构建/部署遵循上节（Release x64、先停进程）；未知设备先跑 `--hid-probe` 确认 tx/缩放
  （`docs/agent-hid.md`）。

## 内存与磁盘 I/O 测量（无外部工具时的做法）

本机没有 `dotnet-counters`/`dotnet-dump`/`dotnet-gcdump`/`handle.exe`/ProcMon（安装需联网），
所以归因靠进程计数器 + 临时探针。**探针一律测完即删**（改动落到提交里之前先摘掉）。

### 磁盘 I/O

`GetProcessIoCounters` 就是任务管理器"磁盘"列的同源计数器：

```powershell
# 200ms 采样两次差值可抓启动突发；30s 采样看稳态
(Get-CimInstance Win32_Process -Filter "Name='razer-taskbar.exe'").ReadTransferCount
```

- 这是**逻辑**字节：命中文件缓存也算，不等于磁盘物理吞吐。
- 稳态指纹（2026-09-21，2 台设备在接收器上）：**每 5 秒读 766 B（settings.json）+ 写 8240 B
  （`battery.db-wal` 2 个页帧）** ≈ 2 KB/s。写的是 `Record` 每轮**无条件** UPSERT 的 `devices` 行
  （采样点只在变化/15 分钟心跳时才写），不是采样点。
- 启动突发：200 ms 采样可见一次性大读。2026-09-21 修掉的两处 V4 日志整文件读（解析首读 + 串号
  名称收割）合计 4.66 MB → 修后 2.79 MB；剩下的 ~1.9-2.8 MB 是 WindowsAppSDK/XAML 框架加载
  （随文件缓存冷热波动，应用侧压不掉）。用户报的"0.1 MB/s"就是那笔突发摊到任务管理器的 60 秒窗口。

### 内存

```powershell
$p = Get-Process razer-taskbar
$p.WorkingSet64; $p.PrivateMemorySize64; $p.HandleCount; $p.Threads.Count
Add-Type -Name G -Namespace W -MemberDefinition '[DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr h, uint f);'
[W.G]::GetGuiResources($p.Handle, 0)   # GDI 对象；1 = USER 对象
$p.Modules | Sort-Object ModuleMemorySize -Descending | Select-Object -First 12   # 模块归因
```

- **`GetGuiResources` 是区分"GDI/USER 泄漏"与"内核句柄泄漏"的关键**：GDI/USER 平而 `HandleCount`
  在涨 ⇒ 内核句柄（文件/事件/注册表/驱动）。注意 `HandleCount` 是**进程级**的，无法区分线程，
  做阶段归因时会被并发线程的 churn 干扰（2026-09-21 在 HID 轮询阶段量到 +5，但无法排除其他线程）。
- **区分"垃圾未回收"与"真被持有"**：临时在 1s 定时器里打一行 `GC.GetTotalMemory(false)` +
  `GC.CollectionCount(0)`；若 `gc0` 长时间为 0 而堆在涨，再插一次
  `GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true)` +
  `GC.WaitForPendingFinalizers()` 对比前后——堆大幅回落 = 垃圾，不回落 = 真持有。
  2026-09-21 的内存问题就是这样定位的（堆 8 MB → 2 MB 存活，见 `docs/agent-csharp.md` 内存节）。
- 基线（2026-09-21，空闲无窗口）：私有 ~93 MB、WS ~126 MB、线程 ~78、句柄 ~950。大头是框架模块
  （NVIDIA UMD、WindowsAppSDK/XAML、.NET），应用自身数据很小（`_series` 4148 样本 ≈ 97 KB）。
  打开一次设置/历史窗口另加 ~98 MB / ~956 句柄（XAML 页面树），且按设计常驻。

## 运行与调试

- 无安装程序：拷贝 `dist/` 目录到任意位置运行；设置页 *Run at startup* 写 `HKCU\...\Run\RazerTaskbar`。
- 单实例：`Native/SingleInstance.cs`（`FindWindowW` + `EnumChildWindows` 兜底）命中则干净退出。
- 日志：`Core/Services/Log.cs` 落 `%APPDATA%\razer-taskbar\csharp-debug.log` 并镜像 stderr（终端启动可见）：
  - `first paint: device=…`：绘制链路存活。
  - `UIA structure listener registered`：UIA 监听就绪。
  - `overlay kind=… pos=(…) size=… embed=…`：定位结果。
  - 单实例退出提示：重复启动。
- 配置：`%APPDATA%\razer-taskbar\settings.json`，设置页修改即时落盘；watcher 每轮重读，无需重启。
- 常见排错：
  - 改完"没生效"：先查 dist/razer-taskbar.dll 时间戳（构建时进程没停 → 复制静默失败），再确认启动的是
    dist 而非 bin 树旧副本。
  - 显示 `--`：先查 Synapse 日志是否存在（见 `docs/agent-watcher.md`），再看 V4 camelCase/显式 null 回填
    是否被破坏；USB/蓝牙直读问题见 `docs/agent-hid.md`（探针定位）。
  - 位置不对：先读 `docs/agent-taskbar.md`，用日志判断 widgets 板避让/锚定分支。
  - 设置/历史窗口打不开：Windows App SDK Runtime 缺失 → 挂件-only 降级（托盘菜单两项置灰属预期）。
