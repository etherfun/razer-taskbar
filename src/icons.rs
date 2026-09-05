//! Device-type icons: Segoe Fluent Icons glyphs drawn as GDI text
//! (headset E7F6, keyboard E765, mouse E962, gamepad E7FC) — tinted like
//! the rest of the UI, with Segoe MDL2 Assets as the Win10 fallback (it
//! carries the same codepoints).

use windows::core::w;
use windows::Win32::Foundation::{COLORREF, RECT};
use windows::Win32::Graphics::Gdi::*;

use crate::battery::DeviceKind;

/// The Segoe Fluent Icons glyph for `kind`.
pub fn glyph_for(kind: DeviceKind) -> char {
    match kind {
        DeviceKind::Headset => '\u{E7F6}',
        DeviceKind::Keyboard => '\u{E765}',
        DeviceKind::Mouse => '\u{E962}',
        DeviceKind::Other => '\u{E7FC}',
    }
}

fn icon_font(h: i32) -> HFONT {
    unsafe {
        let font = CreateFontW(
            h,
            0,
            0,
            0,
            FW_NORMAL.0 as i32,
            0,
            0,
            0,
            DEFAULT_CHARSET.0 as u32,
            OUT_DEFAULT_PRECIS.0 as u32,
            CLIP_DEFAULT_PRECIS.0 as u32,
            ANTIALIASED_QUALITY.0 as u32,
            (DEFAULT_PITCH.0 | FF_DONTCARE.0) as u32,
            w!("Segoe Fluent Icons"),
        );
        if font.is_invalid() {
            CreateFontW(
                h,
                0,
                0,
                0,
                FW_NORMAL.0 as i32,
                0,
                0,
                0,
                DEFAULT_CHARSET.0 as u32,
                OUT_DEFAULT_PRECIS.0 as u32,
                CLIP_DEFAULT_PRECIS.0 as u32,
                ANTIALIASED_QUALITY.0 as u32,
                (DEFAULT_PITCH.0 | FF_DONTCARE.0) as u32,
                w!("Segoe MDL2 Assets"),
            )
        } else {
            font
        }
    }
}

/// Advance width of `kind`'s glyph at box height `h`.
pub fn width_for(hdc: HDC, h: i32, kind: DeviceKind) -> i32 {
    unsafe {
        let font = icon_font(h);
        let old = SelectObject(hdc, font);
        let mut rect = RECT::default();
        let mut buf: Vec<u16> = vec![glyph_for(kind) as u16];
        DrawTextW(hdc, &mut buf, &mut rect, DT_SINGLELINE | DT_CALCRECT | DT_LEFT);
        SelectObject(hdc, old);
        let _ = DeleteObject(font);
        (rect.right - rect.left).max(h / 3)
    }
}

/// Draw `kind` at top-left (x, y) in `rgb`, box height `h`.
/// Returns the width used.
pub fn draw(hdc: HDC, x: i32, y: i32, h: i32, kind: DeviceKind, rgb: (u8, u8, u8)) -> i32 {
    unsafe {
        let font = icon_font(h);
        let old_font = SelectObject(hdc, font);
        let _ = SetBkMode(hdc, TRANSPARENT);
        let _ = SetTextColor(
            hdc,
            COLORREF(rgb.0 as u32 | ((rgb.1 as u32) << 8) | ((rgb.2 as u32) << 16)),
        );
        let mut rect = RECT { left: x, top: y, right: x + h * 2, bottom: y + h };
        let mut buf: Vec<u16> = vec![glyph_for(kind) as u16];
        DrawTextW(hdc, &mut buf, &mut rect, DT_SINGLELINE | DT_VCENTER | DT_LEFT);
        let w = (rect.right - rect.left).max(h / 3);
        SelectObject(hdc, old_font);
        let _ = DeleteObject(font);
        w
    }
}
