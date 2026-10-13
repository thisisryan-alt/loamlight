"""
Sky backdrop art for the day/night cycle: three seamless parallax silhouette layers (far crags, mid hills, near
treeline), sun, moon, a tiling star field and a few clouds. Everything is painted with numpy from a fixed seed, so
the output is byte-identical every run. Colours are the daytime look; the game tints each layer by time of day.

  python Tools/art/gen_sky.py --out Assets/Art/Generated/Sprites
"""
import argparse
import json
import math
import os

import numpy as np
from PIL import Image

W = 512   # every parallax layer tiles horizontally at this width


def hex_rgb(h):
    h = h.lstrip("#")
    return np.array([int(h[i:i + 2], 16) for i in (0, 2, 4)], dtype=np.float64)


def periodic(rng, n, terms):
    """Sum of random sines with integer frequencies: seamless over n samples."""
    x = np.arange(n) / n * 2 * math.pi
    out = np.zeros(n)
    for f, amp in terms:
        out += amp * np.sin(f * x + rng.random() * 2 * math.pi)
    return out


def silhouette(height_px, ridge, body, edge, light, rng, detail=None):
    """Fill below a ridge line; a lighter rim along the top and dithered shading downward."""
    img = np.zeros((height_px, W, 4), dtype=np.uint8)
    for x in range(W):
        top = int(np.clip(ridge[x], 1, height_px - 1))
        for y in range(top, height_px):
            d = y - top
            c = edge if d < 1 else light if d < 3 and (x + y) % 2 == 0 else body
            shade = 1.0 - min(0.18, d / height_px * 0.25)
            img[y, x, :3] = np.clip(c * shade, 0, 255)
            img[y, x, 3] = 255
    if detail:
        detail(img, ridge)
    return img


def far_crags(rng):
    h = 160
    ridge = 70 + periodic(rng, W, [(2, 22), (5, 12), (11, 6), (23, 3)])
    peaks = np.abs(periodic(rng, W, [(7, 1.0), (13, 0.5)]))
    ridge = ridge - peaks * 18
    body, edge, light = hex_rgb("#7d93b8"), hex_rgb("#c9d8ee"), hex_rgb("#93a8ca")

    def snow(img, r):
        for x in range(W):
            top = int(r[x])
            if top < 62:
                depth = int((62 - top) * 0.5) + 2
                for y in range(top, min(h, top + depth)):
                    if (x * 3 + y) % 5 or y - top < depth - 1:
                        img[y, x, :3] = hex_rgb("#eef4fb")
    return silhouette(h, ridge, body, edge, light, rng, snow)


def mid_hills(rng):
    h = 128
    ridge = 60 + periodic(rng, W, [(3, 14), (6, 8), (14, 3)])
    body, edge, light = hex_rgb("#5f8f7a"), hex_rgb("#a9d3a0"), hex_rgb("#77a68a")
    return silhouette(h, ridge, body, edge, light, rng)


def near_trees(rng):
    h = 112
    ground = 66 + periodic(rng, W, [(2, 6), (5, 4)])
    ridge = ground.copy()
    x = 0
    while x < W:                               # conifer and round-crown trees along the ridge
        width = int(rng.integers(9, 17))
        tall = int(rng.integers(16, 34))
        round_crown = rng.random() < 0.4
        for dx in range(-width, width + 1):
            xx = (x + dx) % W
            u = abs(dx) / width
            top = ground[xx] - (tall * (1 - u * u) ** 0.5 if round_crown else tall * (1 - u))
            ridge[xx] = min(ridge[xx], top)
        x += int(rng.integers(8, 18))
    body, edge, light = hex_rgb("#2f5a3e"), hex_rgb("#6a9a62"), hex_rgb("#3e6e4a")
    return silhouette(h, ridge, body, edge, light, rng)


def sun():
    s = 32
    img = np.zeros((s, s, 4), dtype=np.uint8)
    yy, xx = np.mgrid[0:s, 0:s]
    r = np.hypot(xx - 15.5, yy - 15.5)
    core = r < 9
    rim = (r >= 9) & (r < 11)
    halo = (r >= 11) & (r < 15) & ((xx + yy) % 2 == 0)
    img[core] = (255, 244, 196, 255)
    img[(r < 6.5)] = (255, 252, 232, 255)
    img[rim] = (255, 214, 120, 255)
    img[halo] = (255, 226, 150, 120)
    return img


def moon():
    s = 24
    img = np.zeros((s, s, 4), dtype=np.uint8)
    yy, xx = np.mgrid[0:s, 0:s]
    r = np.hypot(xx - 11.5, yy - 11.5)
    disc = r < 9.5
    img[disc] = (226, 232, 246, 255)
    img[disc & (r > 8.2)] = (176, 188, 214, 255)
    for cx, cy, cr in ((8, 9, 2.2), (14, 14, 2.8), (13, 6, 1.4), (7, 15, 1.2)):
        crater = np.hypot(xx - cx, yy - cy) < cr
        img[disc & crater] = (186, 196, 222, 255)
    return img


def stars(rng):
    w, h = 256, 160
    img = np.zeros((h, w, 4), dtype=np.uint8)
    for _ in range(150):
        x, y = int(rng.integers(0, w)), int(rng.integers(0, h))
        b = int(rng.integers(120, 256))
        tint = rng.choice([(255, 255, 255), (200, 220, 255), (255, 236, 200)])
        img[y, x] = (*tint, b)
        if rng.random() < 0.12:                 # a few bright crosses
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                img[(y + dy) % h, (x + dx) % w] = (*tint, b // 2)
    return img


def cloud(rng):
    w, h = 96, 40
    img = np.zeros((h, w, 4), dtype=np.uint8)
    yy, xx = np.mgrid[0:h, 0:w]
    mask = np.zeros((h, w), dtype=bool)
    shade = np.zeros((h, w))
    for _ in range(int(rng.integers(5, 9))):
        cx, cy = rng.integers(16, w - 16), rng.integers(16, h - 8)
        rx, ry = rng.integers(10, 20), rng.integers(7, 12)
        d = ((xx - cx) / rx) ** 2 + ((yy - cy) / ry) ** 2
        mask |= d < 1
        shade = np.maximum(shade, np.clip(1 - d, 0, 1) * (1 - (yy - cy + ry) / (2 * ry)))
    mask &= yy < h - 4
    img[mask] = (238, 244, 252, 235)
    under = mask & (shade < 0.25)
    img[under] = (200, 214, 232, 235)
    return img


def save_sheet(out, name, img, pivot):
    Image.fromarray(img, "RGBA").save(os.path.join(out, name + ".png"), optimize=False, compress_level=9)
    h, w = img.shape[:2]
    sidecar = {
        "version": 1, "name": name, "frameWidth": w, "frameHeight": h, "columns": 1, "rows": 1,
        "pixelsPerUnit": 16, "pivot": pivot, "meshType": "FullRect", "normalMap": "",
        "animations": [{"name": "all", "fps": 1, "loop": False, "frames": [0]}],
        "frames": [{"name": name, "x": 0, "y": 0, "w": w, "h": h}], "source": "Tools/art/gen_sky.py",
    }
    with open(os.path.join(out, name + ".sheet.json"), "w", encoding="utf-8", newline="\n") as fh:
        json.dump(sidecar, fh, indent=2)
        fh.write("\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    rng = np.random.default_rng(20261012)
    save_sheet(args.out, "sky_far", far_crags(rng), [0.5, 0.0])
    save_sheet(args.out, "sky_mid", mid_hills(rng), [0.5, 0.0])
    save_sheet(args.out, "sky_near", near_trees(rng), [0.5, 0.0])
    save_sheet(args.out, "sky_sun", sun(), [0.5, 0.5])
    save_sheet(args.out, "sky_moon", moon(), [0.5, 0.5])
    save_sheet(args.out, "sky_stars", stars(rng), [0.5, 0.5])
    for i in range(3):
        save_sheet(args.out, f"sky_cloud{i}", cloud(rng), [0.5, 0.5])
    print("[gen_sky] 9 sky sheets")


if __name__ == "__main__":
    main()
