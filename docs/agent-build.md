# 构建 / 测试 / 调试

适用：`cargo build`、`cargo test`、本地运行排错。

## 构建

```powershell
cargo build --release   # -> target/release/razer-taskbar.exe（约 1.3 MB，单文件）
cargo run --release     # 直接构建并运行
```

- `Cargo.toml`：edition 2021，`windows 0.58`、`notify 6`、`regex 1`、`serde`/`serde_json`。
- `profile.release`：`opt-level="z"`、`lto=true`、`strip=true`、`panic="abort"`。改动该节需说明体积/崩溃行为影响。
- Windows-only：仅在 Windows 上构建运行，不添加跨平台抽象或 CI 矩阵。

## 测试

```powershell
cargo test --quiet
```

- 单测位置：与源码同文件 `#[cfg(test)]`。
  - `src/battery.rs`：`pick_device_to_display`（非充电优先、selection 尊重）、字形/颜色分段。
  - `src/watcher.rs`：V4 camelCase 反序列化、显式 `null` 回退、`chargingStatus == "Charging"` 严格相等。
- 无集成测试。解析/选择逻辑改动必须同步增补单测，`cargo test` 全绿才算完成。

## 运行与调试

- 无安装程序：拷贝 exe 到任意位置运行；右键菜单可写 `HKCU\...\Run\RazerTaskbar` 实现自启动。
- 单实例：`window::find_existing_instance`（`FindWindowW(RazerTaskbarWidget)`）命中则直接退出并打印 stderr。
- 日志：全部走 `eprintln!`（stderr），终端启动可见：
  - `first paint, devices=N`：绘制链路存活。
  - `yielding to occupant(s): exe (class) [avoids,new]`：正在给第三方挂件让位。
  - `staying despite overlap ... (avoid jump capped at 500px)`：jump-cap 生效。
  - `another instance is already running`：重复启动。
- 配置：`%APPDATA%\razer-taskbar\settings.json`，菜单修改即时落盘；watcher 每次循环重读，无需重启。
- 常见排错：
  - 显示 `--`：先查 Synapse 日志是否存在（见 `docs/agent-watcher.md`），再看 V4 `rename_all`/`null_to_default` 是否被破坏。
  - 位置不对：先读 `docs/agent-taskbar.md`，用 stderr 的 occupant 日志判断是被顶走还是 hold 住。
