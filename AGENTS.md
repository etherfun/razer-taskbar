# razer-taskbar — Agent 协作规范

Razer Synapse 日志驱动的 Windows 任务栏电池挂件（Rust，无托盘依赖，无图片资源，GDI 原生绘制）。

- 运行环境：Windows 10 / 11，需 Razer Synapse 3 或 4 在后台运行。
- 构建产物：单个 `target/release/razer-taskbar.exe`，无安装程序。
- 入口：`src/main.rs`（STA COM 初始化 → 加载配置 → 启动 watcher 线程 → 主线程消息循环）。
- 任务栏布局事件驱动（Taskbar-Lyrics 式）：`TaskbarCreated` 广播 + UIA 结构变化事件触发去抖重排，1s 定时器兜底；详见 `docs/agent-taskbar.md`。

## 目录结构

| 路径 | 说明 |
|---|---|
| `src/main.rs` | 入口，单实例检查，线程划分 |
| `src/watcher.rs` | Synapse V3/V4 日志监听与解析 |
| `src/battery.rs` | 设备模型、`pick_device_to_display` 选择规则、`device_kind` 类型判定、颜色/字形 |
| `src/taskbar.rs` | 任务栏发现、Win10/Win11 定位、共存避让 |
| `src/window.rs` | 覆盖层窗口、GDI 绘制、菜单、事件驱动布局（TaskbarCreated + 去抖） |
| `src/uia_events.rs` | UIA 结构变化监听线程（任务栏布局变化即时重排） |
| `src/hover.rs` | 悬停设备列表面板（光标轮询，非交互只读） |
| `src/icons.rs` | GDI 矢量设备类型图标（鼠标/耳机/键盘/其他） |
| `src/tray.rs` | 托盘兜底图标（菜单入口，explorer 重启后重挂） |
| `src/i18n.rs` | 极简国际化：英文源串即 key，`tr()` 映射 zh；语言 auto（跟随系统）/en/zh，设置页/菜单热切换 |
| `src/history.rs` | 电量历史：SQLite 采样（`battery.db`）、充放电周期切分（换电跳变/关机排除）、加权预测（剩余可用/距充满） |
| `src/viewer.rs` | "Battery history…" 查看窗口（深色 Win11 风格：DWM 深色标题栏/圆角、卡片布局、owner-draw pill/列表、GDI 图表） |
| `src/settings.rs` | "Settings…" 设置窗口（同 viewer 的深色风格）：整合原托盘菜单全部设置项，改动即时生效并落盘；通过 `window.rs` 的 pub 辅助函数（`modify_config`/`set_shown_device`/`reposition_widget` 等）改 UI 线程状态 |
| `src/config.rs` | `%APPDATA%\razer-taskbar\settings.json` 读写、自启动同步 |
| `Cargo.toml` | 依赖（`windows 0.58`、`notify 6`、`serde_json`、`rusqlite(bundled)` 等） |

## 常用命令

```powershell
cargo build --release   # 发布构建
cargo run --release     # 直接运行
cargo test --quiet      # 单元测试（battery / watcher 解析规则）
```

详情见 `docs/agent-build.md`。

## 配置速览

- 路径：`%APPDATA%\razer-taskbar\settings.json`，缺失键由 `serde(default)` 回填。
- 关键字段：`polling_throttle_secs`、`shown_device_handle`、`synapse_version`（auto/v3/v4）、`widget_side`（left/right）、`avoid_overlap_with_widgets`、`show_tray_icon`、`hover_devices`、`window_offset_*`、`taskbar_*_space_win11`、`record_battery_history`（默认 true）、`show_estimated_time`（默认 true，挂件第二行显示预计时间）、`history_poll_interval_secs`（默认 5）、`language`（auto/en/zh）。
- 电量历史库：`%APPDATA%\razer-taskbar\battery.db`（SQLite/WAL，永久保留，表 `samples`/`devices`）。
- 自启动：`HKCU\...\Run\RazerTaskbar`，由菜单切换同步。

## Agent 工作守则

- Windows-only 项目：不引入跨平台抽象，不假设 Linux/macOS 可编译。
- `windows` crate 保持 0.58 API 用法；Win32 调用一律 `unsafe` 包裹，失败路径只记日志不 panic（消息循环内）。
- 日志解析改动必须同步更新 `watcher.rs` 内对应单测；显示选择改动必须覆盖 `battery.rs` 的选择单测。
- 任务栏定位改动先读 `docs/agent-taskbar.md`，不破坏 hold/grace/jump-cap 状态机与 OS 白名单。
- 配置新增字段必须带 `serde(default)` 默认值，保持旧配置文件可加载。

## 分文档索引

- 构建/测试/调试：`docs/agent-build.md`
- 编码规范与检查：`docs/agent-conventions.md`
- 日志解析与设备选择：`docs/agent-watcher.md`
- 任务栏挂载/共存避让/UI 绘制：`docs/agent-taskbar.md`
