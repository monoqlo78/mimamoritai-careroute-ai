"""Master build script for the Mimamo opus rebuild.

Run:
    blender --background --factory-startup --python opus_build.py -- [--render] [--export]
"""
import math
import os
import sys

import bpy
from mathutils import Euler, Vector

HERE = os.path.dirname(os.path.abspath(bpy.data.filepath or __file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)
SCRIPT_DIR = os.path.dirname(os.path.abspath(
    [a for a in sys.argv if a.endswith("opus_build.py")][0]
    if any(a.endswith("opus_build.py") for a in sys.argv) else __file__))
if SCRIPT_DIR not in sys.path:
    sys.path.insert(0, SCRIPT_DIR)

from opus_lib import (  # noqa: E402
    apply_all,
    ensure_collection,
    join,
    link,
    make_material,
    wipe_scene,
)
import opus_head  # noqa: E402
import opus_body  # noqa: E402
import opus_rig  # noqa: E402

ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
ASSETS = os.path.join(ROOT, "assets")
WORK = os.path.join(ASSETS, "opus-work")
TEX = os.path.join(WORK, "tex")
REF_IMG = (r"C:\Users\msoga\.copilot\workspaces\fe9aca11-79ab-4d6d-a028-c44b6544089c"
           r"\attachments\09f6fd54-74b5-4ae0-90b4-b88b0e31fccf-ChatGPT Image "
           "2026\u5e748\u67089\u65e5 19_57_40.png")

OUT_BLEND = os.path.join(ASSETS, "mimamo-robot-opus-working.blend")

S = 0.0025
CAM_LOC = (0.0, -6.0, 1.05)
ORTHO_SCALE = 2.25


# --------------------------------------------------------------------------- #
def build_materials():
    img_blush = bpy.data.images.load(os.path.join(TEX, "opus_blush.png"), check_existing=True)
    img_phone = bpy.data.images.load(os.path.join(TEX, "opus_phone_screen.png"), check_existing=True)
    img_watch = bpy.data.images.load(os.path.join(TEX, "opus_watch_screen.png"), check_existing=True)
    for im in (img_blush, img_phone, img_watch):
        im.pack()

    M = {}
    M["white"] = make_material("MI_WhiteShell", (0.962, 0.960, 0.950), rough=0.265,
                               coat=0.45, coat_rough=0.06, subsurf=0.10)
    M["white_face"] = make_material("MI_WhiteFace", (1.000, 0.968, 0.948), rough=0.470,
                                    coat=0.00, coat_rough=0.30, subsurf=0.10)
    M["mint"] = make_material("MI_Mint", (0.235, 0.760, 0.690), rough=0.245,
                              coat=0.40, coat_rough=0.06)
    M["mint_dark"] = make_material("MI_MintDeep", (0.075, 0.470, 0.450), rough=0.285,
                                   coat=0.35)
    M["mint_pale"] = make_material("MI_MintPale", (0.640, 0.900, 0.870), rough=0.265,
                                   coat=0.40, coat_rough=0.06)
    M["pink"] = make_material("MI_Pink", (0.960, 0.320, 0.390), rough=0.230,
                              coat=0.45, coat_rough=0.05)
    M["pink_vc"] = make_material("MI_PinkHeart", (1.0, 1.0, 1.0), rough=0.180,
                                 coat=0.60, coat_rough=0.04, use_vcol=True)
    M["pink_lining"] = make_material("MI_PinkLining", (0.905, 0.470, 0.510), rough=0.330)
    M["eye_rim"] = make_material("MI_EyeRim", (0.008, 0.014, 0.022), rough=0.420, coat=0.05)
    M["eye_white"] = make_material("MI_EyeWhite", (0.900, 0.915, 0.920), rough=0.260, coat=0.20)
    M["iris"] = make_material("MI_Iris", (1.0, 1.0, 1.0), rough=0.280, coat=0.08,
                              coat_rough=0.10, ior=1.32, use_vcol=True)
    M["pupil"] = make_material("MI_Pupil", (0.003, 0.006, 0.009), rough=0.600, coat=0.00,
                               coat_rough=0.30, ior=1.05)
    M["hilite"] = make_material("MI_Highlight", (1.0, 1.0, 1.0), rough=0.055,
                                emission=(1.0, 1.0, 1.0), emission_strength=0.30, coat=0.4)
    M["lens"] = make_material("MI_EyeLens", (1.0, 1.0, 1.0), rough=0.035, alpha=0.055,
                              coat=1.0, coat_rough=0.01, ior=1.46, blend=True)
    M["brow"] = make_material("MI_Brow", (0.075, 0.105, 0.145), rough=0.360)
    M["mouth"] = make_material("MI_MouthCavity", (0.190, 0.038, 0.058), rough=0.400)
    M["mouth_rim"] = make_material("MI_MouthRim", (0.145, 0.055, 0.075), rough=0.340)
    M["tongue"] = make_material("MI_Tongue", (0.900, 0.340, 0.390), rough=0.260,
                                coat=0.30, subsurf=0.20)
    M["blush"] = make_material("MI_Blush", (1.0, 1.0, 1.0), rough=0.400,
                               use_vcol=True, coat=0.15, subsurf=0.10)
    M["face_rim"] = make_material("MI_FaceRim", (0.965, 0.960, 0.952), rough=0.34, coat=0.35)
    M["ear_glow"] = make_material("MI_EarGlow", (0.42, 0.98, 0.95), rough=0.10,
                                  emission=(0.35, 0.98, 0.95), emission_strength=0.85, coat=0.5)
    M["spark"] = make_material("MI_Sparkle", (0.82, 1.0, 0.98), rough=0.070,
                                emission=(0.72, 1.0, 0.97), emission_strength=0.22, coat=0.4)
    M["dark_body"] = make_material("MI_Device", (0.870, 0.905, 0.925), rough=0.190,
                                   metallic=0.05, coat=0.60)
    M["phone_screen"] = make_material("MI_PhoneScreen", (0.880, 0.962, 0.958), rough=0.075,
                                      coat=0.90, coat_rough=0.02,
                                      emission=(0.840, 0.965, 0.960),
                                      emission_strength=0.26)
    M["watch_screen"] = make_material("MI_WatchScreen", (0.045, 0.330, 0.320), rough=0.110,
                                      coat=0.90, coat_rough=0.02,
                                      emission=(0.040, 0.300, 0.290),
                                      emission_strength=0.12)
    # screens are now solid emissive glass - no image texture wiring needed
    for im in (img_blush, img_phone, img_watch):
        pass
    return M


# --------------------------------------------------------------------------- #
def build_lights(coll):
    specs = [
        ("KeyLight", "AREA", (-2.10, -3.30, 3.05), 74.0, 3.0, (1.0, 0.985, 0.960)),
        ("FillLight", "AREA", (2.60, -2.40, 1.35), 30.0, 3.4, (0.905, 0.955, 1.0)),
        ("RimLight", "AREA", (1.35, 3.10, 2.60), 52.0, 2.6, (0.760, 0.980, 0.960)),
        ("TopLight", "AREA", (0.0, 0.35, 3.60), 36.0, 3.6, (1.0, 1.0, 1.0)),
        ("BounceLight", "AREA", (0.0, -1.90, -0.75), 11.0, 4.2, (1.0, 0.945, 0.930)),
    ]
    for name, kind, loc, energy, size, color in specs:
        ld = bpy.data.lights.new(name, kind)
        ld.energy = energy
        ld.color = color
        ld.size = size
        try:
            ld.shape = "DISK"
        except Exception:
            pass
        ob = bpy.data.objects.new(name, ld)
        coll.objects.link(ob)
        ob.location = loc
        d = Vector((0.0, 0.0, 1.15)) - Vector(loc)
        ob.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()


def build_world():
    w = bpy.data.worlds.new("MimamoWorld")
    bpy.context.scene.world = w
    w.use_nodes = True
    nt = w.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputWorld")
    bg = nt.nodes.new("ShaderNodeBackground")
    bg.inputs["Color"].default_value = (0.775, 0.930, 0.945, 1.0)
    bg.inputs["Strength"].default_value = 0.42
    nt.links.new(bg.outputs["Background"], out.inputs["Surface"])


def build_cameras(coll):
    cd = bpy.data.cameras.new("FrontOrthoCam")
    cd.type = "ORTHO"
    cd.ortho_scale = ORTHO_SCALE
    cam = bpy.data.objects.new("FrontOrthoCam", cd)
    coll.objects.link(cam)
    cam.location = CAM_LOC
    cam.rotation_euler = Euler((math.radians(90), 0, 0), "XYZ")

    pd = bpy.data.cameras.new("PreviewCam")
    pd.lens = 85.0
    prev = bpy.data.objects.new("PreviewCam", pd)
    coll.objects.link(prev)
    prev.location = (0.55, -6.10, 1.42)
    d = Vector((0.0, 0.0, 1.02)) - Vector(prev.location)
    prev.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()

    bpy.context.scene.camera = cam
    return cam, prev


def build_reference(cam):
    """Packed reference image, camera-locked, excluded from renders."""
    ref_coll = ensure_collection("REFERENCE_DO_NOT_RENDER", color="COLOR_01")
    img = bpy.data.images.load(REF_IMG, check_existing=True)
    img.name = "MimamoReferenceImage"
    img.pack()
    e = bpy.data.objects.new("MimamoReferencePlate", None)
    ref_coll.objects.link(e)
    e.empty_display_type = "IMAGE"
    e.data = img
    e.empty_display_size = img.size[0] * S
    e.empty_image_offset = (-0.5, -0.5)
    e.empty_image_depth = "BACK"
    e.use_empty_image_alpha = True
    e.color = (1, 1, 1, 0.45)
    e.location = (0.015, 3.0, 0.9975)
    e.rotation_euler = Euler((math.radians(90), 0, 0), "XYZ")
    e.hide_render = True
    e.hide_select = True
    ref_coll.hide_render = True

    # also lock it to the camera as a background image
    cam.data.show_background_images = True
    bg = cam.data.background_images.new()
    bg.image = img
    bg.alpha = 0.45
    bg.display_depth = "BACK"
    bg.frame_method = "FIT"

    vl = bpy.context.view_layer
    for lc in vl.layer_collection.children:
        if lc.name == "REFERENCE_DO_NOT_RENDER":
            lc.indirect_only = True
    return ref_coll, e


# --------------------------------------------------------------------------- #
def setup_render():
    sc = bpy.context.scene
    engines = list(bpy.types.RenderEngine.bl_rna.properties["bl_idname"].default) if False else []
    for cand in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
        try:
            sc.render.engine = cand
            break
        except Exception:
            continue
    sc.render.film_transparent = False
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.render.resolution_x = 1520
    sc.render.resolution_y = 1800
    sc.render.resolution_percentage = 100
    sc.view_settings.view_transform = "Standard"
    ee = getattr(sc, "eevee", None)
    if ee:
        for attr, val in (("taa_render_samples", 96), ("use_gtao", True),
                          ("use_bloom", True), ("use_raytracing", True),
                          ("use_shadows", True), ("shadow_ray_count", 2)):
            if hasattr(ee, attr):
                try:
                    setattr(ee, attr, val)
                except Exception:
                    pass
    return sc


# --------------------------------------------------------------------------- #
def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    wipe_scene()

    char_coll = ensure_collection("MIMAMO", color="COLOR_04")
    rig_coll = ensure_collection("RIG", color="COLOR_05")
    light_coll = ensure_collection("LIGHTING", color="COLOR_03")

    M = build_materials()
    parts = {}
    parts.update(opus_head.build_head(M, char_coll, TEX))
    parts.update(opus_body.build_body(M, char_coll, TEX))
    parts.update(opus_body.build_cape(M, char_coll))

    build_lights(light_coll)
    build_world()
    cam, prev = build_cameras(light_coll)
    build_reference(cam)
    setup_render()

    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = None

    # apply modifiers, then skin
    objs = [o for o in char_coll.objects if o.type == "MESH"]
    for ob in objs:
        apply_all(ob)
    opus_rig.assign_groups(objs)

    rig = opus_rig.build_armature(rig_coll)
    mesh = join(objs, "Mimamo")
    link(mesh, char_coll)
    mesh.parent = rig
    mod = mesh.modifiers.new("Armature", "ARMATURE")
    mod.object = rig
    mod.use_vertex_groups = True

    bpy.context.view_layer.objects.active = mesh
    opus_rig.add_shape_keys(mesh)
    opus_rig.make_actions(rig, mesh)

    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = mesh

    os.makedirs(ASSETS, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=OUT_BLEND)
    print("SAVED", OUT_BLEND)

    dims = mesh.dimensions
    print("MESH_DIMS %.4f %.4f %.4f" % (dims.x, dims.y, dims.z))
    print("VERTS", len(mesh.data.vertices), "POLYS", len(mesh.data.polygons))
    print("MATERIALS", len(mesh.data.materials))
    print("BONES", len(rig.data.bones))
    print("ACTIONS", [a.name for a in bpy.data.actions])


main()
