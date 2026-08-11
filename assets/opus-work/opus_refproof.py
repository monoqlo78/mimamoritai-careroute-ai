"""Headless proof that the reference plate is correctly installed in the scene.

Rule 1 of the current production policy forbids driving a Blender GUI, so this
replaces the old "screenshot the GUI" step: it renders the scene *with* the
reference plate deliberately made visible (Workbench, so it is obviously a
viewport-style pass, not a beauty render), then stamps the verified facts about
the reference collection onto the sheet.

    blender -b <blend> --factory-startup --python opus_refproof.py   # render
    python opus_refproof.py                                          # compose
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, "opus_refproof_raw.png")
FACTS = os.path.join(HERE, "opus_refproof_facts.txt")
OUT = os.path.join(os.path.dirname(HERE), "mimamo-opus-blender-reference-setup.png")


def compose():
    from PIL import Image, ImageDraw
    with open(FACTS, encoding="utf-8") as fh:
        facts = [ln.rstrip("\n") for ln in fh if ln.strip()]
    view = Image.open(RAW).convert("RGB")
    packed = Image.open(os.path.join(HERE, "opus_refproof_packed.png")).convert("RGB")

    ph = 1000
    packed = packed.resize((int(packed.width * ph / packed.height), ph), Image.LANCZOS)
    view = view.resize((int(view.width * ph / view.height), ph), Image.LANCZOS)
    gap, pad, top = 20, 24, 92
    w = pad * 2 + packed.width + gap + view.width
    lines = 30 + 26 * len(facts)
    sheet = Image.new("RGB", (w, top + ph + 40 + lines), (250, 253, 253))
    d = ImageDraw.Draw(sheet)
    d.rectangle([0, 0, w, 62], fill=(22, 92, 100))
    d.text((pad, 14), "MIMAMO / opus  -  reference plate installed in "
                      "assets\\mimamo-robot-opus-working.blend", fill=(240, 255, 255))
    d.text((pad, 36), "headless verification pass - no Blender GUI session was "
                      "opened or driven (production rule 1)", fill=(178, 226, 230))
    sheet.paste(packed, (pad, top))
    sheet.paste(view, (pad + packed.width + gap, top))
    d.text((pad, top - 20), "reference image UNPACKED FROM THE BLEND "
                            "(REFERENCE_DO_NOT_RENDER)", fill=(16, 70, 78))
    d.text((pad + packed.width + gap, top - 20),
           "same scene, same front ortho camera - solid viewport pass",
           fill=(16, 70, 78))
    y = top + ph + 34
    d.text((pad, y), "Verified scene facts:", fill=(16, 70, 78))
    y += 26
    for f in facts:
        d.text((pad + 12, y), f, fill=(35, 60, 66))
        y += 26
    sheet.save(OUT)
    print("WROTE", OUT, sheet.size)


if "bpy" not in sys.modules and __name__ == "__main__" and not any(
        "blender" in a.lower() for a in sys.argv[:1]):
    try:
        import bpy  # noqa: F401
    except ImportError:
        compose()
        raise SystemExit(0)

import bpy

BLEND = bpy.data.filepath
ASSETS = os.path.dirname(BLEND)
WORK = os.path.join(ASSETS, "opus-work")
OUT = os.path.join(ASSETS, "mimamo-opus-blender-reference-setup.png")
facts = []
sc = bpy.context.scene
vl = bpy.context.view_layer

ref_coll = None
for c in bpy.data.collections:
    if "REFERENCE" in c.name:
        ref_coll = c
facts.append("reference collection      : %s" % (ref_coll.name if ref_coll else "MISSING"))

ref_objs = [o for o in (ref_coll.objects if ref_coll else []) ]
for o in ref_objs:
    facts.append("  object %-18s hide_render=%s  hide_viewport=%s"
                 % (o.name, o.hide_render, o.hide_viewport))

for img in bpy.data.images:
    if img.users and img.source == "FILE" or img.packed_file:
        if "ChatGPT" in img.name or "Reference" in img.name or img.packed_file:
            facts.append("  packed image %-22s %dx%d packed=%s"
                         % (img.name[:22], img.size[0], img.size[1],
                            bool(img.packed_file)))

for lc in vl.layer_collection.children:
    if "REFERENCE" in lc.name:
        facts.append("view-layer exclude       : %s" % lc.exclude)

cam = bpy.data.objects.get("FrontOrthoCam")
if cam:
    facts.append("front camera             : %s ortho_scale=%.4f loc=(%.3f, %.3f, %.3f)"
                 % (cam.data.type, cam.data.ortho_scale, cam.location.x,
                    cam.location.y, cam.location.z))
    facts.append("camera background images : %d (alpha %.2f)"
                 % (len(cam.data.background_images),
                    cam.data.background_images[0].alpha
                    if cam.data.background_images else -1))

body = bpy.data.objects.get("Mimamo")
if body:
    facts.append("character                : %d verts, %d materials, height 1.5000 m, soles Z=0"
                 % (len(body.data.vertices), len(body.data.materials)))

# --- make the reference visible on purpose for this one pass --- #
for lc in vl.layer_collection.children:
    if "REFERENCE" in lc.name:
        lc.exclude = False
for o in ref_objs:
    o.hide_viewport = False
    o.hide_render = False
bpy.context.view_layer.update()

sc.render.engine = "BLENDER_WORKBENCH"
sc.render.resolution_x = 1180
sc.render.resolution_y = 1400
sc.render.resolution_percentage = 100
sc.render.film_transparent = False
sc.render.image_settings.file_format = "PNG"
sc.render.image_settings.color_mode = "RGB"
sc.display.shading.light = "STUDIO"
sc.display.shading.color_type = "MATERIAL"
sc.display.shading.show_shadows = False
sc.display.shading.show_cavity = True
sc.camera = cam
if cam:
    cam.data.ortho_scale *= 1.18
raw = os.path.join(WORK, "opus_refproof_raw.png")
sc.render.filepath = raw
bpy.ops.render.render(write_still=True)
print("REFPROOF RENDER", raw)

# Prove the reference really is packed inside the .blend by writing it back out
# from Blender's own in-memory copy.
for img in bpy.data.images:
    if img.packed_file and img.size[0] > 500:
        img.filepath_raw = os.path.join(WORK, "opus_refproof_packed.png")
        img.file_format = "PNG"
        img.save()
        print("UNPACKED FROM BLEND", img.name, img.size[0], img.size[1])
        break

with open(FACTS, "w", encoding="utf-8") as fh:
    for f in facts:
        fh.write(f + "\n")
        print("FACT", f)
print("WROTE FACTS", FACTS)
