"""Armature, skinning, shape keys and actions for the Mimamo v2 rebuild.

Run:  blender -b stage_rigsrc.blend --python opus2_rig.py -- <out.blend>

Landmarks are the same reference-pixel mapping used by the geometry modules:
    S = 0.0025,  X(xr) = (xr - 555) * S,  Z(yr) = (1100 - yr) * S
"""
import math
import os
import sys

import bpy
from mathutils import Vector

S = 0.0025
CX = 555.0


def X(xr):
    return (xr - CX) * S


def Z(yr):
    return (1100.0 - yr) * S


# --------------------------------------------------------------------------- #
# bones  (name, head, tail, parent, connected)
# --------------------------------------------------------------------------- #
BONES = [
    ("root",        (0, 0, 0.00),               (0, 0, 0.14),              None,        False),
    ("body",        (0, 0, Z(962.0)),           (0, 0, Z(884.0)),          "root",      False),
    ("chest",       (0, 0, Z(884.0)),           (0, 0, Z(772.0)),          "body",      True),
    ("neck",        (0, 0, Z(772.0)),           (0, 0, Z(724.0)),          "chest",     True),
    ("head",        (0, 0, Z(724.0)),           (0, 0, Z(400.0)),          "neck",      True),
    ("jaw",         (0, -0.10, Z(636.0)),       (0, -0.26, Z(694.0)),      "head",      False),
    ("eye_L",       (0.230, -0.34, Z(607.0)),   (0.230, -0.50, Z(607.0)),  "head",      False),
    ("eye_R",       (-0.230, -0.34, Z(607.0)),  (-0.230, -0.50, Z(607.0)), "head",      False),
    ("eyebrow_L",   (0.2175, -0.32, Z(523.5)),  (0.2175, -0.44, Z(523.5)), "head",      False),
    ("eyebrow_R",   (-0.2175, -0.32, Z(523.5)), (-0.2175, -0.44, Z(523.5)), "head",     False),
    ("ear_L",       (0.330, 0, Z(575.0)),       (0.640, 0, Z(575.0)),      "head",      False),
    ("ear_R",       (-0.330, 0, Z(575.0)),      (-0.640, 0, Z(575.0)),     "head",      False),
    ("antenna01",   (0, 0, Z(398.0)),           (0, 0, Z(345.0)),          "head",      False),
    ("antenna02",   (0, 0, Z(345.0)),           (0, 0, Z(252.0)),          "antenna01", True),
    ("scarf",       (0, -0.09, Z(730.0)),       (0, -0.24, Z(792.0)),      "chest",     False),
    ("chest_heart", (0, -0.22, Z(834.0)),       (0, -0.42, Z(834.0)),      "chest",     False),
    # screen-left arm = the waving one
    ("upperarm_L",  (X(452.0), -0.030, Z(778.0)), (X(392.0), -0.115, Z(820.0)), "chest",      False),
    ("lowerarm_L",  (X(392.0), -0.115, Z(820.0)), (X(336.0), -0.150, Z(772.0)), "upperarm_L", True),
    ("hand_L",      (X(336.0), -0.150, Z(772.0)), (X(286.0), -0.196, Z(716.0)), "lowerarm_L", True),
    # screen-right arm = the one holding the phone
    ("upperarm_R",  (X(658.0), -0.030, Z(782.0)), (X(724.0), -0.150, Z(848.0)), "chest",      False),
    ("lowerarm_R",  (X(724.0), -0.150, Z(848.0)), (X(818.0), -0.250, Z(856.0)), "upperarm_R", True),
    ("hand_R",      (X(818.0), -0.250, Z(856.0)), (X(856.0), -0.398, Z(844.0)), "lowerarm_R", True),
    ("thigh_L",     (0.155, 0, Z(918.0)),       (0.155, 0, Z(996.0)),      "root",      False),
    ("shin_L",      (0.155, 0, Z(996.0)),       (0.158, -0.01, Z(1050.0)), "thigh_L",   True),
    ("foot_L",      (0.158, -0.01, Z(1050.0)),  (0.162, -0.20, Z(1098.0)), "shin_L",    False),
    ("thigh_R",     (-0.155, 0, Z(918.0)),      (-0.155, 0, Z(996.0)),     "root",      False),
    ("shin_R",      (-0.155, 0, Z(996.0)),      (-0.158, -0.01, Z(1050.0)), "thigh_R",  True),
    ("foot_R",      (-0.158, -0.01, Z(1050.0)), (-0.162, -0.20, Z(1098.0)), "shin_R",   False),
    ("cape01",      (0, 0.170, Z(760.0)),       (0, 0.250, Z(852.0)),      "chest",     False),
    ("cape02",      (0, 0.250, Z(852.0)),       (0, 0.330, Z(950.0)),      "cape01",    True),
    ("cape03",      (0, 0.330, Z(950.0)),       (0, 0.400, Z(1036.0)),     "cape02",    True),
]

# object-name prefix -> bone  (longest matching prefix wins)
GROUP_RULES = [
    ("Cape", None),  # graded, handled separately
    # ---- head ---- #
    ("HeadShell", "head"), ("FacePlate", "head"), ("FaceRim", "head"),
    ("DomeRim", "head"), ("Crest", "head"), ("CrestDrop", "head"),
    ("CrestDropRim", "head"), ("Nose", "head"), ("Blush_", "head"),
    ("BadgePlate", "head"), ("BadgeRim", "head"), ("BadgeHeart", "head"),
    ("MimamoHead", "head"),
    ("Brow_L", "eyebrow_L"), ("Brow_R", "eyebrow_R"),
    ("EyeDark_L", "eye_L"), ("EyeSocket_L", "eye_L"), ("Iris_L", "eye_L"),
    ("Spec_L", "eye_L"), ("Spec2_L", "eye_L"), ("Lash_L", "eye_L"),
    ("EyeDark_R", "eye_R"), ("EyeSocket_R", "eye_R"), ("Iris_R", "eye_R"),
    ("Spec_R", "eye_R"), ("Spec2_R", "eye_R"), ("Lash_R", "eye_R"),
    ("MouthRim", "jaw"), ("MouthCavity", "jaw"), ("Tongue", "jaw"),
    ("EarCollar_L", "ear_L"), ("EarPod_L", "ear_L"), ("EarGem_L", "ear_L"),
    ("EarCore_L", "ear_L"), ("EarArm_L", "ear_L"),
    ("EarCollar_R", "ear_R"), ("EarPod_R", "ear_R"), ("EarGem_R", "ear_R"),
    ("EarCore_R", "ear_R"), ("EarArm_R", "ear_R"),
    ("AntBase", "antenna01"), ("AntStalk", "antenna01"),
    ("AntKnob", "antenna02"), ("AntHeart", "antenna02"),
    # ---- torso ---- #
    ("Neck", "neck"),
    ("ScarfCollar", "scarf"), ("ScarfKnot", "scarf"), ("ScarfTail_", "scarf"),
    ("Torso", "chest"),
    ("ChestShield", "chest"), ("ChestPlate", "chest"), ("ChestRim", "chest"),
    ("ChestHeart", "chest_heart"),
    ("Belt", "body"), ("Buckle_", "body"), ("BuckleIn_", "body"),
    # ---- arms ---- #
    ("Shoulder_L", "upperarm_L"), ("UpperArm_L", "upperarm_L"),
    ("ForeArm_L", "lowerarm_L"), ("Cuff_L", "lowerarm_L"),
    ("WatchStrap", "lowerarm_L"), ("WatchBody", "lowerarm_L"),
    ("WatchCuff", "lowerarm_L"),
    ("WatchFace", "lowerarm_L"), ("WatchGlyph", "lowerarm_L"),
    ("Palm_L", "hand_L"), ("FingerL", "hand_L"), ("ThumbL", "hand_L"),
    ("Shoulder_R", "upperarm_R"), ("UpperArm_R", "upperarm_R"),
    ("ForeArm_R", "lowerarm_R"), ("Cuff_R", "lowerarm_R"),
    ("Palm_R", "hand_R"), ("FingerR", "hand_R"), ("ThumbR", "hand_R"),
    ("Phone", "hand_R"), ("PhoneScreen", "hand_R"), ("PhoneCard", "hand_R"),
    ("PhoneBadge", "hand_R"), ("PhoneTick", "hand_R"), ("PhoneText", "hand_R"),
    ("PhoneHeart", "hand_R"),
    # ---- legs ---- #
    ("Leg_L", "thigh_L"), ("BootBand_L", "shin_L"),
    ("Boot_L", "foot_L"), ("BootSole_L", "foot_L"), ("BootBuckle_L", "foot_L"),
    ("Leg_R", "thigh_R"), ("BootBand_R", "shin_R"),
    ("Boot_R", "foot_R"), ("BootSole_R", "foot_R"), ("BootBuckle_R", "foot_R"),
]


def bone_for(obj_name):
    best = None
    for prefix, bone in GROUP_RULES:
        if obj_name.startswith(prefix) and bone:
            if best is None or len(prefix) > len(best[0]):
                best = (prefix, bone)
    return best[1] if best else "chest"


def assign_groups(objs):
    unmatched = []
    for ob in objs:
        if ob.type != "MESH":
            continue
        me = ob.data
        if ob.name.startswith("Cape"):
            for bname, zr in (("chest", (1.00, 0.66)), ("cape01", (0.86, 0.56)),
                              ("cape02", (0.66, 0.30)), ("cape03", (0.42, -0.16))):
                g = ob.vertex_groups.new(name=bname)
                z0, z1 = zr
                for v in me.vertices:
                    zz = (ob.matrix_world @ v.co).z
                    t = (zz - z1) / max(1e-6, (z0 - z1))
                    w = max(0.0, 1.0 - abs(t - 0.5) * 2.0)
                    if w > 0.001:
                        g.add([v.index], w ** 0.8, "REPLACE")
            continue
        if not any(ob.name.startswith(p) for p, b in GROUP_RULES if b):
            unmatched.append(ob.name)
        g = ob.vertex_groups.new(name=bone_for(ob.name))
        g.add(list(range(len(me.vertices))), 1.0, "REPLACE")
    if unmatched:
        print("UNMATCHED OBJECTS ->", unmatched)
    else:
        print("ALL OBJECTS MATCHED A BONE")


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
    arm_data.display_type = "OCTAHEDRAL"
    return rig


# --------------------------------------------------------------------------- #
# shape keys
# --------------------------------------------------------------------------- #
EYE_CZ = Z(607.0)          # 1.23250
EYE_CX = 0.230
EYE_HW = 0.1050
MOUTH_CZ = Z(674.0)        # 1.06500


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
    print("SHAPEKEY VERTS eyes", len(eye_v), "jaw", len(jaw_v))

    oz = ob.matrix_world.translation.z

    # --- Blink: squash the eye stack onto a thin downward arc ---------------- #
    k = ob.shape_key_add(name="Blink", from_mix=False)
    for i in eye_v:
        p = k.data[i].co
        wz = p.z + oz
        cx = EYE_CX if p.x > 0 else -EYE_CX
        t = min(1.0, abs(p.x - cx) / EYE_HW)
        arc = 0.021 * (1.0 - t * t)
        nz = EYE_CZ - 0.004 + (wz - EYE_CZ) * 0.070 + arc
        k.data[i].co = Vector((p.x, p.y, nz - oz))

    # --- MouthOpen ---------------------------------------------------------- #
    k2 = ob.shape_key_add(name="MouthOpen", from_mix=False)
    for i in jaw_v:
        p = k2.data[i].co
        wz = p.z + oz
        nz = MOUTH_CZ - 0.014 + (wz - MOUTH_CZ) * 1.70
        k2.data[i].co = Vector((p.x * 1.06, p.y + 0.010, nz - oz))

    # --- Talk: mid-open, wider ---------------------------------------------- #
    k3 = ob.shape_key_add(name="Talk", from_mix=False)
    for i in jaw_v:
        p = k3.data[i].co
        wz = p.z + oz
        nz = MOUTH_CZ - 0.005 + (wz - MOUTH_CZ) * 1.30
        k3.data[i].co = Vector((p.x * 1.15, p.y + 0.005, nz - oz))

    for kk in (k, k2, k3):
        kk.slider_min = 0.0
        kk.slider_max = 1.0
        kk.value = 0.0
    return ob


# --------------------------------------------------------------------------- #
# actions
# --------------------------------------------------------------------------- #
def _kf(pb, path, frame, value):
    if path == "location":
        pb.location = Vector(value)
    elif path == "rotation_euler":
        pb.rotation_mode = "XYZ"
        pb.rotation_euler = value
    elif path == "scale":
        pb.scale = Vector(value)
    pb.keyframe_insert(data_path=path, frame=frame)


def R(x=0.0, y=0.0, z=0.0):
    return (math.radians(x), math.radians(y), math.radians(z))


# --------------------------------------------------------------------------- #
# pose helpers used to keep the hands off the face
# --------------------------------------------------------------------------- #
def _reset_pose(rig):
    for pb in rig.pose.bones:
        pb.rotation_mode = "XYZ"
        pb.rotation_euler = (0.0, 0.0, 0.0)
        pb.location = (0.0, 0.0, 0.0)
        pb.scale = (1.0, 1.0, 1.0)


def _model_height(mesh_ob):
    mw = mesh_ob.matrix_world
    zs = [(mw @ v.co).z for v in mesh_ob.data.vertices]
    return max(zs) - min(zs)


def _face_ellipse(mesh_ob):
    """Front-view ellipse covering the head, from the 'head' vertex group."""
    vg = mesh_ob.vertex_groups.get("head")
    mw = mesh_ob.matrix_world
    xs, zs = [], []
    if vg is not None:
        gi = vg.index
        for v in mesh_ob.data.vertices:
            for g in v.groups:
                if g.group == gi and g.weight > 0.5:
                    p = mw @ v.co
                    xs.append(p.x)
                    zs.append(p.z)
                    break
    if not xs:
        return Vector((0.0, 0.0, 1.0)), 0.30, 0.30
    return (Vector((0.5 * (min(xs) + max(xs)), 0.0, 0.5 * (min(zs) + max(zs)))),
            0.5 * (max(xs) - min(xs)), 0.5 * (max(zs) - min(zs)))


def _raise_sign(rig, drive, probe, test=28.0):
    """Which sign of the bone's local Z rotation lifts the hand upward."""
    _reset_pose(rig)
    bpy.context.view_layer.update()
    base = (rig.matrix_world @ rig.pose.bones[probe].tail).z
    rig.pose.bones[drive].rotation_euler = (0.0, 0.0, math.radians(test))
    bpy.context.view_layer.update()
    up = (rig.matrix_world @ rig.pose.bones[probe].tail).z
    _reset_pose(rig)
    bpy.context.view_layer.update()
    return 1.0 if up > base else -1.0


def _clearance(rig, probes, c, a, b):
    """Smallest normalised ellipse radius reached by any probe point.

    >= 1.0 means the point is outside the face ellipse in the front view."""
    worst = 1e9
    mw = rig.matrix_world
    for bn in probes:
        pb = rig.pose.bones[bn]
        for p in (pb.head, pb.tail, (pb.head + pb.tail) * 0.5):
            wp = mw @ p
            worst = min(worst, math.hypot((wp.x - c.x) / a, (wp.z - c.z) / b))
    return worst


def _fit_amp(rig, pose_fn, probes, c, a, b, need=1.12, steps=28):
    """Shrink an animation's amplitude until no hand enters the face zone."""
    s, worst = 1.0, 0.0
    for _ in range(14):
        worst = 1e9
        for i in range(steps):
            _reset_pose(rig)
            pose_fn(i / float(steps), s)
            bpy.context.view_layer.update()
            worst = min(worst, _clearance(rig, probes, c, a, b))
        if worst >= need:
            break
        s *= 0.86
    _reset_pose(rig)
    bpy.context.view_layer.update()
    return s, worst


def make_actions(rig, mesh_ob):
    scene = bpy.context.scene
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="POSE")
    P = rig.pose.bones
    for pb in P:
        pb.rotation_mode = "XYZ"

    HH = _model_height(mesh_ob) / max(1e-9, abs(rig.matrix_world.to_scale().z))
    fc, fa, fb = _face_ellipse(mesh_ob)
    sgn_L = _raise_sign(rig, "upperarm_L", "hand_L")
    sgn_R = _raise_sign(rig, "upperarm_R", "hand_R")
    print("MODEL HEIGHT %.4f" % HH)
    print("FACE ELLIPSE c=(%.4f,%.4f) a=%.4f b=%.4f" % (fc.x, fc.z, fa, fb))
    print("RAISE SIGN L=%+.0f R=%+.0f" % (sgn_L, sgn_R))
    PROBE = ("hand_L", "lowerarm_L", "hand_R", "lowerarm_R")

    # ------------------------------------------------------------------ #
    # pose functions.  policy:  eyes never rotate (blink shape key only),
    # arms do a natural wave / banzai, idle+cape+antenna stay restrained.
    # ------------------------------------------------------------------ #
    def idle_pose(t, s):
        w = 2.0 * math.pi * t
        P["root"].location = (0.0, 0.0, 0.0068 * HH * math.sin(w * 2) * s)
        P["body"].rotation_euler = R(1.1 * math.sin(w * 2) * s, 0, 1.3 * math.sin(w) * s)
        P["chest"].rotation_euler = R(-0.8 * math.sin(w * 2) * s, 0, -0.9 * math.sin(w) * s)
        P["head"].rotation_euler = R(-1.2 * math.sin(w * 2) * s, 1.5 * math.sin(w) * s,
                                     1.7 * math.sin(w) * s)
        P["antenna01"].rotation_euler = R(2.2 * math.sin(w * 2) * s, 0, 2.5 * math.sin(w) * s)
        P["antenna02"].rotation_euler = R(3.0 * math.sin(w * 2) * s, 0, 3.3 * math.sin(w) * s)
        a2 = 1.0 + 0.030 * 0.5 * (1.0 - math.cos(w * 2)) * s
        P["antenna02"].scale = (a2, a2, a2)
        hs = 1.0 + 0.028 * 0.5 * (1.0 - math.cos(w * 2)) * s
        P["chest_heart"].scale = (hs, hs, hs)
        P["scarf"].rotation_euler = R(1.4 * math.sin(w) * s, 0, 1.1 * math.sin(w * 2) * s)
        P["upperarm_L"].rotation_euler = R(0, 0,
                                           sgn_L * 4.2 * (0.5 + 0.5 * math.sin(w * 2)) * s)
        P["lowerarm_L"].rotation_euler = R(0, 7.0 * math.sin(w * 2) * s, 0)
        P["hand_L"].rotation_euler = R(0, 9.0 * math.sin(w * 2) * s, 0)
        P["upperarm_R"].rotation_euler = R(0, 0, 1.2 * math.sin(w) * s)
        P["lowerarm_R"].rotation_euler = R(0, 1.5 * math.sin(w) * s, 0)
        for i, bn in enumerate(("cape01", "cape02", "cape03")):
            amp = (2.2 + 2.4 * i) * s
            P[bn].rotation_euler = R(amp * 0.55 * math.sin(w), amp * math.sin(w),
                                     amp * 0.40 * math.sin(w * 2))
        for bn in ("ear_L", "ear_R"):
            P[bn].rotation_euler = R(0, 0, 1.1 * math.sin(w * 2) * s)

    def wave_pose(t, s):
        w = 2.0 * math.pi * t
        P["upperarm_L"].rotation_euler = R(0, 0,
                                           sgn_L * (9.0 + 10.0 * math.sin(w * 2)) * s)
        P["lowerarm_L"].rotation_euler = R(0, 23.0 * math.sin(w * 2 + 0.40) * s, 0)
        P["hand_L"].rotation_euler = R(0, 26.0 * math.sin(w * 2 + 0.80) * s, 0)
        P["head"].rotation_euler = R(0, 0, 2.6 * math.sin(w * 2) * s)
        P["antenna01"].rotation_euler = R(0, 0, 3.0 * math.sin(w * 2) * s)
        P["antenna02"].rotation_euler = R(0, 0, 4.2 * math.sin(w * 2) * s)
        P["upperarm_R"].rotation_euler = R(0, 0, 0.9 * math.sin(w) * s)
        for i, bn in enumerate(("cape01", "cape02", "cape03")):
            P[bn].rotation_euler = R(0, (2.0 + 2.6 * i) * math.sin(w * 2) * s, 0)

    def banzai_pose(t, s):
        e = math.sin(math.pi * t) ** 0.80
        P["upperarm_L"].rotation_euler = R(0, 0, sgn_L * 44.0 * e * s)
        P["lowerarm_L"].rotation_euler = R(0, -7.0 * e * s, 0)
        P["upperarm_R"].rotation_euler = R(0, 0, sgn_R * 44.0 * e * s)
        P["lowerarm_R"].rotation_euler = R(0, 7.0 * e * s, 0)
        P["body"].rotation_euler = R(-2.6 * e * s, 0, 0)
        P["chest"].rotation_euler = R(-2.0 * e * s, 0, 0)
        P["head"].rotation_euler = R(-3.4 * e * s, 0, 0)
        P["root"].location = (0.0, 0.0, 0.010 * HH * e * s)
        P["antenna01"].rotation_euler = R(-5.0 * e * s, 0, 0)
        P["antenna02"].rotation_euler = R(-7.0 * e * s, 0, 0)
        P["scarf"].rotation_euler = R(-3.0 * e * s, 0, 0)
        for i, bn in enumerate(("cape01", "cape02", "cape03")):
            P[bn].rotation_euler = R(-(2.5 + 3.0 * i) * e * s, 0, 0)

    fits = {}
    for nm, fn in (("idle", idle_pose), ("wave", wave_pose), ("banzai", banzai_pose)):
        fits[nm] = _fit_amp(rig, fn, PROBE, fc, fa, fb)
        print("AMP FIT %-7s scale=%.3f clearance=%.3f" % (nm, fits[nm][0], fits[nm][1]))

    rig.animation_data_create()

    def bake(name, pose_fn, s, length, step):
        frames = list(range(1, length + 2, step))
        if frames[-1] != length + 1:
            frames.append(length + 1)
        touched = set()
        for fr in frames:
            _reset_pose(rig)
            pose_fn((fr - 1) / float(length), s)
            for pb in P:
                if any(abs(v) > 1e-6 for v in pb.rotation_euler):
                    touched.add((pb.name, "rotation_euler"))
                if any(abs(v) > 1e-9 for v in pb.location):
                    touched.add((pb.name, "location"))
                if any(abs(v - 1.0) > 1e-6 for v in pb.scale):
                    touched.add((pb.name, "scale"))
        a = bpy.data.actions.new(name)
        a.use_fake_user = True
        rig.animation_data.action = a
        for fr in frames:
            _reset_pose(rig)
            pose_fn((fr - 1) / float(length), s)
            for bn, path in touched:
                P[bn].keyframe_insert(data_path=path, frame=fr)
        _reset_pose(rig)
        print("ACTION %-14s frames=%d channels=%d" % (name, len(frames), len(touched)))
        return a

    idle = bake("MimamoIdle", idle_pose, fits["idle"][0], 120, 6)
    bake("MimamoWave", wave_pose, fits["wave"][0], 48, 3)
    bake("MimamoBanzai", banzai_pose, fits["banzai"][0], 60, 4)
    rig.animation_data.action = idle
    bpy.ops.object.mode_set(mode="OBJECT")

    # ---- shape-key action: blink + talk only (no eye rotation anywhere) ---- #
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
    for k in kb.values():
        k.value = 0.0

    scene.frame_start = 1
    scene.frame_end = 120
    scene.frame_set(1)


# --------------------------------------------------------------------------- #
def main(outp=None, save=True, pre_action_hook=None):
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    here = os.path.dirname(os.path.abspath(__file__))
    if outp is None:
        outp = argv[0] if argv else os.path.join(here, "stage_rigged.blend")

    coll = bpy.data.collections.get("MIMAMO")
    objs = [o for o in coll.objects if o.type == "MESH"]
    print("MESH PARTS", len(objs))

    assign_groups(objs)

    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    body = bpy.context.view_layer.objects.active
    body.name = "Mimamo"
    body.data.name = "MimamoMesh"
    print("JOINED", len(body.data.vertices), "verts",
          len(body.vertex_groups), "vgroups")

    rig = build_armature(coll)
    add_shape_keys(body)

    body.parent = rig
    mod = body.modifiers.new("Armature", "ARMATURE")
    mod.object = rig
    mod.use_vertex_groups = True

    if pre_action_hook is not None:
        pre_action_hook(rig, body)

    make_actions(rig, body)

    if save:
        bpy.ops.wm.save_as_mainfile(filepath=outp)
        print("SAVED", outp)
    print("ACTIONS", sorted(a.name for a in bpy.data.actions))
    print("BONES", len(rig.data.bones))
    return rig, body


if os.environ.get("OPUS_AUTORUN", "1") == "1":
    main()
