//! Device-type icons: Segoe Fluent Icons glyphs drawn as GDI text
//! (headset E7F6, keyboard E765, mouse E962, gamepad E7FC) — tinted like
//! the rest of the UI, with Segoe MDL2 Assets as the Win10 fallback (it
//! carries the same codepoints).

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, OnceLock};

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

/// Cached icon font for `h` (snapped to Microsoft's magic sizes). Fonts live
/// until process exit — a handful of handles, recreated only on DPI changes;
/// create/delete per draw used to run on every repaint and hover relayout.
/// Also serves the widget's battery glyph (same family + fallback). Handles
/// are stored as raw ints because HFONT is not Send/Sync — GDI font handles
/// are process-wide and usable from any thread, so this is only a type-level
/// workaround.
pub fn icon_font(h: i32) -> HFONT {
    static CACHE: OnceLock<Mutex<HashMap<i32, isize>>> = OnceLock::new();
    let snapped = snap_size(h);
    let cache = CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    let mut cache = cache.lock().unwrap();
    let handle = *cache
        .entry(snapped)
        .or_insert_with(|| create_icon_font(snapped).0 as isize);
    HFONT(handle as *mut std::ffi::c_void)
}

fn create_icon_font(h: i32) -> HFONT {
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

/// Ink box of the glyph as actually rasterized: leftmost/rightmost drawn
/// column relative to the text origin. ABC widths are unhinted design
/// metrics, but GDI grid-fits at the small icon sizes, so each glyph's
/// rendering drifts up to a pixel from them — enough to make stacked
/// per-kind rows look ragged when centered on ABC. Measuring the rendered
/// bitmap keeps the centering basis identical to the pixels on screen.
type InkBox = (i32, i32);

fn ink_cache() -> &'static Mutex<HashMap<(i32, u32), InkBox>> {
    static CACHE: OnceLock<Mutex<HashMap<(i32, u32), InkBox>>> = OnceLock::new();
    CACHE.get_or_init(|| Mutex::new(HashMap::new()))
}

fn ink_box(h: i32, kind: DeviceKind) -> Option<InkBox> {
    let key = (snap_size(h), kind as u32);
    if let Ok(cache) = ink_cache().lock() {
        if let Some(hit) = cache.get(&key) {
            return Some(*hit);
        }
    }
    let scanned = scan_ink_box(key.0, kind);
    if let Some(hit) = scanned {
        if let Ok(mut cache) = ink_cache().lock() {
            cache.insert(key, hit);
        }
    } else {
        static LOGGED: AtomicBool = AtomicBool::new(false);
        if !LOGGED.swap(true, Ordering::Relaxed) {
            eprintln!("razer-taskbar: icon ink scan failed, falling back to ABC widths");
        }
    }
    scanned
}

/// Render the glyph white-on-black into a memory DIB and scan the ink
/// columns. Returns None when GDI could not produce the bitmap or the glyph
/// drew nothing (missing font mapped to a blank).
fn scan_ink_box(h: i32, kind: DeviceKind) -> Option<InkBox> {
    unsafe {
        let w = h * 3; // room for both side bearings
        let screen = GetDC(None);
        if screen.is_invalid() {
            return None;
        }
        let mem = CreateCompatibleDC(screen);
        let bmi = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: w,
                biHeight: -h, // top-down rows
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut bits: *mut std::ffi::c_void = std::ptr::null_mut();
        let hbmp = CreateDIBSection(mem, &bmi, DIB_RGB_COLORS, &mut bits, None, 0)
            .unwrap_or_default();
        if hbmp.is_invalid() {
            let _ = DeleteDC(mem);
            let _ = ReleaseDC(None, screen);
            return None;
        }
        let old_bmp = SelectObject(mem, hbmp);
        let _ = PatBlt(mem, 0, 0, w, h, BLACKNESS);
        let font = icon_font(h);
        let old_font = SelectObject(mem, font);
        let _ = SetBkMode(mem, TRANSPARENT);
        let _ = SetTextColor(mem, COLORREF(0x00FFFFFF));
        // Drawn at column `origin_x` so a negative left bearing stays inside
        // the bitmap. DT_VCENTER is omitted — it only shifts y, and horizontal
        // extents are what we measure (GDI rasterizes one bitmap per glyph
        // and blits them, so they match the later on-screen draws). DT_NOCLIP
        // because the snapped font's line box can exceed the layout rect.
        let origin_x = h;
        let mut buf: Vec<u16> = vec![glyph_for(kind) as u16];
        let mut rc = RECT { left: origin_x, top: 0, right: w, bottom: h };
        let _ = DrawTextW(mem, &mut buf, &mut rc, DT_SINGLELINE | DT_LEFT | DT_NOCLIP);

        let px = bits as *const u32;
        let mut min_x: Option<i32> = None;
        let mut max_x: Option<i32> = None;
        for x in 0..w {
            for y in 0..h {
                let p = *px.add((y * w + x) as usize);
                // White ink on black; grayscale AA edges count from 0x10 up.
                if (p & 0xFF) > 0x10 || ((p >> 8) & 0xFF) > 0x10 || ((p >> 16) & 0xFF) > 0x10 {
                    if min_x.is_none() {
                        min_x = Some(x);
                    }
                    max_x = Some(x);
                    break;
                }
            }
        }

        SelectObject(mem, old_font);
        SelectObject(mem, old_bmp);
        let _ = DeleteObject(hbmp);
        let _ = DeleteDC(mem);
        let _ = ReleaseDC(None, screen);
        min_x.map(|l| (l - origin_x, max_x.unwrap_or(l) - origin_x))
    }
}

/// (origin→ink-left offset, ink width) at font height `h`: the scanned
/// rasterization when available, else the (A, B) ABC widths.
fn ink_metrics(hdc: HDC, h: i32, kind: DeviceKind) -> (i32, i32) {
    if let Some((left, right)) = ink_box(h, kind) {
        return (left, right - left + 1);
    }
    glyph_metrics(hdc, h, kind)
}

/// Ink width of `kind`'s glyph at box height `h` (the drawn pixels, not the
/// advance width — side bearings would make per-row centering look ragged).
pub fn width_for(hdc: HDC, h: i32, kind: DeviceKind) -> i32 {
    ink_metrics(hdc, h, kind).1.max(h / 3)
}

/// Draw `kind`'s ink starting at x (the glyph's origin is back-computed from
/// the measured ink offset), vertically centered in the box height `h`.
/// Returns the ink width used.
pub fn draw(hdc: HDC, x: i32, y: i32, h: i32, kind: DeviceKind, rgb: (u8, u8, u8)) -> i32 {
    let (off, ink_w) = ink_metrics(hdc, h, kind);
    let ink_w = ink_w.max(h / 3);
    unsafe {
        let font = icon_font(h);
        let old_font = SelectObject(hdc, font);
        let _ = SetBkMode(hdc, TRANSPARENT);
        let _ = SetTextColor(
            hdc,
            COLORREF(rgb.0 as u32 | ((rgb.1 as u32) << 8) | ((rgb.2 as u32) << 16)),
        );
        // DT_NOCLIP: the snapped font's line box is usually taller than the
        // layout box (a 16px font in a 14px slot), and without it DrawTextW
        // shaves the glyph's top/bottom ink off.
        let mut rect = RECT { left: x - off, top: y, right: x - off + h * 2, bottom: y + h };
        let mut buf: Vec<u16> = vec![glyph_for(kind) as u16];
        DrawTextW(hdc, &mut buf, &mut rect, DT_SINGLELINE | DT_VCENTER | DT_LEFT | DT_NOCLIP);
        SelectObject(hdc, old_font);
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
        if ok {
            (abc.abcA, abc.abcB as i32)
        } else {
            (0, h / 2)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const KINDS: [DeviceKind; 4] = [
        DeviceKind::Headset,
        DeviceKind::Keyboard,
        DeviceKind::Mouse,
        DeviceKind::Other,
    ];

    #[test]
    fn ink_boxes_are_scanned_and_distinct() {
        for k in KINDS {
            let (off, w) = ink_metrics(unsafe { GetDC(None) }, 16, k);
            // A real icon box: positive ink of plausible icon width, origin
            // offset within a side bearing's range — not the (0, h/2)
            // fallback, which would collapse every kind to the same 8px.
            assert!(w > 4 && w <= 32, "kind {k:?}: ink width {w}");
            assert!(off.abs() < 16, "kind {k:?}: ink offset {off}");
        }
        let kb = ink_metrics(unsafe { GetDC(None) }, 16, DeviceKind::Keyboard).1;
        let mouse = ink_metrics(unsafe { GetDC(None) }, 16, DeviceKind::Mouse).1;
        assert!(kb > mouse, "keyboard ink {kb} should exceed mouse ink {mouse}");
    }

    #[test]
    fn ink_boxes_are_cached_deterministically() {
        for k in KINDS {
            let a = ink_box(16, k);
            let b = ink_box(16, k);
            assert_eq!(a, b, "kind {k:?}");
            assert!(a.is_some(), "kind {k:?}: no scanned ink box");
        }
        let cached = ink_cache().lock().unwrap();
        for k in KINDS {
            assert!(cached.contains_key(&(16, k as u32)), "kind {k:?} not cached");
        }
    }
}
