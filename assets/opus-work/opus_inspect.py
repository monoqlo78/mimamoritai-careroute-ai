import bpy
import sys

path = sys.argv[sys.argv.index("--") + 1]
bpy.ops.wm.open_mainfile(filepath=path)
print("=== COLLECTIONS ===")
for c in bpy.data.collections:
    print(" ", c.name, "objects:", len(c.objects))
print("=== OBJECTS ===")
for o in sorted(bpy.data.objects, key=lambda x: x.name):
    dims = tuple(round(v, 3) for v in o.dimensions)
    print(f"  {o.name:34s} {o.type:9s} dims={dims} loc={tuple(round(v,3) for v in o.location)}")
print("=== MATERIALS ===")
for m in bpy.data.materials:
    print(" ", m.name)
print("=== ARMATURES ===")
for a in bpy.data.armatures:
    print(" ", a.name, "bones:", len(a.bones))
    for b in a.bones:
        print("    ", b.name, tuple(round(v, 3) for v in b.head_local), tuple(round(v, 3) for v in b.tail_local))
print("=== ACTIONS ===")
for a in bpy.data.actions:
    print(" ", a.name, "frames", a.frame_range[:], "fcurves", len(a.fcurves))
print("=== SHAPEKEYS ===")
for k in bpy.data.shape_keys:
    print(" ", k.name, [b.name for b in k.key_blocks])
print("=== IMAGES ===")
for i in bpy.data.images:
    print(" ", i.name, i.size[:], "packed:", bool(i.packed_file), i.filepath)
print("=== SCENE ===")
sc = bpy.context.scene
print(" engine", sc.render.engine, "res", sc.render.resolution_x, sc.render.resolution_y,
      "frames", sc.frame_start, sc.frame_end, "camera", sc.camera.name if sc.camera else None)
