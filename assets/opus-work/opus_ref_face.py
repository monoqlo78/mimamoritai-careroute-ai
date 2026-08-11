"""Detailed eye + face landmark measurement on reference."""
import json
import os

import numpy as np
from PIL import Image
from scipy import ndimage

REF = r"C:\Users\msoga\.copilot\workspaces\fe9aca11-79ab-4d6d-a028-c44b6544089c\attachments\09f6fd54-74b5-4ae0-90b4-b88b0e31fccf-ChatGPT Image 2026年8月9日 19_57_40.png"
OUT = os.path.dirname(os.path.abspath(__file__))
img = Image.open(REF).convert("RGB")
A = np.asarray(img).astype(float)
R, G, B = A[..., 0], A[..., 1], A[..., 2]
lum = 0.299 * R + 0.587 * G + 0.114 * B


def bb(mask, label):
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        print(label, "EMPTY")
        return None
    d = dict(
        n=int(len(xs)),
        x0=int(xs.min()),
        x1=int(xs.max()),
        y0=int(ys.min()),
        y1=int(ys.max()),
        cx=round(float(xs.mean()), 1),
        cy=round(float(ys.mean()), 1),
    )
    d["w"] = d["x1"] - d["x0"]
    d["h"] = d["y1"] - d["y0"]
    print(label, d)
    return d


def biggest(mask, min_px=200):
    lab, n = ndimage.label(mask)
    best, bestn = None, 0
    for i in range(1, n + 1):
        c = int((lab == i).sum())
        if c > bestn:
            bestn, best = c, i
    if best is None or bestn < min_px:
        return np.zeros_like(mask)
    return lab == best


out = {}

# ---- full eye = dark outline + iris (anything notably darker than face) -----
dark = lum < 165
for name, box in (("eyeL", (410, 520, 560, 700)), ("eyeR", (585, 530, 715, 705))):
    x0, y0, x1, y1 = box
    m = np.zeros_like(dark)
    m[y0:y1, x0:x1] = dark[y0:y1, x0:x1]
    m = biggest(m, 400)
    out[name + "_full"] = bb(m, name + "_full(outline+iris)")
    # iris = teal
    iris = m & (G > R + 10)
    out[name + "_iris"] = bb(iris, name + "_iris(teal)")
    # highlight = bright white inside full eye bbox
    d = out[name + "_full"]
    hb = np.zeros_like(dark)
    hb[d["y0"]: d["y1"] + 1, d["x0"]: d["x1"] + 1] = True
    hl = hb & (lum > 235)
    lab, n = ndimage.label(hl)
    sizes = [(int((lab == i).sum()), i) for i in range(1, n + 1)]
    sizes.sort(reverse=True)
    for k, (cnt, i) in enumerate(sizes[:2]):
        out[f"{name}_hl{k}"] = bb(lab == i, f"{name}_highlight{k}")

# ---- eyebrows: near-black small arcs above eyes ----------------------------
brow = lum < 110
for name, box in (("browL", (415, 480, 570, 545)), ("browR", (600, 490, 740, 555))):
    x0, y0, x1, y1 = box
    m = np.zeros_like(brow)
    m[y0:y1, x0:x1] = brow[y0:y1, x0:x1]
    out[name] = bb(biggest(m, 100), name)

# ---- mouth: dark red/maroon interior ---------------------------------------
mouth_c = (R > G + 40) & (R > B + 30) & (lum < 190) & (lum > 40)
m = np.zeros_like(mouth_c)
m[680:790, 480:640] = mouth_c[680:790, 480:640]
out["mouth"] = bb(biggest(m, 200), "mouth")

# ---- nose: subtle; use shadow under nose -----------------------------------
# ---- blush ------------------------------------------------------------------
blush = (R > 215) & (R > G + 14) & (R > B + 6) & (G > 170)
for name, box in (("blushL", (400, 630, 480, 700)), ("blushR", (630, 640, 720, 715))):
    x0, y0, x1, y1 = box
    m = np.zeros_like(blush)
    m[y0:y1, x0:x1] = blush[y0:y1, x0:x1]
    out[name] = bb(biggest(m, 100), name)

# ---- face plate white oval: bright white bounded by helmet rim --------------
white = lum > 225
m = np.zeros_like(white)
m[430:760, 360:740] = white[430:760, 360:740]
out["faceplate_whiteish"] = bb(m, "faceplate_whitish(rough)")

# ---- head silhouette: scan rows for non-background ---------------------------
# background near head is very light blue; head white is brighter/greyer.
# use gradient/edge based: find columns where saturation low & lum>200 contiguous
print("\n== row scans of head region (find left/right extrema of helmet) ==")
sat = A.max(axis=2) - A.min(axis=2)
solid = (lum > 195) & (sat < 26)
for y in range(400, 780, 20):
    row = solid[y, 300:820]
    xs = np.nonzero(row)[0]
    if len(xs):
        # find longest run
        runs, s = [], xs[0]
        for i in range(1, len(xs)):
            if xs[i] != xs[i - 1] + 1:
                runs.append((s, xs[i - 1]))
                s = xs[i]
        runs.append((s, xs[-1]))
        runs.sort(key=lambda r: -(r[1] - r[0]))
        a0, a1 = runs[0]
        print(f"y={y}: x {a0+300}..{a1+300}  width={a1-a0}")

with open(os.path.join(OUT, "opus_ref_face.json"), "w", encoding="utf-8") as f:
    json.dump(out, f, indent=1)
