import bpy
from mathutils import Vector
for n in ("PhoneBody","PhoneScreen","Blush_L","FacePlate","WatchScreen","ChestPlate","ChestFrame","ChestHeart","Cape"):
    o=bpy.data.objects.get(n)
    if not o: print(n,"MISSING"); continue
    bb=[o.matrix_world @ Vector(c) for c in o.bound_box]
    xs=[p.x for p in bb]; ys=[p.y for p in bb]; zs=[p.z for p in bb]
    nrm = (o.matrix_world.to_3x3() @ o.data.polygons[0].normal).normalized() if o.data.polygons else None
    print(f"{n:12s} x[{min(xs):.3f},{max(xs):.3f}] y[{min(ys):.3f},{max(ys):.3f}] z[{min(zs):.3f},{max(zs):.3f}] n0={tuple(round(v,2) for v in nrm) if nrm else None} mats={[m.name for m in o.data.materials]}")
