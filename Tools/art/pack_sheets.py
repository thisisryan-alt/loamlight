"""
Packs Blender-rendered frames (Tools/.cache/frames/<sheet>/) into sprite sheets for Unity.

For every sheet it writes, into Assets/Art/Generated/Sprites/:
  <sheet>.png          albedo sheet (RGBA, sRGB)
  <sheet>_n.png        normal-map sheet (RGB = n * 0.5 + 0.5, +Y up, +Z toward the viewer)
  <sheet>.sheet.json   sidecar describing frames, pivot and animations (schema in DESIGN.md)

Optional 1 px outline: transparent pixels that touch the silhouette become a darkened copy of the
neighbouring color, with an outward-facing normal so rim light catches the edge.

The calibration sphere is checked against its analytic normals, which pins down the channel convention
end to end on the Blender side (the Unity side is checked visually in the PipelineDemo scene).
A second, normal-less copy ('calib_flat') is emitted as the control for that scene.

Output is deterministic: the same frames always give byte-identical files.
"""
import argparse
import json
import math
import os
import sys

import numpy as np
from PIL import Image

FLAT = np.array([128, 128, 255], dtype=np.uint8)


def load_rgba(path):
    with Image.open(path) as im:
        return np.array(im.convert("RGBA"), dtype=np.uint8)


def encode_normal(nx, ny, nz):
    v = np.stack([nx, ny, nz], axis=-1).astype(np.float64)
    v /= np.linalg.norm(v, axis=-1, keepdims=True)
    return np.clip(np.round((v * 0.5 + 0.5) * 255.0), 0, 255).astype(np.uint8)


def add_outline(albedo, normal):
    """Grow the silhouette by one pixel (4-neighbourhood) inside the frame."""
    a = albedo[..., 3] > 0
    h, w = a.shape
    acc_rgb = np.zeros((h, w, 3), dtype=np.float64)
    count = np.zeros((h, w), dtype=np.float64)
    out_x = np.zeros((h, w), dtype=np.float64)
    out_y = np.zeros((h, w), dtype=np.float64)   # +Y is up in the encoded normal; image rows grow downward
    for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
        # neighbour of pixel (y, x) is (y + dy, x + dx)
        shifted = np.zeros_like(a)
        src = np.zeros((h, w, 3), dtype=np.float64)
        ys = slice(max(0, -dy), h - max(0, dy))
        yd = slice(max(0, dy), h - max(0, -dy))
        xs = slice(max(0, -dx), w - max(0, dx))
        xd = slice(max(0, dx), w - max(0, -dx))
        shifted[ys, xs] = a[yd, xd]
        src[ys, xs] = albedo[yd, xd, :3]
        hit = shifted & ~a
        acc_rgb[hit] += src[hit]
        count[hit] += 1
        out_x[hit] += -dx          # pointing away from the neighbour
        out_y[hit] += dy           # neighbour below (dy = +1) means the outline faces up
    ring = count > 0
    rgb = acc_rgb[ring] / count[ring][:, None]
    albedo[ring, :3] = np.round(rgb * 0.32 + np.array([10, 8, 14]) * 0.68 * 0.5).astype(np.uint8)
    albedo[ring, 3] = 255
    normal[ring, :3] = encode_normal(out_x[ring], out_y[ring], np.full(ring.sum(), 0.8))


def check_calibration(albedo, normal, radius_px, center_px):
    """Compare the rendered sphere with analytic normals. Returns the max channel error in 8-bit units."""
    h, w = albedo.shape[:2]
    ys, xs = np.nonzero(albedo[..., 3] > 0)
    px = xs + 0.5 - center_px[0]
    py = -(ys + 0.5 - (h - center_px[1]))        # image rows down -> +Y up
    r2 = (px * px + py * py) / (radius_px * radius_px)
    inner = r2 < 0.85                              # skip the silhouette rim, where facets dominate
    nx, ny = px[inner] / radius_px, py[inner] / radius_px
    nz = np.sqrt(np.clip(1.0 - nx * nx - ny * ny, 0, 1))
    expected = encode_normal(nx, ny, nz).astype(np.int32)
    got = normal[ys[inner], xs[inner], :3].astype(np.int32)
    err = np.abs(expected - got)
    # sign checks that would catch a flipped channel even if magnitudes were off
    left = normal[ys[inner & (px < -radius_px * 0.5)], xs[inner & (px < -radius_px * 0.5)], 0]
    top = normal[ys[inner & (py > radius_px * 0.5)], xs[inner & (py > radius_px * 0.5)], 1]
    if left.mean() >= 128 or top.mean() <= 128 or got[:, 2].min() < 128:
        raise SystemExit(f"[pack_sheets] calibration FAILED: channel convention wrong "
                         f"(left R={left.mean():.0f}, top G={top.mean():.0f}, min B={got[:, 2].min()})")
    return int(err.max()), float(err.mean())


def pack(sheet_dir, out_dir, columns_max=8, emit_flat_copy=False):
    with open(os.path.join(sheet_dir, "frames.json"), encoding="utf-8") as f:
        man = json.load(f)
    name, fw, fh = man["name"], man["frameWidth"], man["frameHeight"]
    frames = man["frames"]
    n = len(frames)
    cols = min(columns_max, n)
    rows = int(math.ceil(n / cols))
    sheet_a = np.zeros((rows * fh, cols * fw, 4), dtype=np.uint8)
    sheet_n = np.zeros((rows * fh, cols * fw, 3), dtype=np.uint8)
    sheet_n[...] = FLAT

    rects = []
    for i, frame in enumerate(frames):
        albedo = load_rgba(os.path.join(sheet_dir, frame + "_albedo.png"))
        normal = load_rgba(os.path.join(sheet_dir, frame + "_normal.png"))
        if albedo.shape[:2] != (fh, fw):
            raise SystemExit(f"[pack_sheets] {frame}: expected {fw}x{fh}, got {albedo.shape[1]}x{albedo.shape[0]}")
        opaque = albedo[..., 3] > 0
        if not opaque.any():
            raise SystemExit(f"[pack_sheets] {frame}: frame is empty")
        # hard alpha only (pixel art); empty pixels get the flat normal so filtering never pulls in garbage
        albedo[..., 3] = np.where(opaque, 255, 0)
        albedo[~opaque, :3] = 0
        normal[~opaque, :3] = FLAT
        if name == "calib":
            mx, mean = check_calibration(albedo, normal, 19, man["pivotPx"])
            print(f"[pack_sheets] calibration sphere: normal error max {mx}, mean {mean:.2f} (8-bit units)")
            if mx > 6:
                raise SystemExit("[pack_sheets] calibration FAILED: normals deviate from the analytic sphere")
        if man.get("outline"):
            add_outline(albedo, normal)
        # the frame must not touch the frame border (otherwise it was clipped by the camera)
        border = np.concatenate([opaque[0], opaque[-1], opaque[:, 0], opaque[:, -1]])
        if border.any():
            print(f"[pack_sheets] WARNING {frame}: silhouette touches the frame border (clipped?)")
        c, r = i % cols, i // cols
        sheet_a[r * fh:(r + 1) * fh, c * fw:(c + 1) * fw] = albedo
        sheet_n[r * fh:(r + 1) * fh, c * fw:(c + 1) * fw] = normal[..., :3]
        # Unity sprite rects are measured from the bottom-left of the texture
        rects.append({"name": frame, "x": c * fw, "y": (rows - 1 - r) * fh, "w": fw, "h": fh})

    os.makedirs(out_dir, exist_ok=True)
    pvx, pvy = man["pivotPx"]

    def write(sheet_name, normal_map):
        Image.fromarray(sheet_a, "RGBA").save(os.path.join(out_dir, sheet_name + ".png"), optimize=False, compress_level=9)
        if normal_map:
            Image.fromarray(sheet_n, "RGB").save(os.path.join(out_dir, sheet_name + "_n.png"), optimize=False, compress_level=9)
        sidecar = {
            "version": 1,
            "name": sheet_name,
            "frameWidth": fw, "frameHeight": fh,
            "columns": cols, "rows": rows,
            "pixelsPerUnit": man["pixelsPerUnit"],
            "pivot": [round(pvx / fw, 6), round(pvy / fh, 6)],
            "meshType": man.get("meshType", "Tight"),
            "normalMap": (sheet_name + "_n.png") if normal_map else "",
            "animations": man["animations"],
            "frames": [dict(r, name=r["name"].replace(name + "_", sheet_name + "_", 1)) for r in rects],
            "source": "Tools/blender/render_sprites.py + Tools/art/pack_sheets.py",
        }
        with open(os.path.join(out_dir, sheet_name + ".sheet.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(sidecar, f, indent=2)
            f.write("\n")
        print(f"[pack_sheets] {sheet_name}: {n} frames, {cols}x{rows} grid, {cols * fw}x{rows * fh} px"
              f"{'' if normal_map else ' (no normal map)'}")

    write(name, True)
    if emit_flat_copy:
        write(name + "_flat", False)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--frames", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    sheets = sorted(d for d in os.listdir(args.frames) if os.path.isfile(os.path.join(args.frames, d, "frames.json")))
    if not sheets:
        raise SystemExit(f"[pack_sheets] no rendered sheets under {args.frames}")
    for s in sheets:
        pack(os.path.join(args.frames, s), args.out, emit_flat_copy=(s == "calib"))


if __name__ == "__main__":
    main()
