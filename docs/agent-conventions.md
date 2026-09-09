# 编码规范与检查

适用：改动 `src/` 前后自查；审查 C# + Win32 interop 用法。

## 基本约定

- `Directory.Build.props`：`LangVersion=latest`、`Nullable=enable`、`ImplicitUsings=enable`；
  file-scoped namespace（`namespace X;`）、4 空格缩进。
- 命名：类型/方法/属性 PascalCase，私有字段 `_camelCase`；P/Invoke 入口保持 Win32 原名；配置键保持
  settings.json 既有 snake_case。
- 文件头 `//` 注释说明职责与移植来源（TrafficMonitor / TS 上游 / OpenRazer 移植需注明）。
- 错误处理：缺日志/缺窗口/解析失败一律静默返回 + 诊断点 `Log`；消息循环与原生回调内（WndProc、ULW、
  UIA/WinRT 回调、托盘回调）禁止未捕获异常。
- 新增配置字段必须在 `ConfigService` 带默认值，保证旧 settings.json 可加载。
- 测试在 `tests/RazerTaskbar.Tests`（xUnit）；禁止新增 PNG/图片资源（原生绘制是硬性约束；
  `assets/app.ico` 由 `assets/make_icon.py` 生成，属唯一既有资产）。

## Interop（易错）

- P/Invoke 声明集中在 `Native/Interop/Win32.cs`、`Native/Interop/Uia.cs`、`Core/Interop/HidApi.cs`，
  不在业务文件里散落 `[DllImport]`；`SetLastError=true` + 失败时 `Log` 错误码。
- 手写 COM interop（UIA）的 IID/vtable 必须对齐官方 Win32 元数据；互操作 struct 布局按 Windows 原生
  定义（`GetCurrentBoundingRectangle` 返回 RECT = 4×int32，勿声明成 double——曾致 boardLeft 恒假 0、
  避让整体失效）。
- `CoInitializeEx` 返回 `S_OK/S_FALSE`（0/1）都是成功（.NET 线程模型可能已初始化 COM），按 `< 0` 判失败。
- `unsafe` 仅用于指针/ULW 帧操作（csproj `AllowUnsafeBlocks` 已开）；GDI 对象（Brush/Pen/Font/DC）必须
  配对 `DeleteObject`/`DeleteDC`/`ReleaseDC`/`EndPaint`，`SelectObject` 恢复旧对象，句柄判零后再用。
- 透明管线：只用 ULW 预乘帧（嵌入靠 `AlphaPresent` 末尾 poke 带重建出帧）；任何模式**不**调
  `SetLayeredWindowAttributes` COLORKEY（黑 key 有 AA 暗边）；阴影 `0x202020` 非纯黑（纯黑被抠掉）。

## 配置与序列化

- V4 日志模型 camelCase 命名是 load-bearing 的；每个字段容忍缺失**与显式 null**（Synapse 会发显式
  null，缺失键默认值只覆盖缺失场景），一个 null 字段不得中断整个快照。
- 充电判断保持 `chargingStatus == "Charging"` 严格相等，不做大小写/包含匹配（`NoCharge_BatteryFull`
  是默认非充电态，61% 未插线也报它）。
- 时间间隔一律毫秒域并钳制下限（曾有秒值被当毫秒用的 ~3000 倍空转事故）；防手编 settings.json 的
  ulong→long 回绕。

## 检查清单

```powershell
dotnet test tests/RazerTaskbar.Tests/RazerTaskbar.Tests.csproj
dotnet build src/RazerTaskbar/RazerTaskbar.csproj -c Release -p:Platform=x64
```

- 提交前至少跑通 `dotnet test`；构建新告警需修复或说明理由。
- 部署验证看 dist/razer-taskbar.dll 时间戳（构建坑见 `docs/agent-build.md`）。
- 项目词典：`.vscode/settings.json` 的 `cSpell.words`（systray、Blackshark 等），新专有名词同步追加。
- `.gitignore` 已覆盖 `dist/`、`bin/`/`obj/`：调试日志、dump/截图产物不入库。
