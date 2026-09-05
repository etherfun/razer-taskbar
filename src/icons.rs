//! GDI vector icons for device kinds (mouse / headset / keyboard / other).
//!
//! Drawn stroke-by-stroke like the tray bolt (no icon font, no PNG assets —
//! same "native GDI only" rule, and no tofu-box risk from missing glyphs).
//! All sub-shape coordinates are fractions of the icon-box height `h`;
//! `width_for` and `draw` share the same metrics so callers can lay out and
//! center before drawing.

use windows::Win32::Foundation::{COLORREF, POINT};
use windows::Win32::Graphics::Gdi::*;

use crate::battery::DeviceKind;

/// Width of `kind`'s icon at box height `h` (pure math, mirrors `draw`).
pub fn width_for(h: i32, kind: DeviceKind) -> i32 {
    match kind {
        DeviceKind::Headset => (h * 9 + 5) / 10, // 0.9h
        DeviceKind::Keyboard => (h * 23 + 10) / 20, // 1.15h
        DeviceKind::Mouse | DeviceKind::Other => (h * 7 + 5) / 10, // 0.7h
    }
}

/// Draw `kind` at top-left (x, y) in `rgb`, box height `h`.
/// Returns the width used (== `width_for(h, kind)`).
pub fn draw(hdc: HDC, x: i32, y: i32, h: i32, kind: DeviceKind, rgb: (u8, u8, u8)) -> i32 {
    unsafe {
        let color = COLORREF(rgb.0 as u32 | ((rgb.1 as u32) << 8) | ((rgb.2 as u32) << 16));
        let pen = CreatePen(PS_SOLID, (h / 12).max(1), color);
        let brush = CreateSolidBrush(color);
        let old_pen = SelectObject(hdc, pen);
        let old_brush = SelectObject(hdc, GetStockObject(NULL_BRUSH));
        let w = width_for(h, kind);
        match kind {
            DeviceKind::Mouse => mouse(hdc, brush, x, y, h, w),
            DeviceKind::Headset => headset(hdc, brush, x, y, h, w),
            DeviceKind::Keyboard => keyboard(hdc, brush, x, y, h, w),
            DeviceKind::Other => dongle(hdc, brush, x, y, h, w),
        }
        let _ = SelectObject(hdc, old_pen);
        let _ = SelectObject(hdc, old_brush);
        let _ = DeleteObject(pen);
        let _ = DeleteObject(brush);
        w
    }
}

/// Top-view mouse: rounded body, center split line, scroll wheel.
fn mouse(hdc: HDC, brush: HBRUSH, x: i32, y: i32, h: i32, w: i32) {
    unsafe {
        let r = (w * 6 / 10).max(3);
        let _ = RoundRect(hdc, x, y, x + w, y + h, r, r);
        let cx = x + w / 2;
        let _ = MoveToEx(hdc, cx, y + h / 12, None);
        let _ = LineTo(hdc, cx, y + 2 * h / 5);
        let _ = SelectObject(hdc, brush);
        let wheel_w = (w / 6).max(1);
        let _ = RoundRect(
            hdc,
            cx - wheel_w,
            y + h * 3 / 20,
            cx + wheel_w,
            y + h / 4,
            wheel_w.max(1),
            wheel_w.max(1),
        );
    }
}

/// Front-view headset: "∩" headband polyline + two filled ear cups.
fn headset(hdc: HDC, brush: HBRUSH, x: i32, y: i32, h: i32, w: i32) {
    unsafe {
        let pts = [
            POINT { x: x + h / 10, y: y + h / 2 },
            POINT { x: x + h / 10, y: y + h * 28 / 100 },
            POINT { x: x + h * 22 / 100, y: y + h / 10 },
            POINT { x: x + h * 68 / 100, y: y + h / 10 },
            POINT { x: x + h * 8 / 10, y: y + h * 28 / 100 },
            POINT { x: x + h * 8 / 10, y: y + h / 2 },
        ];
        let _ = Polyline(hdc, &pts);
        let _ = SelectObject(hdc, brush);
        let cup_w = h / 5;
        let cup_r = (cup_w / 2).max(2);
        let top = y + h * 42 / 100;
        let bottom = y + h * 9 / 10;
        let _ = RoundRect(hdc, x, top, x + cup_w, bottom, cup_r, cup_r);
        let _ = RoundRect(hdc, x + w - cup_w, top, x + w, bottom, cup_r, cup_r);
    }
}

/// Keyboard: thin rounded body, a row of keys, spacebar.
fn keyboard(hdc: HDC, brush: HBRUSH, x: i32, y: i32, h: i32, w: i32) {
    unsafe {
        let top = y + h * 19 / 100;
        let bottom = y + h * 81 / 100;
        let _ = RoundRect(hdc, x, top, x + w, bottom, 3, 3);
        let _ = SelectObject(hdc, brush);
        let ks = (h / 10).max(2);
        let ky = y + h * 30 / 100;
        for i in 0..4u32 {
            let kx = x + w * (2 + 2 * i as i32) / 10;
            let _ = Rectangle(hdc, kx, ky, kx + ks, ky + ks);
        }
        let sy = ky + ks + ks / 2;
        let _ = Rectangle(hdc, x + w * 28 / 100, sy, x + w * 72 / 100, sy + ks);
    }
}

/// "Other": USB wireless dongle — connector stub, rounded body, LED dot.
fn dongle(hdc: HDC, brush: HBRUSH, x: i32, y: i32, h: i32, w: i32) {
    unsafe {
        let _ = SelectObject(hdc, brush);
        let _ = Rectangle(
            hdc,
            x + w * 25 / 100,
            y,
            x + w * 75 / 100,
            y + h * 3 / 10,
        );
        let _ = SelectObject(hdc, GetStockObject(NULL_BRUSH));
        let _ = RoundRect(hdc, x, y + h * 3 / 10, x + w, y + h, 3, 3);
        let _ = SelectObject(hdc, brush);
        let cx = x + w / 2;
        let cy = y + h * 62 / 100;
        let r = (h / 10).max(1);
        let _ = Ellipse(hdc, cx - r, cy - r, cx + r, cy + r);
    }
}

