"""Probe the head connected components before any local scale (task C).

The parent asked to x-scale ONLY the white helmet shell island.  That is only
safe if the shell is its own connected component, separate from the ear pods and
from the face features (eyes / brows / jaw / forehead heart).  This script lists
every island that reaches the head region with vertex count, material mix and
x/y/z range, and reports which island each key vertex group lands in, so we can
decide before touching anything.

Run: blender -b <blend> -P scripts/head_shell_probe.py
"""

import bpy

obj = bpy.data.objects["Mimamo"]
mesh = obj.data
idx = {g.name: g.index for g in obj.vertex_groups}

mat_names = [m.name for m in mesh.materials]
vmat = [set() for _ in mesh.vertices]
for poly in mesh.polygons:
    mn = mat_names[poly.material_index]
    for vi in poly.vertices:
        vmat[vi].add(mn)

# union-find islands
n = len(mesh.vertices)
par = list(range(n))


def find(a):
    while par[a] != a:
        par[a] = par[par[a]]
        a = par[a]
    return a


for e in mesh.edges:
    ra, rb = find(e.vertices[0]), find(e.vertices[1])
    if ra != rb:
        par[ra] = rb
comp = [find(i) for i in range(n)]
members = {}
for i, r in enumerate(comp):
    members.setdefault(r, []).append(i)

co = [v.co for v in mesh.vertices]
zmax = max(p.z for p in co)
zmin = min(p.z for p in co)
# "head region": upper third of the model
head_z = zmin + (zmax - zmin) * 0.62
print(f"MODEL z {zmin:.3f}..{zmax:.3f}  head_region z>{head_z:.3f}")


def matmix(mem):
    c = {}
    for i in mem:
        for m in vmat[i]:
            c[m] = c.get(m, 0) + 1
    return dict(sorted(c.items(), key=lambda kv: -kv[1])[:4])


# groups whose island we care about
GROUPS = ["head", "jaw", "eye_L", "eye_R", "eyebrow_L", "eyebrow_R",
          "ear_L", "ear_R", "antenna01", "antenna02", "chest_heart"]


def group_island(gname):
    if gname not in idx:
        return None
    gi = idx[gname]
    isl = {}
    for i, v in enumerate(mesh.vertices):
        for g in v.groups:
            if g.group == gi and g.weight > 0.5:
                r = comp[i]
                isl[r] = isl.get(r, 0) + 1
    return dict(sorted(isl.items(), key=lambda kv: -kv[1]))


# list big islands reaching the head region
big = []
for r, mem in members.items():
    zc = sum(co[i].z for i in mem) / len(mem)
    if max(co[i].z for i in mem) > head_z and len(mem) >= 40:
        xs = [co[i].x for i in mem]
        ys = [co[i].y for i in mem]
        zs = [co[i].z for i in mem]
        big.append((len(mem), r, min(xs), max(xs), min(zs), max(zs),
                    sum(xs) / len(mem), sum(zs) / len(mem)))
big.sort(reverse=True)
print("\n== ISLANDS reaching head region (size desc) ==")
for sz, r, x0, x1, z0, z1, xc, zc in big[:18]:
    print(f" id{r:6d} n{sz:6d} x[{x0:+.3f},{x1:+.3f}] z[{z0:.3f},{z1:.3f}] "
          f"xc{xc:+.3f} zc{zc:.3f} mats{matmix(members[r])}")

print("\n== which island holds each vertex group ==")
for g in GROUPS:
    gi = group_island(g)
    if gi is None:
        print(f" {g:12s}: (no group)")
        continue
    parts = ", ".join(f"id{r}:{c}" for r, c in list(gi.items())[:3])
    print(f" {g:12s}: {parts}")
