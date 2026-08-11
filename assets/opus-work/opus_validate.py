"""Independent validation of the staged blend: objects, materials, rig, actions,
reference packing/render flags, shape keys, and a live pose sample proving the
idle/wave animation actually moves the antenna, heart and cape bones."""
import bpy
import os

print("=" * 68)
print("BLEND", bpy.data.filepath)
print("=" * 68)

objs = list(bpy.data.objects)
print("objects        ", len(objs))
for o in objs:
    print("   %-26s %-10s parent=%s" % (o.name, o.type, o.parent.name if o.parent else "-"))

me = bpy.data.objects["Mimamo"].data
print("mesh verts     ", len(me.vertices))
print("mesh polys     ", len(me.polygons))
print("materials      ", len(bpy.data.materials))
print("slots on mesh  ", len(me.materials))
print("vertex colors  ", [c.name for c in me.color_attributes])
print("uv layers      ", [u.name for u in me.uv_layers])
print("vertex groups  ", len(bpy.data.objects["Mimamo"].vertex_groups))
mods = [(m.name, m.type) for m in bpy.data.objects["Mimamo"].modifiers]
print("modifiers      ", mods)

kb = me.shape_keys
print("shape keys     ", [k.name for k in kb.key_blocks] if kb else None)
if kb:
    for k in kb.key_blocks:
        print("     %-12s value=%.3f" % (k.name, k.value))

arm = bpy.data.objects.get("MimamoRig")
print("armature       ", arm.name if arm else None)
print("bones          ", len(arm.data.bones) if arm else 0)
print("bone names     ", sorted(b.name for b in arm.data.bones)[:40] if arm else [])

print("actions        ", [a.name for a in bpy.data.actions])

imgs = [(i.name, i.packed_file is not None, i.size[:]) for i in bpy.data.images]
print("images         ", imgs)

vl = bpy.context.view_layer
for lc in vl.layer_collection.children:
    print("collection     %-28s exclude=%s hide_vp=%s"
          % (lc.name, lc.exclude, lc.hide_viewport))
for ob in bpy.data.objects:
    if "Reference" in ob.name:
        print("reference obj  %-24s hide_render=%s hide_viewport=%s"
              % (ob.name, ob.hide_render, ob.hide_viewport))

cam = bpy.data.objects.get("FrontOrthoCam")
print("front cam      loc=%s ortho=%.4f type=%s"
      % (tuple(round(v, 4) for v in cam.location), cam.data.ortho_scale, cam.data.type))
print("frame range    ", bpy.context.scene.frame_start, bpy.context.scene.frame_end)

# ---- animation liveness: sample bones across the timeline ------------------ #
sc = bpy.context.scene
watch = ["antenna", "heart", "cape", "upperarm_R", "root", "chest"]
if arm:
    names = [b.name for b in arm.pose.bones]
    sel = [n for n in names
           if any(w.lower() in n.lower() for w in watch)]
    print("sampled bones  ", sel)
    for act_name in ("MimamoIdle", "MimamoWave"):
        act = bpy.data.actions.get(act_name)
        if not act:
            continue
        if arm.animation_data is None:
            arm.animation_data_create()
        arm.animation_data.action = act
        try:
            for slot in getattr(act, "slots", []):
                arm.animation_data.action_slot = slot
                break
        except Exception:
            pass
        rows = {}
        for f in (1, 20, 40, 60, 80, 100, 120):
            sc.frame_set(f)
            bpy.context.view_layer.update()
            for n in sel:
                pb = arm.pose.bones[n]
                if pb.rotation_mode == "QUATERNION":
                    rot = tuple(pb.rotation_quaternion[1:4])
                else:
                    rot = tuple(pb.rotation_euler)
                v = tuple(round(x, 5) for x in
                          (pb.location[0], pb.location[1], pb.location[2],
                           rot[0], rot[1], rot[2],
                           pb.scale[0], pb.scale[2]))
                rows.setdefault(n, []).append(v)
        print("--", act_name)
        for n, vs in rows.items():
            uniq = len(set(vs))
            span = max(max(abs(x) for x in v) for v in vs)
            print("   %-16s distinct_frames=%d  max|val|=%.5f  %s"
                  % (n, uniq, span, "MOVES" if uniq > 1 else "STATIC"))

sc.frame_set(1)

# ---- prove a render is non-empty ------------------------------------------ #
sc.render.engine = "BLENDER_WORKBENCH"
sc.render.resolution_x = 300
sc.render.resolution_y = 360
sc.camera = cam
out = os.path.join(os.path.dirname(bpy.data.filepath), "opus-work", "validate_render.png")
sc.render.filepath = out
bpy.ops.render.render(write_still=True)
print("test render    ", os.path.exists(out), os.path.getsize(out) if os.path.exists(out) else 0)
print("=" * 68)
