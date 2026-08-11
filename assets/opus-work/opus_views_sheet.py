"""Compose the three orthographic view renders into the staged deliverable.

Run with the *system* python (PIL is not available inside Blender):
    python opus_views_sheet.py
"""
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "mimamo-opus-three-views.png")

PANELS = [
    ("opus_view_front.png", "FRONT  (orthographic, -Y)"),
    ("opus_view_q34.png", "3/4  (rotated 38 deg)"),
    ("opus_view_side.png", "SIDE  (orthographic, +X)"),
]

PAD = 24
BAR = 58
BG = (250, 248, 245)


def font(sz):
    for n in ("segoeuib.ttf", "arialbd.ttf", "seguisb.ttf"):
        try:
            return ImageFont.truetype(n, sz)
        except OSError:
            continue
    return ImageFont.load_default()


def flatten(im):
    if im.mode != "RGBA":
        return im.convert("RGB")
    bg = Image.new("RGB", im.size, (255, 255, 255))
    bg.paste(im, (0, 0), im)
    return bg


def main():
    ims = []
    for fn, _ in PANELS:
        p = os.path.join(HERE, fn)
        if not os.path.exists(p):
            raise SystemExit("missing " + p)
        ims.append(flatten(Image.open(p)))

    w, h = ims[0].size
    W = PAD + (w + PAD) * len(ims)
    H = BAR + PAD + h + PAD + BAR
    sheet = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(sheet)
    fb, fs = font(30), font(22)

    d.text((PAD, 16), "mimamo-opus  -  three orthographic views  (1.50 m, feet at Z=0)",
           fill=(28, 32, 38), font=fb)

    for i, (im, (_, label)) in enumerate(zip(ims, PANELS)):
        x = PAD + (w + PAD) * i
        y = BAR + PAD
        d.rectangle([x - 2, y - 2, x + w + 1, y + h + 1], outline=(206, 202, 196))
        sheet.paste(im, (x, y))
        d.text((x + 6, y + h + 8), label, fill=(64, 70, 78), font=fs)

    sheet.save(os.path.abspath(OUT))
    print("saved", os.path.abspath(OUT), sheet.size)


if __name__ == "__main__":
    main()
