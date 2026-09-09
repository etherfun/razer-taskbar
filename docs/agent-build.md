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
