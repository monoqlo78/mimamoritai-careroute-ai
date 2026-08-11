"""Scan the CG alpha channel and report the silhouette half-width per row,
expressed in reference-poster pixels, so it can be compared directly with
values read off the annotated reference grids.

usage: python cgsil.py y0 y1 step
"""
import sys
from PIL import Image

RX0, RY0, RX1, RY1 = 175.0, 230.0, 935.0, 1130.0
CX = 554.5

cg = Image.open("opus2_cg.png").convert("RGBA")
W, H = cg.size
a = cg.split()[3].load()

sx = W / (RX1 - RX0)
sy = H / (RY1 - RY0)

y0, y1, step = int(sys.argv[1]), int(sys.argv[2]), int(sys.argv[3])
for yr in range(y0, y1 + 1, step):
    py = int(round((yr - RY0) * sy))
    if not (0 <= py < H):
        continue
    xs = [x for x in range(W) if a[x, py] > 140]
    if not xs:
        print("%4d  -" % yr)
        continue
    lo = RX0 + min(xs) / sx
    hi = RX0 + max(xs) / sx
    print("%4d  x %6.1f .. %6.1f   hw %6.1f" % (yr, lo, hi, (hi - lo) / 2.0))
