"""Measure feature bounding boxes in the CG-only pass and the reference crop.

Both are mapped into the same 1520x1800 overlay space so deltas are directly
comparable in overlay pixels.  overlay px -> ref px : ref = crop_origin + px/2
"""
import numpy as np
from PIL import Image

REF = (r"C:\Users\msoga\.copilot\workspaces\fe9aca11-79ab-4d6d-a028-c44b6544089c"
       r"\attachments\09f6fd54-74b5-4ae0-90b4-b88b0e31fccf-ChatGPT Image "
       "2026\u5e748\u67089\u65e5 19_57_40.png")
CROP = (175, 230, 935, 1130)
W, H = 1520, 1800

ref = Image.open(REF).convert("RGB").crop(CROP).resize((W, H), Image.LANCZOS)
R = np.asarray(ref).astype(np.int16)

cg = Image.open("opus_cg_alpha.png").convert("RGBA")
if cg.size != (W, H):
    cg = cg.resize((W, H), Image.LANCZOS)
C = np.asarray(cg).astype(np.int16)
A = C[..., 3] > 40
Crgb = C[..., :3]


def bbox(mask, label):
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        print("%-22s EMPTY" % label)
        return None
    b = (int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max()))
    print("%-22s x %4d-%4d  y %4d-%4d   cx %4d cy %4d  w %3d h %3d"
          % (label, b[0], b[2], b[1], b[3],
             (b[0] + b[2]) // 2, (b[1] + b[3]) // 2, b[2] - b[0], b[3] - b[1]))
    return b


def pink(img, extra=None):
    r, g, b = img[..., 0], img[..., 1], img[..., 2]
    m = (r > 150) & (r - g > 22) & (r - b > 12) & (g > 90)
    if extra is not None:
        m &= extra
    return m


def dark(img, extra=None):
    m = img.max(axis=2) < 120
    if extra is not None:
        m &= extra
    return m


def band(y0, y1, x0=0, x1=W):
    m = np.zeros((H, W), bool)
    m[y0:y1, x0:x1] = True
    return m


print("=== ANTENNA HEART (top of frame) ===")
bbox(pink(R, band(0, 330)), "REF antenna heart")
bbox(pink(Crgb, band(0, 330) & A), "CG  antenna heart")

print()
print("=== HEAD SILHOUETTE (helmet incl. ears) ===")
bbox(band(300, 1000) & A, "CG  head+ears band")
nonwhite = (R.max(axis=2) < 245) | (np.abs(R[..., 0] - R[..., 2]) > 10)
bbox(nonwhite & band(330, 990, 330, 1250), "REF head+ears band")

print()
print("=== PHONE (dark frame, right side) ===")
bbox(dark(R, band(600, 1450, 950, 1500)), "REF phone frame")
bbox(dark(Crgb, band(600, 1450, 950, 1500) & A), "CG  phone frame")

print()
print("=== CAPE LOWER-LEFT EXTENT ===")
bbox(pink(R, band(1150, 1800, 0, 760)), "REF cape pink LL")
bbox(pink(Crgb, band(1150, 1800, 0, 760) & A), "CG  cape pink LL")

print()
print("=== FULL CG ALPHA EXTENT ===")
bbox(A, "CG  all")
