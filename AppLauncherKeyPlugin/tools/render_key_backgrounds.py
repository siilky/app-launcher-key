"""Renders the lit-button lamp images into src/Resources (Pillow + numpy).

BitmapBuilder has no gradients, blur or image opacity, so the soft light of a lamp behind a translucent cap is pre-rendered
here, for every colour and brightness level. The plugin draws Lamp<Colour><Off|On<Level>>.png, the app icon, then the
matching ...Overlay.png. Geometry is in pixels of the 116 px key;
the icon sits at x 17-98, y 1-82 (AppKeyCommand.DrawKey) and the host label at y 71-93 %
(package/metadata/DefaultIconTemplate.ict).

Usage: python tools/render_key_backgrounds.py
"""
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

KEY = 116
OUT_SIZE = 232             # 2x the key, so the 143 px previews in Options+ stay sharp
S = 4                      # supersampling of the 116 px key
N = KEY * S
WHITE = np.array([255, 255, 255], float)

# Per colour: hot centre, main colour and dim rim when lit; centre and rim of the unlit cap. The names must match
# AppSettings.Colours in the plugin.
LAMPS = {
    "Gold": ((255, 244, 205), (255, 180, 50), (160, 95, 10), (76, 64, 40), (34, 29, 18)),
    "Green": ((215, 255, 215), (60, 215, 80), (15, 110, 30), (48, 70, 48), (20, 32, 20)),
    "Red": ((255, 215, 200), (235, 55, 40), (120, 12, 10), (74, 44, 40), (32, 18, 16)),
    "Blue": ((210, 230, 255), (50, 130, 255), (10, 45, 140), (44, 54, 76), (16, 22, 34)),
    "White": ((255, 255, 255), (225, 230, 235), (120, 125, 135), (68, 68, 70), (28, 28, 30)),
    "Purple": ((240, 215, 255), (160, 80, 240), (70, 20, 130), (60, 46, 74), (26, 18, 32)),
    "Cyan": ((215, 255, 255), (40, 210, 220), (5, 100, 115), (40, 68, 70), (16, 30, 32)),
    "Pink": ((255, 220, 240), (245, 90, 170), (130, 20, 80), (74, 46, 60), (32, 18, 26)),
}

# Brightness levels of the lit lamp in percent, the steps of the App Settings slider (AppSettings.BrightnessStep).
LEVELS = (20, 40, 60, 80, 100)


def grid():
    y, x = np.mgrid[0:N, 0:N].astype(float)
    return x / S, y / S


def rounded_mask(x0, y0, x1, y1, r, blur=0):
    m = Image.new("L", (N, N), 0)
    ImageDraw.Draw(m).rounded_rectangle([x0 * S, y0 * S, x1 * S, y1 * S], r * S, fill=255)
    if blur:
        m = m.filter(ImageFilter.GaussianBlur(blur * S))
    return np.asarray(m, float) / 255


def mix(a, b, t):
    t = np.clip(t, 0, 1)[..., None]
    return a * (1 - t) + b * t


def lamp(on, colours):
    """The whole translucent cap glows, hottest behind the icon centre and dimmer towards its rim.

    Returns the background and an RGBA overlay drawn over the icon: a light tint of the lamp and the gloss of the cap,
    so the icon reads as printed under the glowing plastic.
    """
    hot, main, deep, off_centre, off_rim = (np.array(c, float) for c in colours)
    x, y = grid()

    # Dark body, lighter at the top as if lit from above.
    img = mix(np.array([46, 46, 50], float), np.array([12, 12, 13], float), y / KEY)
    cap = rounded_mask(4, 3, 112, 113, 14)
    d = np.hypot(x - 58, (y - 42) * 1.1) / 70
    if on:
        col = mix(mix(hot, main, d * 1.6), deep, (d - 0.55) * 2.2)
        # Light leaking onto the body around the cap.
        img = mix(img, main, rounded_mask(4, 3, 112, 113, 14, blur=5) * 0.45)
    else:
        col = mix(off_centre, off_rim, d * 1.3)

    # The rim of a translucent cap transmits less light.
    inner = 1 - rounded_mask(4, 3, 112, 113, 14, blur=3)
    col = col * (1 - np.clip(inner * 2.2, 0, 0.55))[..., None]
    img = mix(img, col, cap)

    # The tint under the gloss, composed into one straight-alpha layer.
    tint = mix(hot, main, d * 1.6) if on else np.broadcast_to(off_centre, d.shape + (3,))
    tint_alpha = cap * (0.22 if on else 0.18)
    gloss = rounded_mask(14, 6, 102, 33, 12, blur=4) * cap * (0.16 if on else 0.10)
    alpha = tint_alpha + gloss * (1 - tint_alpha)
    colour = (tint * (tint_alpha * (1 - gloss))[..., None] + WHITE * gloss[..., None]) / np.maximum(alpha, 1e-6)[..., None]
    return img, np.dstack([colour, alpha * 255])


def dim(off, on, level):
    """A dimmer lamp adds less light to the unlit cap: a blend from off towards on, so the lowest level still reads lit."""
    t = level / 100
    off_background, off_overlay = off
    on_background, on_overlay = on
    background = off_background * (1 - t) + on_background * t

    # Overlays blend premultiplied, so a colour does not leak in through the transparent pixels of the other.
    off_alpha, on_alpha = off_overlay[..., 3:] / 255, on_overlay[..., 3:] / 255
    alpha = off_alpha * (1 - t) + on_alpha * t
    colour = (off_overlay[..., :3] * off_alpha * (1 - t) + on_overlay[..., :3] * on_alpha * t) / np.maximum(alpha, 1e-6)
    return background, np.dstack([colour, alpha[..., 0] * 255])


def save(pixels, path):
    image = Image.fromarray(np.clip(pixels, 0, 255).astype(np.uint8))
    image.resize((OUT_SIZE, OUT_SIZE), Image.LANCZOS).save(path, optimize=True)


def main():
    out = Path(__file__).resolve().parent.parent / "src" / "Resources"
    out.mkdir(exist_ok=True)
    for old in out.glob("Lamp*.png"):
        old.unlink()
    for name, colours in LAMPS.items():
        off, on = lamp(False, colours), lamp(True, colours)
        layers = {"Off": off} | {f"On{level}": dim(off, on, level) for level in LEVELS}
        for state, (background, overlay) in layers.items():
            save(background, out / f"Lamp{name}{state}.png")
            save(overlay, out / f"Lamp{name}{state}Overlay.png")


if __name__ == "__main__":
    main()
