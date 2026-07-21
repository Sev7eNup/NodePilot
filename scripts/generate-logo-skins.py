"""Generate per-skin recolored variants of the NodePilot app logo.

The brand logo (`public/appicon.png`) is a multi-hue node-graph raster. Each color
skin (see `src/stores/themeStore.ts` + the `--color-primary` blocks in `index.css`)
has a single accent hue, so a fixed-orange logo clashes on the blue / lilac / violet
skins. Rather than ship a flat vector that loses the 3D shading + white inner symbols,
we pre-render one variant per skin by hue-remapping the untouched original
(`appicon.original.png`), preserving saturation/value so gradients and shading survive
and the white play-triangle / checkmark stay white.

`BrandLogo.tsx` then picks `appicon-<skin>.png` for the active skin.

Re-run after the source logo changes:  python scripts/generate-logo-skins.py
Requires Pillow (`pip install pillow`).
"""
from __future__ import annotations

import colorsys
import os

import PIL.Image as Image

HERE = os.path.dirname(os.path.abspath(__file__))
PUBLIC = os.path.normpath(os.path.join(HERE, "..", "src", "nodepilot-ui", "public"))
SRC = os.path.join(PUBLIC, "appicon.original.png")

# Largest on-screen use is the 64 px login logo; 384 px gives ~6x hi-dpi headroom
# while keeping each variant small (~40-70 KB vs the 226 KB full-res source).
MAX_SIZE = 384

# Uniform target hue (degrees) per skin id, matching that skin's --color-primary
# accent family. `dark` = 25 reproduces the previously-approved orange appicon.png.
SKIN_HUE = {
    "light": 210,           # blue    (#004ac6 / #2563eb)
    "dark": 25,             # orange  (#fc8861 / #c5620b)
    "dark-lila": 254,       # lilac   (#9b7dff / #7c5cfc)
    "light-grey": 262,      # violet  (#7c3aed / #7c5cfc)
    "dark-sparkasse": 0,    # red     (#ff5a5a / #ee0000)
    "light-sparkasse": 0,   # red     (#c80000 / #ee0000)
    "dark-nebula": 188,     # cyan    (#4de4f7 / #22d3ee)
}

# Below this saturation a pixel is treated as a white/grey inner symbol and left
# untouched, so the play-triangle + checkmark keep reading on the colored shapes.
WHITE_SAT_CUTOFF = 0.12


def recolor(target_hue_deg: int) -> Image.Image:
    im = Image.open(SRC).convert("RGBA")
    px = im.load()
    w, h = im.size
    th = target_hue_deg / 360.0
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a < 8:
                continue
            _, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            if s < WHITE_SAT_CUTOFF:
                continue
            nr, ng, nb = colorsys.hsv_to_rgb(th, s, v)
            px[x, y] = (round(nr * 255), round(ng * 255), round(nb * 255), a)
    if max(im.size) > MAX_SIZE:
        ratio = MAX_SIZE / max(im.size)
        im = im.resize((round(w * ratio), round(h * ratio)), Image.LANCZOS)
    return im


def main() -> None:
    if not os.path.exists(SRC):
        raise SystemExit(f"source logo not found: {SRC}")
    for skin, hue in SKIN_HUE.items():
        out = os.path.join(PUBLIC, f"appicon-{skin}.png")
        recolor(hue).save(out)
        print("wrote", out)


if __name__ == "__main__":
    main()
