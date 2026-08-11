"""Armature, skinning, shape keys and actions for the Mimamo opus rebuild."""
import math

import bpy
from mathutils import Vector

BONES = [
    # name, head, tail, parent, connected
    ("root",        (0, 0, 0.00),        (0, 0, 0.16),        None,        False),
    ("body",        (0, 0, 0.30),        (0, 0, 0.62),        "root",      False),
    ("chest",       (0, 0, 0.62),        (0, 0, 0.84),        "body",      True),
    ("neck",        (0, 0, 0.84),        (0, 0, 0.91),        "chest",     True),
    ("head",        (0, 0, 0.91),        (0, 0, 1.79),        "neck",      True),
    ("jaw",         (0, -0.10, 1.13),    (0, -0.26, 1.05),    "head",      False),
    ("eye_L",       (0.204, -0.36, 1.23), (0.204, -0.52, 1.23), "head",    False),
    ("eye_R",       (-0.204, -0.36, 1.23), (-0.204, -0.52, 1.23), "head",  False),
    ("eyebrow_L",   (0.215, -0.34, 1.445), (0.215, -0.46, 1.445), "head",  False),
    ("eyebrow_R",   (-0.215, -0.34, 1.445), (-0.215, -0.46, 1.445), "head", False),
    ("ear_L",       (0.50, 0, 1.305),    (0.70, 0, 1.305),    "head",      False),
    ("ear_R",       (-0.50, 0, 1.305),   (-0.70, 0, 1.305),   "head",      False),
    ("antenna01",   (0, 0, 1.788),       (0, 0, 1.890),       "head",      False),
    ("antenna02",   (0, 0, 1.890),       (0, 0, 2.130),       "antenna01", True),
    ("chest_heart", (0, -0.24, 0.60),    (0, -0.36, 0.68),    "chest",     False),
    ("upperarm_L",  (0.275, -0.02, 0.755),  (0.450, -0.085, 0.605), "chest", False),
    ("lowerarm_L",  (0.450, -0.085, 0.605), (0.505, -0.165, 0.700), "upperarm_L", True),
    ("hand_L",      (0.505, -0.165, 0.700), (0.600, -0.230, 0.760), "lowerarm_L", True),
    ("upperarm_R",  (-0.275, -0.02, 0.755), (-0.445, -0.060, 0.700), "chest", False),
    ("lowerarm_R",  (-0.445, -0.060, 0.700), (-0.585, -0.090, 0.825), "upperarm_R", True),
    ("hand_R",      (-0.585, -0.090, 0.825), (-0.700, -0.110, 1.030), "lowerarm_R", True),
    ("thigh_L",     (0.162, 0, 0.400),   (0.190, 0, 0.245),   "root",      False),
    ("shin_L",      (0.190, 0, 0.245),   (0.208, -0.01, 0.095), "thigh_L", True),
    ("foot_L",      (0.208, -0.01, 0.095), (0.215, -0.20, 0.045), "shin_L", False),
    ("thigh_R",     (-0.162, 0, 0.400),  (-0.190, 0, 0.245),  "root",      False),
    ("shin_R",      (-0.190, 0, 0.245),  (-0.208, -0.01, 0.095), "thigh_R", True),
    ("foot_R",      (-0.208, -0.01, 0.095), (-0.215, -0.20, 0.045), "shin_R", False),
    ("cape01",      (0, 0.150, 0.820),   (0, 0.215, 0.590),   "chest",     False),
    ("cape02",      (0, 0.215, 0.590),   (0, 0.290, 0.350),   "cape01",    True),
    ("cape03",      (0, 0.290, 0.350),   (0, 0.350, 0.075),   "cape02",    True),
]

# object-name prefix -> bone
GROUP_RULES = [
    ("Cape", None),  # graded, handled separately
    ("AntennaHeart", "antenna02"),
    ("AntennaHeartHi", "antenna02"),
    ("AntennaJoint", "antenna01"),
    ("AntennaStalk", "antenna01"),
    ("EarPod_L", "ear_L"), ("EarDisc_L", "ear_L"), ("EarGem_L", "ear_L"),
    ("EarPod_R", "ear_R"), ("EarDisc_R", "ear_R"), ("EarGem_R", "ear_R"),
    ("Brow_L", "eyebrow_L"), ("Brow_R", "eyebrow_R"),
    ("EyeL_", "eye_L"), ("EyeR_", "eye_R"),
    ("MouthCavity", "jaw"), ("Tongue", "jaw"), ("MouthRim", "jaw"), ("Teeth", "jaw"),
    ("HeadShell", "head"), ("FacePlate", "head"), ("FacePlateRim", "head"), ("Nose", "head"),
    ("Blush_", "head"), ("HelmetCrest", "head"),
    ("ForeheadFrame", "head"), ("ForeheadRing", "head"), ("ForeheadHeart", "head"),
    ("SideBand_", "head"),
    ("Neck", "neck"),
    ("Collar", "chest"), ("ScarfKnot", "chest"), ("ScarfTail_", "chest"),
    ("ChestFrame", "chest"), ("ChestRim", "chest"), ("ChestPlate2", "chest"),
    ("ChestPlate", "chest"),
    ("ChestHeart", "chest_heart"),
    ("Torso", "body"), ("Hips", "root"), ("Belt", "body"), ("BeltBuckle", "body"),
    ("Shoulder_L", "upperarm_L"), ("UpperArm_L", "upperarm_L"),
    ("Elbow_L", "lowerarm_L"), ("LowerArm_L", "lowerarm_L"), ("Cuff_L", "lowerarm_L"),
    ("Palm_L", "hand_L"), ("Finger_L", "hand_L"), ("Thumb_L", "hand_L"),
    ("PhoneBody", "hand_L"), ("PhoneScreen", "hand_L"), ("PhoneBadge", "hand_L"),
    ("PhoneTick", "hand_L"), ("PhoneBar", "hand_L"), ("PhoneHeart", "hand_L"),
    ("Shoulder_R", "upperarm_R"), ("UpperArm_R", "upperarm_R"),
    ("Elbow_R", "lowerarm_R"), ("LowerArm_R", "lowerarm_R"), ("Cuff_R", "lowerarm_R"),
    ("Palm_R", "hand_R"), ("Finger_R", "hand_R"), ("Thumb_R", "hand_R"),
    ("WatchBand", "lowerarm_R"), ("WatchCase", "lowerarm_R"), ("WatchScreen", "lowerarm_R"),
    ("WatchHeart", "lowerarm_R"), ("WatchLine", "lowerarm_R"),
    ("Thigh_L", "thigh_L"), ("KneeTrim_L", "thigh_L"), ("Shin_L", "shin_L"),
    ("Boot_L", "foot_L"), ("Sole_L", "foot_L"), ("BootTrim_L", "foot_L"),
    ("BootBuckle_L", "foot_L"),
    ("Thigh_R", "thigh_R"), ("KneeTrim_R", "thigh_R"), ("Shin_R", "shin_R"),
    ("Boot_R", "foot_R"), ("Sole_R", "foot_R"), ("BootTrim_R", "foot_R"),
    ("BootBuckle_R", "foot_R"),
]


def bone_for(obj_name):
    best = None
    for prefix, bone in GROUP_RULES:
        if obj_name.startswith(prefix) and bone:
            if best is None or len(prefix) > len(best[0]):
                best = (prefix, bone)
    return best[1] if best else "body"


def assign_groups(objs):
    """Create one full-weight vertex group per object (graded for the cape)."""
    for ob in objs:
        if ob.type != "MESH":
            continue
        me = ob.data
        idx = list(range(len(me.vertices)))
        if ob.name.startswith("Cape"):
            for bname, zr in (("chest", (1.20, 0.70)), ("cape01", (0.86, 0.55)),
                              ("cape02", (0.62, 0.30)), ("cape03", (0.40, -0.20))):
                g = ob.vertex_groups.new(name=bname)
                z0, z1 = zr
                for v in me.vertices:
                    zz = (ob.matrix_world @ v.co).z
                    t = (zz - z1) / max(1e-6, (z0 - z1))
                    w = max(0.0, 1.0 - abs(t - 0.5) * 2.0)
                    if w > 0.001:
                        g.add([v.index], w ** 0.8, "REPLACE")
            continue
        g = ob.vertex_groups.new(name=bone_for(ob.name))
        g.add(idx, 1.0, "REPLACE")


def build_armature(coll):
    arm_data = bpy.data.armatures.new("MimamoRigData")
    rig = bpy.data.objects.new("MimamoRig", arm_data)
    coll.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    eb = arm_data.edit_bones
    for name, h, t, parent, conn in BONES:
        b = eb.new(name)
        b.head = Vector(h)
        b.tail = Vector(t)
        b.use_deform = True
    for name, h, t, parent, conn in BONES:
        if parent:
            eb[name].parent = eb[parent]
            eb[name].use_connect = bool(conn)
    bpy.ops.object.mode_set(mode="OBJECT")
    try:
        arm_data.display_type = "OCTAHEDRAL"
    except Exception:
        pass
    return rig


def add_shape_keys(ob):
    me = ob.data
    ob.shape_key_add(name="Basis", from_mix=False)

    def group_verts(names):
        gi = [ob.vertex_groups[n].index for n in names if n in ob.vertex_groups]
        res = []
        for v in me.vertices:
            for g in v.groups:
                if g.group in gi and g.weight > 0.4:
                    res.append(v.index)
                    break
        return res

    eye_v = group_verts(["eye_L", "eye_R"])
    jaw_v = group_verts(["jaw"])

    # Shape-key coordinates live in OBJECT-LOCAL space, but every landmark
    # constant in this project is expressed in world Z.  Convert once.
    oz = ob.matrix_world.translation.z

    # --- Blink: squash the whole eye stack onto a thin arc --------------- #
    k = ob.shape_key_add(name="Blink", from_mix=False)
    for i in eye_v:
        p = k.data[i].co
        wz = p.z + oz
        cz = 1.2244
        cx = 0.2290 if p.x > 0 else -0.2290
        t = min(1.0, abs(p.x - cx) / 0.0956)
        arc = 0.0190 * (1.0 - t * t)
        nz = cz - 0.004 + (wz - cz) * 0.070 + arc
        k.data[i].co = Vector((p.x, p.y, nz - oz))

    # --- MouthOpen: drop the jaw geometry, deepen the cavity ------------- #
    k2 = ob.shape_key_add(name="MouthOpen", from_mix=False)
    for i in jaw_v:
        p = k2.data[i].co
        wz = p.z + oz
        cz = 1.0700
        nz = cz - 0.0130 + (wz - cz) * 1.65
        k2.data[i].co = Vector((p.x * 1.05, p.y + 0.010, nz - oz))

    # --- Talk: mid-open, wider (visemes) --------------------------------- #
    k3 = ob.shape_key_add(name="Talk", from_mix=False)
    for i in jaw_v:
        p = k3.data[i].co
        wz = p.z + oz
        cz = 1.0700
        nz = cz - 0.0045 + (wz - cz) * 1.28
        k3.data[i].co = Vector((p.x * 1.14, p.y + 0.005, nz - oz))
    for kk in (k, k2, k3):
        kk.slider_min = 0.0
        kk.slider_max = 1.0
        kk.value = 0.0
    return ob


# --------------------------------------------------------------------------- #
# actions
# --------------------------------------------------------------------------- #
def _kf(pb, path, frame, value, rot_mode="QUATERNION"):
    if path == "location":
        pb.location = Vector(value)
    elif path == "rotation_euler":
        pb.rotation_mode = "XYZ"
        pb.rotation_euler = value
    elif path == "scale":
        pb.scale = Vector(value)
    pb.keyframe_insert(data_path=path, frame=frame)


def make_actions(rig, mesh_ob):
    scene = bpy.context.scene
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="POSE")
    for pb in rig.pose.bones:
        pb.rotation_mode = "XYZ"

    rig.animation_data_create()
    act = bpy.data.actions.new("MimamoIdle")
    act.use_fake_user = True
    rig.animation_data.action = act

    F = 120
    frames = list(range(1, F + 2, 5))

    def R(x=0.0, y=0.0, z=0.0):
        return (math.radians(x), math.radians(y), math.radians(z))

    for fr in frames:
        t = (fr - 1) / F
        w = 2 * math.pi * t
        _kf(rig.pose.bones["root"], "location", fr, (0, 0, 0.028 * math.sin(w * 2)))
        _kf(rig.pose.bones["body"], "rotation_euler", fr,
            R(2.2 * math.sin(w * 2), 0, 2.6 * math.sin(w)))
        _kf(rig.pose.bones["chest"], "rotation_euler", fr,
            R(-1.6 * math.sin(w * 2), 0, -1.8 * math.sin(w)))
        _kf(rig.pose.bones["head"], "rotation_euler", fr,
            R(-2.4 * math.sin(w * 2), 3.0 * math.sin(w), 3.4 * math.sin(w)))
        _kf(rig.pose.bones["antenna01"], "rotation_euler", fr,
            R(4.0 * math.sin(w * 2), 0, 4.5 * math.sin(w)))
        _kf(rig.pose.bones["antenna02"], "rotation_euler", fr,
            R(5.5 * math.sin(w * 2), 0, 6.0 * math.sin(w)))
        sc = 1.0 + 0.055 * 0.5 * (1.0 - math.cos(w * 2))
        _kf(rig.pose.bones["antenna02"], "scale", fr, (sc, sc, sc))
        hsc = 1.0 + 0.05 * 0.5 * (1.0 - math.cos(w * 2))
        _kf(rig.pose.bones["chest_heart"], "scale", fr, (hsc, hsc, hsc))
        # waving arm (character's right / viewer left)
        _kf(rig.pose.bones["upperarm_R"], "rotation_euler", fr,
            R(0, 0, 9.0 * math.sin(w * 2)))
        _kf(rig.pose.bones["lowerarm_R"], "rotation_euler", fr,
            R(0, 16.0 * math.sin(w * 2), 0))
        _kf(rig.pose.bones["hand_R"], "rotation_euler", fr,
            R(0, 22.0 * math.sin(w * 2), 0))
        # phone arm subtle
        _kf(rig.pose.bones["upperarm_L"], "rotation_euler", fr,
            R(0, 0, 2.4 * math.sin(w)))
        _kf(rig.pose.bones["lowerarm_L"], "rotation_euler", fr,
            R(0, 3.0 * math.sin(w), 0))
        # cape
        for i, bn in enumerate(("cape01", "cape02", "cape03")):
            amp = 4.0 + 4.5 * i
            _kf(rig.pose.bones[bn], "rotation_euler", fr,
                R(amp * 0.55 * math.sin(w), amp * math.sin(w),
                  amp * 0.4 * math.sin(w * 2)))
        for bn in ("ear_L", "ear_R"):
            _kf(rig.pose.bones[bn], "rotation_euler", fr, R(0, 0, 2.0 * math.sin(w * 2)))

    bpy.ops.object.mode_set(mode="OBJECT")

    # ---- separate single-purpose actions for the GLB ---- #
    def pose_action(name, fn, length):
        a = bpy.data.actions.new(name)
        a.use_fake_user = True
        rig.animation_data.action = a
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode="POSE")
        for pb in rig.pose.bones:
            pb.rotation_euler = (0, 0, 0)
            pb.location = (0, 0, 0)
            pb.scale = (1, 1, 1)
        fn(length)
        bpy.ops.object.mode_set(mode="OBJECT")
        return a

    def wave_fn(length):
        for fr in range(1, length + 1, 3):
            t = (fr - 1) / length
            w = 2 * math.pi * t
            _kf(rig.pose.bones["upperarm_R"], "rotation_euler", fr,
                R(0, 0, -8.0 + 14.0 * math.sin(w * 2)))
            _kf(rig.pose.bones["lowerarm_R"], "rotation_euler", fr,
                R(0, 26.0 * math.sin(w * 2 + 0.4), 0))
            _kf(rig.pose.bones["hand_R"], "rotation_euler", fr,
                R(0, 30.0 * math.sin(w * 2 + 0.8), 0))
            _kf(rig.pose.bones["head"], "rotation_euler", fr, R(0, 0, 4.0 * math.sin(w * 2)))

    pose_action("MimamoWave", wave_fn, 48)

    def gaze_fn(length):
        for fr, ang in ((1, 0), (12, 12), (24, 0), (36, -12), (48, 0)):
            for bn in ("eye_L", "eye_R"):
                _kf(rig.pose.bones[bn], "rotation_euler", fr, R(0, 0, ang))
            _kf(rig.pose.bones["head"], "rotation_euler", fr, R(0, 0, ang * 0.28))

    pose_action("MimamoGaze", gaze_fn, 48)

    rig.animation_data.action = act

    # ---- shape-key actions on the mesh ---- #
    sk = mesh_ob.data.shape_keys
    sk.animation_data_create()
    ska = bpy.data.actions.new("MimamoFaceIdle")
    ska.use_fake_user = True
    sk.animation_data.action = ska
    kb = {k.name: k for k in sk.key_blocks}

    def key(name, fr, val):
        kb[name].value = val
        kb[name].keyframe_insert("value", frame=fr)

    for fr, v in ((1, 0.0), (34, 0.0), (37, 1.0), (40, 0.0), (86, 0.0),
                  (89, 1.0), (92, 0.0), (121, 0.0)):
        key("Blink", fr, v)
    for fr, v in ((1, 0.0), (12, 0.42), (22, 0.08), (34, 0.50), (46, 0.06),
                  (60, 0.38), (74, 0.04), (88, 0.46), (100, 0.10), (121, 0.0)):
        key("Talk", fr, v)
    for fr, v in ((1, 0.0), (30, 0.30), (58, 0.0), (92, 0.26), (121, 0.0)):
        key("MouthOpen", fr, v)

    scene.frame_start = 1
    scene.frame_end = 120
    scene.frame_set(1)
    return act
