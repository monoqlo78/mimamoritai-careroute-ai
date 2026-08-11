"""Vertex census of the joined mascot, grouped by material and vertex group."""
import bpy
from collections import defaultdict

body = bpy.data.objects["Mimamo"]
me = body.data

mat_v = defaultdict(set)
for p in me.polygons:
    nm = me.materials[p.material_index].name if me.materials else "?"
    for vi in p.vertices:
        mat_v[nm].add(vi)

print("TOTAL VERTS", len(me.vertices), "POLYS", len(me.polygons))
print("--- by material ---")
for nm, s in sorted(mat_v.items(), key=lambda kv: -len(kv[1])):
    print("%-22s %6d" % (nm, len(s)))

grp = {g.index: g.name for g in body.vertex_groups}
gv = defaultdict(int)
for v in me.vertices:
    best, bw = None, -1.0
    for g in v.groups:
        if g.weight > bw:
            best, bw = g.group, g.weight
    gv[grp.get(best, "<none>")] += 1
print("--- by dominant vertex group ---")
for nm, n in sorted(gv.items(), key=lambda kv: -kv[1]):
    print("%-22s %6d" % (nm, n))

print("--- colour layers ---")
for a in me.color_attributes:
    print(a.name, a.domain, a.data_type)
print("--- shape keys ---")
if me.shape_keys:
    print([k.name for k in me.shape_keys.key_blocks])
