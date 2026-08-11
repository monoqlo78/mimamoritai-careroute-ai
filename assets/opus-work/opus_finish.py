"""Composite the staged hero PNG and GIF frames over a soft gradient backdrop."""
import os
from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.dirname(HERE)


def backdrop(w, h):
    bg = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(bg)
    top = (243, 251, 252)
    bot = (214, 238, 240)
    for y in range(h):
        t = y / max(1, h - 1)
        d.line([(0, y), (w, y)],
               fill=tuple(int(top[i] + (bot[i] - top[i]) * t) for i in range(3)))
    return bg


def shadow(rgba, blur, dy, alpha):
    w, h = rgba.size
    sh = Image.new("L", (w, h), 0)
    sh.paste(rgba.split()[3], (0, dy))
    sh = sh.filter(ImageFilter.GaussianBlur(blur))
    sh = sh.point(lambda v: int(v * alpha))
    return sh


def compose(rgba):
    w, h = rgba.size
    bg = backdrop(w, h)
    sm = shadow(rgba, blur=max(4, w // 55), dy=max(3, h // 90), alpha=0.30)
    bg.paste(Image.new("RGB", (w, h), (150, 178, 182)), (0, 0), sm)
    bg.paste(rgba, (0, 0), rgba)
    return bg


def main():
    hero = os.path.join(HERE, "opus_hero_raw.png")
    if os.path.exists(hero):
        out = compose(Image.open(hero).convert("RGBA"))
        p = os.path.join(ASSETS, "mimamo-opus-final-front.png")
        out.save(p)
        print("FINAL", p, out.size)

    gdir = os.path.join(HERE, "gif")
    if os.path.isdir(gdir):
        files = sorted(f for f in os.listdir(gdir) if f.endswith(".png"))
        frames = [compose(Image.open(os.path.join(gdir, f)).convert("RGBA"))
                  for f in files]
        if frames:
            pal = [f.convert("P", palette=Image.ADAPTIVE, colors=200) for f in frames]
            p = os.path.join(ASSETS, "mimamo-opus-preview.gif")
            pal[0].save(p, save_all=True, append_images=pal[1:], loop=0,
                        duration=75, disposal=2, optimize=True)
            print("GIF", p, len(pal), pal[0].size)


main()
