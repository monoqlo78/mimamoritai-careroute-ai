import bpy, sys
from mathutils import Vector

path = sys.argv[-1]
bpy.ops.wm.open_mainfile(filepath=path)

obj = bpy.data.objects.get("Mimamo")
me = obj.data
mats = [m.name if m else "<none>" for m in me.materials]
print("MESH MATERIALS (%d):" % len(mats))
for i, n in enumerate(mats):
    print("  [%2d] %s" % (i, n))

# per-vertex material-name set from polygons
vmat = [set() for _ in range(len(me.vertices))]
for poly in me.polygons:
    mn = mats[poly.material_index] if poly.material_index < len(mats) else "<none>"
    for vi in poly.vertices:
        vmat[vi].add(mn)

# chest_heart group centroid
gi = obj.vertex_groups.get("chest_heart")
gidx = gi.index if gi else None
hv = []
for v in me.vertices:
    w = 0.0
    for g in v.groups:
        if g.group == gidx:
            w = g.weight
    if w > 0.5:
        hv.append(v.co.copy())
hc = sum(hv, Vector()) / len(hv)
hx, hy, hz = hc.x, hc.y, hc.z
rad = max(((p.x-hx)**2 + (p.z-hz)**2) ** 0.5 for p in hv)
print("\nchest_heart group: n=%d centroid (%.4f, %.4f, %.4f) rad %.4f" % (len(hv), hx, hy, hz, rad))

R = rad * 1.8
# tally materials for FRONT chest region within 1.8*rad (xz), regardless of material
from collections import defaultdict
cnt = defaultdict(int)
ctr = defaultdict(lambda: Vector())
depthmin = defaultdict(lambda: 1e9); depthmax = defaultdict(lambda: -1e9)
for i, v in enumerate(me.vertices):
    p = v.co
    d = ((p.x-hx)**2 + (p.z-hz)**2) ** 0.5
    if d <= R and p.y <= hy + 0.09:
        for mn in vmat[i]:
            cnt[mn] += 1
            ctr[mn] += p
            depthmin[mn] = min(depthmin[mn], p.y)
            depthmax[mn] = max(depthmax[mn], p.y)
print("\nFRONT chest region (xz<=1.8*rad, y<=hy+0.09) material tally:")
for mn in sorted(cnt, key=lambda k: -cnt[k]):
    c = ctr[mn] / cnt[mn]
    print("  %-18s n=%5d  ctr(%.3f,%.3f,%.3f)  y[%.3f..%.3f]" %
          (mn, cnt[mn], c.x, c.y, c.z, depthmin[mn], depthmax[mn]))

# also: full-mesh count per material of interest
print("\nFull-mesh per-material vertex counts:")
full = defaultdict(int)
for i in range(len(me.vertices)):
    for mn in vmat[i]:
        full[mn] += 1
for mn in sorted(full, key=lambda k: -full[k]):
    print("  %-18s n=%5d" % (mn, full[mn]))
