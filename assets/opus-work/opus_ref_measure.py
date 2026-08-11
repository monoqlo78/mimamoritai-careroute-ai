"""Measure landmarks on the canonical reference poster image (Mimamo mascot)."""
import json
import os

import numpy as np
from PIL import Image
from scipy import ndimage

REF = r"C:\Users\msoga\.copilot\workspaces\fe9aca11-79ab-4d6d-a028-c44b6544089c\attachments\09f6fd54-74b5-4ae0-90b4-b88b0e31fccf-ChatGPT Image 2026年8月9日 19_57_40.png"
OUT = os.path.dirname(os.path.abspath(__file__))

img = Image.open(REF).convert("RGB")
W, H = img.size
A = np.asarray(img).astype(float)
print("reference size", W, H)

R, G, B = A[..., 0], A[..., 1], A[..., 2]
lum = 0.299 * R + 0.587 * G + 0.114 * B


def blobs(mask, min_px=60):
    lab, n = ndimage.label(mask)
    out = []
    for i in range(1, n + 1):
        ys, xs = np.nonzero(lab == i)
        if len(xs) < min_px:
            continue
        out.append(
            dict(
                n=int(len(xs)),
                cx=round(float(xs.mean()), 1),
                cy=round(float(ys.mean()), 1),
                x0=int(xs.min()),
                x1=int(xs.max()),
                y0=int(ys.min()),
                y1=int(ys.max()),
            )
        )
    out.sort(key=lambda d: -d["n"])
    return out


res = {"image": [W, H]}

eye_mask = (lum < 130) & (G >= R) & (G >= B - 6)
band = np.zeros_like(eye_mask)
band[430:720, 380:720] = True
eyes = blobs(eye_mask & band, min_px=800)
print("\n== EYE blobs (face band) ==")
for b in eyes[:6]:
    print(b)
res["eyes_raw"] = eyes[:6]

pink = (R > 200) & (R > G + 30) & (R > B + 15) & (G > 110)
pb = blobs(pink, min_px=400)
print("\n== PINK blobs (top 14) ==")
for b in pb[:14]:
    print(b)
res["pink_raw"] = pb[:14]

mint = (G > R + 12) & (G > 150) & (B > 150) & (B > R + 5)
mb = blobs(mint, min_px=600)
print("\n== MINT blobs (top 14) ==")
for b in mb[:14]:
    print(b)
res["mint_raw"] = mb[:14]

with open(os.path.join(OUT, "opus_ref_landmarks.json"), "w", encoding="utf-8") as f:
    json.dump(res, f, indent=1)

crops = {
    "opus_ref_crop_full.png": (200, 230, 920, 1140),
    "opus_ref_crop_head.png": (330, 250, 800, 720),
    "opus_ref_crop_face.png": (380, 430, 730, 730),
    "opus_ref_crop_eyeL.png": (400, 480, 560, 680),
    "opus_ref_crop_body.png": (300, 640, 880, 1140),
}
for name, box in crops.items():
    c = img.crop(box)
    s = 2 if c.size[0] < 400 else 1
    if s > 1:
        c = c.resize((c.size[0] * s, c.size[1] * s), Image.LANCZOS)
    c.save(os.path.join(OUT, name))
print("\nsaved crops")
