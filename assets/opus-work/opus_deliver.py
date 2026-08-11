"""Final deliverables: hero front PNG, GIF frames, GLB export.

Run:  blender --background <blend> --python opus_deliver.py -- <stage>
stages: hero | gif | glb
"""
import bpy
import os
import sys
import math

ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
STAGE = ARGS[0] if ARGS else "hero"

BLEND = bpy.data.filepath
ASSETS = os.path.dirname(BLEND)
WORK = os.path.join(ASSETS, "opus-work")


def cycles(samples, res_x, res_y, transparent=True):
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = samples
    sc.cycles.use_denoising = True
    sc.cycles.max_bounces = 6
    sc.render.resolution_x = res_x
    sc.render.resolution_y = res_y
    sc.render.resolution_percentage = 100
    sc.render.film_transparent = transparent
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    return sc


def hide_reference():
    vl = bpy.context.view_layer
    for lc in vl.layer_collection.children:
        if "REFERENCE" in lc.name:
            lc.exclude = True
    for ob in bpy.data.objects:
        if "Reference" in ob.name:
            ob.hide_render = True


def use_cam(name="FrontOrthoCam", ortho=None, z=None):
    cam = bpy.data.objects.get(name)
    if cam is None:
        return None
    bpy.context.scene.camera = cam
    if ortho is not None:
        cam.data.ortho_scale = ortho
    if z is not None:
        cam.location.z = z
    return cam


def framed_cam(widen, lift=0.01):
    """Re-frame FrontOrthoCam relative to its authored framing.

    The scene is normalised to a fixed real-world height, so absolute camera
    numbers would break whenever the target height changes.  `widen` is a
    multiplier on the authored ortho_scale (2.25) and `lift` is expressed in
    the same authored units, then converted through the current ortho_scale.
    """
    cam = bpy.data.objects["FrontOrthoCam"]
    base_o = cam.data.ortho_scale
    cam.data.ortho_scale = base_o * widen
    cam.location.z += base_o * (lift / 2.25)
    bpy.context.scene.camera = cam
    return cam


def main():
    hide_reference()
    sc = bpy.context.scene

    if STAGE == "hero":
        framed_cam(2.62 / 2.25)
        cycles(150, 1200, 1500)
        sc.render.filepath = os.path.join(WORK, "opus_hero_raw.png")
        bpy.ops.render.render(write_still=True)
        print("HERO DONE")

    elif STAGE == "views":
        # three-direction turntable proof: front, 3/4 left, right profile
        import mathutils
        cam = framed_cam(2.62 / 2.25)
        rig = bpy.data.objects.get("MimamoRig")
        body = bpy.data.objects.get("Mimamo")
        pivot = bpy.data.objects.new("ViewPivot", None)
        sc.collection.objects.link(pivot)
        for ob in (rig, body):
            if ob is not None and ob.parent is None:
                ob.parent = pivot
                ob.matrix_parent_inverse = pivot.matrix_world.inverted()
        cycles(110, 900, 1200)
        for tag, deg in (("front", 0.0), ("q34", -34.0), ("side", -90.0)):
            pivot.rotation_euler = mathutils.Euler((0, 0, math.radians(deg)), "XYZ")
            bpy.context.view_layer.update()
            sc.render.filepath = os.path.join(WORK, "opus_view_%s.png" % tag)
            bpy.ops.render.render(write_still=True)
            print("VIEW DONE", tag)
        print("VIEWS DONE")

    elif STAGE == "gif":
        framed_cam(2.72 / 2.25)
        cycles(28, 460, 560)
        arm = bpy.data.objects.get("MimamoRig")
        act = bpy.data.actions.get("MimamoWave")
        if arm and act:
            if arm.animation_data is None:
                arm.animation_data_create()
            try:
                arm.animation_data.action = act
                for slot in getattr(act, "slots", []):
                    arm.animation_data.action_slot = slot
                    break
            except Exception as e:
                print("SLOT ERR", e)
        n = 18
        f0, f1 = int(sc.frame_start), int(sc.frame_end)
        span = max(1, f1 - f0)
        outdir = os.path.join(WORK, "gif")
        os.makedirs(outdir, exist_ok=True)
        for i in range(n):
            sc.frame_set(f0 + int(round(span * i / n)))
            sc.render.filepath = os.path.join(outdir, "f%02d.png" % i)
            bpy.ops.render.render(write_still=True)
            print("GIF FRAME", i)
        print("GIF DONE")

    elif STAGE == "glb":
        # Vertex colours are exported as COLOR_0; byte precision is plenty and
        # matches what Draco quantises to anyway.
        body = bpy.data.objects.get("Mimamo")
        if body is not None and body.data.color_attributes:
            bpy.context.view_layer.objects.active = body
            try:
                body.data.color_attributes.active_color_index = 0
                bpy.ops.geometry.color_attribute_convert(
                    domain="CORNER", data_type="BYTE_COLOR")
            except Exception as e:
                print("COLOR CONVERT SKIPPED", e)
        out = os.path.join(ASSETS, "mimamo-robot-opus-rigged.glb")
        kw = dict(
            filepath=out,
            export_format="GLB",
            export_apply=True,
            export_yup=True,
            export_animations=True,
            export_skins=True,
            export_morph=True,
            export_morph_normal=False,
            export_morph_tangent=False,
            export_materials="EXPORT",
            export_optimize_animation_size=True,
        )
        try:
            kw["export_draco_mesh_compression_enable"] = True
            kw["export_draco_mesh_compression_level"] = 6
        except Exception:
            pass
        try:
            kw["export_animation_mode"] = "ACTIONS"
            bpy.ops.export_scene.gltf(**kw)
        except TypeError as e:
            print("RETRY without animation_mode:", e)
            kw.pop("export_animation_mode", None)
            bpy.ops.export_scene.gltf(**kw)
        print("GLB DONE", os.path.exists(out), os.path.getsize(out))


main()

