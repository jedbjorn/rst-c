#!/usr/bin/env python3
"""Draws the Search-It ribbon icons (icon_search.png 32 px, icon_search_16.png 16 px).

Same style as icon_loader / icon_health / icon_creator in src/RST.Engine/Assets/icons: the
#d9d9d9 chip with transparent corner pixels (RST-043), a simple glyph inside about 6 px of
padding, Health's dark outline grey for the lens and the Loader's blue for the handle. The
chip's shape (its alpha) is copied from icon_loader so the corners are identical.

Not shipped: run it by hand when the icon needs to change (needs Pillow).

    python3 build/icons/make_search_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

ICONS = Path(__file__).resolve().parents[2] / "src" / "RST.Engine" / "Assets" / "icons"
CHIP = (217, 217, 217, 255)      # the chip the other RST icons sit on
OUTLINE = (64, 64, 64, 255)      # Health's heart outline
ACCENT = (79, 142, 247, 255)     # the Loader's arrow
SCALE = 16                       # supersampling


def draw(size: int, reference: str, out: str) -> None:
    big = size * SCALE
    unit = big / 32.0            # one 32 px-grid pixel, supersampled
    glyph = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(glyph)

    # The 16 px icon gets heavier strokes, as the other *_16 icons do, so it survives the size.
    ring = 2.0 if size == 32 else 3.0
    handle = 3.2 if size == 32 else 4.4
    cx, cy, r = 14.0, 14.0, 6.6

    # Handle first (blue), from the lens's edge towards the lower right, with round ends.
    k = 0.7071
    x0, y0 = cx + (r + 0.6) * k, cy + (r + 0.6) * k
    x1, y1 = 24.6, 24.6
    d.line([(x0 * unit, y0 * unit), (x1 * unit, y1 * unit)], fill=ACCENT, width=round(handle * unit))
    for x, y in ((x0, y0), (x1, y1)):
        h = handle / 2
        d.ellipse([(x - h) * unit, (y - h) * unit, (x + h) * unit, (y + h) * unit], fill=ACCENT)

    # The lens: a dark ring over the handle's start.
    outer = r + ring / 2
    d.ellipse([(cx - outer) * unit, (cy - outer) * unit, (cx + outer) * unit, (cy + outer) * unit],
              outline=OUTLINE, width=round(ring * unit))

    glyph = glyph.resize((size, size), Image.LANCZOS)
    chip = Image.new("RGBA", (size, size), CHIP)
    icon = Image.alpha_composite(chip, glyph)
    icon.putalpha(Image.open(ICONS / reference).convert("RGBA").getchannel("A"))
    # Fully transparent corner pixels carry no colour, as in the other icons.
    px = icon.load()
    for y in range(size):
        for x in range(size):
            if px[x, y][3] == 0:
                px[x, y] = (0, 0, 0, 0)
    icon.save(ICONS / out, optimize=True)
    print(f"wrote {ICONS / out}")


if __name__ == "__main__":
    draw(32, "icon_loader.png", "icon_search.png")
    draw(16, "icon_loader_16.png", "icon_search_16.png")
