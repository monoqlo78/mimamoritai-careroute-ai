"""Segment a colour class in BOTH the reference poster and the rendered CG,
reporting results in identical reference-pixel coordinates so they can be diffed.

usage: python cgscan.py <mint|pink|dark|teal> y0 y1 step x0 x1
"""
import sys
from PIL import Image

REF = ("C:\\Users\\msoga\\.copilot\\workspaces\\fe9aca11-79ab-4d6d-a028-c44b6544089c"
       "\\attachments\\09f6fd54-74b5-4ae0-90b4-b88b0e31fccf-ChatGPT Image 2026"
       + "\u5e74" + "8" + "\u6708" + "9" + "\u65e5" + " 19_57_40.png")
CG = "opus2_cg_lit.png"

# render frame in reference px
RX0, RY0, RX1, RY1 = 175.0, 230.0, 935.0, 1130.0
RW, RH = 1520, 1800


def classify(r, g, b, kind):
    mx, mn = max(r, g, b), min(r, g, b)
    sat = mx - mn
    if kind == "mint":
        return g > 120 and b > 110 and g >= r + 14 and b >= r + 6 and sat > 18 and mx > 110
    if kind == "teal":  # saturated dark teal (eyes / deep trim)
        return g >= r + 12 and b >= r + 4 and mx < 190 and sat > 22
    if kind == "pink":
        return r > 150 and r >= g + 20 and r >= b + 12 and sat > 16
    if kind == "dark":
        return mx < 95
    if kind == "dark2":
        return mx < 140
    if kind == "white":
        return mn > 243 and sat < 9
    raise SystemExit("bad kind")


def runs(px, w, y, x0, x1, kind, tox=None):
    out, s = [], None
    for x in range(x0, x1):
        if tox is not None:
            sx, sy = tox(x, y)
            if sx is None:
                ok = False
            else:
                p = px[sx, sy]
                ok = (len(p) < 4 or p[3] > 100) and classify(p[0], p[1], p[2], kind)
        else:
            p = px[x, y]
            ok = classify(p[0], p[1], p[2], kind)
        if ok and s is None:
            s = x
        elif not ok and s is not None:
            if x - s >= 3:
                out.append((s, x - 1))
            s = None
    if s is not None and x1 - s >= 3:
        out.append((s, x1 - 1))
    return out


def main():
    kind = sys.argv[1]
    y0, y1, step = int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
    x0, x1 = int(sys.argv[5]), int(sys.argv[6])

    ref = Image.open(REF).convert("RGB")
    rp = ref.load()
    cg = Image.open(CG).convert("RGBA")
    cp = cg.load()

    def tocg(xr, yr):
        u = (xr - RX0) / (RX1 - RX0) * RW
        v = (yr - RY0) / (RY1 - RY0) * RH
        if u < 0 or v < 0 or u >= RW or v >= RH:
            return None, None
        return int(u), int(v)

    print("%-5s %-42s %s" % ("y", "REF " + kind, "CG " + kind))
    for y in range(y0, y1 + 1, step):
        a = runs(rp, ref.width, y, x0, x1, kind)
        b = runs(cp, RW, y, x0, x1, kind, tox=tocg)
        fa = " ".join("%d-%d" % r for r in a)
        fb = " ".join("%d-%d" % r for r in b)
        print("%-5d %-42s %s" % (y, fa, fb))


main()
