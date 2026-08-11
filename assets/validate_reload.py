import bpy
bl = r"C:\Users\msoga\OneDrive - Smart Designer\Projects\見守り隊\assets\mimamo-robot-rigged.blend"
bpy.ops.wm.open_mainfile(filepath=bl)
arm = [o for o in bpy.data.objects if o.type=="ARMATURE"][0]
print("ARMATURE:", arm.name)
print("BONES:", sorted([b.name for b in arm.data.bones]))
print("ACTIONS:", [a.name for a in bpy.data.actions])
sk_objs = [o.name for o in bpy.data.objects if o.type=="MESH" and o.data.shape_keys]
print("SHAPEKEY_OBJS:", sk_objs)
for o in bpy.data.objects:
    if o.name.startswith("SmileCorner") or o.name.startswith("Blush"):
        print(o.name, "loc=", tuple(o.location), "mat=", o.data.materials[0].name if o.data.materials else None)
act = bpy.data.actions[0] if bpy.data.actions else None
if act:
    print("FRAME_RANGE:", act.frame_range[:])
print("SCENE_FRAME_END:", bpy.context.scene.frame_end)
