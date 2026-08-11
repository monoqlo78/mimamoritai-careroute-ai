"""Extract character silhouette from the reference poster via background modelling."""
import json
import os

import numpy as np
from PIL import Image
from scipy import ndimage

REF = r"C:\Users\msoga\.copilot\workspaces\fe9aca11-79ab-4d6d-a028-c44b6544089c\attachments\09f6fd54-74b5-4ae0-90b4-b88b0e31fccf-ChatGPT Image 2026年8月9日 19_57_40.png"
OUT = os.path.dirname(os.path.abspath(__file__))
img = Image.open(REF).convert("RGB")
A = np.asarray(img).astype(float)
H, W = A.shape[:2]

# Sobel edge magnitude on luminance
lum = 0.299 * A[..., 0] + 0.587 * A[..., 1] + 0.114 * A[..., 2]
sm = ndimage.gaussian_filter(lum, 1.2)
gx = ndimage.sobel(sm, 1)
gy = ndimage.sobel(sm, 0)
mag = np.hypot(gx, gy)
print("edge mag pct", [round(float(np.percentile(mag, p)), 1) for p in (50, 80, 90, 95, 99)])

edge = mag > 22
Image.fromarray((np.clip(mag, 0, 120) / 120 * 255).astype(np.uint8)).save(
    os.path.join(OUT, "opus_ref_edges.png")
)

# Character mask: flood from image border through non-edge area, then invert.
free = ~ndimage.binary_dilation(edge, iterations=1)
lab, n = ndimage.label(free)
border_labels = set(lab[0, :]) | set(lab[-1, :]) | set(lab[:, 0]) | set(lab[:, -1])
border_labels.discard(0)
bg = np.isin(lab, list(border_labels))
fg = ~bg
fg = ndimage.binary_closing(fg, np.ones((7, 7)))
fg = ndimage.binary_fill_holes(fg)
lab2, n2 = ndimage.label(fg)
# component containing the face centre (555, 600)
target = lab2[600, 555]
char = lab2 == target
print("char px", int(char.sum()), "target label", target)
ys, xs = np.nonzero(char)
print("char bbox x", xs.min(), xs.max(), "y", ys.min(), ys.max())
Image.fromarray((char * 255).astype(np.uint8)).save(os.path.join(OUT, "opus_ref_silhouette.png"))

# Head-only: rows 380..760
rows = {}
for y in range(380, 780, 5):
    r = np.nonzero(char[y])[0]
    if len(r):
        rows[y] = (int(r.min()), int(r.max()))
for y in sorted(rows):
    print(f"y={y}: {rows[y][0]}..{rows[y][1]} w={rows[y][1]-rows[y][0]}")

json.dump({str(k): v for k, v in rows.items()}, open(os.path.join(OUT, "opus_ref_rows.json"), "w"))
