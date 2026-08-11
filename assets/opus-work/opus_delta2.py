import numpy as np
from PIL import Image

REF = (r"C:\Users\msoga\.copilot\workspaces\fe9aca11-79ab-4d6d-a028-c44b6544089c"
       r"\attachments\09f6fd54-74b5-4ae0-90b4-b88b0e31fccf-ChatGPT Image "
       "2026\u5e748\u67089\u65e5 19_57_40.png")
CROP = (175, 230, 935, 1130)
W, H = 1520, 1800

R = np.asarray(Image.open(REF).convert("RGB").crop(CROP).resize((W, H), Image.LANCZOS)).astype(np.int16)
cgi = Image.open("opus_cg_alpha.png").convert("RGBA")
print("cg size", cgi.size)
C = np.asarray(cgi.resize((W, H), Image.LANCZOS)).astype(np.int16)
A = C[..., 3] > 40


def bb(mask, label):
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        print("%-24s EMPTY" % label)
        return
    print("%-24s x %4d-%4d (cx %4d w %4d)   y %4d-%4d (cy %4d h %4d)"
          % (label, xs.min(), xs.max(), (xs.min() + xs.max()) // 2, xs.max() - xs.min(),
             ys.min(), ys.max(), (ys.min() + ys.max()) // 2, ys.max() - ys.min()))


def win(y0, y1, x0, x1):
    m = np.zeros((H, W), bool)
    m[y0:y1, x0:x1] = True
    return m


def refpx(ox, oy):
    return 175 + ox / 2.0, 230 + oy / 2.0


print("== ANTENNA HEART: CG alpha silhouette above y=250 ==")
bb(A & win(0, 250, 400, 1120), "CG antenna alpha")
# reference heart measured directly: ref px x508-622 y250-343
print("REF antenna (from measured ref px 508-622 / 250-343) -> overlay x 666-894 cx 780 w 228, y 40-226 cy 133 h 186")

print()
print("== PHONE tight window ==")
d = C[..., :3].max(axis=2) < 130
bb(d & A & win(800, 1420, 960, 1460), "CG phone dark")
rd = R.max(axis=2) < 130
bb(rd & win(800, 1420, 960, 1460), "REF phone dark")

print()
print("== CAPE: lowest CG alpha row per x on the left flare ==")
for x in (150, 250, 350, 450):
    col = np.nonzero(A[:, x])[0]
    print("   x=%4d  CG alpha rows %s" % (x, ("%d-%d" % (col.min(), col.max())) if len(col) else "none"))

print()
print("== BOOT / SOLE bottom ==")
bb(A & win(1500, 1800, 500, 1100), "CG boots band")
