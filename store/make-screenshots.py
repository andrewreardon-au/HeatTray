#!/usr/bin/env python3
"""Composes the Microsoft Store listing screenshots (1366x768, the Store minimum) on a subtle desktop wallpaper
(shotkey.wallpaper), each under a one-line caption. Images are enlarged with Lanczos smoothing (nearest-neighbour
magnifies the ClearType colour fringes into blocks and looks fuzzy).
- Tray and menu: the 1x captures in store/shots/ (flat backdrop, keyed by shotkey.key), enlarged and anchored to the
  bottom right like a real taskbar, which is extended across the full width.
- Settings and About: the README dialog images in ../screenshots, centred with a soft shadow.
Needs Pillow and Segoe UI (Windows). Writes store/listing/."""
import os
import time
from PIL import Image, ImageDraw, ImageFilter, ImageFont

from shotkey import key, taskbar_top, wallpaper

HERE = os.path.dirname(os.path.abspath(__file__))
SHOTS_DIR = os.path.join(HERE, "shots")
README_SHOTS = os.path.join(os.path.dirname(HERE), "screenshots")
OUT = os.path.join(HERE, "listing")
W, H = 1366, 768
CAPTION = (150, 210, 255)   # pale sky blue, Semibold: reads as a caption, not as Windows UI text
FONT = r"C:\Windows\Fonts\seguisb.ttf"

TASKBAR = [  # (source in store/shots, scale, caption, output)
    ("tray-green.png", 2.7, "How fast your CPU really runs, in the system tray", "screenshot-1-tray.png"),
    ("tray-orange.png", 2.7, "Orange when it is running slower than it should", "screenshot-1-tray-orange.png"),
    ("tray-red.png", 2.7, "Red when it is far slower", "screenshot-1-tray-red.png"),
    ("menu.png", 2.3, "Right-click for Details, Settings and Exit", "screenshot-2-menu.png"),
]
DIALOGS = [  # (source in ../screenshots, scale, caption, output)
    ("HeatTray-screenshot_details.png", 0.8, "Details: why it is slow and what to try", "screenshot-3-details.png"),
    ("HeatTray-screenshot_settings.png", 1.4, "Set the colour limits and what the icon shows", "screenshot-4-settings.png"),
]

font = ImageFont.truetype(FONT, 42)


def canvas_with_caption(caption):
    canvas = wallpaper(W, H).convert("RGBA")
    d = ImageDraw.Draw(canvas)
    tw = d.textlength(caption, font=font)
    d.text(((W - tw) / 2 + 2, 32), caption, font=font, fill=(0, 0, 0, 120))   # soft offset shadow
    d.text(((W - tw) / 2, 30), caption, font=font, fill=CAPTION)
    return canvas, 30 + d.textbbox((0, 0), caption, font=font)[3] + 25


def save(im, path):
    """Retry briefly: right after a write, something on this machine (AV or a viewer) can hold the file (Errno 22)."""
    for attempt in range(10):
        try:
            im.convert("RGB").save(path)
            return
        except OSError:
            if attempt == 9:
                raise
            time.sleep(0.5)


def enlarge(im, scale):
    return im.resize((round(im.width * scale), round(im.height * scale)), Image.LANCZOS)


os.makedirs(OUT, exist_ok=True)
for name, scale, caption, out in TASKBAR:
    src = Image.open(os.path.join(SHOTS_DIR, name)).convert("RGB")
    tb_top = round(taskbar_top(src) * scale)
    im = enlarge(key(src), scale)
    canvas, top = canvas_with_caption(caption)
    x, y = W - im.width, H - im.height
    assert x >= 0 and y >= top, (out, im.size, top)
    col = im.crop((1, tb_top, 2, im.height))              # carry the taskbar (edge line included) across to the left
    canvas.alpha_composite(col.resize((x + 2, col.height)), (0, y + tb_top))
    canvas.alpha_composite(im, (x, y))
    save(canvas, os.path.join(OUT, out))
    print("wrote", out, im.width, "x", im.height, "at", scale, "x")

for name, scale, caption, out in DIALOGS:
    im = enlarge(Image.open(os.path.join(README_SHOTS, name)).convert("RGBA"), scale)
    canvas, top = canvas_with_caption(caption)
    room = H - top - 20
    assert im.height <= room, (out, im.height, room)
    x, y = (W - im.width) // 2, top + (room - im.height) // 2
    shadow = Image.new("L", (W, H), 0)
    ImageDraw.Draw(shadow).rectangle([x + 6, y + 10, x + im.width + 6, y + im.height + 10], fill=150)
    canvas = Image.composite(Image.new("RGBA", (W, H), (0, 0, 0, 255)), canvas, shadow.filter(ImageFilter.GaussianBlur(14)))
    canvas.alpha_composite(im, (x, y))
    save(canvas, os.path.join(OUT, out))
    print("wrote", out, im.width, "x", im.height, "at", scale, "x")
