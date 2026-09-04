# 编码规范与检查

适用：改动 `src/*.rs` 前后自查；审查 Rust + Win32 用法。

## 基本约定

- edition 2021，全 `snake_case`；模块首行 `//!` 说明职责与来源（TrafficMonitor/TS 移植需注明）。
- 错误处理：缺日志/缺窗口/解析失败一律静默返回（`Option`/`let Ok/else return`），只在诊断点 `eprintln!`；消息循环内禁止 `panic!`/`unwrap`（`devices.lock().unwrap` 除外， poison 即 bug）。
- 新增配置字段必须 `#[serde(default)]` + `Default` 实现，保证旧 `settings.json` 可加载（见 `src/config.rs`）。
- 单测与实现同文件 `#[cfg(test)]`；禁止新增 PNG/资源文件（GDI 原生绘制是硬性约束）。

## Serde（易错）

- V4 结构体必须保留 `#[serde(default, rename_all = "camelCase")]`：日志键是 `serialNumber`/`hasBattery`/`powerStatus` 等，去掉则全员 `has_battery=false`，挂件永久 `--`。
- 每个可空字段保留 `deserialize_with = "null_to_default"`：Synapse 会发显式 `null`，`#[serde(default)]` 只覆盖缺失键。
- 充电判断保持 `charging_status == "Charging"` 严格相等，不做大小写/包含匹配。

## Win32 / GDI

- `windows` crate 锁定 0.58 用法；`w!` 宏、`HWND(std::ptr::null_mut())` 空检查、`GetLastError` 日志。
- 所有 Win32 调用包 `unsafe`；GDI 对象（`CreateSolidBrush`/`CreatePen`/`CreateFontW`、`GetDC`）必须配对 `DeleteObject`/`DeleteDC`/`ReleaseDC`/`EndPaint`，`SelectObject` 恢复旧对象。
- 透明键只用黑色 `LWA_COLORKEY`（`SetLayeredWindowAttributes` 单次调用）：阴影用 `0x202020`，禁用纯黑；第二遍 `LWA_ALPHA` 调用会替换掉 colorkey 模式。
- 全局 `static mut STATE` 访问保持现有 `state_mut()` + `#[allow(static_mut_refs)]` 模式，不引入新的可变静态。

## 检查清单

```powershell
cargo test --quiet
cargo fmt --check
cargo clippy -- -D warnings
```

- 提交前至少跑通 `cargo test`；`fmt`/`clippy` 有告警需修复或在 PR 说明理由。
- 项目词典：`.vscode/settings.json` 的 `cSpell.words`（systray、Blackshark 等），新专有名词同步追加。
- `.gitignore` 已覆盖 `*.log`/`target/`：调试用的 `hook.log`、dump 脚本产物不入库。
