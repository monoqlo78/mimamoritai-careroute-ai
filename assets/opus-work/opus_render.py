"""Render passes + true alpha overlay + side-by-side for the Mimamo opus rebuild.

Blender part:
    blender -b <blend> --factory-startup --python opus_render.py -- pass1
Python (PIL) part:
    python opus_render.py overlay
"""
import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
ASSETS = os.path.join(ROOT, "assets")
WORK = os.path.join(ASSETS, "opus-work")
REF_IMG = (r"C:\Users\msoga\.copilot\workspaces\fe9aca11-79ab-4d6d-a028-c44b6544089c"
           r"\attachments\09f6fd54-74b5-4ae0-90b4-b88b0e31fccf-ChatGPT Image "
           "2026\u5e748\u67089\u65e5 19_57_40.png")

CROP = (175, 230, 935, 1130)          # 760 x 900 in reference pixels
RES = (1520, 1800)                    # 2x the crop
CG_ALPHA_PATH = os.path.join(WORK, "opus_cg_alpha.png")
CG_LIT_PATH = os.path.join(WORK, "opus_cg_lit.png")


def blender_pass():
    import bpy
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = 96
    sc.cycles.use_denoising = True
    sc.cycles.max_bounces = 6
    sc.cycles.transmission_bounces = 4
    sc.cycles.transparent_max_bounces = 8
    cam = bpy.data.objects["FrontOrthoCam"]
    sc.camera = cam
    sc.render.resolution_x, sc.render.resolution_y = RES
    sc.render.resolution_percentage = 100
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.frame_set(1)

    # reference must never render
    ref = bpy.data.collections.get("REFERENCE_DO_NOT_RENDER")
    if ref:
        ref.hide_render = True
        for o in ref.objects:
            o.hide_render = True
    cam.data.show_background_images = False

    sc.render.film_transparent = True
    sc.render.filepath = CG_ALPHA_PATH
    bpy.ops.render.render(write_still=True)

    cam.data.show_background_images = True
    print("RENDERED", CG_ALPHA_PATH)


def overlay():
    from PIL import Image
    ref = Image.open(REF_IMG).convert("RGB").crop(CROP).resize(RES, Image.LANCZOS)
    cg = Image.open(CG_ALPHA_PATH).convert("RGBA")
    if cg.size != RES:
        cg = cg.resize(RES, Image.LANCZOS)

    import numpy as np
    r = np.asarray(ref, dtype=np.float32)
    c = np.asarray(cg, dtype=np.float32)
    a = (c[:, :, 3:4] / 255.0) * 0.5
    out = r * (1.0 - a) + c[:, :, :3] * a
    ov = Image.fromarray(np.clip(out, 0, 255).astype(np.uint8))
    ov_path = os.path.join(ASSETS, "mimamo-opus-reference-overlay.png")
    ov.save(ov_path)

    # side-by-side (reference | CG on soft background | overlay)
    ca = c[:, :, 3:4] / 255.0
    bg = np.zeros_like(c[:, :, :3])
    gy = np.linspace(252.0, 226.0, RES[1], dtype=np.float32)[:, None]
    bg[:, :, 0] = gy
    bg[:, :, 1] = gy
    bg[:, :, 2] = np.clip(gy + 3.0, 0, 255)
    lit_arr = c[:, :, :3] * ca + bg * (1.0 - ca)
    lit = Image.fromarray(np.clip(lit_arr, 0, 255).astype(np.uint8))
    lit.save(CG_LIT_PATH)
    w, h = RES
    sw, sh = w // 2, h // 2
    sbs = Image.new("RGB", (sw * 3 + 40, sh + 20), (250, 250, 250))
    for i, im in enumerate((ref, lit, ov)):
        sbs.paste(im.resize((sw, sh), Image.LANCZOS), (10 + i * (sw + 10), 10))
    sbs_path = os.path.join(ASSETS, "mimamo-opus-reference-side-by-side.png")
    sbs.save(sbs_path)
    print("OVERLAY", ov_path)
    print("SBS", sbs_path)

    # working diagnostics: edge overlay + landmark deltas
    diag(np.asarray(ref, np.float32), c)


def diag(ref_arr, cg_arr):
    import numpy as np
    from PIL import Image
    alpha = cg_arr[:, :, 3] > 96
    ys, xs = np.nonzero(alpha)
    if len(xs) == 0:
        print("DIAG: empty render")
        return
    info = {
        "cg_x0": int(xs.min()), "cg_x1": int(xs.max()),
        "cg_y0": int(ys.min()), "cg_y1": int(ys.max()),
    }
    print("DIAG_CG_BBOX", info)
    # per-row extents of the CG silhouette, saved for the iteration loop
    rows = {}
    for y in range(0, RES[1], 10):
        m = np.nonzero(alpha[y])[0]
        if len(m):
            rows[y] = (int(m.min()), int(m.max()))
    import json
    with open(os.path.join(WORK, "opus_cg_rows.json"), "w") as f:
        json.dump(rows, f)
    edge = np.zeros((RES[1], RES[0], 3), np.uint8)
    edge[:, :, :] = ref_arr.astype(np.uint8)
    b = alpha.astype(np.uint8)
    e = np.zeros_like(b)
    e[1:-1, 1:-1] = (b[1:-1, 1:-1] * 4 - b[:-2, 1:-1] - b[2:, 1:-1]
                     - b[1:-1, :-2] - b[1:-1, 2:]) != 0
    edge[e > 0] = (255, 0, 128)
    Image.fromarray(edge).save(os.path.join(WORK, "opus_edge_overlay.png"))
    print("EDGE", os.path.join(WORK, "opus_edge_overlay.png"))


if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    if args and args[0] == "pass1":
        blender_pass()
    else:
        overlay()
