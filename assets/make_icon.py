# App icon generator: Razer triple-snake + corner battery, transparent bg.
#
# Input : snake-raw.png (1280px, official green #44D62C on transparency,
#         rendered from Wikipedia's File:Razer snake logo.svg)
# Output: app.ico (16..256, per-size renders), icon-preview.png
#
# Small sizes are NOT a plain downscale: they get their own composition
# (battery enlarged / dominant, snake shrunk, 16px battery-only) so the
# battery stays recognizable on taskbar / title-bar / explorer list sizes.
#
# Usage: python make_icon.py   (requires Pillow)

from PIL import Image, ImageDraw

GREEN = (68, 214, 44, 255)       # Razer brand green #44D62C
OUTLINE = (60, 60, 60, 255)      # standalone battery: visible on light AND dark
BODY_EDGE = (245, 245, 245, 255) # battery outline inside the dark badge disc

SNAKE = "snake-raw.png"
ICO_SIZES = [256, 128, 64, 48, 32, 24, 16]

SS = 4  # supersampling factor for vector-drawn parts


def layout(size):
    """Per-size composition.

    snake: side of the snake box as fraction of the canvas (0 = omit);
    bat:   battery body width fraction;
    disc:  badge circle radius fraction, 0 = no badge (16px has no snake to
           separate the battery from, so it stays battery-only);
    sc/bc: snake / battery center (fractions).
    """
    if size <= 16:
        return dict(snake=0.00, bat=0.54, disc=0.00, sc=(0.46, 0.45), bc=(0.50, 0.52))
    if size <= 24:
        return dict(snake=0.44, bat=0.36, disc=0.27, sc=(0.44, 0.41), bc=(0.72, 0.72))
    if size <= 32:
        return dict(snake=0.55, bat=0.32, disc=0.25, sc=(0.46, 0.43), bc=(0.73, 0.73))
    return dict(snake=0.78, bat=0.28, disc=0.235, sc=(0.49, 0.45), bc=(0.745, 0.745))


def render_snake_master():
    """Trim + recolor the source logo onto a square RGBA canvas."""
    im = Image.open(SNAKE).convert("RGBA")
    bbox = im.getchannel("A").getbbox()
    im = im.crop(bbox)
    side = max(im.size)
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.alpha_composite(im, ((side - im.width) // 2, (side - im.height) // 2))
    # force exact brand green through the alpha mask (AA edges stay smooth)
    flat = Image.new("RGBA", canvas.size, GREEN)
    flat.putalpha(canvas.getchannel("A"))
    return flat


def draw_battery(s, bat_frac, cx, cy, framed):
    """Battery glyph on a supersampled canvas (no downscale here).

    Inside the badge disc the outline is white (the disc is always dark);
    standalone it is dark gray so it reads on both light and dark shells."""
    body_w = bat_frac * s
    body_h, body_r = 0.463 * body_w, 0.10 * body_w
    ow = max(SS, int(s * 0.003))
    cap_w, cap_h = int(0.118 * body_w), int(0.464 * body_h)
    edge = BODY_EDGE if framed else OUTLINE

    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    glyph_w = body_w + ow + cap_w
    left = cx * s - glyph_w / 2
    top = cy * s - body_h / 2
    d.rounded_rectangle([left, top, left + body_w, top + body_h],
                        radius=body_r, outline=edge, width=ow)
    cap_left = left + body_w + ow
    d.rounded_rectangle([cap_left, cy * s - cap_h / 2, cap_left + cap_w, cy * s + cap_h / 2],
                        radius=cap_w / 3, fill=edge)
    m = max(1, int(ow * 1.2))
    fw = (body_w - 2 * m) * 0.78
    if fw >= 1:
        d.rounded_rectangle([left + m, top + m, left + m + fw, top + body_h - m],
                            radius=max(1, body_r - m / 2), fill=GREEN)
    return im


def render(size, snake_master):
    s4 = size * SS
    im = Image.new("RGBA", (s4, s4), (0, 0, 0, 0))

    L = layout(size)
    if L["snake"] > 0:
        box = int(s4 * L["snake"])
        snake = snake_master.resize((box, box), Image.LANCZOS)
        im.alpha_composite(snake, (int(s4 * L["sc"][0]) - box // 2,
                                   int(s4 * L["sc"][1]) - box // 2))

    # badge disc: opaque dark plate separating the battery from the snake
    if L["disc"] > 0:
        d = ImageDraw.Draw(im)
        R = L["disc"] * s4
        cx, cy = L["bc"][0] * s4, L["bc"][1] * s4
        d.ellipse([cx - R, cy - R, cx + R, cy + R], fill=(13, 13, 13, 255),
                  outline=(85, 85, 85, 255), width=max(SS, int(s4 * 0.002)))

    im.alpha_composite(draw_battery(s4, L["bat"], L["bc"][0], L["bc"][1],
                                    framed=L["disc"] > 0))
    return im.resize((size, size), Image.LANCZOS)


def main():
    master = render_snake_master()
    frames = {s: render(s, master) for s in ICO_SIZES}

    big = frames[256]
    big.save("app.ico", format="ICO", sizes=[(s, s) for s in ICO_SIZES],
             append_images=[frames[s] for s in ICO_SIZES if s != 256])

    # preview sheet: true-size row on a light and on a dark backing (the icon
    # must read on both), then 4x zoom of the small sizes
    row = [frames[s] for s in [256, 64, 48, 32, 24, 16]]
    zoom = [frames[s].resize((s * 4, s * 4), Image.NEAREST)
            for s in [32, 24, 16]]
    pad = 24
    w = sum(i.width for i in row) + pad * (len(row) + 1)
    h = pad + 256 + pad + 256 + pad + 128 + pad
    sheet = Image.new("RGB", (w, h), (70, 70, 74))
    light = Image.new("RGB", (w, 256 + pad), (245, 245, 245))
    sheet.paste(light, (0, 0))
    dark = Image.new("RGB", (w, 256 + pad), (20, 20, 20))
    sheet.paste(dark, (0, pad + 256))
    for y, frames_row in [(pad, row), (pad * 2 + 256, row),
                          (pad * 3 + 512, zoom)]:
        x = pad
        for i in frames_row:
            sheet.paste(i, (x, y), i)
            x += i.width + pad
    sheet.save("icon-preview.png")


if __name__ == "__main__":
    main()
