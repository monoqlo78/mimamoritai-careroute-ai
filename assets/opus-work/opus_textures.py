"""Procedural textures for the Mimamo opus rebuild (blush, phone UI, watch UI, heart gloss)."""
import math
import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "tex")
os.makedirs(OUT, exist_ok=True)

JP_FONTS = [
    r"C:\Windows\Fonts\YuGothB.ttc",
    r"C:\Windows\Fonts\meiryob.ttc",
    r"C:\Windows\Fonts\msgothic.ttc",
    r"C:\Windows\Fonts\YuGothM.ttc",
]


def jp_font(size):
    for p in JP_FONTS:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                continue
    return ImageFont.load_default()


def blush(path, size=256):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    px = img.load()
    c = (size - 1) / 2.0
    for y in range(size):
        for x in range(size):
            dx = (x - c) / c
            dy = (y - c) / c
            r = math.hypot(dx, dy * 1.18)
            if r >= 1.0:
                continue
            a = (1.0 - r) ** 1.9
            px[x, y] = (247, 150, 158, int(255 * a * 0.92))
    img = img.filter(ImageFilter.GaussianBlur(4))
    img.save(path)
    print("wrote", path)


def phone_screen(path, w=440, h=900):
    img = Image.new("RGBA", (w, h), (238, 250, 250, 255))
    d = ImageDraw.Draw(img)
    for y in range(h):
        t = y / (h - 1)
        col = (
            int(232 + 16 * t),
            int(248 + 6 * t),
            int(248 + 6 * t),
        )
        d.line([(0, y), (w, y)], fill=col)
    # teal check disc
    cx, cy, r = w // 2, int(h * 0.30), int(w * 0.235)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(64, 190, 186, 255))
    d.line(
        [(cx - r * 0.42, cy + r * 0.03), (cx - r * 0.10, cy + r * 0.36), (cx + r * 0.48, cy - r * 0.34)],
        fill=(255, 255, 255, 255),
        width=int(r * 0.30),
        joint="curve",
    )
    f1 = jp_font(int(w * 0.155))
    f2 = jp_font(int(w * 0.093))
    t1, t2 = "見守り完了", "今日も安心です"
    for txt, fnt, yy, col in ((t1, f1, 0.505, (40, 130, 128)), (t2, f2, 0.605, (95, 155, 155))):
        bb = d.textbbox((0, 0), txt, font=fnt)
        d.text(((w - (bb[2] - bb[0])) / 2 - bb[0], h * yy), txt, font=fnt, fill=col)
    # pink heart
    hx, hy, hr = w // 2, int(h * 0.735), int(w * 0.085)
    pts = []
    for i in range(90):
        t = i / 89 * 2 * math.pi
        X = 16 * math.sin(t) ** 3
        Y = 13 * math.cos(t) - 5 * math.cos(2 * t) - 2 * math.cos(3 * t) - math.cos(4 * t)
        pts.append((hx + X / 16 * hr, hy - Y / 16 * hr))
    d.polygon(pts, fill=(246, 150, 158, 255))
    # rounded-corner alpha mask
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, w - 1, h - 1], radius=int(w * 0.11), fill=255)
    img.putalpha(mask)
    img.save(path)
    print("wrote", path)


def watch_screen(path, s=256):
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.26), fill=(46, 176, 170, 255))
    cx, cy, hr = s * 0.47, s * 0.52, s * 0.30
    pts = []
    for i in range(120):
        t = i / 119 * 2 * math.pi
        X = 16 * math.sin(t) ** 3
        Y = 13 * math.cos(t) - 5 * math.cos(2 * t) - 2 * math.cos(3 * t) - math.cos(4 * t)
        pts.append((cx + X / 16 * hr, cy - Y / 16 * hr))
    d.line(pts + [pts[0]], fill=(255, 255, 255, 255), width=int(s * 0.055), joint="curve")
    yy = cy + hr * 0.08
    d.line(
        [
            (cx - hr * 0.95, yy),
            (cx - hr * 0.35, yy),
            (cx - hr * 0.16, yy - hr * 0.45),
            (cx + hr * 0.05, yy + hr * 0.40),
            (cx + hr * 0.28, yy - hr * 0.22),
            (cx + hr * 0.45, yy),
            (cx + hr * 1.0, yy),
        ],
        fill=(255, 255, 255, 255),
        width=int(s * 0.045),
        joint="curve",
    )
    img.save(path)
    print("wrote", path)


if __name__ == "__main__":
    blush(os.path.join(OUT, "opus_blush.png"))
    phone_screen(os.path.join(OUT, "opus_phone_screen.png"))
    watch_screen(os.path.join(OUT, "opus_watch_screen.png"))
