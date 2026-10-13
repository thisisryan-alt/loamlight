"""
Builds the world tile atlas from the content JSON (Assets/Content/Data/tiles.json + walls.json).

Outputs (Assets/Art/Generated/Atlas/):
  tiles.png         albedo atlas (RGBA)
  tiles_n.png       normal atlas (RGB = n * 0.5 + 0.5, +Y up, +Z toward the viewer)
  tiles_e.png       emission atlas (RGB added after lighting: torch flames, glowing ores)
  tiles.atlas.json  frame layout per tile/wall id (schema in DESIGN.md)
and sprite sheets for the importer (Assets/Art/Generated/Sprites/):
  icons_tiles.*     16x16 item icons for every block/wall/torch item
  cracks.*          4 mining-crack overlay frames

Every 16 px cell sits in an 18 px slot whose 1 px border repeats the cell's edge pixels, so point-sampled
UVs never bleed into a neighbour at fractional camera positions.

Terrain autotiling: frame = mask * 3 + variant, mask bit 1 = open above, 2 = open right, 4 = open below,
8 = open left ("open" = the neighbour is not solid terrain). Open edges get a dark outline, rounded corners and
a bevelled height profile, so the normal map makes block edges catch light.

Deterministic: every random draw is seeded from (tile id, variant).
"""
import argparse
import hashlib
import json
import math
import os

import numpy as np
from PIL import Image

CELL = 16
SLOT = 18
ATLAS_W = 1024
COLS = ATLAS_W // SLOT
VARIANTS = 3


def hex_rgb(h):
    h = h.lstrip("#")
    return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], dtype=np.float64)


def rng_for(*parts):
    digest = hashlib.sha256("/".join(str(p) for p in parts).encode()).digest()
    return np.random.default_rng(int.from_bytes(digest[:8], "little"))


def value_noise(rng, size, cells):
    """Smooth-ish noise in [0,1] by bilinear upsampling a small random grid."""
    g = rng.random((cells + 1, cells + 1))
    xs = np.linspace(0, cells, size, endpoint=False)
    x0 = np.floor(xs).astype(int)
    t = xs - x0
    t = t * t * (3 - 2 * t)
    a = g[x0][:, x0] * (1 - t)[None, :] + g[x0][:, x0 + 1] * t[None, :]
    b = g[x0 + 1][:, x0] * (1 - t)[None, :] + g[x0 + 1][:, x0 + 1] * t[None, :]
    return a * (1 - t)[:, None] + b * t[:, None]


def palette(art):
    return [hex_rgb(art.get(k, art["base"])) for k in ("speck", "dark", "base", "light")]


# ------------------------------------------------------------------------------------------------- styles
# Each style returns (rgb float array HxWx3, height HxW in 0..1).

def style_earth(art, rng):
    pal = palette(art)
    n = value_noise(rng, CELL, 4) * 0.6 + rng.random((CELL, CELL)) * 0.4
    idx = np.clip((n * 3.2).astype(int), 0, 3)
    idx = np.where(idx == 0, 1, idx)
    rgb = np.array(pal)[idx]
    height = 0.55 + 0.25 * n
    for _ in range(rng.integers(2, 5)):            # dark specks
        y, x = rng.integers(0, CELL, 2)
        rgb[y, x] = pal[0]
        height[y, x] -= 0.15
    for _ in range(rng.integers(1, 3)):            # pebbles
        y, x = rng.integers(1, CELL - 2, 2)
        rgb[y:y + 2, x:x + 2] = pal[3]
        rgb[y + 1, x + 1] = pal[2]
        height[y:y + 2, x:x + 2] += 0.3
    return rgb, height


def style_stone(art, rng):
    """Mostly base tone with soft mottling, a couple of hairline cracks and a few chips: calm enough that
    large rock faces don't read as a checkerboard, but with height detail for the normal map."""
    pal = palette(art)
    mottle = value_noise(rng, CELL, 3)
    grain = rng.random((CELL, CELL))
    v = 0.7 * mottle + 0.3 * grain
    idx = np.where(v > 0.72, 3, np.where(v < 0.22, 1, 2))
    rgb = np.array(pal)[idx]
    height = 0.55 + 0.3 * mottle + 0.06 * grain
    for _ in range(rng.integers(1, 3)):              # hairline cracks: short random walks
        y, x = rng.integers(2, CELL - 2, 2)
        dx = rng.choice([-1, 1])
        for _ in range(rng.integers(3, 7)):
            rgb[y, x] = pal[1] * 0.85
            height[y, x] = 0.2
            if 0 <= y + 1 < CELL:
                rgb[y + 1, x] = rgb[y + 1, x] * 0.9 + pal[3] * 0.1  # lit lip under the crack
            x = int(np.clip(x + dx, 0, CELL - 1))
            y = int(np.clip(y + rng.integers(-1, 2), 0, CELL - 1))
    for _ in range(rng.integers(2, 4)):              # chips
        y, x = rng.integers(0, CELL, 2)
        rgb[y, x] = pal[0]
        height[y, x] -= 0.2
    return rgb, height


def style_sand(art, rng):
    pal = palette(art)
    n = rng.random((CELL, CELL))
    ripple = 0.5 + 0.5 * np.sin((np.arange(CELL)[:, None] * 0.9 + np.arange(CELL)[None, :] * 0.25) + rng.random() * 6)
    v = 0.55 * n + 0.45 * ripple
    idx = np.where(v > 0.78, 3, np.where(v < 0.2, 1, 2))
    rgb = np.array(pal)[idx]
    for _ in range(rng.integers(1, 3)):
        y, x = rng.integers(0, CELL, 2)
        rgb[y, x] = pal[0]
    height = 0.5 + 0.2 * ripple + 0.1 * n
    return rgb, height


def style_planks(art, rng):
    pal = palette(art)
    rgb = np.zeros((CELL, CELL, 3))
    height = np.zeros((CELL, CELL))
    for row in range(4):
        y0 = row * 4
        tone = pal[2] if row % 2 == 0 else (pal[2] * 0.6 + pal[3] * 0.4)
        grain = rng.random((4, CELL))
        rgb[y0:y0 + 4] = tone[None, None, :] * (0.92 + 0.12 * grain[..., None])
        rgb[y0 + 3] = pal[1]                          # seam under each board
        height[y0:y0 + 4] = 0.75
        height[y0 + 3] = 0.25
        cut = int(rng.integers(3, 13))                # board end
        rgb[y0:y0 + 3, cut] = pal[1]
        height[y0:y0 + 3, cut] = 0.3
        nail = int(rng.integers(1, 15))
        rgb[y0 + 1, nail] = pal[0]
    return rgb, height


def style_bricks(art, rng):
    pal = palette(art)
    rgb = np.zeros((CELL, CELL, 3))
    height = np.zeros((CELL, CELL))
    for row in range(4):
        y0 = row * 4
        off = 0 if row % 2 == 0 else 4
        for bx in range(-1, 3):
            x0 = bx * 8 + off
            tone = pal[2] * (0.9 + 0.2 * rng.random())
            for y in range(y0, y0 + 3):
                for x in range(max(0, x0), min(CELL, x0 + 7)):
                    rgb[y, x] = tone
                    height[y, x] = 0.8
            if 0 <= x0 + 7 < CELL:
                rgb[y0:y0 + 3, x0 + 7] = pal[0]
            if 0 <= x0 < CELL:
                rgb[y0, max(0, x0):min(CELL, x0 + 7)] = pal[3]       # top highlight row on each brick
        rgb[y0 + 3] = pal[0]                                         # mortar
        height[y0 + 3] = 0.2
    return rgb, height


def style_gravel(art, rng):
    pal = palette(art)
    rgb = np.zeros((CELL, CELL, 3))
    height = np.full((CELL, CELL), 0.25)
    rgb[...] = pal[1] * 0.85
    for _ in range(14):                              # pebbles of 2-3 px
        r = int(rng.integers(1, 3))
        cy, cx = rng.integers(0, CELL, 2)
        tone = pal[int(rng.integers(1, 4))]
        for dy in range(-r, r + 1):
            for dx in range(-r, r + 1):
                if dx * dx + dy * dy > r * r + 1:
                    continue
                y, x = (cy + dy) % CELL, (cx + dx) % CELL
                rgb[y, x] = tone * (1.08 if dy < 0 else 0.92)
                height[y, x] = 0.6 + 0.3 * (1 - (dx * dx + dy * dy) / (r * r + 1))
    return rgb, height


def style_obsidian(art, rng):
    pal = palette(art)
    n = value_noise(rng, CELL, 3)
    rgb = np.array(pal)[np.where(n > 0.65, 3, np.where(n < 0.3, 1, 2))]
    height = 0.5 + 0.35 * n
    for _ in range(rng.integers(2, 4)):              # glassy glints
        y, x = rng.integers(1, CELL - 1, 2)
        rgb[y, x] = pal[0]
        rgb[y - 1, x] = pal[3] * 1.15
        height[y, x] = 0.9
    for _ in range(rng.integers(1, 3)):              # conchoidal facets: diagonal light lines
        y, x = rng.integers(2, CELL - 4, 2)
        for i in range(3):
            rgb[y + i, x + i] = pal[3]
            height[y + i, x + i] = 0.75
    return np.clip(rgb, 0, 255), height


def style_grass(art, rng):
    return style_earth(art, rng)   # the grass cap is applied per mask in terrain_frame()


def style_ore(art, rng):
    rgb, height = style_stone(art, rng)
    ore = [hex_rgb(art[k]) for k in ("oreDark", "ore", "oreLight")]
    for _ in range(rng.integers(2, 4)):
        cy, cx = rng.integers(2, CELL - 3, 2)
        for dy in range(-1, 2):
            for dx in range(-1, 2):
                if rng.random() < 0.75:
                    y, x = cy + dy, cx + dx
                    rgb[y, x] = ore[1]
                    height[y, x] = 0.95
        rgb[cy - 1, cx - 1] = ore[2]
        rgb[cy + 1, cx + 1] = ore[0]
    return rgb, height


def style_bark(art, rng):
    pal = palette(art)
    rgb = np.zeros((CELL, CELL, 3))
    height = np.zeros((CELL, CELL))
    alpha = np.zeros((CELL, CELL))
    x0, x1 = 3, 13                                    # trunk is 10 px wide, centred
    for x in range(x0, x1):
        stripe = (x * 7 + int(rng.integers(0, 3))) % 4
        tone = pal[2] if stripe else pal[1]
        for y in range(CELL):
            rgb[y, x] = tone * (0.9 + 0.15 * rng.random())
            alpha[y, x] = 1
            height[y, x] = 0.6 + 0.3 * np.sin((x - x0) / (x1 - x0) * np.pi)
    for _ in range(rng.integers(1, 3)):              # knots
        y, x = rng.integers(2, CELL - 3), rng.integers(x0 + 2, x1 - 2)
        rgb[y:y + 2, x:x + 2] = pal[0]
        height[y:y + 2, x:x + 2] = 0.3
    rgb[:, x0] = pal[1] * 0.8
    rgb[:, x1 - 1] = pal[1] * 0.7
    return rgb, height, alpha


def style_platform(art, rng):
    pal = palette(art)
    rgb = np.zeros((CELL, CELL, 3))
    height = np.zeros((CELL, CELL))
    alpha = np.zeros((CELL, CELL))
    for y in range(0, 5):                            # a thick board at the top of the tile
        for x in range(CELL):
            rgb[y, x] = (pal[3] if y == 0 else pal[2] if y < 4 else pal[1]) * (0.92 + 0.1 * rng.random())
            alpha[y, x] = 1
            height[y, x] = 0.8 if 0 < y < 4 else 0.4
    for x in (2, 13):                                # brackets
        for y in range(5, 9):
            rgb[y, x] = pal[1]
            alpha[y, x] = 1
            height[y, x] = 0.5
    rgb[2, int(rng.integers(3, 12))] = pal[0]
    return rgb, height, alpha


def style_grass_decor(art, rng):
    top = [hex_rgb(art[k]) for k in ("topDark", "top", "topLight")]
    rgb = np.zeros((CELL, CELL, 3))
    height = np.zeros((CELL, CELL))
    alpha = np.zeros((CELL, CELL))
    for _ in range(rng.integers(4, 8)):
        x = int(rng.integers(1, CELL - 1))
        h = int(rng.integers(3, 9))
        lean = rng.choice([-1, 0, 0, 1])
        for i in range(h):
            y = CELL - 1 - i
            xx = int(np.clip(x + (lean if i > h // 2 else 0), 0, CELL - 1))
            rgb[y, xx] = top[2] if i == h - 1 else top[1] if i > 1 else top[0]
            alpha[y, xx] = 1
            height[y, xx] = 0.5 + 0.05 * i
    if rng.random() < 0.4:                           # a little flower
        x = int(rng.integers(3, 13))
        y = CELL - 1 - int(rng.integers(5, 8))
        c = hex_rgb(rng.choice(["#f2e266", "#e88ab0", "#f4f0e8", "#9ab8f0"]))
        for dx, dy in ((0, 0), (1, 0), (-1, 0), (0, 1), (0, -1)):
            rgb[y + dy, x + dx] = c if (dx, dy) != (0, 0) else hex_rgb("#f2c844")
            alpha[y + dy, x + dx] = 1
            height[y + dy, x + dx] = 0.8
    return rgb, height, alpha


def style_glowcap(art, rng):
    cap, capl, stem = hex_rgb(art["cap"]), hex_rgb(art["capLight"]), hex_rgb(art["stem"])
    rgb = np.zeros((CELL, CELL, 3))
    height = np.zeros((CELL, CELL))
    alpha = np.zeros((CELL, CELL))
    for _ in range(int(rng.integers(1, 3))):
        x = int(rng.integers(4, 12))
        h = int(rng.integers(4, 9))
        w = int(rng.integers(2, 4))
        for y in range(CELL - h, CELL):                       # stem
            rgb[y, x] = stem
            alpha[y, x] = 1
            height[y, x] = 0.6
        top = CELL - h
        for dy in range(-2, 1):                               # cap
            for dx in range(-w, w + 1):
                if abs(dx) > w - (1 if dy == -2 else 0):
                    continue
                y, xx = top + dy, x + dx
                if 0 <= y < CELL and 0 <= xx < CELL:
                    rgb[y, xx] = capl if dy == -2 or (dy == -1 and abs(dx) < 2) else cap
                    alpha[y, xx] = 1
                    height[y, xx] = 0.9 - 0.1 * abs(dx)
    return rgb, height, alpha


def style_ice(art, rng):
    """Clear blue with long diagonal glints and a few trapped bubbles; smooth height so it reads glassy."""
    pal = palette(art)
    yy, xx = np.mgrid[0:CELL, 0:CELL]
    depth = value_noise(rng, CELL, 2)
    idx = np.where(depth > 0.62, 3, np.where(depth < 0.25, 1, 2))
    rgb = np.array(pal)[idx] * 0.85 + np.array(pal)[2] * 0.15
    height = 0.7 + 0.1 * depth
    for _ in range(rng.integers(1, 3)):              # glints: thin bright diagonals
        c = int(rng.integers(-8, 24))
        length = int(rng.integers(4, 9))
        start = int(rng.integers(0, CELL - length))
        for k in range(length):
            y, x = start + k, c - (start + k)
            if 0 <= x < CELL:
                rgb[y, x] = pal[3]
                height[y, x] = 0.85
    for _ in range(rng.integers(1, 4)):              # bubbles
        y, x = rng.integers(1, CELL - 1, 2)
        rgb[y, x] = pal[3] * 0.7 + pal[2] * 0.3
        rgb[y + 1, x] = pal[1]
    for _ in range(rng.integers(0, 2)):              # a crack
        y, x = rng.integers(2, CELL - 2, 2)
        for _ in range(rng.integers(3, 6)):
            rgb[y, x] = pal[0]
            height[y, x] = 0.4
            x = int(np.clip(x + rng.choice([-1, 1]), 0, CELL - 1))
            y = int(np.clip(y + 1, 0, CELL - 1))
    return rgb, height


def style_strata(art, rng):
    """Sandstone: wavy horizontal bands of light and dark with a soft grain."""
    pal = palette(art)
    yy, xx = np.mgrid[0:CELL, 0:CELL]
    phase = rng.random() * 6.28
    wave = yy + 1.2 * np.sin(xx * 0.35 + phase)
    band = (np.floor(wave / 4).astype(int) + int(rng.integers(0, 4))) % 4
    tone = np.array([pal[2], pal[3], pal[2], pal[1]])[band]
    grain = rng.random((CELL, CELL))
    rgb = tone * (0.93 + 0.1 * grain[..., None])
    height = 0.55 + 0.08 * band + 0.05 * grain
    edge = (np.floor(wave).astype(int) % 4) == 3
    rgb[edge] = rgb[edge] * 0.9
    height[edge] -= 0.12
    for _ in range(rng.integers(1, 3)):
        y, x = rng.integers(0, CELL, 2)
        rgb[y, x] = pal[0]
    return rgb, height


def style_cactus(art, rng):
    """A ribbed green column with pale spines; arms on some variants."""
    pal = palette(art)
    rgb = np.zeros((CELL, CELL, 3))
    height = np.zeros((CELL, CELL))
    alpha = np.zeros((CELL, CELL))
    x0, x1 = 4, 12
    for x in range(x0, x1):
        u = (x - x0 + 0.5) / (x1 - x0)
        rib = (x - x0) % 3 == 1
        tone = pal[3] if rib else pal[2] if 0.2 < u < 0.8 else pal[1]
        for y in range(CELL):
            rgb[y, x] = tone * (0.94 + 0.08 * rng.random())
            alpha[y, x] = 1
            height[y, x] = 0.5 + 0.4 * np.sin(u * np.pi) + (0.08 if rib else 0)
    arm = rng.random()
    if arm < 0.5:
        side = 1 if arm < 0.25 else -1
        ay = int(rng.integers(5, 9))
        xs = range(x1, CELL - 1) if side > 0 else range(1, x0)
        for x in xs:
            for y in range(ay, ay + 4):
                rgb[y, x] = pal[2]
                alpha[y, x] = 1
                height[y, x] = 0.7
        tip = CELL - 3 if side > 0 else 1
        for y in range(ay - 4, ay):
            for x in (tip, tip + 1):
                rgb[y, x] = pal[2] if x == tip else pal[1]
                alpha[y, x] = 1
                height[y, x] = 0.7
    spine = hex_rgb(art.get("speck", "#efe6b8"))
    for _ in range(int(rng.integers(5, 9))):
        y = int(rng.integers(0, CELL))
        x = x0 - 1 if rng.random() < 0.5 else x1
        if alpha[y, x] == 0:
            rgb[y, x] = spine
            alpha[y, x] = 1
            height[y, x] = 0.9
    rgb[:, x0] = np.where(alpha[:, x0:x0 + 1] > 0, pal[1][None, :] * 0.8, rgb[:, x0])
    return rgb, height, alpha


SHAPED = {"cactus": style_cactus, "glowcap": style_glowcap, "bark": style_bark, "platform": style_platform, "grass_decor": style_grass_decor}

STYLES = {"gravel": style_gravel, "obsidian": style_obsidian, "moss": style_stone, "earth": style_earth, "stone": style_stone, "sand": style_sand, "planks": style_planks,
          "bricks": style_bricks, "grass": style_grass, "ore": style_ore, "ice": style_ice, "strata": style_strata}


# ------------------------------------------------------------------------------------------------- terrain
def bevel(height, mask):
    """Lower the height toward open edges so normals tilt outward there."""
    yy, xx = np.mgrid[0:CELL, 0:CELL]
    dist = np.full((CELL, CELL), 99.0)
    if mask & 1: dist = np.minimum(dist, yy)
    if mask & 2: dist = np.minimum(dist, CELL - 1 - xx)
    if mask & 4: dist = np.minimum(dist, CELL - 1 - yy)
    if mask & 8: dist = np.minimum(dist, xx)
    ramp = np.clip((dist + 0.5) / 3.0, 0.15, 1.0)
    return height * ramp


def terrain_frame(art, style, rng_base, mask):
    rgb, height = STYLES[style](art, rng_base)
    rgb = rgb.copy()
    height = height.copy()
    alpha = np.ones((CELL, CELL))
    if style == "grass" and mask & 1:
        top = [hex_rgb(art[k]) for k in ("topDark", "top", "topLight")]
        r = rng_for(art.get("top"), mask)
        depth = 3 + r.integers(0, 2, CELL)
        for x in range(CELL):
            d = int(depth[x])
            for y in range(d):
                rgb[y, x] = top[2] if y == 0 and r.random() < 0.55 else top[1]
                height[y, x] = 0.85
            rgb[d, x] = top[0]
            # grass wraps down open sides
        for side, xs in ((8, range(0, 2)), (2, range(CELL - 2, CELL))):
            if mask & side:
                for x in xs:
                    for y in range(0, 6 + int(r.integers(0, 3))):
                        rgb[y, x] = top[1] if y < 5 else top[0]
    emission = np.zeros((CELL, CELL, 3))
    if style == "moss":
        top = [hex_rgb(art[k]) for k in ("topDark", "top", "topLight")]
        r = rng_for(art.get("top"), mask, "moss")
        yy, xx = np.mgrid[0:CELL, 0:CELL]
        dist = np.full((CELL, CELL), 99.0)
        if mask & 1: dist = np.minimum(dist, yy)
        if mask & 2: dist = np.minimum(dist, CELL - 1 - xx)
        if mask & 4: dist = np.minimum(dist, CELL - 1 - yy)
        if mask & 8: dist = np.minimum(dist, xx)
        depth = 2 + r.integers(0, 3, (CELL, CELL))
        moss = dist < depth
        rgb[moss] = np.array(top)[np.clip((r.random((CELL, CELL))[moss] * 3).astype(int), 0, 2)]
        height[moss] += 0.15
        emission[moss & (r.random((CELL, CELL)) < 0.25)] = np.array(top[2]) * 0.22
    if style == "ore" and art.get("glow"):
        ore = np.array([hex_rgb(art[k]) for k in ("oreDark", "ore", "oreLight")])
        hit = np.zeros((CELL, CELL), dtype=bool)
        for c in ore:
            hit |= np.all(np.abs(rgb - c) < 1.0, axis=-1)
        emission[hit] = rgb[hit] * float(art["glow"])
    # outline + rounded corners on open sides
    outline = None
    dark = hex_rgb(art.get("dark", art["base"])) * 0.55
    if mask & 1: rgb[0, :] = dark if style != "grass" else rgb[0, :] * 0.75
    if mask & 4: rgb[CELL - 1, :] = dark
    if mask & 8: rgb[:, 0] = np.where(np.arange(CELL)[:, None] < 4, rgb[:, 0] * 0.7, dark) if style == "grass" and mask & 1 else dark
    if mask & 2: rgb[:, CELL - 1] = np.where(np.arange(CELL)[:, None] < 4, rgb[:, CELL - 1] * 0.7, dark) if style == "grass" and mask & 1 else dark
    corners = [((1 | 8), (0, 0)), ((1 | 2), (0, CELL - 1)), ((4 | 8), (CELL - 1, 0)), ((4 | 2), (CELL - 1, CELL - 1))]
    for bits, (y, x) in corners:
        if mask & bits == bits:
            alpha[y, x] = 0
            ny = 1 if y == 0 else CELL - 2
            nx = 1 if x == 0 else CELL - 2
            rgb[ny, x] = dark if not (style == "grass" and y == 0) else rgb[ny, x]
            rgb[y, nx] = dark if not (style == "grass" and y == 0) else rgb[y, nx]
    height = bevel(height, mask)
    terrain_frame.last_emission = emission
    return rgb, height, alpha


def normals_from_height(height, alpha, strength=2.2):
    h = np.pad(height, 1, mode="edge")
    gx = (h[1:-1, 2:] - h[1:-1, :-2]) * 0.5
    gy = (h[:-2, 1:-1] - h[2:, 1:-1]) * 0.5       # row above minus row below = d/d(up)
    n = np.stack([-gx * strength, -gy * strength, np.ones_like(height)], axis=-1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    enc = np.clip(np.round((n * 0.5 + 0.5) * 255), 0, 255).astype(np.uint8)
    enc[alpha == 0] = (128, 128, 255)
    return enc


def to_rgba(rgb, alpha):
    out = np.zeros((CELL, CELL, 4), dtype=np.uint8)
    out[..., :3] = np.clip(np.round(rgb), 0, 255).astype(np.uint8)
    out[..., 3] = (alpha > 0).astype(np.uint8) * 255
    out[alpha == 0, :3] = 0
    return out


# ------------------------------------------------------------------------------------------------- objects
def torch_frame():
    rgb = np.zeros((CELL, CELL, 3))
    alpha = np.zeros((CELL, CELL))
    height = np.zeros((CELL, CELL))
    stick = hex_rgb("#7a5230"), hex_rgb("#5a3a20")
    for y in range(7, 15):
        for x, c in ((7, stick[0]), (8, stick[1])):
            rgb[y, x] = c
            alpha[y, x] = 1
            height[y, x] = 0.7 if x == 7 else 0.5
    flame = {(3, 7): "#fff1b0", (3, 8): "#ffe08a", (4, 6): "#ffb347", (4, 7): "#fff7d6", (4, 8): "#ffd36b", (4, 9): "#ff9a3c",
             (5, 6): "#ff8a33", (5, 7): "#ffd36b", (5, 8): "#ffc455", (5, 9): "#ff7a2e", (6, 7): "#e8642a", (6, 8): "#d9522a",
             (2, 8): "#ffe9a0"}
    for (y, x), c in flame.items():
        rgb[y, x] = hex_rgb(c)
        alpha[y, x] = 1
        height[y, x] = 0.9
    return rgb, height, alpha


def sheet_frame_cells(sheet_dir, sheet, frame, w, h):
    """Slice a frame of a generated sprite sheet (e.g. the Blender calibration sphere) into w x h cells."""
    meta = json.load(open(os.path.join(sheet_dir, sheet + ".sheet.json"), encoding="utf-8"))
    f = next(fr for fr in meta["frames"] if fr["name"] == frame)
    img = np.array(Image.open(os.path.join(sheet_dir, sheet + ".png")).convert("RGBA"))
    nrm = np.array(Image.open(os.path.join(sheet_dir, meta["normalMap"])).convert("RGB"))
    H = img.shape[0]
    top = H - f["y"] - f["h"]
    a = img[top:top + f["h"], f["x"]:f["x"] + f["w"]]
    n = nrm[top:top + f["h"], f["x"]:f["x"] + f["w"]]
    # centre the frame in w x h cells
    W, Hh = w * CELL, h * CELL
    oy, ox = (f["h"] - Hh) // 2, (f["w"] - W) // 2
    a = a[oy:oy + Hh, ox:ox + W]
    n = n[oy:oy + Hh, ox:ox + W]
    cells = []
    for cy in range(h):          # cell index = cx + cy * w, cy counted from the bottom (world up)
        for cx in range(w):
            y0 = (h - 1 - cy) * CELL
            cells.append((a[y0:y0 + CELL, cx * CELL:(cx + 1) * CELL].copy(), n[y0:y0 + CELL, cx * CELL:(cx + 1) * CELL].copy()))
    return cells


def load_object_cells(cache, name, w, h):
    """Slice a Blender object render (w*16 x h*16) into cells; cell index = cx + cy * w, cy from the bottom."""
    a = np.array(Image.open(os.path.join(cache, name + "_albedo.png")).convert("RGBA"))
    n = np.array(Image.open(os.path.join(cache, name + "_normal.png")).convert("RGBA"))
    e = np.array(Image.open(os.path.join(cache, name + "_emission.png")).convert("RGB"))
    if a.shape[:2] != (h * CELL, w * CELL):
        raise SystemExit(f"[gen_atlas] object {name}: render is {a.shape[1]}x{a.shape[0]}, expected {w * CELL}x{h * CELL}")
    opaque = a[..., 3] > 0
    a[..., 3] = np.where(opaque, 255, 0)
    a[~opaque, :3] = 0
    n[~opaque, :3] = (128, 128, 255)
    cells = []
    for cy in range(h):
        for cx in range(w):
            y0 = (h - 1 - cy) * CELL
            sl = (slice(y0, y0 + CELL), slice(cx * CELL, (cx + 1) * CELL))
            cells.append((a[sl].copy(), n[sl][..., :3].copy(), e[sl].copy()))
    return cells


def crack_frames():
    frames = []
    rng = rng_for("cracks")
    lines = []
    for stage in range(4):
        img = np.zeros((CELL, CELL, 4), dtype=np.uint8)
        for _ in range(2):
            y, x = rng.integers(3, 13, 2)
            dx, dy = rng.choice([-1, 1]), rng.choice([-1, 0, 1])
            seg = []
            for _ in range(3 + stage * 2):
                seg.append((y, x))
                y = int(np.clip(y + dy + rng.integers(-1, 2), 0, CELL - 1))
                x = int(np.clip(x + dx, 0, CELL - 1))
            lines.append(seg)
        for seg in lines:
            for y, x in seg:
                img[y, x] = (20, 14, 10, 210)
        frames.append(img)
    return frames


# ------------------------------------------------------------------------------------------------- packing
class Atlas:
    def __init__(self):
        self.albedo = []
        self.normal = []
        self.emission = []

    def add(self, rgba, nrm, emission=None):
        self.albedo.append(rgba)
        self.normal.append(nrm)
        self.emission.append(np.zeros((CELL, CELL, 3), dtype=np.uint8) if emission is None else emission)
        return len(self.albedo) - 1

    def render(self):
        n = len(self.albedo)
        rows = int(math.ceil(n / COLS))
        H = max(SLOT, 1 << int(math.ceil(math.log2(rows * SLOT))))
        A = np.zeros((H, ATLAS_W, 4), dtype=np.uint8)
        N = np.zeros((H, ATLAS_W, 3), dtype=np.uint8)
        N[...] = (128, 128, 255)
        E = np.zeros((H, ATLAS_W, 3), dtype=np.uint8)
        frames = []
        for i, (a, nn, e) in enumerate(zip(self.albedo, self.normal, self.emission)):
            sx, sy = (i % COLS) * SLOT, (i // COLS) * SLOT
            A[sy:sy + SLOT, sx:sx + SLOT] = np.pad(a, ((1, 1), (1, 1), (0, 0)), mode="edge")
            N[sy:sy + SLOT, sx:sx + SLOT] = np.pad(nn, ((1, 1), (1, 1), (0, 0)), mode="edge")
            E[sy:sy + SLOT, sx:sx + SLOT] = np.pad(e, ((1, 1), (1, 1), (0, 0)), mode="edge")
            frames.append([sx + 1, sy + 1])
        return A, N, E, frames


def add_outline_rgba(a):
    """1 px dark outline around an icon's silhouette (pixel-art readability on any background)."""
    opaque = a[..., 3] > 0
    ring = np.zeros_like(opaque)
    for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
        ring |= np.roll(np.roll(opaque, dy, axis=0), dx, axis=1)
    ring &= ~opaque
    a[ring] = (24, 18, 16, 255)


def write_sheet(out_dir, name, frames_rgba, names, animations=None, normal=None):
    cols = min(16, len(frames_rgba))
    rows = int(math.ceil(len(frames_rgba) / cols))
    img = np.zeros((rows * CELL, cols * CELL, 4), dtype=np.uint8)
    rects = []
    for i, f in enumerate(frames_rgba):
        c, r = i % cols, i // cols
        img[r * CELL:(r + 1) * CELL, c * CELL:(c + 1) * CELL] = f
        rects.append({"name": names[i], "x": c * CELL, "y": (rows - 1 - r) * CELL, "w": CELL, "h": CELL})
    Image.fromarray(img, "RGBA").save(os.path.join(out_dir, name + ".png"), optimize=False, compress_level=9)
    if normal is not None:
        nimg = np.zeros((rows * CELL, cols * CELL, 3), dtype=np.uint8)
        nimg[...] = (128, 128, 255)
        for i, f in enumerate(normal):
            c, r = i % cols, i // cols
            nimg[r * CELL:(r + 1) * CELL, c * CELL:(c + 1) * CELL] = f
        Image.fromarray(nimg, "RGB").save(os.path.join(out_dir, name + "_n.png"), optimize=False, compress_level=9)
    sidecar = {
        "version": 1, "name": name, "frameWidth": CELL, "frameHeight": CELL, "columns": cols, "rows": rows,
        "pixelsPerUnit": 16, "pivot": [0.5, 0.5], "meshType": "FullRect",
        "normalMap": (name + "_n.png") if normal is not None else "",
        "animations": animations or [{"name": "all", "fps": 1, "loop": False, "frames": list(range(len(frames_rgba)))}],
        "frames": rects, "source": "Tools/art/gen_atlas.py",
    }
    with open(os.path.join(out_dir, name + ".sheet.json"), "w", encoding="utf-8", newline="\n") as fh:
        json.dump(sidecar, fh, indent=2)
        fh.write("\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--content", required=True)
    ap.add_argument("--sprites", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--objects", default="Tools/.cache/objects")
    ap.add_argument("--icons", default="Tools/.cache/icons")
    args = ap.parse_args()

    tiles = json.load(open(os.path.join(args.content, "tiles.json"), encoding="utf-8"))["tiles"]
    walls = json.load(open(os.path.join(args.content, "walls.json"), encoding="utf-8"))["walls"]
    items = json.load(open(os.path.join(args.content, "items.json"), encoding="utf-8"))["items"]

    atlas = Atlas()
    layout = {"tiles": {}, "walls": {}}
    icons, icon_normals, icon_names = [], [], []

    for t in sorted(tiles, key=lambda t: t["id"]):
        art = t.get("art", {})
        style = art.get("style", "earth")
        kind = t.get("kind", "Terrain")
        if kind in ("Terrain", "Platform") and style in STYLES:
            start = None
            for mask in range(16):
                for v in range(VARIANTS):
                    rgb, height, alpha = terrain_frame(art, style, rng_for(t["id"], v), mask)
                    em = np.clip(terrain_frame.last_emission * alpha[..., None], 0, 255).astype(np.uint8)
                    i = atlas.add(to_rgba(rgb, alpha), normals_from_height(height, alpha), em)
                    start = i if start is None else start
            layout["tiles"][t["id"]] = {"layout": "terrain", "start": start, "count": 16 * VARIANTS, "variants": VARIANTS}
            rgb, height, alpha = terrain_frame(art, style, rng_for(t["id"], 0), 15)
            icons.append(to_rgba(rgb, alpha)); icon_normals.append(normals_from_height(height, alpha))
            icon_names.append(t["id"])
        elif style == "torch":
            rgb, height, alpha = torch_frame()
            emission = np.zeros((CELL, CELL, 3), dtype=np.uint8)
            emission[:7] = np.clip(rgb[:7] * alpha[:7, :, None] * 0.9, 0, 255).astype(np.uint8)   # the flame glows
            i = atlas.add(to_rgba(rgb, alpha), normals_from_height(height, alpha, 1.5), emission)
            layout["tiles"][t["id"]] = {"layout": "single", "start": i, "count": 1}
            icons.append(to_rgba(rgb, alpha)); icon_normals.append(normals_from_height(height, alpha, 1.5))
            icon_names.append(t["id"])
        elif style in SHAPED:
            start = None
            count = 1 if style == "platform" else VARIANTS
            for v in range(count):
                rgb, height, alpha = SHAPED[style](art, rng_for(t["id"], v))
                em = None
                if style == "glowcap":   # the whole mushroom glows; caps brightest
                    em = np.clip(rgb * alpha[..., None] * 0.75, 0, 255).astype(np.uint8)
                i = atlas.add(to_rgba(rgb, alpha), normals_from_height(height, alpha, 1.8), em)
                start = i if start is None else start
            layout["tiles"][t["id"]] = {"layout": "variants", "start": start, "count": count}
            if style in ("platform", "glowcap", "cactus"):
                icons.append(to_rgba(rgb, alpha)); icon_normals.append(normals_from_height(height, alpha, 1.8))
                icon_names.append(t["id"])
        elif style == "object":
            w, h = t.get("size", [1, 1])
            names = [art["object"]] + ([art["openObject"]] if art.get("openObject") else [])
            start = None
            for obj in names:
                for a, n, e in load_object_cells(args.objects, obj, w, h):
                    i = atlas.add(a, n, e)
                    start = i if start is None else start
            layout["tiles"][t["id"]] = {"layout": "object", "start": start, "count": w * h * len(names), "w": w, "h": h}
        elif style == "sheet":
            w, h = t.get("size", [1, 1])
            cells = sheet_frame_cells(args.sprites, art["sheet"], art["frame"], w, h)
            start = None
            for a, n in cells:
                i = atlas.add(a, n)
                start = i if start is None else start
            layout["tiles"][t["id"]] = {"layout": "object", "start": start, "count": w * h, "w": w, "h": h}
        else:
            raise SystemExit(f"[gen_atlas] tile {t['id']}: no generator for kind {kind} / style {style}")

    for w in sorted(walls, key=lambda w: w["id"]):
        art = w["art"]
        start = None
        for v in range(VARIANTS):
            rgb, height = STYLES[art["style"]](art, rng_for(w["id"], v))
            alpha = np.ones((CELL, CELL))
            i = atlas.add(to_rgba(rgb, alpha), normals_from_height(height * 0.6, alpha, 1.6))
            start = i if start is None else start
        layout["walls"][w["id"]] = {"layout": "variants", "start": start, "count": VARIANTS}
        rgb, height = STYLES[art["style"]](art, rng_for(w["id"], 0))
        icon = to_rgba(rgb, np.ones((CELL, CELL)))
        icon[0, :, :3] = icon[-1, :, :3] = icon[:, 0, :3] = icon[:, -1, :3] = (20, 16, 14)
        icons.append(icon); icon_normals.append(normals_from_height(height * 0.6, np.ones((CELL, CELL)), 1.6))
        icon_names.append(w["id"])

    # liquids: frame 0 = body, frame 1 = surface (top rows lighter); lava emits
    layout["liquids"] = {}
    for name, (deep, mid, light, glow) in {"water": ("#1f4f9a", "#2f6fc8", "#8fd0ff", 0.0),
                                           "lava": ("#a8240c", "#f0641e", "#ffd25a", 1.0)}.items():
        r = rng_for("liquid", name)
        start = None
        for surface in (False, True):
            n = value_noise(r, CELL, 4)
            pal = np.array([hex_rgb(deep), hex_rgb(mid), hex_rgb(light)])
            idx = np.clip((n * 2.4).astype(int), 0, 2)
            idx[idx == 2] = 1
            rgb = pal[idx]
            if name == "lava":
                crust = value_noise(r, CELL, 6) > 0.72
                rgb[crust] = hex_rgb("#4a140a")
            if surface:
                rgb[0:2] = pal[2]
                rgb[2] = (pal[1] + pal[2]) / 2
            alpha = np.ones((CELL, CELL))
            em = np.clip(rgb * glow * 0.85, 0, 255).astype(np.uint8) if glow > 0 else None
            if em is not None and name == "lava":
                em[crust] = 0
            height = 0.5 + 0.1 * n
            i = atlas.add(to_rgba(rgb, alpha), normals_from_height(height, alpha, 0.8), em)
            start = i if start is None else start
        layout["liquids"][name] = {"layout": "variants", "start": start, "count": 2}

    A, N, E, frames = atlas.render()
    os.makedirs(args.out, exist_ok=True)
    Image.fromarray(A, "RGBA").save(os.path.join(args.out, "tiles.png"), optimize=False, compress_level=9)
    Image.fromarray(N, "RGB").save(os.path.join(args.out, "tiles_n.png"), optimize=False, compress_level=9)
    Image.fromarray(E, "RGB").save(os.path.join(args.out, "tiles_e.png"), optimize=False, compress_level=9)
    meta = {"version": 1, "cell": CELL, "slot": SLOT, "width": A.shape[1], "height": A.shape[0],
            "tiles": layout["tiles"], "walls": layout["walls"], "liquids": layout["liquids"], "frames": frames,
            "source": "Tools/art/gen_atlas.py"}
    with open(os.path.join(args.out, "tiles.atlas.json"), "w", encoding="utf-8", newline="\n") as fh:
        json.dump(meta, fh, indent=1)
        fh.write("\n")

    # item icons: every item that places a tile/wall we drew
    by_place = {}
    for it in items:
        key = it.get("placesTile") or it.get("placesWall")
        if key:
            by_place.setdefault(key, []).append(it["id"])
        elif it["id"] in icon_names and it.get("category") == "Material":
            by_place.setdefault(it["id"], []).append(it["id"])
    sheet_frames, sheet_normals, sheet_names = [], [], []
    for icon, nrm, src in zip(icons, icon_normals, icon_names):
        for item_id in by_place.get(src, []):
            sheet_frames.append(icon); sheet_normals.append(nrm); sheet_names.append("icon_" + item_id)
    if sheet_frames:
        write_sheet(args.sprites, "icons_tiles", sheet_frames, sheet_names, normal=sheet_normals)
    # item icons rendered in Blender (tools, bars, furniture)
    item_ids = {it["id"] for it in items}
    rendered = json.load(open(os.path.join(args.icons, "manifest.json"), encoding="utf-8")) if os.path.exists(
        os.path.join(args.icons, "manifest.json")) else []
    rendered = [r for r in rendered if r in item_ids and "icon_" + r not in sheet_names]
    if rendered:
        frames_i, normals_i = [], []
        for r in rendered:
            a = np.array(Image.open(os.path.join(args.icons, r + "_albedo.png")).convert("RGBA"))
            n = np.array(Image.open(os.path.join(args.icons, r + "_normal.png")).convert("RGB"))
            opaque = a[..., 3] > 0
            a[..., 3] = np.where(opaque, 255, 0)
            a[~opaque, :3] = 0
            n[~opaque] = (128, 128, 255)
            add_outline_rgba(a)
            frames_i.append(a); normals_i.append(n)
        write_sheet(args.sprites, "icons_items", frames_i, ["icon_" + r for r in rendered], normal=normals_i)
    # held weapon sprites (Blender, in-hand size): one single-frame sheet per weapon, "held_<item>"
    held_dir = os.path.join(os.path.dirname(args.icons), "held")
    held_manifest = os.path.join(held_dir, "manifest.json")
    if os.path.exists(held_manifest):
        for item_id, px in json.load(open(held_manifest, encoding="utf-8")).items():
            a = np.array(Image.open(os.path.join(held_dir, item_id + "_albedo.png")).convert("RGBA"))
            n = np.array(Image.open(os.path.join(held_dir, item_id + "_normal.png")).convert("RGB"))
            opaque = a[..., 3] > 0
            a[..., 3] = np.where(opaque, 255, 0)
            a[~opaque, :3] = 0
            n[~opaque] = (128, 128, 255)
            add_outline_rgba(a)
            name = "held_" + item_id
            Image.fromarray(a, "RGBA").save(os.path.join(args.sprites, name + ".png"), optimize=False, compress_level=9)
            Image.fromarray(n, "RGB").save(os.path.join(args.sprites, name + "_n.png"), optimize=False, compress_level=9)
            h, w = a.shape[:2]
            sidecar = {"version": 1, "name": name, "frameWidth": w, "frameHeight": h, "columns": 1, "rows": 1, "pixelsPerUnit": 16,
                       "pivot": [0.5, 0.5], "meshType": "FullRect", "normalMap": name + "_n.png",
                       "animations": [{"name": "all", "fps": 1, "loop": False, "frames": [0]}],
                       "frames": [{"name": name, "x": 0, "y": 0, "w": w, "h": h}], "source": "Tools/art/gen_atlas.py"}
            with open(os.path.join(args.sprites, name + ".sheet.json"), "w", encoding="utf-8", newline="\n") as fh:
                json.dump(sidecar, fh, indent=2)
                fh.write("\n")
    write_sheet(args.sprites, "cracks", crack_frames(), [f"cracks_{i}" for i in range(4)],
                animations=[{"name": "crack", "fps": 1, "loop": False, "frames": [0, 1, 2, 3]}])
    print(f"[gen_atlas] {len(frames)} frames, atlas {A.shape[1]}x{A.shape[0]}, {len(layout['tiles'])} tiles, "
          f"{len(layout['walls'])} walls, {len(sheet_frames)} icons")


if __name__ == "__main__":
    main()
