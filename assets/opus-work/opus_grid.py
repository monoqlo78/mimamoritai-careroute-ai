"""Render reference crops with coordinate grid overlays for manual landmarking."""
import os
import sys

from PIL import Image, ImageDraw

REF = r"C:\Users\msoga\.copilot\workspaces\fe9aca11-79ab-4d6d-a028-c44b6544089c\attachments\09f6fd54-74b5-4ae0-90b4-b88b0e31fccf-ChatGPT Image 2026年8月9日 19_57_40.png"
OUT = os.path.dirname(os.path.abspath(__file__))


def grid(src, box, step, scale, name, minor=None):
    img = Image.open(src).convert("RGB")
    c = img.crop(box).resize(
        (int((box[2] - box[0]) * scale), int((box[3] - box[1]) * scale)), Image.LANCZOS
    )
    d = ImageDraw.Draw(c)
    x0, y0 = box[0], box[1]
    if minor:
        for X in range(int(x0 // minor) * minor, box[2] + minor, minor):
            px = (X - x0) * scale
            if 0 <= px < c.size[0]:
                d.line([(px, 0), (px, c.size[1])], fill=(255, 210, 210), width=1)
        for Y in range(int(y0 // minor) * minor, box[3] + minor, minor):
            py = (Y - y0) * scale
            if 0 <= py < c.size[1]:
                d.line([(0, py), (c.size[0], py)], fill=(255, 210, 210), width=1)
    for X in range(int(x0 // step) * step, box[2] + step, step):
        px = (X - x0) * scale
        if 0 <= px < c.size[0]:
            d.line([(px, 0), (px, c.size[1])], fill=(255, 0, 0), width=1)
            d.text((px + 2, 2), str(X), fill=(200, 0, 0))
    for Y in range(int(y0 // step) * step, box[3] + step, step):
        py = (Y - y0) * scale
        if 0 <= py < c.size[1]:
            d.line([(0, py), (c.size[0], py)], fill=(0, 0, 255), width=1)
            d.text((2, py + 2), str(Y), fill=(0, 0, 200))
    c.save(os.path.join(OUT, name))
    print("wrote", name, c.size)


if __name__ == "__main__":
    which = sys.argv[1] if len(sys.argv) > 1 else "ref"
    if which == "ref":
        grid(REF, (300, 240, 900, 780), 50, 1.3, "opus_grid_head.png", minor=10)
        grid(REF, (250, 700, 900, 1150), 50, 1.2, "opus_grid_body.png", minor=10)
        grid(REF, (400, 480, 720, 740), 20, 2.6, "opus_grid_face.png", minor=5)
    else:
        # generic: python opus_grid.py <img> <x0> <y0> <x1> <y1> <step> <scale> <out>
        _, _, p, a, b, cc, dd, st, sc, nm = sys.argv
        grid(p, (int(a), int(b), int(cc), int(dd)), int(st), float(sc), nm, minor=None)
