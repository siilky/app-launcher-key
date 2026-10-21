"""Renders the Marketplace icon and the README preview (Pillow + numpy), reusing the lamps of render_key_backgrounds.py.

Writes src/package/metadata/Icon256x256.png (a lit Gold key with a launch glyph, artwork inside the central 192 px as the
Marketplace requires) and ../assets/preview.png (the key in four states, composed as AppKeyCommand.DrawKey does: lamp,
app icon at 70 % of the key height and 1 % from the top, lamp overlay, then the host's label in the y 71-93 % band).

Usage: python tools/render_store_art.py
"""
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from render_key_backgrounds import KEY, LAMPS, N, OUT_SIZE, S, dim, lamp, rounded_mask

FONT = "C:/Windows/Fonts/segoeui.ttf"
GLYPH = (60, 40, 10, 255)


def layered(lamp_layers, middle):
    """The lamp background, a straight-alpha layer of the icon, then the lamp overlay, as the plugin draws a key."""
    background, overlay = lamp_layers
    image = Image.fromarray(np.clip(background, 0, 255).astype(np.uint8)).convert("RGBA")
    image.alpha_composite(middle)
    image.alpha_composite(Image.fromarray(np.clip(overlay, 0, 255).astype(np.uint8), "RGBA"))
    return image


def store_icon():
    centre, width = KEY / 2, KEY * 0.4
    middle = Image.new("RGBA", (N, N))
    # The triangle's visual centre is nearer its base than its bounding box centre, so nudge it right.
    left = centre - width / 2 + width / 12
    ImageDraw.Draw(middle).polygon(
        [(left * S, (centre - width * 0.5) * S), (left * S, (centre + width * 0.5) * S), ((left + width * 0.9) * S, centre * S)],
        fill=GLYPH)

    key = layered(lamp(True, LAMPS["Gold"]), middle)
    key.putalpha(Image.fromarray((rounded_mask(0, 0, KEY, KEY, 16) * 255).astype(np.uint8)))
    icon = Image.new("RGBA", (256, 256))
    icon.paste(key.resize((192, 192), Image.LANCZOS), (32, 32))
    return icon


def app_window():
    """A neutral app window: coloured title bar, light content lines, transparent surroundings."""
    size = KEY * 70 // 100 * S
    image = Image.new("RGBA", (size, size))
    draw = ImageDraw.Draw(image)
    x0, y0, x1, y1, r, bar = size * 0.08, size * 0.14, size * 0.92, size * 0.88, size * 0.07, size * 0.15
    draw.rounded_rectangle([x0, y0, x1, y1], r, fill=(52, 120, 220, 255))
    draw.rounded_rectangle([x0, y0 + bar, x1, y1], r, fill=(250, 250, 250, 255), corners=(False, False, True, True))
    for i in range(3):
        cx, cy, d = x0 + size * (0.07 + 0.06 * i), y0 + bar / 2, size * 0.022
        draw.ellipse([cx - d, cy - d, cx + d, cy + d], fill=(255, 255, 255, 220))
    for i, end in enumerate((0.82, 0.68, 0.76)):
        y = y0 + bar + size * (0.1 + 0.11 * i)
        draw.rounded_rectangle([x0 + size * 0.07, y, x0 + (x1 - x0) * end, y + size * 0.045], size * 0.022, fill=(205, 208, 214, 255))
    return image


def preview_key(lamp_layers, label, font):
    icon_size = KEY * 70 // 100
    middle = Image.new("RGBA", (N, N))
    middle.paste(app_window(), (((KEY - icon_size) // 2) * S, (KEY // 100) * S))
    key = layered(lamp_layers, middle).resize((OUT_SIZE, OUT_SIZE), Image.LANCZOS)
    ImageDraw.Draw(key).text((OUT_SIZE / 2, OUT_SIZE * 0.82), label, fill="white", font=font, anchor="mm")
    return key


def preview():
    gold_off, gold_on = lamp(False, LAMPS["Gold"]), lamp(True, LAMPS["Gold"])
    blue_off, blue_on = lamp(False, LAMPS["Blue"]), lamp(True, LAMPS["Blue"])
    states = [
        (gold_off, "Not running"),
        (gold_on, "Running"),
        (lamp(True, LAMPS["Green"]), "Any of 8 colours"),
        (dim(blue_off, blue_on, 40), "Adjustable brightness"),
    ]
    label_font = ImageFont.truetype(FONT, round(OUT_SIZE * 0.11))
    caption_font = ImageFont.truetype(FONT, 22)
    pad, gap, caption = 32, 24, 44
    panel = Image.new("RGBA", (pad * 2 + OUT_SIZE * 4 + gap * 3, pad * 2 + OUT_SIZE + caption))
    ImageDraw.Draw(panel).rounded_rectangle([0, 0, panel.width - 1, panel.height - 1], 24, fill=(28, 28, 30, 255))
    draw = ImageDraw.Draw(panel)
    for i, (layers, text) in enumerate(states):
        x = pad + i * (OUT_SIZE + gap)
        panel.paste(preview_key(layers, "My App", label_font), (x, pad))
        draw.text((x + OUT_SIZE / 2, pad + OUT_SIZE + caption * 0.55), text, fill=(190, 190, 195), font=caption_font, anchor="mm")
    return panel


def main():
    plugin = Path(__file__).resolve().parent.parent
    store_icon().save(plugin / "src" / "package" / "metadata" / "Icon256x256.png", optimize=True)
    assets = plugin.parent / "assets"
    assets.mkdir(exist_ok=True)
    preview().save(assets / "preview.png", optimize=True)


if __name__ == "__main__":
    main()
