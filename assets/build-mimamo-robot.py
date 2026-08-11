"""
build-mimamo-robot.py
----------------------
Reproducible Blender 5.1 build script for the "Mimamo" robot mascot.

Run headless & isolated (does NOT touch any other .blend file):

    "C:\\Program Files\\Blender Foundation\\Blender 5.1\\blender.exe" ^
        --background --factory-startup ^
        --python "build-mimamo-robot.py"

Outputs (all brand-new files, never overwrites other assets):
    assets/mimamo-robot-rigged.blend
    src/MimamoriTai.Web/wwwroot/models/mimamo-robot-rigged.glb
    src/MimamoriTai.Web/wwwroot/images/mimamo-robot.png
    assets/mimamo-robot-preview-frames/frame_####.png  (source frames for the GIF,
        the GIF itself is assembled afterwards by a separate ffmpeg pass)
"""

import bpy
import bmesh
import math
import os
from mathutils import Vector, Matrix

# ---------------------------------------------------------------------------
# Paths (all new, isolated from any existing project file)
# ---------------------------------------------------------------------------
PROJECT_ROOT = r"C:\Users\msoga\OneDrive - Smart Designer\Projects\見守り隊"
ASSETS_DIR = os.path.join(PROJECT_ROOT, "assets")
MODELS_DIR = os.path.join(PROJECT_ROOT, "src", "MimamoriTai.Web", "wwwroot", "models")
IMAGES_DIR = os.path.join(PROJECT_ROOT, "src", "MimamoriTai.Web", "wwwroot", "images")
FRAMES_DIR = os.path.join(ASSETS_DIR, "mimamo-robot-preview-frames")

BLEND_PATH = os.path.join(ASSETS_DIR, "mimamo-robot-rigged.blend")
GLB_PATH = os.path.join(MODELS_DIR, "mimamo-robot-rigged.glb")
POSTER_PATH = os.path.join(IMAGES_DIR, "mimamo-robot.png")

os.makedirs(FRAMES_DIR, exist_ok=True)

FRAME_START = 1
FRAME_END = 120
FPS = 24
LOOP_N = FRAME_END - FRAME_START  # 119 -> perfect-loop divisor


def phase(frame, cycles=1.0, offset=0.0):
    """Return radians so that value(FRAME_START) == value(FRAME_END) exactly."""
    t = (frame - FRAME_START) / LOOP_N
    return 2.0 * math.pi * cycles * t + offset


# ---------------------------------------------------------------------------
# Scene reset (factory-startup already gives a clean scene, but make sure)
# ---------------------------------------------------------------------------
def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scn = bpy.context.scene
    scn.frame_start = FRAME_START
    scn.frame_end = FRAME_END
    scn.render.fps = FPS
    scn.unit_settings.system = 'METRIC'
    return scn


# ---------------------------------------------------------------------------
# Material helper
# ---------------------------------------------------------------------------
def new_material(name, base_color, roughness=0.25, metallic=0.0,
                  coat=0.0, coat_rough=0.1, emission_color=None, emission_strength=0.0,
                  subsurface=0.0):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*base_color, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = metallic
    for coat_key in ("Coat Weight", "Clearcoat"):
        if coat_key in bsdf.inputs:
            bsdf.inputs[coat_key].default_value = coat
            break
    for coat_r_key in ("Coat Roughness", "Clearcoat Roughness"):
        if coat_r_key in bsdf.inputs:
            bsdf.inputs[coat_r_key].default_value = coat_rough
            break
    for ss_key in ("Subsurface Weight", "Subsurface"):
        if ss_key in bsdf.inputs:
            bsdf.inputs[ss_key].default_value = subsurface
            break
    if emission_color is not None:
        if "Emission Color" in bsdf.inputs:
            bsdf.inputs["Emission Color"].default_value = (*emission_color, 1.0)
        if "Emission Strength" in bsdf.inputs:
            bsdf.inputs["Emission Strength"].default_value = emission_strength
    return mat


PALETTE = {}


def build_materials():
    PALETTE["white"] = new_material("Mimamo_White", (0.94, 0.95, 0.97), roughness=0.18, coat=0.4, coat_rough=0.08)
    PALETTE["mint"] = new_material("Mimamo_Mint", (0.36, 0.78, 0.72), roughness=0.22, coat=0.35, coat_rough=0.1)
    PALETTE["mint_dark"] = new_material("Mimamo_MintDark", (0.22, 0.55, 0.52), roughness=0.28, coat=0.2)
    PALETTE["pink"] = new_material("Mimamo_Pink", (0.96, 0.58, 0.66), roughness=0.2, coat=0.4, coat_rough=0.08)
    PALETTE["pink_deep"] = new_material("Mimamo_PinkDeep", (0.92, 0.42, 0.53), roughness=0.22, coat=0.3)
    PALETTE["eye_teal"] = new_material("Mimamo_EyeTeal", (0.03, 0.30, 0.34), roughness=0.05, coat=0.6, coat_rough=0.02)
    PALETTE["eye_iris"] = new_material("Mimamo_EyeIris", (0.06, 0.55, 0.55), roughness=0.08, coat=0.5)
    PALETTE["eye_white"] = new_material("Mimamo_EyeHighlight", (1.0, 1.0, 1.0), roughness=0.05,
                                         emission_color=(1.0, 1.0, 1.0), emission_strength=1.4)
    PALETTE["blush"] = new_material("Mimamo_Blush", (0.95, 0.47, 0.53), roughness=0.55)
    PALETTE["mouth"] = new_material("Mimamo_Mouth", (0.62, 0.22, 0.32), roughness=0.35)
    PALETTE["mouth_inner"] = new_material("Mimamo_MouthInner", (0.42, 0.12, 0.2), roughness=0.5)
    PALETTE["brow"] = new_material("Mimamo_Brow", (0.30, 0.26, 0.30), roughness=0.4)
    PALETTE["nose"] = new_material("Mimamo_Nose", (0.90, 0.78, 0.80), roughness=0.3)
    PALETTE["phone_body"] = new_material("Mimamo_PhoneBody", (0.06, 0.06, 0.08), roughness=0.25, coat=0.5)
    PALETTE["phone_screen"] = new_material("Mimamo_PhoneScreen", (0.35, 0.85, 0.78), roughness=0.15,
                                            emission_color=(0.35, 0.9, 0.8), emission_strength=1.2)
    PALETTE["watch_screen"] = new_material("Mimamo_WatchScreen", (0.95, 0.55, 0.62), roughness=0.15,
                                             emission_color=(0.95, 0.55, 0.62), emission_strength=0.9)
    PALETTE["dark_trim"] = new_material("Mimamo_DarkTrim", (0.16, 0.42, 0.40), roughness=0.3)


# ---------------------------------------------------------------------------
# Geometry helpers
# ---------------------------------------------------------------------------
def add_uv_sphere(name, radius, location, scale=(1, 1, 1), material=None, segments=28, rings=18):
    bpy.ops.mesh.primitive_uv_sphere_add(radius=radius, location=location, segments=segments, ring_count=rings)
    obj = bpy.context.active_object
    obj.name = name
    obj.scale = scale
    bpy.ops.object.shade_smooth()
    if material:
        obj.data.materials.append(material)
    return obj


def add_cylinder(name, radius, depth, location, rotation=(0, 0, 0), scale=(1, 1, 1), material=None, verts=24, cap="ROUND"):
    bpy.ops.mesh.primitive_cylinder_add(radius=radius, depth=depth, location=location, vertices=verts)
    obj = bpy.context.active_object
    obj.name = name
    obj.rotation_euler = rotation
    obj.scale = scale
    bpy.ops.object.shade_smooth()
    if material:
        obj.data.materials.append(material)
    return obj


def add_cone(name, radius1, radius2, depth, location, rotation=(0, 0, 0), scale=(1, 1, 1), material=None, verts=24):
    bpy.ops.mesh.primitive_cone_add(radius1=radius1, radius2=radius2, depth=depth, location=location, vertices=verts)
    obj = bpy.context.active_object
    obj.name = name
    obj.rotation_euler = rotation
    obj.scale = scale
    bpy.ops.object.shade_smooth()
    if material:
        obj.data.materials.append(material)
    return obj


def add_torus(name, major_radius, minor_radius, location, rotation=(0, 0, 0), scale=(1, 1, 1), material=None):
    bpy.ops.mesh.primitive_torus_add(major_radius=major_radius, minor_radius=minor_radius, location=location,
                                      major_segments=32, minor_segments=14)
    obj = bpy.context.active_object
    obj.name = name
    obj.rotation_euler = rotation
    obj.scale = scale
    bpy.ops.object.shade_smooth()
    if material:
        obj.data.materials.append(material)
    return obj


def add_cube(name, size, location, rotation=(0, 0, 0), scale=(1, 1, 1), material=None, bevel=0.0):
    bpy.ops.mesh.primitive_cube_add(size=size, location=location)
    obj = bpy.context.active_object
    obj.name = name
    obj.rotation_euler = rotation
    obj.scale = scale
    if bevel > 0:
        bpy.ops.object.modifier_add(type='BEVEL')
        obj.modifiers["Bevel"].width = bevel
        obj.modifiers["Bevel"].segments = 3
        bpy.ops.object.modifier_apply(modifier="Bevel")
    bpy.ops.object.shade_smooth()
    if material:
        obj.data.materials.append(material)
    return obj


def apply_transforms(obj):
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)


def boolean_union(base_obj, other_objs):
    bpy.context.view_layer.objects.active = base_obj
    for o in other_objs:
        mod = base_obj.modifiers.new("Union", 'BOOLEAN')
        mod.operation = 'UNION'
        mod.object = o
        mod.solver = 'EXACT'
        bpy.ops.object.modifier_apply(modifier=mod.name)
    for o in other_objs:
        bpy.data.objects.remove(o, do_unlink=True)


def make_heart(name, size, location, rotation=(0, 0, 0), material=None):
    """Classic two-sphere-lobes + rotated-cube-point heart primitive."""
    r = size * 0.5
    lobe_l = add_uv_sphere(name + "_LobeL", r, (location[0] - r * 0.55, location[1], location[2] + r * 0.32),
                            segments=16, rings=10)
    lobe_r = add_uv_sphere(name + "_LobeR", r, (location[0] + r * 0.55, location[1], location[2] + r * 0.32),
                            segments=16, rings=10)
    base = add_cube(name + "_Base", 1.0, (location[0], location[1], location[2] - r * 0.28),
                     rotation=(0, math.radians(45), 0),
                     scale=(r * 0.98, r * 0.62, r * 0.98))
    boolean_union(base, [lobe_l, lobe_r])
    base.name = name
    bpy.ops.object.modifier_add(type='SUBSURF')
    base.modifiers["Subdivision"].levels = 2
    base.modifiers["Subdivision"].render_levels = 2
    bpy.context.view_layer.objects.active = base
    bpy.ops.object.modifier_apply(modifier="Subdivision")
    bpy.ops.object.shade_smooth()
    base.rotation_euler = rotation
    apply_transforms(base)
    if material:
        base.data.materials.clear()
        base.data.materials.append(material)
    return base


def build_morph_blob(name, center, radii, z_open, z_closed, material, segments=20, rings=14, y_offset=0.0,
                      anchor_top=False):
    """
    Build an ellipsoid whose REST (basis) shape is squashed flat (z scaled by z_open)
    and whose shape-key target (name 'Morph') expands it (z scaled by z_closed).
    Used for eyelids (Blink) and mouth (Talk).

    anchor_top=True keeps the TOP of the blob fixed at z=+radii[2] in both states
    (so it behaves like an eyelid hinging down from the top instead of scaling
    symmetrically around its own center) -- open=thin sliver tucked at the top,
    closed=fully covers down to the bottom of the eye.
    """
    def scaled_z(z, scale):
        if anchor_top:
            return (1.0 - (1.0 - z) * scale) * radii[2]
        return z * radii[2] * scale

    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=segments, v_segments=rings, radius=1.0)
    unit_coords = [v.co.copy() for v in bm.verts]
    for v in bm.verts:
        v.co = Vector((v.co.x * radii[0], v.co.y * radii[1] + y_offset, scaled_z(v.co.z, z_open)))
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()

    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.location = center
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.shade_smooth()
    if material:
        mesh.materials.append(material)

    obj.shape_key_add(name="Basis", from_mix=False)
    sk = obj.shape_key_add(name="Morph", from_mix=False)
    for i, c in enumerate(unit_coords):
        sk.data[i].co = Vector((c.x * radii[0], c.y * radii[1] + y_offset, scaled_z(c.z, z_closed)))
    sk.value = 0.0
    return obj, sk


def parent_rigid_to_bone(obj, armature_obj, bone_name):
    bpy.ops.object.select_all(action='DESELECT')
    armature_obj.data.bones.active = armature_obj.data.bones[bone_name]
    obj.select_set(True)
    armature_obj.select_set(True)
    bpy.context.view_layer.objects.active = armature_obj
    bpy.ops.object.parent_set(type='BONE', keep_transform=True)


def parent_skin_to_bones(obj, armature_obj):
    obj.parent = armature_obj
    mod = obj.modifiers.new("Armature", 'ARMATURE')
    mod.object = armature_obj


print("Materials, mesh & rig helpers ready.")

# ---------------------------------------------------------------------------
# Layout constants (meters). Character faces -Y. +X = character's LEFT side.
# ---------------------------------------------------------------------------
L = {
    "boot_h": 0.14,
    "leg_bottom": 0.14,
    "leg_top": 0.30,
    "knee_z": 0.27,
    "torso_bottom": 0.30,
    "torso_top": 0.62,
    "torso_cz": 0.455,
    "belt_z": 0.345,
    "shoulder_z": 0.575,
    "shoulder_x": 0.225,
    "neck_z": 0.635,
    "head_cz": 0.845,
    "head_r": 0.255,
}


def build_character():
    """Builds every mesh part in world space (not yet parented to bones)."""
    P = PALETTE
    parts = {}

    # ---- LEGS & BOOTS -----------------------------------------------------
    for side, sx in (("L", 1), ("R", -1)):
        x = L["shoulder_x"] * 0.42 * sx
        leg = add_cylinder(f"Leg_{side}", 0.072, L["leg_top"] - L["leg_bottom"],
                            (x, 0, (L["leg_top"] + L["leg_bottom"]) / 2), material=P["white"])
        parts[f"leg_{side}"] = leg
        knee = add_torus(f"KneeTrim_{side}", 0.075, 0.018, (x, 0, L["knee_z"]),
                          rotation=(math.radians(90), 0, 0), material=P["mint"])
        parts[f"knee_{side}"] = knee
        boot = add_uv_sphere(f"Boot_{side}", 0.1, (x, -0.01, L["boot_h"] * 0.52),
                              scale=(1.05, 1.35, 0.85), material=P["white"])
        parts[f"boot_{side}"] = boot
        boot_trim = add_torus(f"BootTrim_{side}", 0.098, 0.02, (x, -0.01, L["boot_h"] * 0.86),
                               rotation=(math.radians(90), 0, 0), scale=(1.05, 1.3, 1), material=P["mint"])
        parts[f"boot_trim_{side}"] = boot_trim

    # ---- TORSO --------------------------------------------------------------
    torso = add_uv_sphere("Torso", 0.205, (0, 0, L["torso_cz"]),
                           scale=(1.05, 0.92, 1.28), material=P["white"])
    parts["torso"] = torso

    belt = add_torus("Belt", 0.205, 0.028, (0, 0, L["belt_z"]),
                      rotation=(math.radians(90), 0, 0), scale=(1.02, 1.15, 1), material=P["dark_trim"])
    parts["belt"] = belt
    buckle = add_cube("BeltBuckle", 1.0, (0, -0.205, L["belt_z"]), scale=(0.05, 0.018, 0.05),
                       material=P["mint"], bevel=0.006)
    parts["buckle"] = buckle

    # chest emblem: mint badge + pink heart inset
    chest_badge = add_uv_sphere("ChestBadge", 0.085, (0, -0.19, L["torso_cz"] + 0.03),
                                 scale=(1.15, 0.35, 1.3), material=P["mint"])
    parts["chest_badge"] = chest_badge
    chest_heart = make_heart("ChestHeart", 0.085, (0, -0.225, L["torso_cz"] + 0.03), material=P["pink"])
    parts["chest_heart"] = chest_heart

    # scarf / collar (mint with pink lining peeking out beneath)
    collar_pink = add_torus("CollarLining", 0.145, 0.032, (0, 0, L["neck_z"] - 0.01),
                             rotation=(math.radians(90), 0, 0), material=P["pink"])
    parts["collar_pink"] = collar_pink
    collar = add_torus("Collar", 0.14, 0.03, (0, 0, L["neck_z"]),
                        rotation=(math.radians(90), 0, 0), material=P["mint"])
    parts["collar"] = collar

    # ---- CAPE (skinned to cape01 / cape02 for real cloth-like sway) --------
    cape_main, cape_lining, cape_vgroups = build_cape()
    parts["cape_main"] = cape_main
    parts["cape_lining"] = cape_lining
    parts["cape_vgroups"] = cape_vgroups

    # ---- ARMS ---------------------------------------------------------------
    # Explicit (non-mirrored) per-side geometry: LEFT arm rests already raised
    # to the side (its "wave" animation only needs a small wrist wiggle on top),
    # RIGHT arm rests bent at the elbow presenting the phone at chest height.
    shoulder_L = Vector((L["shoulder_x"], 0, L["shoulder_z"]))
    elbow_L = shoulder_L + Vector((0.045, -0.04, 0.11))
    wrist_L = elbow_L + Vector((0.02, -0.05, 0.16))
    hand_L = wrist_L + Vector((0.01, -0.03, 0.05))

    shoulder_R = Vector((-L["shoulder_x"], 0, L["shoulder_z"]))
    elbow_R = shoulder_R + Vector((0.015, -0.05, -0.13))
    wrist_R = elbow_R + Vector((0.0, -0.15, 0.10))
    hand_R = wrist_R + Vector((0.0, -0.045, 0.02))

    arm_defs = {
        "L": dict(sx=1, shoulder_v=shoulder_L, elbow_v=elbow_L, wrist_v=wrist_L, hand_v=hand_L),
        "R": dict(sx=-1, shoulder_v=shoulder_R, elbow_v=elbow_R, wrist_v=wrist_R, hand_v=hand_R),
    }
    for side, d in arm_defs.items():
        shoulder = d["shoulder_v"]
        elbow = d["elbow_v"]
        wrist = d["wrist_v"]

        sleeve = add_torus(f"SleeveTrim_{side}", 0.075, 0.017, shoulder + Vector((0, 0, -0.01)),
                            rotation=(math.radians(80), 0, 0), material=P["mint"])
        parts[f"sleeve_{side}"] = sleeve

        upper_dir = (elbow - shoulder)
        upper_mid = shoulder.lerp(elbow, 0.5)
        upperarm = add_uv_sphere(f"UpperArm_{side}", 0.062, upper_mid, scale=(1, 1, upper_dir.length / 0.062 / 2 * 1.15),
                                  material=P["white"])
        align_ellipsoid_to_dir(upperarm, upper_dir)
        parts[f"upperarm_{side}"] = upperarm

        lower_dir = (wrist - elbow)
        lower_mid = elbow.lerp(wrist, 0.5)
        lowerarm = add_uv_sphere(f"LowerArm_{side}", 0.052, lower_mid, scale=(1, 1, lower_dir.length / 0.052 / 2 * 1.15),
                                  material=P["white"])
        align_ellipsoid_to_dir(lowerarm, lower_dir)
        parts[f"lowerarm_{side}"] = lowerarm

        cuff = add_torus(f"Cuff_{side}", 0.055, 0.013, wrist, rotation=(math.radians(80), 0, 0), material=P["mint"])
        parts[f"cuff_{side}"] = cuff

        hand = add_uv_sphere(f"Hand_{side}", 0.058, d["hand_v"],
                              scale=(1.05, 0.85, 1.05), material=P["white"])
        parts[f"hand_{side}"] = hand

    # Left wrist wearable (smartwatch) on the LEFT lower arm
    watch_center = arm_defs["L"]["elbow_v"].lerp(arm_defs["L"]["wrist_v"], 0.78)
    watch_band = add_torus("WatchBand", 0.052, 0.017, watch_center, rotation=(math.radians(80), 0, 0),
                            material=P["mint_dark"])
    parts["watch_band"] = watch_band
    watch_face = add_cube("WatchFace", 1.0, watch_center + Vector((0.028, 0, 0)),
                           rotation=(0, math.radians(90), 0), scale=(0.006, 0.03, 0.03),
                           material=P["phone_body"], bevel=0.004)
    parts["watch_face"] = watch_face
    watch_screen = add_cube("WatchScreen", 1.0, watch_center + Vector((0.033, 0, 0)),
                             rotation=(0, math.radians(90), 0), scale=(0.002, 0.021, 0.021),
                             material=P["watch_screen"])
    parts["watch_screen"] = watch_screen

    # Phone in RIGHT hand, presented facing the camera at chest height
    phone_center = arm_defs["R"]["hand_v"] + Vector((0, -0.055, 0.03))
    phone_body = add_cube("PhoneBody", 1.0, phone_center, rotation=(math.radians(70), 0, math.radians(-8)),
                           scale=(0.045, 0.01, 0.09), material=P["phone_body"], bevel=0.008)
    parts["phone_body"] = phone_body
    phone_screen = add_cube("PhoneScreen", 1.0, phone_center + Vector((0, -0.011, 0.002)),
                             rotation=(math.radians(70), 0, math.radians(-8)),
                             scale=(0.038, 0.001, 0.078), material=P["phone_screen"])
    parts["phone_screen"] = phone_screen

    parts["arm_defs"] = arm_defs


    # ---- HEAD -----------------------------------------------------------
    head_c = Vector((0, 0, L["head_cz"]))
    head = add_uv_sphere("Head", L["head_r"], head_c, scale=(1.0, 0.97, 1.06), material=P["white"], segments=36, rings=24)
    parts["head"] = head

    for side, sx in (("L", 1), ("R", -1)):
        ear = add_cylinder(f"EarDisc_{side}", 0.05, 0.022, head_c + Vector((sx * L["head_r"] * 0.97, 0.01, -0.01)),
                            rotation=(0, math.radians(90), 0), material=P["mint"])
        parts[f"ear_{side}"] = ear

    # Forehead heart crest: mint shield + pink heart
    crest_c = head_c + Vector((0, -L["head_r"] * 0.86, L["head_r"] * 0.42))
    crest = add_uv_sphere("ForeheadCrest", 0.075, crest_c, scale=(1.25, 0.35, 1.05), material=P["mint"])
    parts["crest"] = crest
    crest_heart = make_heart("ForeheadHeart", 0.062, crest_c + Vector((0, -0.018, 0)), material=P["pink"])
    parts["crest_heart"] = crest_heart

    # Antenna: stalk + heart tip
    antenna_base = head_c + Vector((0, -0.01, L["head_r"] * 1.02))
    antenna_mid = antenna_base + Vector((0, 0, 0.09))
    antenna_stalk = add_cylinder("AntennaStalk", 0.013, 0.16, antenna_base + Vector((0, 0, 0.08)),
                                  material=P["mint_dark"])
    parts["antenna_stalk"] = antenna_stalk
    antenna_tip = antenna_mid + Vector((0, 0, 0.075))
    antenna_heart = make_heart("AntennaHeart", 0.09, antenna_tip, material=P["pink"])
    parts["antenna_heart"] = antenna_heart
    parts["antenna_base_v"] = antenna_base
    parts["antenna_tip_v"] = antenna_tip

    # ---- FACE -------------------------------------------------------------
    eye_c = {}
    for side, sx in (("L", 1), ("R", -1)):
        ex = sx * L["head_r"] * 0.5
        ec = head_c + Vector((ex, -L["head_r"] * 0.92, 0.02))
        eye_c[side] = ec

        eyeball = add_uv_sphere(f"Eye_{side}", 0.082, ec, scale=(1.0, 0.62, 1.22), material=P["eye_teal"], segments=24, rings=16)
        parts[f"eye_{side}"] = eyeball

        iris_hi = add_uv_sphere(f"EyeIrisRing_{side}", 0.05, ec + Vector((0, -0.02, 0.0)), scale=(1.0, 0.35, 1.0),
                                 material=P["eye_iris"], segments=20, rings=12)
        parts[f"eye_iris_{side}"] = iris_hi

        hi1 = add_uv_sphere(f"EyeHighlight1_{side}", 0.026, ec + Vector((-0.028 * sx, -0.045, 0.032)),
                             material=P["eye_white"], segments=12, rings=8)
        parts[f"eye_hi1_{side}"] = hi1
        hi2 = add_uv_sphere(f"EyeHighlight2_{side}", 0.012, ec + Vector((0.02 * sx, -0.05, -0.02)),
                             material=P["eye_white"], segments=10, rings=6)
        parts[f"eye_hi2_{side}"] = hi2

        # eyelashes: three tiny curved slivers at the outer-top corner
        for i in range(3):
            lash = add_cone(f"Lash{i}_{side}", 0.006, 0.001, 0.045,
                             ec + Vector((sx * (0.055 + i * 0.012), -0.06, 0.058 + i * 0.006)),
                             rotation=(math.radians(-58), 0, sx * math.radians(-20 + i * 14)),
                             material=P["brow"])
            parts[f"lash{i}_{side}"] = lash

        # eyebrow: small flattened capsule above the eye
        brow = add_uv_sphere(f"Eyebrow_{side}", 0.055, ec + Vector((0.0, -0.05, 0.095)),
                              scale=(1.1, 0.35, 0.28), material=P["brow"], segments=16, rings=10)
        brow.rotation_euler = (math.radians(-8), 0, sx * math.radians(8))
        apply_transforms(brow)
        parts[f"eyebrow_{side}"] = brow

        # blush -- pulled forward of the head surface (previous placement sat just inside the
        # head sphere and was almost entirely occluded/shadowed) and enlarged slightly for visibility.
        blush = add_uv_sphere(f"Blush_{side}", 0.062, ec + Vector((0.035 * sx, -0.058, -0.058)),
                               scale=(1.0, 0.32, 0.78), material=P["blush"], segments=16, rings=10)
        parts[f"blush_{side}"] = blush

        # eyelid morph blob (Basis = thin sliver tucked at top, 'Morph' shape key = closed, covers whole eye)
        lid_obj, lid_sk = build_morph_blob(
            f"Eyelid_{side}", ec + Vector((0, -0.012, 0.0)),
            radii=(0.095, 0.05, 0.105), z_open=0.03, z_closed=1.02,
            material=P["white"], segments=20, rings=14, y_offset=-0.005, anchor_top=True)
        parts[f"eyelid_{side}"] = lid_obj
        parts[f"eyelid_sk_{side}"] = lid_sk

    parts["eye_c"] = eye_c

    # nose
    nose = add_uv_sphere("Nose", 0.018, head_c + Vector((0, -L["head_r"] * 1.0, -0.05)),
                          scale=(1, 0.6, 0.8), material=P["nose"], segments=10, rings=8)
    parts["nose"] = nose

    # mouth morph blob (Basis = small smile, 'Morph' shape key = open talking mouth)
    mouth_c = head_c + Vector((0, -L["head_r"] * 0.94, -0.115))
    mouth_obj, mouth_sk = build_morph_blob(
        "Mouth", mouth_c, radii=(0.05, 0.028, 0.028), z_open=0.55, z_closed=1.6,
        material=P["mouth"], segments=18, rings=12)
    parts["mouth"] = mouth_obj
    parts["mouth_sk"] = mouth_sk

    # smile corners: tiny upturned tips at each end of the mouth blob so the closed/idle
    # mouth silhouette reads as a warm smile instead of a flat neutral line.
    for side, sx in (("L", 1), ("R", -1)):
        corner = add_uv_sphere(f"SmileCorner_{side}", 0.02, mouth_c + Vector((sx * 0.044, -0.006, 0.016)),
                                scale=(0.85, 0.6, 0.6), material=P["mouth"], segments=12, rings=8)
        corner.rotation_euler = (math.radians(-18), 0, sx * math.radians(-30))
        apply_transforms(corner)
        parts[f"smile_corner_{side}"] = corner

    parts["head_c"] = head_c
    return parts


def align_ellipsoid_to_dir(obj, direction):
    """Rotate a Z-stretched ellipsoid so its long axis points along `direction`."""
    direction = direction.normalized()
    z = Vector((0, 0, 1))
    rot = z.rotation_difference(direction)
    obj.rotation_euler = rot.to_euler()
    apply_transforms(obj)


def build_cape():
    """A segmented, gently curved cape plane with vertex groups for cape01/cape02 bones."""
    rows = 6
    cols = 9
    top_z = L["shoulder_z"] + 0.02
    bottom_z = 0.16
    half_w_top = 0.16
    half_w_bottom = 0.235
    y_top = 0.12
    y_curve = 0.10  # how far the cape drifts backward as it falls

    bm = bmesh.new()
    grid = [[None] * cols for _ in range(rows)]
    for r in range(rows):
        t = r / (rows - 1)
        z = top_z + (bottom_z - top_z) * t
        half_w = half_w_top + (half_w_bottom - half_w_top) * t
        y = y_top + y_curve * (t ** 1.6)
        for c in range(cols):
            u = c / (cols - 1)
            x = (u - 0.5) * 2 * half_w
            v = bm.verts.new((x, y, z))
            grid[r][c] = v
    bm.verts.ensure_lookup_table()
    for r in range(rows - 1):
        for c in range(cols - 1):
            bm.faces.new((grid[r][c], grid[r][c + 1], grid[r + 1][c + 1], grid[r + 1][c]))

    mesh = bpy.data.meshes.new("Cape")
    bm.to_mesh(mesh)
    bm.free()
    cape = bpy.data.objects.new("Cape", mesh)
    bpy.context.collection.objects.link(cape)
    mesh.materials.append(PALETTE["mint"])
    bpy.context.view_layer.objects.active = cape
    bpy.ops.object.shade_smooth()

    # Group names MUST match the armature bone names exactly for automatic
    # Armature-modifier vertex weight lookup.
    vg_body = cape.vertex_groups.new(name="body")
    vg_c1 = cape.vertex_groups.new(name="cape01")
    vg_c2 = cape.vertex_groups.new(name="cape02")

    idx = 0
    for r in range(rows):
        t = r / (rows - 1)
        # smooth blend across body -> cape01 -> cape02 for a natural bend
        w_body = max(0.0, 1.0 - t / 0.35)
        w_c2 = max(0.0, (t - 0.55) / 0.45)
        w_c1 = max(0.0, 1.0 - w_body - w_c2)
        for c in range(cols):
            vg_body.add([idx], w_body, 'REPLACE')
            vg_c1.add([idx], w_c1, 'REPLACE')
            vg_c2.add([idx], w_c2, 'REPLACE')
            idx += 1

    # Lining: a slightly larger pink duplicate placed just behind, so edges peek out as trim
    lining = cape.copy()
    lining.data = mesh.copy()
    lining.name = "CapeLining"
    lining.data.materials.clear()
    lining.data.materials.append(PALETTE["pink"])
    bpy.context.collection.objects.link(lining)
    for v in lining.data.vertices:
        v.co.y += 0.012
        # push edges outward a touch for a visible lining border
    bpy.context.view_layer.objects.active = lining
    bpy.ops.object.shade_smooth()

    return cape, lining, (vg_body.name, vg_c1.name, vg_c2.name)


# ---------------------------------------------------------------------------
# Armature
# ---------------------------------------------------------------------------
def build_armature(parts):
    arm_data = bpy.data.armatures.new("MimamoRigData")
    arm_obj = bpy.data.objects.new("MimamoRig", arm_data)
    bpy.context.collection.objects.link(arm_obj)
    bpy.context.view_layer.objects.active = arm_obj
    bpy.ops.object.mode_set(mode='EDIT')
    eb = arm_data.edit_bones

    def mkbone(name, head, tail, parent=None, connect=False):
        b = eb.new(name)
        b.head = head
        b.tail = tail
        if parent:
            b.parent = eb[parent]
            b.use_connect = connect
        b.use_deform = True
        return b

    head_c = parts["head_c"]
    eye_c = parts["eye_c"]
    arm_defs = parts["arm_defs"]

    mkbone("root", (0, 0, 0), (0, 0, 0.06))
    mkbone("body", (0, 0, L["torso_bottom"]), (0, 0, L["shoulder_z"]), parent="root")
    mkbone("head", (0, 0, L["neck_z"]), (0, 0, L["head_cz"] + L["head_r"] * 0.65), parent="body")

    for side, sx in (("L", 1), ("R", -1)):
        ec = eye_c[side]
        mkbone(f"eye_{side}", ec, ec + Vector((0, -0.06, 0)), parent="head")
        mkbone(f"eyebrow_{side}", ec + Vector((0, -0.05, 0.09)), ec + Vector((0, -0.05, 0.13)), parent="head")

    mouth_c = head_c + Vector((0, -L["head_r"] * 0.94, -0.115))
    mkbone("jaw", mouth_c + Vector((0, 0.04, 0.03)), mouth_c + Vector((0, -0.05, -0.04)), parent="head")

    for side in ("L", "R"):
        d = arm_defs[side]
        mkbone(f"upperarm_{side}", d["shoulder_v"], d["elbow_v"], parent="body")
        mkbone(f"lowerarm_{side}", d["elbow_v"], d["wrist_v"], parent=f"upperarm_{side}", connect=True)
        mkbone(f"hand_{side}", d["wrist_v"], d["hand_v"] + Vector((0, -0.05, 0)), parent=f"lowerarm_{side}", connect=True)

    # cape bones follow the same falloff curve used in build_cape()
    def cape_row_point(t):
        top_z = L["shoulder_z"] + 0.02
        bottom_z = 0.16
        y_top = 0.12
        y_curve = 0.10
        z = top_z + (bottom_z - top_z) * t
        y = y_top + y_curve * (t ** 1.6)
        return Vector((0, y, z))

    cape_p_body_end = cape_row_point(0.35)
    cape_p_mid = cape_row_point(0.68)
    cape_p_tip = cape_row_point(1.0) + Vector((0, 0.02, -0.02))
    mkbone("cape01", cape_p_body_end, cape_p_mid, parent="body")
    mkbone("cape02", cape_p_mid, cape_p_tip, parent="cape01", connect=True)

    ab = parts["antenna_base_v"]
    at = parts["antenna_tip_v"]
    a_mid = ab.lerp(at, 0.45)
    mkbone("antenna01", ab, a_mid, parent="head")
    mkbone("antenna02", a_mid, at + Vector((0, 0, 0.04)), parent="antenna01", connect=True)

    bpy.ops.object.mode_set(mode='OBJECT')
    for pb in arm_obj.pose.bones:
        pb.rotation_mode = 'XYZ'
    return arm_obj


RIGID_BONE_MAP = {
    "leg_L": "body", "leg_R": "body",
    "knee_L": "body", "knee_R": "body",
    "boot_L": "body", "boot_R": "body",
    "boot_trim_L": "body", "boot_trim_R": "body",
    "torso": "body", "belt": "body", "buckle": "body",
    "chest_badge": "body", "chest_heart": "body",
    "collar": "body", "collar_pink": "body",
    "sleeve_L": "upperarm_L", "sleeve_R": "upperarm_R",
    "upperarm_L": "upperarm_L", "upperarm_R": "upperarm_R",
    "lowerarm_L": "lowerarm_L", "lowerarm_R": "lowerarm_R",
    "cuff_L": "lowerarm_L", "cuff_R": "lowerarm_R",
    "hand_L": "hand_L", "hand_R": "hand_R",
    "watch_band": "lowerarm_L", "watch_face": "lowerarm_L", "watch_screen": "lowerarm_L",
    "phone_body": "hand_R", "phone_screen": "hand_R",
    "head": "head",
    "ear_L": "head", "ear_R": "head",
    "crest": "head", "crest_heart": "head",
    "nose": "head",
    "eye_L": "eye_L", "eye_R": "eye_R",
    "eye_iris_L": "eye_L", "eye_iris_R": "eye_R",
    "eye_hi1_L": "eye_L", "eye_hi1_R": "eye_R",
    "eye_hi2_L": "eye_L", "eye_hi2_R": "eye_R",
    "lash0_L": "eye_L", "lash1_L": "eye_L", "lash2_L": "eye_L",
    "lash0_R": "eye_R", "lash1_R": "eye_R", "lash2_R": "eye_R",
    "eyebrow_L": "eyebrow_L", "eyebrow_R": "eyebrow_R",
    "blush_L": "head", "blush_R": "head",
    "eyelid_L": "head", "eyelid_R": "head",
    "mouth": "jaw",
    "smile_corner_L": "jaw", "smile_corner_R": "jaw",
    "antenna_stalk": "antenna01",
    "antenna_heart": "antenna02",
}


def rig_and_parent(parts, arm_obj):
    for key, bone in RIGID_BONE_MAP.items():
        obj = parts.get(key)
        if obj is None:
            continue
        parent_rigid_to_bone(obj, arm_obj, bone)

    for cape_key in ("cape_main", "cape_lining"):
        parent_skin_to_bones(parts[cape_key], arm_obj)


# ---------------------------------------------------------------------------
# Animation - single looping "MimamoIdle" action, frames 1-120 @ 24fps (5s)
# ---------------------------------------------------------------------------
def iter_fcurves(action):
    """Blender 5.x uses layered actions (layers/strips/channelbags); fall back
    to the legacy .fcurves for older files if ever reopened in an older API."""
    if action is None:
        return
    if hasattr(action, "fcurves"):
        for fc in action.fcurves:
            yield fc
        return
    for layer in action.layers:
        for strip in layer.strips:
            for cb in getattr(strip, "channelbags", []):
                for fc in cb.fcurves:
                    yield fc


def d2r(t):
    return tuple(math.radians(v) for v in t)


BONE_ANIM = {
    "root": {
        "loc": [(1, (0, 0, 0)), (60, (0.004, 0, 0)), (120, (0, 0, 0))],
    },
    "body": {
        "loc": [(1, (0, 0, 0.0)), (30, (0, 0, 0.006)), (60, (0, 0, 0.010)),
                (90, (0, 0, 0.006)), (120, (0, 0, 0.0))],
        "scale": [(1, (1, 1, 1)), (30, (1.01, 1.0, 0.995)), (60, (1.02, 1.0, 0.99)),
                  (90, (1.01, 1.0, 0.995)), (120, (1, 1, 1))],
    },
    "head": {
        "rot": [(1, (0, 0, 0)), (40, (2, 0, 6)), (80, (-1, 0, -6)), (120, (0, 0, 0))],
    },
    "eye_L": {
        "rot": [(1, (0, 0, 0)), (20, (0, 0, -14)), (45, (0, 0, 0)), (70, (0, 0, 12)),
                (95, (2, 0, 0)), (120, (0, 0, 0))],
    },
    "eye_R": {
        "rot": [(1, (0, 0, 0)), (20, (0, 0, -14)), (45, (0, 0, 0)), (70, (0, 0, 12)),
                (95, (2, 0, 0)), (120, (0, 0, 0))],
    },
    "eyebrow_L": {
        "rot": [(1, (0, 0, 0)), (45, (3, 0, 0)), (60, (0, 0, 0)), (100, (-2, 0, 0)), (120, (0, 0, 0))],
    },
    "eyebrow_R": {
        "rot": [(1, (0, 0, 0)), (45, (3, 0, 0)), (60, (0, 0, 0)), (100, (-2, 0, 0)), (120, (0, 0, 0))],
    },
    "jaw": {
        "rot": [(1, (0, 0, 0)), (38, (0, 0, 0)), (42, (-6, 0, 0)), (47, (-1, 0, 0)),
                (52, (-6, 0, 0)), (57, (-1, 0, 0)), (62, (-5, 0, 0)), (67, (0, 0, 0)),
                (72, (-6, 0, 0)), (77, (-1, 0, 0)), (82, (0, 0, 0)), (120, (0, 0, 0))],
    },
    "upperarm_R": {
        "rot": [(1, (0, 0, 0)), (60, (2, 0, 1)), (120, (0, 0, 0))],
    },
    "lowerarm_R": {
        "rot": [(1, (0, 0, 0)), (60, (-1, 0, 0)), (120, (0, 0, 0))],
    },
    "hand_R": {
        "rot": [(1, (0, 0, 0)), (60, (1, 0, 0)), (120, (0, 0, 0))],
    },
    "upperarm_L": {
        "rot": [(1, (0, 0, 0)), (30, (2, 0, -3)), (60, (0, 0, 0)), (90, (-2, 0, 3)), (120, (0, 0, 0))],
    },
    "lowerarm_L": {
        "rot": [(1, (0, 0, 0)), (15, (0, 0, 20)), (30, (0, 0, -20)), (45, (0, 0, 20)),
                (60, (0, 0, -20)), (75, (0, 0, 20)), (90, (0, 0, -20)), (105, (0, 0, 20)),
                (120, (0, 0, 0))],
    },
    "hand_L": {
        "rot": [(1, (0, 0, 0)), (19, (0, 0, -12)), (34, (0, 0, 12)), (49, (0, 0, -12)),
                (64, (0, 0, 12)), (79, (0, 0, -12)), (94, (0, 0, 12)), (109, (0, 0, -12)),
                (120, (0, 0, 0))],
    },
    "cape01": {
        "rot": [(1, (0, 0, 0)), (30, (6, 0, 3)), (60, (0, 0, 0)), (90, (-6, 0, -3)), (120, (0, 0, 0))],
    },
    "cape02": {
        "rot": [(1, (0, 0, 0)), (45, (9, 0, -4)), (90, (-9, 0, 4)), (120, (0, 0, 0))],
    },
    "antenna01": {
        "rot": [(1, (0, 0, 0)), (30, (4, 0, 2)), (60, (0, 0, 0)), (90, (-4, 0, -2)), (120, (0, 0, 0))],
    },
    "antenna02": {
        "rot": [(1, (0, 0, 0)), (20, (8, 0, 3)), (40, (-6, 0, -2)), (60, (4, 0, 2)),
                (80, (-8, 0, -3)), (100, (6, 0, 2)), (120, (0, 0, 0))],
        "scale": [(1, (1, 1, 1)), (10, (1.15, 1.15, 1.15)), (18, (1, 1, 1)),
                  (55, (1, 1, 1)), (64, (1.15, 1.15, 1.15)), (72, (1, 1, 1)), (120, (1, 1, 1))],
    },
}

SHAPEKEY_ANIM = {
    "eyelid_L": [(1, 0.0), (24, 0.0), (26, 1.0), (28, 0.0), (94, 0.0), (96, 1.0), (98, 0.0), (120, 0.0)],
    "eyelid_R": [(1, 0.0), (24, 0.0), (26, 1.0), (28, 0.0), (94, 0.0), (96, 1.0), (98, 0.0), (120, 0.0)],
    "mouth": [(1, 0.0), (38, 0.0), (42, 0.85), (47, 0.15), (52, 0.9), (57, 0.1), (62, 0.8),
              (67, 0.05), (72, 0.85), (77, 0.1), (82, 0.0), (120, 0.0)],
}


def animate(arm_obj, parts):
    action = bpy.data.actions.new("MimamoIdle")
    arm_obj.animation_data_create()
    arm_obj.animation_data.action = action

    for bone_name, chans in BONE_ANIM.items():
        pb = arm_obj.pose.bones[bone_name]
        for frame, val in chans.get("loc", []):
            pb.location = val
            pb.keyframe_insert(data_path="location", frame=frame)
        for frame, val in chans.get("rot", []):
            pb.rotation_euler = d2r(val)
            pb.keyframe_insert(data_path="rotation_euler", frame=frame)
        for frame, val in chans.get("scale", []):
            pb.scale = val
            pb.keyframe_insert(data_path="scale", frame=frame)

    sk_lookup = {"eyelid_L": "eyelid_sk_L", "eyelid_R": "eyelid_sk_R", "mouth": "mouth_sk"}
    for sk_key, keys in SHAPEKEY_ANIM.items():
        sk = parts[sk_lookup[sk_key]]
        for frame, val in keys:
            sk.value = val
            sk.keyframe_insert(data_path="value", frame=frame)
        # crisp, linear in/out for blink & talk pulses
        sk_action = sk.id_data.animation_data.action
        for fc in iter_fcurves(sk_action):
            if fc.data_path == 'key_blocks["Morph"].value':
                for kp in fc.keyframe_points:
                    kp.interpolation = 'LINEAR'

    action.use_cyclic = True

    # Push every animated object's action onto an NLA track with a SHARED name
    # ("MimamoIdle"). Blender's glTF exporter (NLA_TRACKS mode) merges tracks
    # that share a name across different objects into a single combined
    # animation clip, so blink/talk/body all play together as one loop in
    # simple web viewers instead of 4 separate un-synced animations.
    def push_to_shared_nla(anim_data, act, track_name="MimamoIdle"):
        track = anim_data.nla_tracks.new()
        track.name = track_name
        strip = track.strips.new(track_name, int(act.frame_range[0]), act)
        strip.action_frame_start = act.frame_range[0]
        strip.action_frame_end = act.frame_range[1]
        anim_data.action = None

    push_to_shared_nla(arm_obj.animation_data, action)
    for sk_key in ("eyelid_L", "eyelid_R", "mouth"):
        sk = parts[sk_lookup[sk_key]]
        key_data = sk.id_data  # the Key datablock owning the shape-key fcurves
        push_to_shared_nla(key_data.animation_data, key_data.animation_data.action)

    return action


# ---------------------------------------------------------------------------
# Camera / lighting / render / export
# ---------------------------------------------------------------------------
def setup_camera_and_lights():
    cam_data = bpy.data.cameras.new("PosterCam")
    cam_data.lens = 36
    cam_obj = bpy.data.objects.new("PosterCam", cam_data)
    bpy.context.collection.objects.link(cam_obj)
    cam_obj.location = (0.0, -2.75, 0.62)
    cam_obj.rotation_euler = (math.radians(90), 0, 0)
    bpy.context.scene.camera = cam_obj

    key = bpy.data.lights.new("KeyLight", 'AREA')
    key.energy = 260
    key.size = 1.2
    key_obj = bpy.data.objects.new("KeyLight", key)
    bpy.context.collection.objects.link(key_obj)
    key_obj.location = (-1.4, -1.6, 1.9)
    key_obj.rotation_euler = (math.radians(55), 0, math.radians(-35))

    fill = bpy.data.lights.new("FillLight", 'AREA')
    fill.energy = 90
    fill.size = 1.5
    fill_obj = bpy.data.objects.new("FillLight", fill)
    bpy.context.collection.objects.link(fill_obj)
    fill_obj.location = (1.6, -1.2, 1.0)
    fill_obj.rotation_euler = (math.radians(70), 0, math.radians(40))

    rim = bpy.data.lights.new("RimLight", 'AREA')
    rim.energy = 140
    rim.size = 1.0
    rim_obj = bpy.data.objects.new("RimLight", rim)
    bpy.context.collection.objects.link(rim_obj)
    rim_obj.location = (0, 1.6, 1.4)
    rim_obj.rotation_euler = (math.radians(110), 0, 0)

    return cam_obj


def configure_render(resolution=(1200, 1600), transparent=True, samples=64):
    scn = bpy.context.scene
    try:
        scn.render.engine = 'BLENDER_EEVEE_NEXT'
    except TypeError:
        scn.render.engine = 'BLENDER_EEVEE'
    scn.render.resolution_x = resolution[0]
    scn.render.resolution_y = resolution[1]
    scn.render.film_transparent = transparent
    scn.render.image_settings.file_format = 'PNG'
    scn.render.image_settings.color_mode = 'RGBA'
    scn.view_settings.view_transform = 'Standard'
    if hasattr(scn.eevee, "taa_render_samples"):
        scn.eevee.taa_render_samples = samples
    world = bpy.data.worlds.new("MimamoWorld")
    scn.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    if bg:
        bg.inputs[0].default_value = (0.06, 0.06, 0.07, 1.0)
        bg.inputs[1].default_value = 0.15


def render_poster(path, frame=15):
    scn = bpy.context.scene
    scn.frame_set(frame)
    scn.render.filepath = path
    bpy.ops.render.render(write_still=True)


def render_preview_frames(folder, step=4):
    scn = bpy.context.scene
    for f in range(FRAME_START, FRAME_END + 1, step):
        scn.frame_set(f)
        scn.render.filepath = os.path.join(folder, f"frame_{f:04d}.png")
        bpy.ops.render.render(write_still=True)


def export_glb(path):
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format='GLB',
        use_selection=False,
        export_animations=True,
        export_animation_mode='NLA_TRACKS',
        export_merge_animation='NLA_TRACK',
        export_frame_range=False,
        export_force_sampling=True,
        export_nla_strips=True,
        export_nla_strips_merged_animation_name='MimamoIdle',
        export_optimize_animation_size=True,
        export_morph=True,
        export_skins=True,
        export_yup=True,
        export_apply=False,
        export_materials='EXPORT',
        export_image_format='AUTO',
        export_texcoords=True,
        export_normals=True,
        export_tangents=False,
        export_cameras=False,
        export_lights=False,
    )


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
def main():
    fast = os.environ.get("MIMAMO_FAST") == "1"
    scn = reset_scene()
    build_materials()
    parts = build_character()
    arm_obj = build_armature(parts)
    rig_and_parent(parts, arm_obj)
    animate(arm_obj, parts)

    scn.frame_set(1)

    setup_camera_and_lights()
    configure_render()

    os.makedirs(os.path.dirname(BLEND_PATH), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=BLEND_PATH)
    print(f"SAVED BLEND: {BLEND_PATH}")

    render_poster(POSTER_PATH, frame=15)
    print(f"SAVED POSTER: {POSTER_PATH}")

    if not fast:
        render_preview_frames(FRAMES_DIR, step=4)
        print(f"SAVED PREVIEW FRAMES to: {FRAMES_DIR}")

        os.makedirs(os.path.dirname(GLB_PATH), exist_ok=True)
        export_glb(GLB_PATH)
        print(f"SAVED GLB: {GLB_PATH}")

        # re-save blend once more so it includes camera/lights/world too
        bpy.ops.wm.save_as_mainfile(filepath=BLEND_PATH)
    print("DONE.")


if __name__ == "__main__":
    main()



