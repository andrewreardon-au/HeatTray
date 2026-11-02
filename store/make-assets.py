#!/usr/bin/env python3
"""Generates the MSIX / Store logo PNGs into store/Assets (requires Pillow).
Logo: the app's own icon (src/HeatTray.ico, largest frame), scaled to each size."""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ICO = os.path.join(HERE, "..", "src", "HeatTray.ico")

def frame():
    ico = Image.open(ICO)
    big = max(ico.ico.sizes())
    return ico.ico.getimage(big).convert("RGBA")

SRC = frame()

def logo(size):
    return SRC.resize((size, size), Image.LANCZOS)

OUT = {
    "StoreLogo.png": 50,
    "Square44x44Logo.png": 44,
    "Square150x150Logo.png": 150,
}

if __name__ == "__main__":
    d = os.path.join(HERE, "Assets")
    os.makedirs(d, exist_ok=True)
    for name, size in OUT.items():
        logo(size).save(os.path.join(d, name))
        print("wrote", name)
    # Unplated variants: without these Windows puts the 44x44 icon on a backplate in the user's accent
    # colour (Startup apps list, taskbar, Start). Targetsize files are picked by size; unplated = no plate.
    for size in (16, 24, 32, 44, 48, 256):
        name = "Square44x44Logo.targetsize-%d_altform-unplated.png" % size
        logo(size).save(os.path.join(d, name))
        print("wrote", name)
    # The 300x300 Partner Center listing logo is uploaded in the Store listing, not packaged.
    l = os.path.join(HERE, "listing")
    os.makedirs(l, exist_ok=True)
    logo(300).save(os.path.join(l, "StoreListing_300.png"))
    print("wrote listing/StoreListing_300.png")
