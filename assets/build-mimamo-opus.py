"""Mimamo mascot - single entry, fully independent build script.

MUST be run in its own headless process, never through a Blender GUI/MCP
session:

    "C:\\Program Files\\Blender Foundation\\Blender 5.1\\blender.exe" ^
        --background --factory-startup ^
        --python assets\\build-mimamo-opus.py

It rebuilds the whole character from the measured proportion table
(assets/mimamo-opus-proportions.json), rigs it, normalises it to the
required world size and writes assets/mimamo-robot-opus-working.blend.

Size contract
    total height  = mimamo-opus-proportions.json -> height_axis.total_height_m
    height axis   = Blender +Z (Z-up);  soles sit exactly on Z = 0
    glTF export   = Y-up (export_yup=True), so soles land on Y = 0 there.
"""
import json
import os
import sys

import bpy

os.environ["OPUS_AUTORUN"] = "0"          # must be set before the imports below

HERE = os.path.dirname(os.path.abspath(__file__))
WORK = os.path.join(HERE, "opus-work")
if WORK not in sys.path:
    sys.path.insert(0, WORK)

with open(os.path.join(HERE, "mimamo-opus-proportions.json"), encoding="utf-8") as fh:
    PROPS = json.load(fh)

TARGET_H = float(PROPS["height_axis"]["total_height_m"])
OUT_BLEND = os.path.join(HERE, "mimamo-robot-opus-working.blend")

# Tessellation budget.  The GLB has to stay under 1 MB and Draco costs roughly
# 20 bytes per vertex, so the whole character has to fit in ~50k vertices.
# The face carries its own boost inside opus2_head.build_head().
DENSITY = float(os.environ.get("OPUS_DENSITY", "0.55"))

import opus_lib                                            # noqa: E402
import opus2_build                                          # noqa: E402
import opus2_rig                                            # noqa: E402


# --------------------------------------------------------------------------- #
def normalize(rig, body):
    """Uniform scale + lift: exact TARGET_H tall, soles on Z = 0.

    Geometry is scaled at *data* level (vertices, shape keys, edit bones) so
    every object keeps an identity scale - that is what the glTF exporter
    wants for skinned meshes.  The camera, lights and the reference plate are
    scaled by the same factor about the origin, so the front-ortho projection
    used by the 50/50 reference overlay is completely unchanged.
    """
    dg = bpy.context.evaluated_depsgraph_get()
    ev = body.evaluated_get(dg)
    me = ev.to_mesh()
    mw = body.matrix_world
    zs = [(mw @ v.co).z for v in me.vertices]
    zmin, zmax = min(zs), max(zs)
    ev.to_mesh_clear()

    k = TARGET_H / (zmax - zmin)
    dz = -zmin * k
    print("NORMALIZE  height %.5f -> %.5f   k=%.6f  dz=%+.6f"
          % (zmax - zmin, TARGET_H, k, dz))

    # ---- character geometry ---- #
    for v in body.data.vertices:
        v.co = v.co * k
    if body.data.shape_keys:
        for kb in body.data.shape_keys.key_blocks:
            for el in kb.data:
                el.co = el.co * k
    body.location = tuple(c * k for c in body.location)

    bpy.ops.object.mode_set(mode="OBJECT")
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    for eb in rig.data.edit_bones:
        eb.head = eb.head * k
        eb.tail = eb.tail * k
    bpy.ops.object.mode_set(mode="OBJECT")
    rig.location = (rig.location.x * k, rig.location.y * k,
                    rig.location.z * k + dz)

    # ---- everything else, same transform, so the projection is identical ---- #
    for ob in bpy.data.objects:
        if ob.parent is not None or ob in (rig, body):
            continue
        ob.location = (ob.location.x * k, ob.location.y * k,
                       ob.location.z * k + dz)
        ob.scale = tuple(s * k for s in ob.scale)
    for cam in bpy.data.cameras:
        if cam.type == "ORTHO":
            cam.ortho_scale *= k
        else:
            cam.lens = cam.lens
    for ld in bpy.data.lights:
        ld.size *= k
        ld.energy *= k * k
    bpy.context.view_layer.update()


def report_size(body):
    dg = bpy.context.evaluated_depsgraph_get()
    ev = body.evaluated_get(dg)
    me = ev.to_mesh()
    mw = body.matrix_world
    pts = [mw @ v.co for v in me.vertices]
    ev.to_mesh_clear()
    xs = [p.x for p in pts]
    ys = [p.y for p in pts]
    zs = [p.z for p in pts]
    print("FINAL BBOX  x %.4f..%.4f  y %.4f..%.4f  z %.4f..%.4f"
          % (min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)))
    print("FINAL SIZE  w=%.4f d=%.4f h=%.4f  feet_z=%.6f"
          % (max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs), min(zs)))


# --------------------------------------------------------------------------- #
def main():
    opus_lib.set_density(DENSITY)
    print("DENSITY", DENSITY)
    opus2_build.main(stage="rigsrc", save=False)
    rig, body = opus2_rig.main(save=False, pre_action_hook=normalize)
    report_size(body)
    bpy.ops.wm.save_as_mainfile(filepath=OUT_BLEND)
    print("SAVED", OUT_BLEND)


main()
