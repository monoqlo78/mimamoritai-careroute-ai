"""Turn the raw shot renders into the exact files the web app ships.

    python opus2_publish.py [--dry]

standing   -> images/mimamo-robot-opus.png   1200x1500 RGBA, straight through
avatar     -> images/mimamo-avatar.png        512x512  RGBA, inscribed circle,
                                              opaque inside (soft mint page
                                              behind any gap), transparent out
linealert  -> images/mimamo-line-alert.png   1040x676  RGB flattened on #EFF6FF
                                              (LINE does not honour PNG alpha)
"""
import os
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
WEB = os.path.abspath(os.path.join(
    HERE, "..", "..", "src", "MimamoriTai.Web", "wwwroot", "images"))

LINE_BG = (0xEF / 255.0, 0xF6 / 255.0, 0xFF / 255.0)


def page(h, w):
    """The soft mint page the site shows behind the mascot."""
    p = np.zeros((h, w, 3), np.float32)
    gy = np.linspace(0.0, 1.0, h, dtype=np.float32)[:, None]
    p[..., 0] = 0.905 - 0.085 * gy
    p[..., 1] = 0.965 - 0.055 * gy
    p[..., 2] = 0.960 - 0.055 * gy
    return p


def load(tag):
    p = os.path.join(HERE, "shot_%s.png" % tag)
    a = np.asarray(Image.open(p).convert("RGBA")).astype(np.float32) / 255.0
    return a[..., :3], a[..., 3:4]


def save(arr, alpha, name, dry):
    if alpha is None:
        im = Image.fromarray(np.clip(arr * 255, 0, 255).astype(np.uint8), "RGB")
    else:
        rgba = np.concatenate([arr, alpha], axis=2)
        im = Image.fromarray(np.clip(rgba * 255, 0, 255).astype(np.uint8), "RGBA")
    out = os.path.join(HERE, name) if dry else os.path.join(WEB, name)
    im.save(out)
    print("WROTE %-28s %s %s" % (name, im.size, im.mode), out)


def fit_subject(rgb, alpha, height, top):
    """Rescale so the opaque silhouette is `height` tall, `top` px from the top
    and horizontally centred, keeping the canvas size unchanged."""
    h, w = alpha.shape[:2]
    ys, xs = np.nonzero(alpha[..., 0] > 0.35)
    k = float(height) / (ys.max() - ys.min() + 1)

    im = Image.fromarray(
        np.clip(np.concatenate([rgb, alpha], 2) * 255, 0, 255).astype(np.uint8),
        "RGBA").resize((max(1, int(round(w * k))), max(1, int(round(h * k)))),
                       Image.LANCZOS)
    a = np.asarray(im).astype(np.float32) / 255.0

    sy, sx = np.nonzero(a[..., 3] > 0.35)
    out = np.zeros((h, w, 4), np.float32)
    dy = top - sy.min()
    dx = int(round((w - (sx.max() - sx.min() + 1)) / 2.0)) - sx.min()
    for src, dst, d in ((a, out, None),):
        y0, x0 = max(0, dy), max(0, dx)
        sy0, sx0 = max(0, -dy), max(0, -dx)
        hh = min(src.shape[0] - sy0, h - y0)
        ww = min(src.shape[1] - sx0, w - x0)
        dst[y0:y0 + hh, x0:x0 + ww] = src[sy0:sy0 + hh, sx0:sx0 + ww]
    return out[..., :3], out[..., 3:4]


def main(dry=False):
    rgb, a = load("standing")
    save(rgb, a, "mimamo-robot-opus.png", dry)

    rgb, a = load("avatar")
    h, w = a.shape[:2]
    flat = rgb * a + page(h, w) * (1.0 - a)
    yy, xx = np.mgrid[0:h, 0:w]
    r = np.sqrt((xx - (w - 1) / 2.0) ** 2 + (yy - (h - 1) / 2.0) ** 2)
    edge = min(w, h) / 2.0
    circle = np.clip(edge - r, 0.0, 1.0)[..., None]
    save(flat, circle, "mimamo-avatar.png", dry)

    rgb, a = load("linealert")
    # LINE's frame is specified by the SUBJECT box, not by the eyes: the figure
    # is 634 px tall, horizontally centred, with a 42 px margin above it.  The
    # new cape is wider than the old one, so fit the silhouette explicitly
    # rather than trusting the eye-anchored camera alone.
    rgb, a = fit_subject(rgb, a, height=634, top=42)
    bg = np.empty_like(rgb)
    bg[...] = LINE_BG
    save(rgb * a + bg * (1.0 - a), None, "mimamo-line-alert.png", dry)


if __name__ == "__main__":
    main("--dry" in sys.argv)
