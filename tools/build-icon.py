"""Renders the RAID REVIEW task bar icon.

Three things about this icon were learned the hard way, in the game rather than
in a preview, and each one is a constraint here:

1. The bar's own glyphs are HEXAGONS, not circles. A ring at 24 px comes out
   visibly ragged, because a curve at that size is all antialiasing; the flat
   edges of a hexagon land on pixel boundaries and stay crisp.
2. The colour has to be baked in. The button's animator writes Image.color
   every frame, so tinting a white glyph from code is overwritten and the icon
   shows up plain white next to the muted ones around it.
3. Small beats large. The sprite is drawn near its real size instead of being
   downsampled from 128 px, and the loader builds a mip chain on top.

Run:  python tools/build-icon.py
Out:  assets/task-bar-icon.png  (64x64 RGBA, in the bar's own gold)
"""

import math
import os
from PIL import Image, ImageDraw

SIZE = 64            # close to the ~24-40 px the bar actually shows
SUPERSAMPLE = 16     # drawn 16x and downscaled: antialiasing without a vector library
GOLD = (194, 174, 110, 255)   # the muted gold the other mod buttons use

W = SIZE * SUPERSAMPLE
STROKE = 0.085 * W   # outline weight, ~8.5% of the width; thinner vanishes at 24 px
INSET = 0.04         # gap between canvas edge and the hexagon's outer points

# Three rising bars inside the hexagon, as fractions of the canvas.
BAR_HEIGHTS = [0.45, 0.72, 1.0]
BAR_LEFT, BAR_RIGHT = 0.36, 0.64
BAR_TOP, BAR_BOTTOM = 0.32, 0.68
BAR_WIDTH = 0.07


def hexagon(radius, flat_top=False):
    """Six points around the centre. Pointy-top by default, like the game's."""
    centre = W / 2.0
    start = 0 if flat_top else -math.pi / 2
    return [
        (centre + radius * math.cos(start + i * math.pi / 3),
         centre + radius * math.sin(start + i * math.pi / 3))
        for i in range(6)
    ]


def draw_hexagon_outline(draw, flat_top=False):
    """Filled hexagon with a smaller one punched out of it.

    Drawing the outline as a closed line instead leaves a visible nib where the
    stroke starts and ends, and thickened joins overshoot the points. Punching
    the middle out gives exact corners: ImageDraw writes RGBA values straight
    into the buffer, so filling with a transparent colour clears those pixels
    rather than blending onto them.
    """
    outer = (0.5 - INSET) * W
    # Uniform wall thickness is measured edge to edge, not point to point: the
    # distance from the centre to an edge is radius * cos(30 deg).
    inner = outer - STROKE / math.cos(math.pi / 6)
    draw.polygon(hexagon(outer, flat_top), fill=GOLD)
    draw.polygon(hexagon(inner, flat_top), fill=(0, 0, 0, 0))


def draw_bars(draw):
    count = len(BAR_HEIGHTS)
    bar_width = BAR_WIDTH * W
    step = (BAR_RIGHT - BAR_LEFT) * W / (count - 1)
    for index, height in enumerate(BAR_HEIGHTS):
        centre_x = BAR_LEFT * W + index * step
        bottom = BAR_BOTTOM * W
        top = bottom - height * (BAR_BOTTOM - BAR_TOP) * W
        draw.rectangle([centre_x - bar_width / 2, top, centre_x + bar_width / 2, bottom], fill=GOLD)


def render(flat_top=False):
    canvas = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)
    draw_hexagon_outline(draw, flat_top)
    draw_bars(draw)
    return canvas.resize((SIZE, SIZE), Image.LANCZOS)


def main():
    icon = render()

    here = os.path.dirname(os.path.abspath(__file__))
    target = os.path.join(here, "..", "assets", "task-bar-icon.png")
    os.makedirs(os.path.dirname(target), exist_ok=True)
    icon.save(target, "PNG", optimize=True)

    print("wrote %s (%dx%d, %d bytes)" % (
        os.path.normpath(target), icon.width, icon.height, os.path.getsize(target)))


if __name__ == "__main__":
    main()
