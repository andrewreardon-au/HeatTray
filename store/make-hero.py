#!/usr/bin/env python3
"""Microsoft Store 16:9 super hero art for HeatTray (1920x1080): the tray icon on a magnified taskbar with its
hover tooltip, one image per colour (green, orange, red). Needs Pillow and Segoe UI (Windows). Writes
store/listing/hero-*.png. The tooltip lines must match what the app prints (LEGEND and READ below).
Digits follow the app's RenderIcon/DrawFit (1.9): Segoe UI Semibold, each glyph placed by its ink (gap 9/100 em),
scaled to fill the box, never squeezed narrower than 0.85 of natural shape, thin dark halo; two digits take the
scale of "88" so every pair is the same height and weight (and are squeezed by their own width if wider, e.g. a 4)."""
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "listing")
W, H = 1920, 1080
BOLD = r"C:\Windows\Fonts\seguisb.ttf"   # Semibold, as the app since 1.9 (the name is historic)
REG = r"C:\Windows\Fonts\segoeui.ttf"
GREEN, ORANGE, RED = (50, 205, 50), (255, 165, 0), (255, 85, 85)
TIP_BG, TIP_BORDER, TIP_TEXT = (43, 43, 43), (70, 70, 70), (240, 240, 240)
TASKBAR = (32, 32, 32)


def _strip(text):
    f = ImageFont.truetype(BOLD, 400)
    glyphs = []
    for ch in text:
        m = Image.new("L", (400, 600), 0)
        ImageDraw.Draw(m).text((20, 0), ch, font=f, fill=255)
        glyphs.append(m.crop(m.getbbox()))
    gap = round(400 * 0.09)
    gh = max(g.height for g in glyphs)
    total = sum(g.width for g in glyphs) + gap * (len(glyphs) - 1)
    strip = Image.new("L", (total, gh), 0)
    x = 0
    for g in glyphs:
        strip.paste(g, (x, gh - g.height))  # shared baseline
        x += g.width + gap
    return strip


def digits_mask(text, box_w, box_h):
    """Grey mask of the number fitted into box_w x box_h like DrawFit."""
    strip = _strip(text)
    fitW, fitH = box_w / strip.width, box_h / strip.height
    sy = min(fitH, fitW / 0.85)
    if len(text) == 2:   # same scale as "88", whatever the digits
        r = _strip("88")
        sy = min(box_h / r.height, (box_w / r.width) / 0.85)
    sx = min(sy, fitW)
    return strip.resize((max(1, round(strip.width * sx)), max(1, round(strip.height * sy))), Image.LANCZOS)


def paste_number(img, text, colour, cx, cy, box_w, box_h, glow=0):
    m = digits_mask(text, box_w, box_h)
    halo_px = max(1, round(m.height / 28))  # the app's 2 px pen on a 28 px box
    pad = halo_px + glow * 3 + 4
    big = Image.new("L", (m.width + 2 * pad, m.height + 2 * pad), 0)
    big.paste(m, (pad, pad))
    x0, y0 = round(cx - big.width / 2), round(cy - big.height / 2)
    if glow:
        g = big.filter(ImageFilter.GaussianBlur(glow))
        layer = Image.new("RGBA", big.size, colour + (0,))
        layer.putalpha(g.point(lambda v: int(v * 0.55)))
        img.alpha_composite(layer, (x0, y0))
    halo = big.filter(ImageFilter.MaxFilter(2 * halo_px + 1))
    hl = Image.new("RGBA", big.size, (15, 15, 15, 0))
    hl.putalpha(halo.point(lambda v: v * 190 // 255))
    img.alpha_composite(hl, (x0, y0))
    fl = Image.new("RGBA", big.size, colour + (0,))
    fl.putalpha(big)
    img.alpha_composite(fl, (x0, y0))


def gradient(top, bottom, radial=None):
    img = Image.new("RGBA", (W, H))
    d = ImageDraw.Draw(img)
    for y in range(H):
        t = y / (H - 1)
        d.line([(0, y), (W, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    if radial:
        cx, cy, r, col, a = radial
        g = Image.new("L", (W, H), 0)
        ImageDraw.Draw(g).ellipse([cx - r, cy - r, cx + r, cy + r], fill=a)
        g = g.filter(ImageFilter.GaussianBlur(r / 2.5))
        layer = Image.new("RGBA", (W, H), col + (0,))
        layer.putalpha(g)
        img.alpha_composite(layer)
    return img


def tooltip(img, x, y, lines, size, anchor="tl", accent=None):
    f = ImageFont.truetype(REG, size)
    d = ImageDraw.Draw(img)
    lh = round(size * 1.38)
    padx, pady = round(size * 0.65), round(size * 0.5)
    w = max(d.textlength(s, font=f) for s in lines) + 2 * padx
    h = lh * len(lines) + 2 * pady - round(size * 0.1)
    if anchor == "tc":
        x -= w / 2
    elif anchor == "bc":
        x -= w / 2
        y -= h
    sh = Image.new("L", (W, H), 0)
    ImageDraw.Draw(sh).rounded_rectangle([x + 4, y + 8, x + w + 4, y + h + 8], radius=size * 0.4, fill=140)
    sh = sh.filter(ImageFilter.GaussianBlur(size * 0.5))
    sl = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    sl.putalpha(sh)
    img.alpha_composite(sl)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([x, y, x + w, y + h], radius=size * 0.35, fill=TIP_BG + (255,),
                        outline=TIP_BORDER, width=max(1, size // 22))
    for i, s in enumerate(lines):
        d.text((x + padx, y + pady + i * lh), s, font=f, fill=accent if (accent and i == 2) else TIP_TEXT)
    return (x, y, w, h)


def tile(img, cx, cy, size):
    """A highlighted (hovered) tray slot behind a number."""
    r = size / 2
    ImageDraw.Draw(img).rounded_rectangle([cx - r, cy - r, cx + r, cy + r], radius=size * 0.12, fill=(52, 52, 56, 255))


READ = {   # the tooltips the app prints (HeatTray.cs, Render/SlowLines), as captured on 28 Oct 2026
    "g": ("86", GREEN, ["Speed 86% 2.8GHz of 3.3GHz", "Working hard, all fine", "61C | load 8%"]),
    "o": ("59", ORANGE, ["Speed 59% 1.9GHz of 3.3GHz", "Slower, not hot (63C)", "Plug in, power mode", "Like a Core i5-2500K from 2011"]),
    "r": ("27", RED, ["Speed 27% 0.9GHz of 3.3GHz", "Far slower, not hot (61C)", "Plug in, power mode", "Like a Core 2 Duo E6600 from 2006"]),
}


GLOW = {"g": ((14, 30, 22), (40, 170, 60)), "o": ((32, 24, 12), (190, 120, 20)), "r": ((32, 14, 16), (190, 50, 50))}


def variant_b(key="g"):
    text, colour, lines = READ[key]
    top, glow = GLOW[key]
    img = gradient(top, (8, 10, 14), (W / 2, 470, 560, glow, 110))
    ty, th = 520, 190  # magnified taskbar strip, kept above the bottom third
    d = ImageDraw.Draw(img)
    d.rectangle([0, ty, W, ty + th], fill=TASKBAR + (255,))
    d.line([(0, ty), (W, ty)], fill=(60, 60, 60), width=2)
    fcap = ImageFont.truetype(REG, 46)
    cx, cy = 760, ty + th / 2
    d.line([(cx - 22, cy + 10), (cx, cy - 12), (cx + 22, cy + 10)], fill=(230, 230, 230), width=6, joint="curve")
    sx = W / 2
    d.rounded_rectangle([sx - 120, ty + 18, sx + 120, ty + th - 18], radius=18, fill=(58, 58, 58))
    paste_number(img, text, colour, sx, cy, 190, 128, glow=22)
    d = ImageDraw.Draw(img)
    for i, s in enumerate(("10:21 AM", "28/10/2026")):
        tw = d.textlength(s, font=fcap)
        d.text((1300 - tw / 2, ty + 34 + i * 62), s, font=fcap, fill=(240, 240, 240))
    tooltip(img, sx, ty - 40, lines, 46, "bc")
    return img


for name, fn in (("hero-green.png", lambda: variant_b("g")), ("hero-orange.png", lambda: variant_b("o")),
                 ("hero-red.png", lambda: variant_b("r"))):
    im = fn().convert("RGB")
    assert im.size == (W, H)
    im.save(os.path.join(OUT, name))
    print("wrote", name)
