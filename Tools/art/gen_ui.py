"""
Pixel-art UI kit (Pillow): slots, 9-slice panels, buttons, tooltip frame, heart/mana/boss-bar pieces and small icons.
Written as one sprite sheet with per-frame rects and 9-slice borders (frames[].border = [left, bottom, right, top]).

  python Tools/art/gen_ui.py --out Assets/Art/Generated/Sprites
"""
import argparse
import json
import os

import numpy as np
from PIL import Image


def hexc(h, a=255):
    h = h.lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), a)


INK = hexc("#141018")
FRAME_D, FRAME, FRAME_L = hexc("#2b2433"), hexc("#4a3f55"), hexc("#7d6f8c")
FILL, FILL_L = hexc("#1d2433", 225), hexc("#26304a", 235)
GOLD, GOLD_L = hexc("#c9962e"), hexc("#f0cf6a")


def bevel_box(w, h, fill, light, dark, ink=INK, inner=None):
    img = np.zeros((h, w, 4), dtype=np.uint8)
    img[:, :] = fill
    img[0, :] = img[-1, :] = ink
    img[:, 0] = img[:, -1] = ink
    img[1, 1:-1] = light
    img[1:-1, 1] = light
    img[-2, 1:-1] = dark
    img[1:-1, -2] = dark
    for y, x in ((0, 0), (0, w - 1), (h - 1, 0), (h - 1, w - 1)):   # rounded corners
        img[y, x] = (0, 0, 0, 0)
    if inner is not None:
        img[2:-2, 2:-2] = inner
    return img


def slot(selected=False):
    if selected:
        return bevel_box(20, 20, FILL_L, GOLD_L, GOLD, inner=hexc("#33405e", 235))
    return bevel_box(20, 20, FILL, FRAME_L, FRAME_D, inner=hexc("#232b3e", 220))


def panel():
    img = bevel_box(24, 24, hexc("#1a1f2c", 250), FRAME_L, FRAME_D)
    img[2, 2:-2] = hexc("#5b4f69")
    return img


def button(hot=False):
    return bevel_box(24, 14, hexc("#3b4f7a") if hot else hexc("#2f3c5c"), hexc("#7f9ccf") if hot else hexc("#5d77a8"), hexc("#1d2640"))


def tooltip():
    return bevel_box(12, 12, hexc("#0f121a", 240), hexc("#3d3548"), hexc("#0a0a10"))


def heart(fill):
    shape = [
        "..##...##..",
        ".####.####.",
        "###########",
        "###########",
        ".#########.",
        "..#######..",
        "...#####...",
        "....###....",
        ".....#.....",
    ]
    img = np.zeros((9, 11, 4), dtype=np.uint8)
    for y, row in enumerate(shape):
        for x, c in enumerate(row):
            if c != "#":
                continue
            edge = (y == 0 or x == 0 or x == 10 or shape[y - 1][x] != "#" or (x > 0 and row[x - 1] != "#") or (x < 10 and row[x + 1] != "#")
                    or y == len(shape) - 1 or shape[y + 1][x] != "#")
            if edge:
                img[y, x] = INK
            elif fill == "full" or (fill == "half" and x <= 5):
                img[y, x] = hexc("#ff9a9a") if (y == 2 and x in (2, 3)) else hexc("#e03a4a")
            else:
                img[y, x] = hexc("#4a2630")
    return img


def star(fill):
    shape = [
        "....#....",
        "...###...",
        "#########",
        ".#######.",
        "..#####..",
        ".###.###.",
        ".##...##.",
    ]
    img = np.zeros((7, 9, 4), dtype=np.uint8)
    for y, row in enumerate(shape):
        for x, c in enumerate(row):
            if c == "#":
                img[y, x] = hexc("#5ab0ff") if fill else hexc("#22304a")
    return img


def bar(color, w=100, h=6):
    img = np.zeros((h, w, 4), dtype=np.uint8)
    img[:, :] = hexc(color)
    img[0, :] = tuple(min(255, int(c * 1.3)) for c in hexc(color)[:3]) + (255,)
    img[-1, :] = tuple(int(c * 0.6) for c in hexc(color)[:3]) + (255,)
    return img


def trash():
    img = np.zeros((16, 16, 4), dtype=np.uint8)
    body = hexc("#8a8f9c")
    for y in range(5, 15):
        for x in range(4, 12):
            img[y, x] = body if (x - 4) % 3 else hexc("#5c6170")
    img[3, 3:13] = hexc("#a8adba")
    img[2, 6:10] = hexc("#a8adba")
    img[14, 4:12] = hexc("#5c6170")
    return img


def arrow():
    img = np.zeros((7, 7, 4), dtype=np.uint8)
    pts = [(0, 3), (1, 2), (1, 3), (1, 4), (2, 1), (2, 2), (2, 3), (2, 4), (2, 5), (3, 3), (4, 3), (5, 3), (6, 3)]
    for y, x in pts:
        img[y, x] = hexc("#e8e2d0")
    return img


FRAMES = [
    ("ui_slot", slot(False), [5, 5, 5, 5]),
    ("ui_slot_hot", slot(True), [5, 5, 5, 5]),
    ("ui_panel", panel(), [6, 6, 6, 6]),
    ("ui_button", button(False), [4, 4, 4, 4]),
    ("ui_button_hot", button(True), [4, 4, 4, 4]),
    ("ui_tooltip", tooltip(), [4, 4, 4, 4]),
    ("ui_heart_full", heart("full"), None),
    ("ui_heart_half", heart("half"), None),
    ("ui_heart_empty", heart("empty"), None),
    ("ui_star_full", star(True), None),
    ("ui_star_empty", star(False), None),
    ("ui_bar_red", bar("#c8303e"), [2, 2, 2, 2]),
    ("ui_bar_back", bar("#22202a"), [2, 2, 2, 2]),
    ("ui_trash", trash(), None),
    ("ui_arrow", arrow(), None),
]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    W = 256
    x = y = 0
    rowh = 0
    placed = []
    for name, img, border in FRAMES:
        h, w = img.shape[:2]
        if x + w + 1 > W:
            x, y, rowh = 0, y + rowh + 1, 0
        placed.append((name, img, border, x, y))
        x += w + 1
        rowh = max(rowh, h)
    H = 1
    while H < y + rowh + 1:
        H *= 2
    sheet = np.zeros((H, W, 4), dtype=np.uint8)
    frames = []
    for name, img, border, px, py in placed:
        h, w = img.shape[:2]
        sheet[py:py + h, px:px + w] = img
        f = {"name": name, "x": px, "y": H - py - h, "w": w, "h": h}
        if border:
            f["border"] = border
        frames.append(f)
    os.makedirs(args.out, exist_ok=True)
    Image.fromarray(sheet, "RGBA").save(os.path.join(args.out, "ui.png"), optimize=False, compress_level=9)
    sidecar = {
        "version": 1, "name": "ui", "frameWidth": 0, "frameHeight": 0, "columns": 0, "rows": 0, "pixelsPerUnit": 16,
        "pivot": [0.5, 0.5], "meshType": "FullRect", "normalMap": "",
        "animations": [{"name": "all", "fps": 1, "loop": False, "frames": list(range(len(frames)))}],
        "frames": frames, "source": "Tools/art/gen_ui.py",
    }
    with open(os.path.join(args.out, "ui.sheet.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(sidecar, f, indent=2)
        f.write("\n")
    print(f"[gen_ui] {len(frames)} frames -> {args.out}/ui.png ({W}x{H})")


if __name__ == "__main__":
    main()
