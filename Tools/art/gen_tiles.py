"""
Procedural tile art with Pillow + numpy (no Blender needed for flat tiles).

Each tile is built from a tileable height field. Albedo is a small quantized palette (pixel-art look) and
the normal map is the height gradient (wrapping at the edges so tiles repeat seamlessly).
Output uses the same sheet format as pack_sheets.py: <sheet>.png, <sheet>_n.png, <sheet>.sheet.json.

M0 ships one tile, 'stone_wall', a cobbled back wall used behind the pipeline demo.
Deterministic: every random draw comes from a seeded generator.
"""
import argparse
import json
import os

import numpy as np
from PIL import Image

T = 16  # tile size in pixels (1 tile = 1 unit at 16 PPU)


def wrapped_voronoi(points, size):
    """F1/F2 distances and nearest-cell id on a torus, so the result tiles."""
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float64) + 0.5
    d = []
    for (px, py) in points:
        dx = np.abs(xx - px)
        dy = np.abs(yy - py)
        dx = np.minimum(dx, size - dx)
        dy = np.minimum(dy, size - dy)
        d.append(np.sqrt(dx * dx + dy * dy))
    d = np.stack(d)
    order = np.argsort(d, axis=0)
    f1 = np.take_along_axis(d, order[:1], 0)[0]
    f2 = np.take_along_axis(d, order[1:2], 0)[0]
    return f1, f2, order[0]


def normals_from_height(h, strength):
    gx = (np.roll(h, -1, axis=1) - np.roll(h, 1, axis=1)) * 0.5
    gy = (np.roll(h, 1, axis=0) - np.roll(h, -1, axis=0)) * 0.5   # rows grow downward; +Y is up
    n = np.stack([-gx * strength, -gy * strength, np.ones_like(h)], axis=-1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return np.clip(np.round((n * 0.5 + 0.5) * 255), 0, 255).astype(np.uint8)


def stone_wall(rng):
    pts = [(rng.uniform(0, T), rng.uniform(0, T)) for _ in range(6)]
    f1, f2, cell = wrapped_voronoi(pts, T)
    edge = f2 - f1                                   # 0 on the mortar lines
    height = np.clip(edge / 2.2, 0, 1) ** 0.6        # domed stones, sunken mortar
    height += rng.uniform(-0.06, 0.06, size=(T, T))  # grain
    palette = np.array([[46, 44, 52], [66, 63, 72], [84, 80, 88], [101, 96, 102], [118, 112, 114]], dtype=np.float64)
    cell_tone = rng.uniform(-0.6, 0.6, size=len(pts))
    tone = 1.2 + height * 2.4 + cell_tone[cell] + rng.uniform(-0.35, 0.35, size=(T, T))
    idx = np.clip(np.round(tone), 0, len(palette) - 1).astype(np.int32)
    idx[edge < 0.9] = 0                               # mortar
    albedo = np.zeros((T, T, 4), dtype=np.uint8)
    albedo[..., :3] = palette[idx].astype(np.uint8)
    albedo[..., 3] = 255
    normal = normals_from_height(height * 3.0, 1.0)
    return albedo, normal


TILES = {"stone_wall": (stone_wall, 1201)}


def write_sheet(out_dir, name, albedo, normal):
    os.makedirs(out_dir, exist_ok=True)
    Image.fromarray(albedo, "RGBA").save(os.path.join(out_dir, name + ".png"), optimize=False, compress_level=9)
    Image.fromarray(normal, "RGB").save(os.path.join(out_dir, name + "_n.png"), optimize=False, compress_level=9)
    sidecar = {
        "version": 1, "name": name, "frameWidth": T, "frameHeight": T, "columns": 1, "rows": 1,
        "pixelsPerUnit": 16, "pivot": [0.5, 0.5], "meshType": "FullRect", "normalMap": name + "_n.png",
        "animations": [{"name": "still", "fps": 1, "loop": False, "frames": [0]}],
        "frames": [{"name": name + "_0", "x": 0, "y": 0, "w": T, "h": T}],
        "source": "Tools/art/gen_tiles.py",
    }
    with open(os.path.join(out_dir, name + ".sheet.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(sidecar, f, indent=2)
        f.write("\n")
    print(f"[gen_tiles] {name}: {T}x{T}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    for name, (fn, seed) in TILES.items():
        albedo, normal = fn(np.random.default_rng(seed))
        write_sheet(args.out, name, albedo, normal)


if __name__ == "__main__":
    main()
