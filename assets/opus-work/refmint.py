import sys
from PIL import Image
import numpy as np
REF = (r"C:\Users\msoga\.copilot\workspaces\fe9aca11-79ab-4d6d-a028-c44b6544089c"
       r"\attachments\09f6fd54-74b5-4ae0-90b4-b88b0e31fccf-ChatGPT Image 2026"
       + "\u5e74" + "8" + "\u6708" + "9" + "\u65e5" + " 19_57_40.png")
im = np.asarray(Image.open(REF).convert("RGB"), dtype=np.float32) / 255.0
r, g, b = im[..., 0], im[..., 1], im[..., 2]
mint = (g > r + 0.055) & (g > 0.45) & (b > r + 0.010) & (g - b > -0.02)
for y in range(int(sys.argv[1]), int(sys.argv[2]) + 1, int(sys.argv[3])):
    row = mint[y]
    xs = np.nonzero(row[int(sys.argv[4]):int(sys.argv[5])])[0] + int(sys.argv[4])
    segs = []
    if len(xs):
        s = xs[0]; p = xs[0]
        for x in xs[1:]:
            if x - p > 6:
                if p - s > 3: segs.append((s, p))
                s = x
            p = x
        if p - s > 3: segs.append((s, p))
    print(y, " ".join("%d-%d" % t for t in segs))
