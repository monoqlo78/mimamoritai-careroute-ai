import bpy, sys
bpy.ops.wm.open_mainfile(filepath=sys.argv[sys.argv.index("--")+1])
print("=== ACTIONS ===")
for a in bpy.data.actions:
    n = 0
    try:
        for s in a.slots: pass
        for l in a.layers:
            for st in l.strips:
                for cb in st.channelbags(a.slots[0]) if False else []: pass
    except Exception as e: pass
    print(" ", a.name, "range", tuple(a.frame_range), "users", a.users)
print("=== SHAPEKEYS ===")
for k in bpy.data.shape_keys: print(" ", k.name, [b.name for b in k.key_blocks])
print("=== NLA / anim data ===")
for o in bpy.data.objects:
    ad = o.animation_data
    if ad:
        print(" obj", o.name, "action", ad.action.name if ad.action else None,
              "tracks", [t.name for t in ad.nla_tracks], [[s.name for s in t.strips] for t in ad.nla_tracks])
print("=== IMAGES ===")
for i in bpy.data.images: print(" ", i.name, i.size[:], "packed", bool(i.packed_file))
sc=bpy.context.scene
print("scene", sc.render.engine, sc.render.resolution_x, sc.render.resolution_y, sc.frame_start, sc.frame_end)
