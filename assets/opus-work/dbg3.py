import bpy, collections
from mathutils import Vector
o=bpy.data.objects["Mimamo"]
me=o.data
cnt=collections.Counter(p.material_index for p in me.polygons)
for i,m in enumerate(me.materials):
    print(f"{i:2d} {m.name:20s} faces={cnt.get(i,0)}")
# bounds of faces per material for the ones we care about
want={m.name:i for i,m in enumerate(me.materials)}
for nm in ("MI_Blush","MI_PhoneScreen","MI_WatchScreen","MI_PinkLining","MI_Mint"):
    i=want.get(nm)
    if i is None: continue
    pts=[me.vertices[v].co for p in me.polygons if p.material_index==i for v in p.vertices]
    if not pts: print(nm,"no faces"); continue
    xs=[p.x for p in pts]; ys=[p.y for p in pts]; zs=[p.z for p in pts]
    print(f"  {nm}: x[{min(xs):.3f},{max(xs):.3f}] y[{min(ys):.3f},{max(ys):.3f}] z[{min(zs):.3f},{max(zs):.3f}]")
