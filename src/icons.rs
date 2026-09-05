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

/// Per Microsoft: the icon font renders crisply only at these pixel sizes;
/// anything else comes out unclear or blurry. Snap requests to the nearest
/// (ties go to the larger size).
pub const ICON_SIZES: [i32; 7] = [16, 20, 24, 32, 40, 48, 64];

pub fn snap_size(h: i32) -> i32 {
    let mut best = ICON_SIZES[0];
    for s in ICON_SIZES {
        if (s - h).abs() <= (best - h).abs() {
            best = s;
        }
    }
    best
}

fn icon_font(h: i32) -> HFONT {
    let h = snap_size(h);
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

/// Ink width of `kind`'s glyph at box height `h` (the drawn pixels, from
/// ABC widths — the advance width includes side bearings that would make
/// per-row centering look ragged).
pub fn width_for(hdc: HDC, h: i32, kind: DeviceKind) -> i32 {
    glyph_metrics(hdc, h, kind).1.max(h / 3)
}

/// Draw `kind`'s ink starting at x (the glyph's side bearing is compensated
/// internally), vertically centered in the box height `h`. Returns the ink
/// width used.
pub fn draw(hdc: HDC, x: i32, y: i32, h: i32, kind: DeviceKind, rgb: (u8, u8, u8)) -> i32 {
    let (a, ink_w) = glyph_metrics(hdc, h, kind);
    let ink_w = ink_w.max(h / 3);
    unsafe {
        let font = icon_font(h);
        let old_font = SelectObject(hdc, font);
        let _ = SetBkMode(hdc, TRANSPARENT);
        let _ = SetTextColor(
            hdc,
            COLORREF(rgb.0 as u32 | ((rgb.1 as u32) << 8) | ((rgb.2 as u32) << 16)),
        );
        let mut rect = RECT { left: x - a, top: y, right: x - a + h * 2, bottom: y + h };
        let mut buf: Vec<u16> = vec![glyph_for(kind) as u16];
        DrawTextW(hdc, &mut buf, &mut rect, DT_SINGLELINE | DT_VCENTER | DT_LEFT);
        SelectObject(hdc, old_font);
        let _ = DeleteObject(font);
        ink_w
    }
}

/// (A side bearing, B ink width) of the glyph at font height `h`.
fn glyph_metrics(hdc: HDC, h: i32, kind: DeviceKind) -> (i32, i32) {
    unsafe {
        let font = icon_font(h);
        let old = SelectObject(hdc, font);
        let ch = glyph_for(kind) as u32;
        let mut abc = ABC { abcA: 0, abcB: 0, abcC: 0 };
        let ok = GetCharABCWidthsW(hdc, ch, ch, &mut abc).as_bool();
        SelectObject(hdc, old);
        let _ = DeleteObject(font);
        if ok {
            (abc.abcA, abc.abcB as i32)
        } else {
            (0, h / 2)
        }
    }
}
