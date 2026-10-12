"""
Copies a capture run's screenshots into docs/screenshots/<milestone>/ and builds a contact sheet
(a 2x2 grid of the region of interest, upscaled with nearest-neighbour so pixels stay crisp).

  python Tools/docs_shots.py --capture Screenshots/PipelineDemo --milestone M0 --crop 300,150,980,620
"""
import argparse
import os
import shutil

from PIL import Image


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--capture", required=True)
    ap.add_argument("--milestone", required=True)
    ap.add_argument("--crop", default="", help="x0,y0,x1,y1 region for the contact sheet")
    ap.add_argument("--scale", type=int, default=1)
    args = ap.parse_args()

    out = os.path.join("docs", "screenshots", args.milestone)
    os.makedirs(out, exist_ok=True)
    shots = sorted(f for f in os.listdir(args.capture) if f.endswith(".png"))
    for f in shots + ["report.txt"]:
        src = os.path.join(args.capture, f)
        if os.path.exists(src):
            shutil.copyfile(src, os.path.join(out, f))

    tiles = []
    for f in shots[:4]:
        im = Image.open(os.path.join(args.capture, f)).convert("RGB")
        if args.crop:
            im = im.crop(tuple(int(v) for v in args.crop.split(",")))
        if args.scale > 1:
            im = im.resize((im.width * args.scale, im.height * args.scale), Image.NEAREST)
        tiles.append(im)
    if tiles:
        w, h = tiles[0].size
        sheet = Image.new("RGB", (w * 2 + 4, h * ((len(tiles) + 1) // 2) + 4 * ((len(tiles) - 1) // 2)), (255, 255, 255))
        for i, t in enumerate(tiles):
            sheet.paste(t, ((i % 2) * (w + 4), (i // 2) * (h + 4)))
        sheet.save(os.path.join(out, "contact_sheet.png"), optimize=True)
    print(f"[docs_shots] {len(shots)} shots -> {out}")


if __name__ == "__main__":
    main()
