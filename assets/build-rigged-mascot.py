"""Rig the mimamori owl mascot with a named bone armature, blink shape keys,
eye-gaze bones, a hinged beak, and a short demonstration animation.

Runs ONLY inside an isolated headless Blender subprocess
(`blender --background --factory-startup --python build-rigged-mascot.py`).
It opens the existing static mascot file, immediately Save-As's to a new
rigged file, and never writes back to the original .blend. This script never
touches any other running Blender process/session (e.g. shield-kun-animation
.blend) because it runs in its own separate OS process with no scene link to
anything else.
"""

import math
import bpy
from mathutils import Vector

ROOT = r"C:\Users\msoga\OneDrive - Smart Designer\Projects\見守り隊"
SOURCE_BLEND = ROOT + r"\assets\line-mimamori-mascot.blend"
RIGGED_BLEND = ROOT + r"\assets\line-mimamori-mascot-rigged.blend"
FRAMES_DIR = ROOT + r"\assets\_rig_preview_frames"

FPS = 24
FRAME_END = 192

# ---------------------------------------------------------------------------
# 1. Load the existing static mascot, then IMMEDIATELY Save-As to the new
#    rigged filename so nothing is ever written back to the source file.
# ---------------------------------------------------------------------------
bpy.ops.wm.open_mainfile(filepath=SOURCE_BLEND)
bpy.ops.wm.save_as_mainfile(filepath=RIGGED_BLEND)

scene = bpy.context.scene
objects = bpy.data.objects


def obj(name):
    return objects[name]


# ---------------------------------------------------------------------------
# 2. Split the single beak cone into a hinged upper/lower beak so it can
#    open and close for "talking".
# ---------------------------------------------------------------------------
beak_upper = obj("Beak")
beak_upper.name = "Beak upper"

beak_lower = beak_upper.copy()
beak_lower.data = beak_upper.data.copy()
beak_lower.name = "Beak lower"
bpy.context.collection.objects.link(beak_lower)
# Nudge the lower mandible slightly down/forward and shrink it a touch so the
# two pieces read as a beak that can hinge open.
beak_lower.location = (0.0, -0.90, 2.90)
beak_lower.scale = (0.92, 0.92, 0.8)

beak_upper_loc = Vector((0.0, -0.93, 3.02))
beak_lower_loc = Vector(beak_lower.location)

# ---------------------------------------------------------------------------
# 3. Build the armature with sensibly named bones.
# ---------------------------------------------------------------------------
bpy.ops.object.armature_add(enter_editmode=False, location=(0, 0, 0))
rig = bpy.context.object
rig.name = "MascotRig"
rig.data.name = "MascotRigData"

bpy.ops.object.mode_set(mode="EDIT")
eb = rig.data.edit_bones
for b in list(eb):
    eb.remove(b)


def make_bone(name, head, tail, parent=None, deform=True):
    bone = eb.new(name)
    bone.head = Vector(head)
    bone.tail = Vector(tail)
    bone.use_deform = deform
    if parent:
        bone.parent = eb[parent]
        bone.use_connect = False
    return bone


make_bone("root", (0, 0, 0.0), (0, 0, 0.55))
make_bone("body", (0, 0, 0.55), (0, 0.05, 1.70), parent="root")
make_bone("head", (0, 0, 2.55), (0, 0, 3.65), parent="body")

make_bone("eye_L", (-0.53, -0.60, 3.34), (-0.53, -0.60, 3.55), parent="head")
make_bone("eye_R", (0.53, -0.60, 3.34), (0.53, -0.60, 3.55), parent="head")

# Non-deform control bones whose custom "blink" property drives eyelid
# shape keys via drivers.
make_bone("eyelid_L", (-0.53, -0.95, 3.58), (-0.53, -0.95, 3.80), parent="head", deform=False)
make_bone("eyelid_R", (0.53, -0.95, 3.58), (0.53, -0.95, 3.80), parent="head", deform=False)

make_bone("beak_upper", (0, -0.65, 3.02), (0, -1.05, 3.02), parent="head")
make_bone("beak_lower", (0, -0.65, 2.90), (0, -1.00, 2.85), parent="head")

make_bone("wing_L", (-0.85, -0.10, 1.90), (-1.13, -0.20, 1.83), parent="body")
make_bone("wing_R", (0.85, -0.10, 1.90), (1.13, -0.20, 1.83), parent="body")
make_bone("hand_L", (-1.13, -0.20, 1.83), (-0.52, -1.02, 1.78), parent="wing_L")
make_bone("hand_R", (1.13, -0.20, 1.83), (0.52, -1.02, 1.78), parent="wing_R")

# Feet stay attached to root so the whole rig bobs as one grounded unit.
bpy.ops.object.mode_set(mode="OBJECT")

for b in rig.pose.bones:
    if b.name not in ("eye_L", "eye_R"):
        b.rotation_mode = "XYZ"

# ---------------------------------------------------------------------------
# 4. Weight every mesh part to its controlling bone (rigid vertex-group
#    binding: weight 1.0, single group) and add an Armature modifier.
# ---------------------------------------------------------------------------
BONE_FOR_OBJECT = {
    "Body": "body",
    "Belly": "body",
    "Green scarf": "body",
    "Scarf tail": "body",
    "Care heart": "body",
    "Shield pin": "body",
    "Shield mark vertical": "body",
    "Shield mark horizontal": "body",
    "Head": "head",
    "Left ear": "head",
    "Right ear": "head",
    "Face disc -1": "head",
    "Face disc 1": "head",
    "Cheek -1": "head",
    "Cheek 1": "head",
    "Eye -1": "eye_L",
    "Eye 1": "eye_R",
    "Eye highlight -1": "eye_L",
    "Eye highlight 1": "eye_R",
    "Beak upper": "beak_upper",
    "Beak lower": "beak_lower",
    "Left wing": "wing_L",
    "Right wing": "wing_R",
    "Left hand": "hand_L",
    "Right hand": "hand_R",
    "Foot -1": "root",
    "Foot 1": "root",
}

for name, bone_name in BONE_FOR_OBJECT.items():
    mesh_obj = objects.get(name)
    if mesh_obj is None:
        print(f"WARNING: object '{name}' not found, skipping rig weight")
        continue
    vg = mesh_obj.vertex_groups.new(name=bone_name)
    vg.add(list(range(len(mesh_obj.data.vertices))), 1.0, "REPLACE")
    mod = mesh_obj.modifiers.new("Armature", "ARMATURE")
    mod.object = rig
    mesh_obj.parent = rig
    mesh_obj.matrix_parent_inverse = rig.matrix_world.inverted()

# ---------------------------------------------------------------------------
# 5. Blink shape keys on both eyes + eye highlights, driven by the
#    eyelid_L / eyelid_R bone custom "blink" property (0 = open, 1 = closed).
# ---------------------------------------------------------------------------
def add_blink_shape_key(mesh_name, flatten=0.04, hide=False):
    mesh_obj = obj(mesh_name)
    mesh = mesh_obj.data
    if mesh.shape_keys is None:
        mesh_obj.shape_key_add(name="Basis", from_mix=False)
    blink = mesh_obj.shape_key_add(name="Blink", from_mix=False)
    local_center_z = sum(v.co.z for v in mesh.vertices) / len(mesh.vertices)
    for i, v in enumerate(mesh.vertices):
        co = v.co
        if hide:
            # Highlight dot shrinks away entirely when the eye is shut.
            blink.data[i].co = co * 0.02
        else:
            # Flatten the sphere toward a closed eyelid line.
            new_z = local_center_z + (co.z - local_center_z) * flatten
            blink.data[i].co = (co.x, co.y, new_z)
    blink.value = 0.0
    return blink


def add_blink_driver(mesh_name, bone_name):
    key_blocks = obj(mesh_name).data.shape_keys.key_blocks
    blink_key = key_blocks["Blink"]
    fcurve = blink_key.driver_add("value")
    driver = fcurve.driver
    driver.type = "AVERAGE"
    var = driver.variables.new()
    var.name = "blink"
    var.type = "SINGLE_PROP"
    target = var.targets[0]
    target.id_type = "OBJECT"
    target.id = rig
    target.data_path = f'pose.bones["{bone_name}"]["blink"]'


for side, bone_name in (("-1", "eyelid_L"), ("1", "eyelid_R")):
    add_blink_shape_key(f"Eye {side}", flatten=0.04, hide=False)
    add_blink_shape_key(f"Eye highlight {side}", hide=True)
    add_blink_driver(f"Eye {side}", bone_name)
    add_blink_driver(f"Eye highlight {side}", bone_name)

for bone_name in ("eyelid_L", "eyelid_R"):
    pb = rig.pose.bones[bone_name]
    pb["blink"] = 0.0
    try:
        pb.id_properties_ui("blink").update(min=0.0, max=1.0, soft_min=0.0, soft_max=1.0)
    except Exception as exc:  # pragma: no cover - cosmetic only
        print("id_properties_ui skipped:", exc)

# ---------------------------------------------------------------------------
# 6. Animate: idle bob, two blinks, look-right, look-left, beak talking, and
#    a friendly wing wave. All keyframes live on the armature's pose bones.
# ---------------------------------------------------------------------------
scene.frame_start = 1
scene.frame_end = FRAME_END
scene.render.fps = FPS
bpy.context.view_layer.objects.active = rig

action = bpy.data.actions.new("MascotShow")
rig.animation_data_create()
rig.animation_data.action = action


def key_loc(bone_name, frame, loc):
    pb = rig.pose.bones[bone_name]
    pb.location = Vector(loc)
    pb.keyframe_insert(data_path="location", frame=frame)


def key_rot(bone_name, frame, euler_deg):
    pb = rig.pose.bones[bone_name]
    pb.rotation_euler = tuple(math.radians(d) for d in euler_deg)
    pb.keyframe_insert(data_path="rotation_euler", frame=frame)


def key_blink(bone_name, frame, value):
    pb = rig.pose.bones[bone_name]
    pb["blink"] = value
    pb.keyframe_insert(data_path='["blink"]', frame=frame)


# --- Idle root/body bob across the whole clip -----------------------------
for frame in range(1, FRAME_END + 1, 12):
    phase = (frame / 24.0) * math.pi
    bob = 0.035 * math.sin(phase)
    key_loc("root", frame, (0, 0, bob))
    key_rot("body", frame, (0, 0, 1.5 * math.sin(phase * 0.5)))

# --- Blink #1: frames 1-10 --------------------------------------------------
for f, v in ((1, 0.0), (6, 1.0), (10, 0.0)):
    key_blink("eyelid_L", f, v)
    key_blink("eyelid_R", f, v)

# --- Look right: frames 12-48 ----------------------------------------------
for f, x in ((12, 0.0), (22, 0.16), (40, 0.16), (48, 0.0)):
    key_loc("eye_L", f, (x, -0.03, 0))
    key_loc("eye_R", f, (x, -0.03, 0))
for f, deg in ((12, 0), (22, -8), (40, -8), (48, 0)):
    key_rot("head", f, (0, 0, deg))

# --- Look left: frames 48-84 ------------------------------------------------
for f, x in ((48, 0.0), (58, -0.16), (76, -0.16), (84, 0.0)):
    key_loc("eye_L", f, (x, -0.03, 0))
    key_loc("eye_R", f, (x, -0.03, 0))
for f, deg in ((48, 0), (58, 8), (76, 8), (84, 0)):
    key_rot("head", f, (0, 0, deg))

# --- Blink #2: frames 84-92 -------------------------------------------------
for f, v in ((84, 0.0), (88, 1.0), (92, 0.0)):
    key_blink("eyelid_L", f, v)
    key_blink("eyelid_R", f, v)

# --- Beak talking: frames 92-148, rapid chatter -----------------------------
talk_frames = list(range(92, 149, 4))
for i, f in enumerate(talk_frames):
    open_amount = 14.0 if i % 2 == 0 else 2.0
    key_rot("beak_lower", f, (-open_amount, 0, 0))
key_rot("beak_lower", 148, (-2.0, 0, 0))
key_rot("beak_lower", 150, (0, 0, 0))

# --- Friendly wing wave: frames 150-192 -------------------------------------
wave_frames = [150, 156, 162, 168, 174, 180, 186, 192]
for i, f in enumerate(wave_frames):
    swing = 55.0 if i % 2 == 0 else 25.0
    key_rot("wing_R", f, (0, 0, swing))
    key_rot("hand_R", f, (0, 0, swing * 0.6 - 10))
key_rot("wing_R", 192, (0, 0, 20))
key_rot("hand_R", 192, (0, 0, 0))
# Resting arm stays gently at its side the whole time.
key_rot("wing_L", 1, (0, 0, -6))
key_rot("hand_L", 1, (0, 0, -4))

for layer in action.layers:
    for strip in layer.strips:
        for cb in strip.channelbags:
            for fcurve in cb.fcurves:
                for kp in fcurve.keyframe_points:
                    kp.interpolation = "BEZIER"
                    kp.easing = "EASE_IN_OUT"


def collect_fcurves(anim_action):
    curves = []
    for layer in anim_action.layers:
        for strip in layer.strips:
            for cb in strip.channelbags:
                curves.extend(cb.fcurves)
    return curves

# ---------------------------------------------------------------------------
# 7. Save the rigged file.
# ---------------------------------------------------------------------------
bpy.ops.wm.save_as_mainfile(filepath=RIGGED_BLEND)

# ---------------------------------------------------------------------------
# 8. Validation assertions (fail loudly if the rig isn't wired correctly).
# ---------------------------------------------------------------------------
expected_bones = {
    "root", "body", "head", "eye_L", "eye_R", "eyelid_L", "eyelid_R",
    "beak_upper", "beak_lower", "wing_L", "wing_R", "hand_L", "hand_R",
}
actual_bones = {b.name for b in rig.data.bones}
assert expected_bones <= actual_bones, f"Missing bones: {expected_bones - actual_bones}"

for mesh_name, bone_name in BONE_FOR_OBJECT.items():
    mesh_obj = objects[mesh_name]
    assert bone_name in mesh_obj.vertex_groups, f"{mesh_name} missing vgroup {bone_name}"
    assert any(m.type == "ARMATURE" for m in mesh_obj.modifiers), f"{mesh_name} missing armature modifier"

for side in ("-1", "1"):
    sk = objects[f"Eye {side}"].data.shape_keys
    assert sk is not None and "Blink" in sk.key_blocks, f"Eye {side} missing Blink shape key"

all_fcurves = collect_fcurves(action)
assert all_fcurves, "No animation fcurves were created"
fcurve_paths = {fc.data_path for fc in all_fcurves}
assert any('["blink"]' in p for p in fcurve_paths), "No blink keyframes found"
assert any(p == "pose.bones[\"eye_L\"].location" or "eye_L" in p for p in fcurve_paths), "No eye gaze keyframes found"
assert any("beak_lower" in p for p in fcurve_paths), "No beak talking keyframes found"
assert any("wing_R" in p for p in fcurve_paths), "No wing wave keyframes found"

print("VALIDATION_OK bones=%d fcurves=%d shapekeys_ok=True" % (len(actual_bones), len(all_fcurves)))
print("RIGGED_BLEND_SAVED:", RIGGED_BLEND)

# ---------------------------------------------------------------------------
# 9. Render a preview PNG sequence of the demonstration animation.
# ---------------------------------------------------------------------------
import os
os.makedirs(FRAMES_DIR, exist_ok=True)

scene.render.resolution_x = 480
scene.render.resolution_y = 480
scene.render.resolution_percentage = 100
scene.render.film_transparent = False
scene.world.color = (0.09, 0.10, 0.095)
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGB"
scene.render.filepath = FRAMES_DIR + r"\frame_"

bpy.ops.render.render(animation=True)
print("FRAMES_RENDERED:", FRAMES_DIR)
