"""Focused animation liveness check: sample each action over its own frame range."""
import bpy

sc = bpy.context.scene
arm = bpy.data.objects["MimamoRig"]
if arm.animation_data is None:
    arm.animation_data_create()

RANGES = {"MimamoIdle": 120, "MimamoWave": 48, "MimamoBanzai": 60}
WATCH = ["upperarm_L", "lowerarm_L", "hand_L", "upperarm_R", "lowerarm_R",
         "antenna02", "chest_heart", "cape02", "scarf", "eye_L", "eye_R",
         "jaw", "head", "body", "root"]
EYES = ("eye_L", "eye_R")
gaze_violations = []

if bpy.data.actions.get("MimamoGaze"):
    print("POLICY FAIL: MimamoGaze still exists (eye gaze is forbidden)")
else:
    print("POLICY OK: no MimamoGaze action")

for name, last in RANGES.items():
    act = bpy.data.actions.get(name)
    if not act:
        print("MISSING", name)
        continue
    arm.animation_data.action = act
    for slot in getattr(act, "slots", []):
        arm.animation_data.action_slot = slot
        break
    rows = {}
    frames = [1 + round(i * (last - 1) / 8.0) for i in range(9)]
    for f in frames:
        sc.frame_set(f)
        bpy.context.view_layer.update()
        for n in WATCH:
            pb = arm.pose.bones.get(n)
            if not pb:
                continue
            rot = (tuple(pb.rotation_quaternion[1:4])
                   if pb.rotation_mode == "QUATERNION" else tuple(pb.rotation_euler))
            v = tuple(round(x, 5) for x in
                      (pb.location[0], pb.location[2], rot[0], rot[1], rot[2],
                       pb.scale[0], pb.scale[2]))
            rows.setdefault(n, []).append(v)
    print("--", name, "frames", frames)
    for n, vs in rows.items():
        rng = max(max(abs(a - b) for a, b in zip(v, vs[0])) for v in vs)
        moves = len(set(vs)) > 1
        if n in EYES and moves:
            gaze_violations.append((name, n, rng))
        print("   %-14s distinct=%d  max_delta=%.4f  %s"
              % (n, len(set(vs)), rng, "MOVES" if moves else "STATIC"))

# shape key driven face action
fa = bpy.data.actions.get("MimamoFaceIdle")
me = bpy.data.objects["Mimamo"].data
kb = me.shape_keys
if fa and kb:
    if kb.animation_data is None:
        kb.animation_data_create()
    kb.animation_data.action = fa
    for slot in getattr(fa, "slots", []):
        kb.animation_data.action_slot = slot
        break
    vals = []
    for f in range(1, 121, 5):
        sc.frame_set(f)
        bpy.context.view_layer.update()
        vals.append(round(kb.key_blocks["Blink"].value, 3))
    print("-- MimamoFaceIdle Blink samples", vals)
    print("   blink peak", max(vals), "distinct", len(set(vals)))

print("-- eye-gaze policy:",
      "FAIL %s" % (gaze_violations,) if gaze_violations
      else "OK (eye bones never rotate in any action)")
sc.frame_set(1)
