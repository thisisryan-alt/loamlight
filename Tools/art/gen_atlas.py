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


STYLES = {"earth": style_earth, "stone": style_stone, "sand": style_sand, "planks": style_planks,
          "bricks": style_bricks, "grass": style_grass, "ore": style_ore}


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
                    i = atlas.add(to_rgba(rgb, alpha), normals_from_height(height, alpha))
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

    A, N, E, frames = atlas.render()
    os.makedirs(args.out, exist_ok=True)
    Image.fromarray(A, "RGBA").save(os.path.join(args.out, "tiles.png"), optimize=False, compress_level=9)
    Image.fromarray(N, "RGB").save(os.path.join(args.out, "tiles_n.png"), optimize=False, compress_level=9)
    Image.fromarray(E, "RGB").save(os.path.join(args.out, "tiles_e.png"), optimize=False, compress_level=9)
    meta = {"version": 1, "cell": CELL, "slot": SLOT, "width": A.shape[1], "height": A.shape[0],
            "tiles": layout["tiles"], "walls": layout["walls"], "frames": frames,
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
    sheet_frames, sheet_normals, sheet_names = [], [], []
    for icon, nrm, src in zip(icons, icon_normals, icon_names):
        for item_id in by_place.get(src, []):
            sheet_frames.append(icon); sheet_normals.append(nrm); sheet_names.append("icon_" + item_id)
    if sheet_frames:
        write_sheet(args.sprites, "icons_tiles", sheet_frames, sheet_names, normal=sheet_normals)
    write_sheet(args.sprites, "cracks", crack_frames(), [f"cracks_{i}" for i in range(4)],
                animations=[{"name": "crack", "fps": 1, "loop": False, "frames": [0, 1, 2, 3]}])
    print(f"[gen_atlas] {len(frames)} frames, atlas {A.shape[1]}x{A.shape[0]}, {len(layout['tiles'])} tiles, "
          f"{len(layout['walls'])} walls, {len(sheet_frames)} icons")


if __name__ == "__main__":
    main()
