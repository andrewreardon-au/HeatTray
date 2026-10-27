"""Shared by make-screenshots.py and the README tray finish: the subtle desktop wallpaper, and keying the flat (46,46,46)
backdrop that the tray captures are composed on (tray-dev-tools shots-final-lib) so they sit on that wallpaper.
Needs Pillow."""
from PIL import Image, ImageDraw, ImageFilter

BACK = 46          # the flat backdrop grey of the captures
CORNER = 10        # rounded-corner zone of the tooltip / menu box at 1x


def wallpaper(w, h):
    """Dark navy to deep teal on a diagonal, with two soft blooms: a Windows 11 style desktop, kept subtle."""
    d = int((w * w + h * h) ** 0.5) + 2                    # rotate a gradient bigger than the canvas, then crop the middle:
    grad = Image.linear_gradient("L").resize((d, d)).rotate(-60, resample=Image.BICUBIC)   # no empty corners
    grad = grad.crop(((d - w) // 2, (d - h) // 2, (d - w) // 2 + w, (d - h) // 2 + h))
    a, b = Image.new("RGB", (w, h), (8, 12, 28)), Image.new("RGB", (w, h), (6, 44, 56))
    img = Image.composite(b, a, grad)
    for cx, cy, r, col, alpha in ((w * 0.28, h * 0.30, h * 0.55, (70, 110, 230), 120),   # blue bloom, upper left
                                  (w * 0.80, h * 0.85, h * 0.50, (30, 170, 175), 90),    # teal glow, lower right
                                  (w * 0.62, h * 0.10, h * 0.30, (120, 90, 220), 55)):   # a hint of violet, top
        m = Image.new("L", (w, h), 0)
        ImageDraw.Draw(m).ellipse([cx - r, cy - r, cx + r, cy + r], fill=alpha)
        img = Image.composite(Image.new("RGB", (w, h), col), img, m.filter(ImageFilter.GaussianBlur(r * 0.6)))
    return img


def taskbar_top(im):
    """First row, scanning down the left edge, that is no longer the backdrop: the taskbar's top edge."""
    return next(y for y in range(im.height) if im.getpixel((6, y))[:3] != (BACK,) * 3)


def key(im):
    """RGBA copy of a capture with the backdrop above the taskbar made transparent. Outside the box (tooltip or menu):
    the backdrop is clear and anything darker is its drop shadow (black at alpha 1 - v/46). On the box's edge ring and
    in its rounded corners, greys between the backdrop and the box's own fill are antialiasing, re-expressed as the
    fill at alpha (v - 46) / (fill - 46). Inside, everything stays opaque (text, separators)."""
    im = im.convert("RGB")
    W, H = im.size
    top = taskbar_top(im)
    px = im.load()
    obj = [(x, y) for y in range(top) for x in range(W)
           if not (px[x, y][0] == px[x, y][1] == px[x, y][2] and px[x, y][0] <= BACK + 4)]
    x0, x1 = min(p[0] for p in obj), max(p[0] for p in obj)
    y0, y1 = min(p[1] for p in obj), max(p[1] for p in obj)
    fill = px[(x0 + x1) // 2, y0 + 3]
    out = im.convert("RGBA")
    op = out.load()
    for y in range(top):
        for x in range(W):
            r, g, b = px[x, y]
            grey = r == g == b
            inside = x0 <= x <= x1 and y0 <= y <= y1
            ring = inside and (min(x - x0, x1 - x, y - y0, y1 - y) < 2 or
                               (min(x - x0, x1 - x) < CORNER and min(y - y0, y1 - y) < CORNER))
            if not inside:
                op[x, y] = (0, 0, 0, round(255 * max(0, 1 - r / BACK))) if grey and r < BACK else \
                    ((0, 0, 0, 0) if grey and r <= BACK + 4 else (r, g, b, 255))
            elif ring and grey and r <= BACK:
                op[x, y] = (0, 0, 0, round(255 * max(0, 1 - r / BACK)))
            elif ring and grey and fill[0] > BACK and BACK < r < fill[0]:
                al = (r - BACK) / (fill[0] - BACK)
                op[x, y] = fill[:3] + (round(255 * al),)
    return out
