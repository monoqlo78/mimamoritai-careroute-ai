"""Exact reference / CG overlay for the v2 rebuild.

Camera is ORTHO, ortho_scale 2.25, at z=1.05, render 1520x1800.
Sensor fit AUTO -> ortho_scale spans the LARGER dimension (height 1800),
so the world frame is 1.90 wide x 2.25 tall:
  px = (x + 0.95) / 1.90 * 1520
  py = (2.175 - z) / 2.25 * 1800
With  x = (xr-555)*0.0025 and z = (1100-yr)*0.0025 this gives exactly

  px = 2.0 * xr - 350.0        py = 2.0 * yr - 460.0

so the render frame covers reference px (175.0, 230.0) .. (935.0, 1130.0).
(Verified against the geometry bbox: object xr 250.6..859.4 / yr 250..735
 lands on render cols 151..1369 / rows 40..1010.)

    blender -b <blend> --python opus2_render.py -- pass1 [out.png]
    python opus2_render.py [tag]
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REF_IMG = (r"C:\Users\msoga\.copilot\workspaces"
           r"\fe9aca11-79ab-4d6d-a028-c44b6544089c\attachments"
           r"\09f6fd54-74b5-4ae0-90b4-b88b0e31fccf-ChatGPT Image 2026"
           + "\u5e74" + "8" + "\u6708" + "9" + "\u65e5" + " 19_57_40.png")

W, H = 1520, 1800
REF_BOX = (175.0, 230.0, 935.0, 1130.0)
SCALE = 2.0


def ref_to_px(xr, yr):
    return (SCALE * xr - 350.0, SCALE * yr - 460.0)


def blender_pass(out):
    import bpy
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = 72
    sc.cycles.use_denoising = True
    sc.render.resolution_x = W
    sc.render.resolution_y = H
    sc.render.resolution_percentage = 100
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.view_settings.view_transform = "Standard"
    sc.camera = bpy.data.objects["FrontOrthoCam"]
    sc.frame_set(1)
    sc.render.filepath = out
    bpy.ops.render.render(write_still=True)
    print("PASS1 DONE", out)


def ref_aligned():
    from PIL import Image
    ref = Image.open(REF_IMG).convert("RGB")
    box = (int(round(REF_BOX[0])), int(round(REF_BOX[1])),
           int(round(REF_BOX[2])), int(round(REF_BOX[3])))
    return ref.crop(box).resize((W, H), Image.LANCZOS)


def composite(tag="v2"):
    from PIL import Image
    import numpy as np
    cg = Image.open(os.path.join(HERE, "opus2_cg.png")).convert("RGBA")
    if cg.size != (W, H):
        cg = cg.resize((W, H), Image.LANCZOS)
    ref = ref_aligned()

    c = np.asarray(cg, dtype=np.float32) / 255.0
    r = np.asarray(ref, dtype=np.float32) / 255.0
    a = c[..., 3:4]
    rgb = c[..., :3]

    # lit CG on a soft mint page, for plain viewing
    page = np.zeros_like(r)
    gy = np.linspace(0.0, 1.0, H, dtype=np.float32)[:, None]
    page[..., 0] = 0.905 - 0.085 * gy
    page[..., 1] = 0.965 - 0.055 * gy
    page[..., 2] = 0.960 - 0.055 * gy
    lit = rgb * a + page * (1 - a)
    Image.fromarray((lit * 255).astype(np.uint8)).save(
        os.path.join(HERE, "opus2_cg_lit.png"))

    # TRUE 50/50 alpha overlay - both images occupy the same pixels
    ov = r * (1.0 - 0.5 * a) + rgb * (0.5 * a)
    Image.fromarray((ov * 255).astype(np.uint8)).save(
        os.path.join(HERE, "opus2_overlay.png"))

    sbs = Image.new("RGB", (W * 2 + 24, H), (255, 255, 255))
    sbs.paste(ref, (0, 0))
    sbs.paste(Image.fromarray((lit * 255).astype(np.uint8)), (W + 24, 0))
    sbs.save(os.path.join(HERE, "opus2_sbs.png"))

    # alpha bbox in reference pixel space
    m = a[..., 0] > 0.35
    ys, xs = np.nonzero(m)
    if len(xs):
        x0 = (xs.min() + 350.0) / SCALE
        x1 = (xs.max() + 350.0) / SCALE
        y0 = (ys.min() + 460.0) / SCALE
        y1 = (ys.max() + 460.0) / SCALE
        print("CG bbox in REF px: x %.0f..%.0f  y %.0f..%.0f" % (x0, x1, y0, y1))
    print("WROTE opus2_overlay.png / opus2_cg_lit.png / opus2_sbs.png", tag)


def zoom(x0, y0, x1, y1, z, out):
    """Zoomed 3-up: reference | CG | overlay, in reference pixel coords."""
    from PIL import Image
    import numpy as np
    cg = Image.open(os.path.join(HERE, "opus2_cg.png")).convert("RGBA")
    ref = ref_aligned()
    c = np.asarray(cg, dtype=np.float32) / 255.0
    r = np.asarray(ref, dtype=np.float32) / 255.0
    a = c[..., 3:4]
    page = np.full_like(r, 0.94)
    lit = c[..., :3] * a + page * (1 - a)
    ov = r * (1.0 - 0.5 * a) + c[..., :3] * (0.5 * a)
    p0 = ref_to_px(x0, y0)
    p1 = ref_to_px(x1, y1)
    box = (int(p0[0]), int(p0[1]), int(p1[0]), int(p1[1]))
    ims = []
    for arr in (r, lit, ov):
        im = Image.fromarray((arr * 255).astype(np.uint8)).crop(box)
        ims.append(im.resize((int(im.width * z), int(im.height * z)),
                             Image.LANCZOS))
    w, h = ims[0].size
    sheet = Image.new("RGB", (w * 3 + 16, h), (255, 255, 255))
    for i, im in enumerate(ims):
        sheet.paste(im, (i * (w + 8), 0))
    sheet.save(os.path.join(HERE, out))
    print("ZOOM", out, sheet.size)


def deliver():
    """Write the staged overlay / side-by-side deliverables into assets\\."""
    from PIL import Image, ImageDraw
    import numpy as np
    assets = os.path.dirname(HERE)
    cg = Image.open(os.path.join(HERE, "opus2_cg.png")).convert("RGBA")
    if cg.size != (W, H):
        cg = cg.resize((W, H), Image.LANCZOS)
    ref = ref_aligned()
    c = np.asarray(cg, dtype=np.float32) / 255.0
    r = np.asarray(ref, dtype=np.float32) / 255.0
    a = c[..., 3:4]
    rgb = c[..., :3]

    ov = Image.fromarray(
        ((r * (1.0 - 0.5 * a) + rgb * (0.5 * a)) * 255).astype(np.uint8))
    d = ImageDraw.Draw(ov)
    d.text((18, 18), "50% REFERENCE + 50% CG - identical crop, projection "
                     "and resolution (alpha composite)", fill=(20, 70, 80))
    p = os.path.join(assets, "mimamo-opus-reference-overlay.png")
    ov.save(p)
    print("OVERLAY", p, ov.size)

    page = np.zeros_like(r)
    gy = np.linspace(0.0, 1.0, H, dtype=np.float32)[:, None]
    page[..., 0] = 0.905 - 0.085 * gy
    page[..., 1] = 0.965 - 0.055 * gy
    page[..., 2] = 0.960 - 0.055 * gy
    lit = Image.fromarray(((rgb * a + page * (1 - a)) * 255).astype(np.uint8))
    sbs = Image.new("RGB", (W * 2 + 24, H + 40), (255, 255, 255))
    sbs.paste(ref, (0, 40))
    sbs.paste(lit, (W + 24, 40))
    d2 = ImageDraw.Draw(sbs)
    d2.text((18, 14), "REFERENCE (same crop)", fill=(20, 70, 80))
    d2.text((W + 42, 14), "BLENDER CG - front orthographic, matched projection",
            fill=(20, 70, 80))
    p2 = os.path.join(assets, "mimamo-opus-reference-side-by-side.png")
    sbs.save(p2)
    print("SBS", p2, sbs.size)


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    if argv and argv[0] == "pass1":
        blender_pass(argv[1] if len(argv) > 1 else os.path.join(HERE, "opus2_cg.png"))
    elif argv and argv[0] == "zoom":
        zoom(float(argv[1]), float(argv[2]), float(argv[3]), float(argv[4]),
             float(argv[5]), argv[6])
    elif argv and argv[0] == "deliver":
        deliver()
    else:
        composite(argv[0] if argv else "v2")
