#!/usr/bin/env python3
"""Store logo art: poster (2:3, 1440x2160: Partner Center's "9:16 Poster art" field accepts only 720x1080 or 1440x2160)
and 1:1 box art (2160x2160). The logo bars (make-assets.py) with a soft glow, the app name and a short
tagline on the same wallpaper as the screenshots. Everything sits in the top two-thirds: the Store may overlay text on
the bottom third. White on the dark wallpaper is well over the 4.5:1 contrast the guidance asks for.
Needs Pillow and Segoe UI (Windows). Writes store/listing/."""
import os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

from shotkey import wallpaper

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "listing")
ICON = os.path.join(os.path.dirname(HERE), "src", "HeatTray.ico")


def _logo(size):
    """The app's own icon (largest frame of src/HeatTray.ico), scaled."""
    ico = Image.open(ICON)
    ico.size = max(ico.info["sizes"])
    return ico.convert("RGBA").resize((size, size), Image.LANCZOS)
TITLE_FONT = r"C:\Windows\Fonts\seguisb.ttf"
TAG_FONT = r"C:\Windows\Fonts\segoeui.ttf"
TAG_COLOUR = (150, 210, 255)        # the screenshot captions' sky blue

ART = [  # (output, width, height, logo size / width, logo centre / height, title size / width)
    ("poster-2x3.png", 1440, 2160, 0.62, 0.25, 0.13),
    ("boxart-1x1.png", 2160, 2160, 0.34, 0.22, 0.085),
]


def render(w, h, logo_frac, logo_cy, title_frac):
    img = wallpaper(w, h).convert("RGBA")
    size = round(w * logo_frac)
    logo = _logo(size)
    x, y = (w - size) // 2, round(h * logo_cy - size / 2)
    glow = Image.new("RGBA", (w, h), (0, 0, 0, 0))                # a soft light behind the bars
    ImageDraw.Draw(glow).ellipse([x, y, x + size, y + size], fill=(120, 170, 255, 90))
    img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(size * 0.18)))
    img.alpha_composite(logo, (x, y))
    d = ImageDraw.Draw(img)
    title = ImageFont.truetype(TITLE_FONT, round(w * title_frac))
    tag = ImageFont.truetype(TAG_FONT, round(w * title_frac * 0.35))
    ty = y + size + round(h * 0.02)
    for text, font, col, gap in (("HeatTray", title, (255, 255, 255), 0.015), ("How fast your CPU really runs", tag, TAG_COLOUR, 0)):
        tw = d.textlength(text, font=font)
        d.text(((w - tw) / 2 + 3, ty + 4), text, font=font, fill=(0, 0, 0, 110))
        d.text(((w - tw) / 2, ty), text, font=font, fill=col)
        ty += d.textbbox((0, 0), text, font=font)[3] + round(h * gap)
    assert ty <= h * 2 / 3, ("text runs into the bottom third", ty, h)
    return img.convert("RGB")


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    for name, w, h, lf, cy, tf in ART:
        render(w, h, lf, cy, tf).save(os.path.join(OUT, name))
        print("wrote", name, w, "x", h)
