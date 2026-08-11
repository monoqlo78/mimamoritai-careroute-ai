"""Mimamo v2 scene builder - clean scene, built stage by stage.

    blender -b --factory-startup --python opus2_build.py -- <stage> <outblend>

stages:  head | full
"""
import math
import os
import sys

import bpy
from mathutils import Euler, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

from opus_lib import (S, ensure_collection, join, link, make_material,
                      wipe_scene)

REF_IMG = (r"C:\Users\msoga\.copilot\workspaces"
           r"\fe9aca11-79ab-4d6d-a028-c44b6544089c\attachments"
           r"\09f6fd54-74b5-4ae0-90b4-b88b0e31fccf-ChatGPT Image 2026"
           + "\u5e74" + "8" + "\u6708" + "9" + "\u65e5" + " 19_57_40.png")

ORTHO_SCALE = 2.25
CAM_LOC = (0.0, -6.0, 1.05)


# --------------------------------------------------------------------------- #
def build_materials():
    M = {}
    M["white_shell"] = make_material("MI_WhiteShell", (0.955, 0.968, 0.978),
                                     rough=0.255, coat=0.32, coat_rough=0.055,
                                     subsurf=0.055)
    M["white_face"] = make_material("MI_WhiteFace", (1.000, 0.974, 0.962),
                                    rough=0.300, coat=0.22, coat_rough=0.08,
                                    subsurf=0.085, use_vcol=True)
    M["face_rim"] = make_material("MI_FaceRim", (0.800, 0.806, 0.820),
                                  rough=0.330, coat=0.22)
    M["mint"] = make_material("MI_Mint", (0.235, 0.760, 0.690), rough=0.240,
                              coat=0.40, coat_rough=0.05, subsurf=0.05)
    M["mint_deep"] = make_material("MI_MintDeep", (0.062, 0.395, 0.382),
                                   rough=0.220, coat=0.45)
    M["mint_pale"] = make_material("MI_MintPale", (0.660, 0.912, 0.880),
                                   rough=0.260, coat=0.35)
    # Tiara band only.  The poster's hood is translucent glass over the white
    # helmet, so where it lies flat it reads almost neutral -- sampling the band
    # beside the badge (x 430..460, y 415..428) gives (0.792, 0.820, 0.809)
    # against (0.639, 0.967, 0.933) for MI_Mint.  Rendering the band in the
    # saturated body mint is what made it read as a solid teal dome ("diving
    # helmet") swallowing the top third of the head.  The ear pods, scarf and
    # cape ARE saturated in the poster, so only the band changes.
    M["mint_glass"] = make_material("MI_MintGlass", (0.620, 0.855, 0.830),
                                    rough=0.190, coat=0.55, coat_rough=0.03,
                                    subsurf=0.10)
    M["pink_heart"] = make_material("MI_PinkHeart", (0.968, 0.555, 0.588),
                                    rough=0.215, coat=0.48, coat_rough=0.04,
                                    subsurf=0.16, use_vcol=True)
    M["pink"] = make_material("MI_Pink", (0.980, 0.700, 0.720), rough=0.250,
                              coat=0.30)
    M["blush"] = make_material("MI_Blush", (0.985, 0.560, 0.585), rough=0.480,
                               use_vcol=True)
    M["eye_dark"] = make_material("MI_EyeRim", (0.021, 0.021, 0.028),
                                  rough=0.230, coat=0.55, coat_rough=0.03,
                                  ior=1.05)
    M["eye_white"] = make_material("MI_EyeWhite", (0.985, 0.988, 0.992),
                                   rough=0.190, coat=0.55, coat_rough=0.03)
    # the sliver of face that shows between lash line and lens - reads as a
    # faintly shaded white in the poster, NOT the grey MI_FaceRim used before
    M["eye_socket"] = make_material("MI_EyeSocket", (0.938, 0.912, 0.900),
                                    rough=0.330, coat=0.18, coat_rough=0.06)
    M["iris"] = make_material("MI_Iris", (1.0, 1.0, 1.0), rough=0.165,
                              coat=0.08, coat_rough=0.06, use_vcol=True,
                              ior=1.30)
    M["pupil"] = make_material("MI_Pupil", (0.010, 0.010, 0.016), rough=0.115,
                               coat=0.30, coat_rough=0.02, ior=1.05)
    M["highlight"] = make_material("MI_Highlight", (1.0, 1.0, 1.0), rough=0.08,
                                   emission=(1.0, 1.0, 1.0),
                                   emission_strength=0.55)
    M["highlight2"] = make_material("MI_Highlight2", (0.92, 1.0, 0.99),
                                    rough=0.10, emission=(0.80, 1.0, 0.98),
                                    emission_strength=0.22)
    M["brow"] = make_material("MI_Brow", (0.055, 0.052, 0.060), rough=0.300,
                              coat=0.25, ior=1.05)
    M["mouth_cavity"] = make_material("MI_MouthCavity", (0.240, 0.078, 0.088),
                                      rough=0.330, use_vcol=True, ior=1.15)
    M["tongue"] = make_material("MI_Tongue", (0.885, 0.470, 0.478), rough=0.260,
                                coat=0.35, subsurf=0.22, use_vcol=True)
    M["mouth_rim"] = make_material("MI_MouthRim", (1.000, 0.955, 0.945),
                                   rough=0.300, coat=0.25)
    M["pink_lining"] = make_material("MI_PinkLining", (0.960, 0.610, 0.638),
                                     rough=0.330, coat=0.22, subsurf=0.20)
    M["dark_metal"] = make_material("MI_DarkMetal", (0.075, 0.082, 0.092),
                                    rough=0.180, coat=0.55, coat_rough=0.03)
    M["screen"] = make_material("MI_Screen", (0.905, 0.975, 0.972),
                                rough=0.110, coat=0.60, coat_rough=0.02,
                                emission=(0.86, 0.98, 0.97),
                                emission_strength=0.18)
    M["screen_teal"] = make_material("MI_ScreenTeal", (0.200, 0.735, 0.700),
                                     rough=0.150, coat=0.45,
                                     emission=(0.18, 0.70, 0.67),
                                     emission_strength=0.22)
    return M


# --------------------------------------------------------------------------- #
def build_lights(coll):
    specs = [
        ("KeyLight", (-2.10, -3.30, 3.05), 74.0, 3.0, (1.0, 0.985, 0.960)),
        ("FillLight", (2.60, -2.40, 1.35), 30.0, 3.4, (0.905, 0.955, 1.0)),
        ("RimLight", (1.35, 3.10, 2.60), 52.0, 2.6, (0.760, 0.980, 0.960)),
        ("TopLight", (0.0, 0.35, 3.60), 36.0, 3.6, (1.0, 1.0, 1.0)),
        ("EyeLight", (1.55, -3.05, 2.05), 26.0, 1.1, (1.0, 1.0, 1.0)),
        ("BounceLight", (0.0, -1.90, -0.75), 11.0, 4.2, (1.0, 0.945, 0.930)),
    ]
    for name, loc, energy, size, color in specs:
        ld = bpy.data.lights.new(name, "AREA")
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
        d = Vector((0.0, 0.0, 1.25)) - Vector(loc)
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
    e.location = (0.0, 3.0, 0.9975)
    e.rotation_euler = Euler((math.radians(90), 0, 0), "XYZ")
    e.hide_render = True
    e.hide_select = True
    ref_coll.hide_render = True

    cam.data.show_background_images = True
    bg = cam.data.background_images.new()
    bg.image = img
    bg.alpha = 0.45
    bg.display_depth = "BACK"
    bg.frame_method = "FIT"

    for lc in bpy.context.view_layer.layer_collection.children:
        if lc.name == "REFERENCE_DO_NOT_RENDER":
            lc.indirect_only = True
    return ref_coll, e


def setup_render():
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = 64
    sc.cycles.use_denoising = True
    sc.cycles.max_bounces = 8
    sc.render.resolution_x = 1520
    sc.render.resolution_y = 1800
    sc.render.film_transparent = True
    sc.view_settings.view_transform = "Standard"
    sc.view_settings.look = "None"
    sc.frame_start = 1
    sc.frame_end = 120


# --------------------------------------------------------------------------- #
def main(stage=None, outp=None, save=True):
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if stage is None:
        stage = argv[0] if argv else "head"
    if outp is None:
        outp = argv[1] if len(argv) > 1 else os.path.join(HERE, "stage_head.blend")

    wipe_scene()
    setup_render()
    build_world()
    M = build_materials()

    rig_coll = ensure_collection("MIMAMO")
    aux = ensure_collection("SCENE_AUX")
    build_lights(aux)
    cam, prev = build_cameras(aux)
    build_reference(cam)

    import opus2_head
    import importlib
    importlib.reload(opus2_head)
    head, parts = opus2_head.build_head(M, rig_coll)
    print("HEAD PARTS", len(parts))

    if stage in ("full", "rigsrc"):
        import opus2_body
        import importlib as _il
        _il.reload(opus2_body)
        if stage == "full":
            head_ob = join(parts, "MimamoHead")
            head_ob.location = (0.0, 0.0, 0.0)
            allp = [head_ob] + opus2_body.build_body(M, rig_coll)
        else:
            allp = list(parts) + opus2_body.build_body(M, rig_coll)
        n_v = sum(len(o.data.vertices) for o in allp)
        n_p = sum(len(o.data.polygons) for o in allp)
        print("PARTS", len(allp))
        print("VERTS", n_v, "POLYS", n_p)
    else:
        body = join(parts, "Mimamo")
        body.location = (0.0, 0.0, 0.0)
        print("VERTS", len(body.data.vertices),
              "POLYS", len(body.data.polygons))

    if save:
        bpy.ops.wm.save_as_mainfile(filepath=outp)
        print("SAVED", outp)


if os.environ.get("OPUS_AUTORUN", "1") == "1":
    main()
