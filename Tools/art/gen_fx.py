"""
Projectile and combat-effect sprites, painted with numpy from a fixed seed (byte-identical every run):
  projectiles.png  arrow, fireball, imp_bolt (each a one-frame clip of that name; drawn along the
                   bottom-left -> top-right diagonal like item icons, the game rotates them by velocity - 45 deg)
  fx.png           spark (4 frames), puff (5 frames), ember (2 frames), slash (3 frames)

  python Tools/art/gen_fx.py --out Assets/Art/Generated/Sprites
"""
import argparse
import json
import math
import os

import numpy as np
from PIL import Image

CELL = 16


def hex_rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def blank():
    return np.zeros((CELL, CELL, 4), dtype=np.uint8)


def put(img, x, y, col, a=255):
    if 0 <= x < CELL and 0 <= y < CELL:
        img[CELL - 1 - y, x] = (*col, a)       # y up


def arrow():
    img = blank()
    shaft, fletch, head, nock = hex_rgb("#a07a4a"), hex_rgb("#e8e4dc"), hex_rgb("#b8bcc6"), hex_rgb("#6e4e2c")
    for i in range(2, 13):
        put(img, i, i, shaft)
        put(img, i + 1, i, hex_rgb("#7a5a34"))
    for (x, y) in ((13, 13), (14, 14), (12, 13), (13, 12), (14, 13), (13, 14), (15, 15)):
        put(img, x, y, head)
    put(img, 15, 14, hex_rgb("#8a8e98")); put(img, 14, 15, hex_rgb("#8a8e98"))
    for (x, y) in ((1, 3), (2, 4), (3, 1), (4, 2), (1, 2), (2, 1), (3, 4), (4, 3)):
        put(img, x, y, fletch)
    put(img, 1, 1, nock)
    return img


def orb(core, mid, rim, tail, radius=4.5, tail_len=7):
    img = blank()
    cx = cy = 10.5
    for y in range(CELL):
        for x in range(CELL):
            d = math.hypot(x - cx, y - cy)
            along = ((cx - x) + (cy - y)) / math.sqrt(2)          # distance back along the travel diagonal
            across = abs((x - cx) - (y - cy)) / math.sqrt(2)
            if d < radius * 0.45:
                put(img, x, y, core)
            elif d < radius * 0.8:
                put(img, x, y, mid)
            elif d < radius:
                put(img, x, y, rim)
            elif 0 < along < tail_len and across < (radius * 0.8) * (1 - along / tail_len):
                if (x + y) % 2 == 0 or across < 1:
                    put(img, x, y, tail, 220)
    return img


def spark(i):
    img = blank()
    c = hex_rgb("#fff4c8") if i < 2 else hex_rgb("#ffc860")
    r = [2, 5, 6, 7][i]
    for k in range(8):
        a = k * math.pi / 4
        length = r if k % 2 == 0 else r * 0.6
        for t in np.linspace(max(0, r - 3 - i), length, 6):
            put(img, int(round(7.5 + math.cos(a) * t)), int(round(7.5 + math.sin(a) * t)), c)
    if i < 2:
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                put(img, 7 + dx, 7 + dy, hex_rgb("#ffffff"))
    return img


def puff(i):
    img = blank()
    rng = np.random.default_rng(7 + i)
    r = 2.5 + i * 1.3
    alpha = int(230 - i * 40)
    for y in range(CELL):
        for x in range(CELL):
            d = math.hypot(x - 7.5, y - 7.5)
            if d < r and d > r - 2.6 - (i < 2) * 3 and rng.random() < 0.85:
                shade = 220 - int(d * 6)
                put(img, x, y, (shade, shade, shade), alpha)
    return img


def ember(i):
    img = blank()
    c = hex_rgb("#ffb040") if i == 0 else hex_rgb("#ff6a20")
    for dx, dy in ((0, 0), (1, 0), (0, 1), (1, 1)):
        put(img, 7 + dx, 7 + dy, c)
    put(img, 7, 7, hex_rgb("#fff0b0"))
    return img


def slash(i):
    """A crescent of motion streaks for heavy swings (drawn on the diagonal)."""
    img = blank()
    for t in np.linspace(-1.1, 1.1, 40):
        for w in range(3 - i):
            r = 6.5 - w
            x = 7.5 + math.cos(t + 0.785) * r
            y = 7.5 + math.sin(t + 0.785) * r
            put(img, int(round(x)), int(round(y)), hex_rgb("#ffffff"), 230 - 70 * i - 40 * w)
    return img


def write_sheet(out, name, frames, clips):
    cols = min(16, len(frames))
    rows = int(math.ceil(len(frames) / cols))
    img = np.zeros((rows * CELL, cols * CELL, 4), dtype=np.uint8)
    rects = []
    for i, (fname, f) in enumerate(frames):
        c, r = i % cols, i // cols
        img[r * CELL:(r + 1) * CELL, c * CELL:(c + 1) * CELL] = f
        rects.append({"name": fname, "x": c * CELL, "y": (rows - 1 - r) * CELL, "w": CELL, "h": CELL})
    Image.fromarray(img, "RGBA").save(os.path.join(out, name + ".png"), optimize=False, compress_level=9)
    sidecar = {
        "version": 1, "name": name, "frameWidth": CELL, "frameHeight": CELL, "columns": cols, "rows": rows,
        "pixelsPerUnit": 16, "pivot": [0.5, 0.5], "meshType": "FullRect", "normalMap": "",
        "animations": clips, "frames": rects, "source": "Tools/art/gen_fx.py",
    }
    with open(os.path.join(out, name + ".sheet.json"), "w", encoding="utf-8", newline="\n") as fh:
        json.dump(sidecar, fh, indent=2)
        fh.write("\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    proj = [("arrow", arrow()),
            ("fireball", orb(hex_rgb("#fff6d0"), hex_rgb("#ffb040"), hex_rgb("#ff6418"), hex_rgb("#d8401a"))),
            ("imp_bolt", orb(hex_rgb("#ffe8ff"), hex_rgb("#e070ff"), hex_rgb("#9a30d0"), hex_rgb("#ff7a2a"), 3.8, 6)),
            ("wisp", orb(hex_rgb("#f4f8ff"), hex_rgb("#a8c8ff"), hex_rgb("#5a78d0"), hex_rgb("#3a4a9a"), 4.2, 8))]
    write_sheet(args.out, "projectiles", proj, [{"name": n, "fps": 1, "loop": False, "frames": [i]} for i, (n, _) in enumerate(proj)])
    fx = [(f"spark_{i}", spark(i)) for i in range(4)] + [(f"puff_{i}", puff(i)) for i in range(5)] + \
         [(f"ember_{i}", ember(i)) for i in range(2)] + [(f"slash_{i}", slash(i)) for i in range(3)]
    clips = [{"name": "spark", "fps": 24, "loop": False, "frames": [0, 1, 2, 3]},
             {"name": "puff", "fps": 14, "loop": False, "frames": [4, 5, 6, 7, 8]},
             {"name": "ember", "fps": 8, "loop": True, "frames": [9, 10]},
             {"name": "slash", "fps": 20, "loop": False, "frames": [11, 12, 13]}]
    write_sheet(args.out, "fx", fx, clips)
    print(f"[gen_fx] {len(proj)} projectiles, {len(fx)} fx frames")


if __name__ == "__main__":
    main()
