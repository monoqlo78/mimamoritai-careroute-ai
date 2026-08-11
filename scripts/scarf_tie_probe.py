"""Probe the vertical teal band between the scarf knot and the chest badge.

Run: blender -b <blend> -P scripts/scarf_tie_probe.py
Lists every connected-component island that intersects the neck->badge strip
(centre column, z above the chest heart) with its material mix, vert count and
x/z extents, so we can tell whether the badge move stretched or exposed a tie.
"""
import bpy

obj = bpy.data.objects["Mimamo"]
mesh = obj.data
mats = [m.name for m in mesh.materials]

# per-vertex material tags
vmat = [set() for _ in mesh.vertices]
for poly in mesh.polygons:
    mn = mats[poly.material_index]
    for vi in poly.vertices:
        vmat[vi].add(mn)

# union-find islands
par = list(range(len(mesh.vertices)))
def find(a):
    while par[a] != a:
        par[a] = par[par[a]]
        a = par[a]
    return a
for e in mesh.edges:
    ra, rb = find(e.vertices[0]), find(e.vertices[1])
    if ra != rb:
        par[ra] = rb
comp = {}
for i in range(len(mesh.vertices)):
    comp.setdefault(find(i), []).append(i)

co = [v.co for v in mesh.vertices]
cx = sum(p.x for p in co) / len(co)

# chest heart centroid via vertex group, to anchor the strip
idx = {g.name: g.index for g in obj.vertex_groups}
hi = idx.get("chest_heart")
hpts = [co[i] for i, v in enumerate(mesh.vertices)
        if any(g.group == hi and g.weight > 0.5 for g in v.groups)] if hi is not None else []
if hpts:
    hcx = sum(p.x for p in hpts) / len(hpts)
    hcz = sum(p.z for p in hpts) / len(hpts)
    hz0 = min(p.z for p in hpts); hz1 = max(p.z for p in hpts)
else:
    hcx, hcz, hz0, hz1 = cx, 0.47, 0.45, 0.49
print(f"MATS {mats}")
print(f"HEART cx {hcx:.4f} cz {hcz:.4f} z {hz0:.4f}..{hz1:.4f} bodycx {cx:.4f}")

# strip: centre column (|x-hcx|<0.09), z from heart top up to +0.20 (neck knot)
rows = []
for r, mem in comp.items():
    xs = [co[i].x for i in mem]; zs = [co[i].z for i in mem]
    mx = sum(xs)/len(xs); mz = sum(zs)/len(zs)
    if abs(mx - hcx) < 0.09 and (hz0 - 0.02) < mz < (hz1 + 0.24):
        ms = {}
        for i in mem:
            for m in vmat[i]:
                ms[m] = ms.get(m, 0) + 1
        rows.append((len(mem), round(mz,4), round(min(zs),4), round(max(zs),4),
                     round(mx,4), round(min(xs),4), round(max(xs),4), ms))
rows.sort(key=lambda t: -t[1])  # top-down
print(f"STRIP islands (neck->badge centre column): {len(rows)}")
for n, mz, z0, z1, mx, x0, x1, ms in rows:
    print(f"  n={n:5d} zc={mz:.4f} z[{z0:.4f},{z1:.4f}] xc={mx:.4f} x[{x0:.4f},{x1:.4f}] {ms}")
