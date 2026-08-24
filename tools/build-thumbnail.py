"""Renders the Forge thumbnail.

The Forge shows thumbnails small, next to a mod name that already says what
the mod is called - so these carry a picture, not a caption. Whatever survives
at 144 px is the whole design budget: a handful of large shapes, one accent
colour, hard contrast against the dark page.

VARIANTS holds the drafts that were considered; CHOSEN is the one that shipped.
Run with --all to render the contact sheet and compare them again.

Run:  python tools/build-thumbnail.py [--all]
Out:  assets/thumbnail.png (512) and assets/thumbnail-144.png
      with --all: assets/thumbnail-variants.png
"""

import math
import os
import sys
from PIL import Image, ImageDraw

MASTER = 512
THUMB = 144
S = 2                                  # supersampling for the master

GOLD = (194, 174, 110, 255)
GOLD_DIM = (128, 114, 72, 255)
IVORY = (206, 201, 190, 255)
BACK = (18, 20, 24, 255)
PANEL = (11, 12, 15, 255)

CHOSEN = "window-bars"


def canvas():
    size = MASTER * S
    image = Image.new("RGBA", (size, size), BACK)
    return image, ImageDraw.Draw(image), size


def frame(draw, box, colour, width):
    """Rectangle outline drawn as four filled bars: exact corners, any width."""
    x0, y0, x1, y1 = box
    draw.rectangle([x0, y0, x1, y0 + width], fill=colour)
    draw.rectangle([x0, y1 - width, x1, y1], fill=colour)
    draw.rectangle([x0, y0, x0 + width, y1], fill=colour)
    draw.rectangle([x1 - width, y0, x1, y1], fill=colour)


def bars(draw, box, heights, colour=GOLD, gap=0.34):
    x0, y0, x1, y1 = box
    span = x1 - x0
    width = span / (len(heights) + (len(heights) - 1) * gap)
    step = width * (1 + gap)
    for index, height in enumerate(heights):
        left = x0 + index * step
        top = y1 - height * (y1 - y0)
        draw.rectangle([left, top, left + width, y1], fill=colour)


def hexagon_points(centre, radius, pointy=True):
    start = -math.pi / 2 if pointy else 0
    return [(centre[0] + radius * math.cos(start + i * math.pi / 3),
             centre[1] + radius * math.sin(start + i * math.pi / 3)) for i in range(6)]


# --- drafts ---------------------------------------------------------------

def v_window_bars():
    """An overlay window with the mod's bars in it: what the mod does, plainly."""
    image, draw, size = canvas()
    margin = size * 0.14
    box = [margin, margin * 1.15, size - margin, size - margin * 1.15]
    draw.rectangle(box, fill=PANEL)

    title_height = (box[3] - box[1]) * 0.16
    draw.rectangle([box[0], box[1], box[2], box[1] + title_height], fill=GOLD)
    # Two window dots, knocked out of the title bar.
    radius = title_height * 0.17
    for i in range(2):
        cx = box[2] - title_height * (0.55 + i * 0.75)
        cy = box[1] + title_height / 2
        draw.ellipse([cx - radius, cy - radius, cx + radius, cy + radius], fill=PANEL)

    inner = [box[0] + (box[2] - box[0]) * 0.16,
             box[1] + title_height + (box[3] - box[1]) * 0.20,
             box[2] - (box[2] - box[0]) * 0.16,
             box[3] - (box[3] - box[1]) * 0.14]
    draw.rectangle([inner[0], inner[3], inner[2], inner[3] + size * 0.012], fill=GOLD_DIM)
    bars(draw, inner, [0.42, 0.66, 0.88, 1.0])
    frame(draw, box, GOLD_DIM, size * 0.008)
    return image


def v_hex_mark():
    """The button glyph, blown up. Consistent with the in-game icon."""
    image, draw, size = canvas()
    centre = (size / 2, size / 2)
    outer = size * 0.36
    stroke = size * 0.062
    draw.polygon(hexagon_points(centre, outer), fill=GOLD)
    draw.polygon(hexagon_points(centre, outer - stroke / math.cos(math.pi / 6)), fill=BACK)
    bars(draw, [size * 0.38, size * 0.36, size * 0.62, size * 0.64], [0.45, 0.72, 1.0])
    return image


def v_screen_in_screen():
    """A window sitting over a larger screen: the point of the addon."""
    image, draw, size = canvas()
    outer = [size * 0.08, size * 0.14, size * 0.92, size * 0.86]
    frame(draw, outer, (58, 60, 66, 255), size * 0.012)

    # a hint of the game underneath
    for i, y in enumerate((0.30, 0.42, 0.54)):
        draw.rectangle([outer[0] + size * 0.05, size * y,
                        outer[0] + size * (0.05 + 0.26 - i * 0.06), size * y + size * 0.018],
                       fill=(48, 50, 56, 255))

    window = [size * 0.34, size * 0.34, size * 0.86, size * 0.78]
    draw.rectangle(window, fill=PANEL)
    title = (window[3] - window[1]) * 0.17
    draw.rectangle([window[0], window[1], window[2], window[1] + title], fill=GOLD)
    inner = [window[0] + size * 0.06, window[1] + title + size * 0.05,
             window[2] - size * 0.06, window[3] - size * 0.05]
    bars(draw, inner, [0.45, 0.7, 1.0])
    frame(draw, window, GOLD_DIM, size * 0.007)
    return image


def v_route():
    """A replayed path across a window: the positional side of Raid Review."""
    image, draw, size = canvas()
    margin = size * 0.14
    box = [margin, margin * 1.15, size - margin, size - margin * 1.15]
    draw.rectangle(box, fill=PANEL)
    title = (box[3] - box[1]) * 0.16
    draw.rectangle([box[0], box[1], box[2], box[1] + title], fill=GOLD)
    frame(draw, box, GOLD_DIM, size * 0.008)

    points = [(0.30, 0.72), (0.42, 0.52), (0.55, 0.66), (0.68, 0.42)]
    pixels = [(size * x, size * y) for x, y in points]
    draw.line(pixels, fill=GOLD, width=int(size * 0.022), joint="curve")
    for point, radius in ((pixels[0], size * 0.028), (pixels[-1], size * 0.038)):
        draw.ellipse([point[0] - radius, point[1] - radius, point[0] + radius, point[1] + radius], fill=IVORY)
    return image


def v_hex_on_window():
    """Window in the back, glyph in front - busiest of the drafts."""
    image, draw, size = canvas()
    box = [size * 0.10, size * 0.16, size * 0.78, size * 0.72]
    draw.rectangle(box, fill=PANEL)
    draw.rectangle([box[0], box[1], box[2], box[1] + (box[3] - box[1]) * 0.15], fill=GOLD_DIM)
    frame(draw, box, (58, 60, 66, 255), size * 0.007)

    centre = (size * 0.68, size * 0.68)
    outer = size * 0.24
    stroke = size * 0.05
    draw.polygon(hexagon_points(centre, outer + stroke * 0.6), fill=BACK)
    draw.polygon(hexagon_points(centre, outer), fill=GOLD)
    draw.polygon(hexagon_points(centre, outer - stroke / math.cos(math.pi / 6)), fill=BACK)
    bars(draw, [centre[0] - outer * 0.42, centre[1] - outer * 0.40,
                centre[0] + outer * 0.42, centre[1] + outer * 0.42], [0.45, 0.72, 1.0])
    return image


VARIANTS = [
    ("window-bars", v_window_bars),
    ("hex-mark", v_hex_mark),
    ("screen-in-screen", v_screen_in_screen),
    ("route", v_route),
    ("hex-on-window", v_hex_on_window),
]


def render(name):
    for candidate, draw_fn in VARIANTS:
        if candidate == name:
            return draw_fn().resize((MASTER, MASTER), Image.LANCZOS)
    raise SystemExit("unknown variant: " + name)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    assets = os.path.join(here, "..", "assets")
    os.makedirs(assets, exist_ok=True)

    if "--all" in sys.argv:
        sheet = Image.new("RGBA", (THUMB * len(VARIANTS) + 20 * (len(VARIANTS) + 1), 320), (8, 8, 8, 255))
        draw = ImageDraw.Draw(sheet)
        for index, (name, _) in enumerate(VARIANTS):
            master = render(name)
            x = 20 + index * (THUMB + 20)
            sheet.paste(master.resize((THUMB, THUMB), Image.LANCZOS), (x, 30))
            sheet.paste(master.resize((72, 72), Image.LANCZOS), (x + (THUMB - 72) // 2, 200))
            draw.text((x, 14), name, fill=(150, 150, 150))
        draw.text((20, 300), "oben 144 px (Forge-Kachel), unten 72 px", fill=(110, 110, 110))
        target = os.path.join(assets, "thumbnail-variants.png")
        sheet.save(target)
        print("wrote", os.path.normpath(target))
        return

    master = render(CHOSEN)
    master.save(os.path.join(assets, "thumbnail.png"))
    master.resize((THUMB, THUMB), Image.LANCZOS).save(os.path.join(assets, "thumbnail-144.png"))
    print("wrote thumbnail.png (%d px) and thumbnail-144.png from variant '%s'" % (MASTER, CHOSEN))


if __name__ == "__main__":
    main()
