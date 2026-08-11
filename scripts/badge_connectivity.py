import bpy, sys
from mathutils import Vector

path = sys.argv[-1]
bpy.ops.wm.open_mainfile(filepath=path)
obj = bpy.data.objects.get("Mimamo")
me = obj.data
mats = [m.name if m else "<none>" for m in me.materials]

vmat = [set() for _ in range(len(me.vertices))]
for poly in me.polygons:
    mn = mats[poly.material_index] if poly.material_index < len(mats) else "<none>"
    for vi in poly.vertices:
        vmat[vi].add(mn)

# union-find over edges
n = len(me.vertices)
parent = list(range(n))
def find(a):
    while parent[a] != a:
        parent[a] = parent[parent[a]]
        a = parent[a]
    return a
def union(a, b):
    ra, rb = find(a), find(b)
    if ra != rb: parent[ra] = rb
for e in me.edges:
    union(e.vertices[0], e.vertices[1])

from collections import defaultdict
comp = defaultdict(list)
for i in range(n):
    comp[find(i)].append(i)

# pink gem verts (chest region only): MI_PinkHeart near chest
gi = obj.vertex_groups.get("chest_heart"); gidx = gi.index
hv = [v.co.copy() for v in me.vertices if any(g.group==gidx and g.weight>0.5 for g in v.groups)]
hc = sum(hv, Vector())/len(hv); hx,hy,hz = hc.x,hc.y,hc.z
rad = max(((p.x-hx)**2+(p.z-hz)**2)**0.5 for p in hv)
print("heart ctr (%.4f,%.4f,%.4f) rad %.4f  total comps %d" % (hx,hy,hz,rad,len(comp)))

# which component(s) do the chest pink-gem verts belong to?
gem_ids = [v.index for v in me.vertices
           if "MI_PinkHeart" in vmat[v.index]
           and ((v.co.x-hx)**2+(v.co.z-hz)**2)**0.5 <= rad*1.3
           and v.co.y <= hy+0.09]
gem_comps = set(find(i) for i in gem_ids)
print("chest pink-gem verts n=%d  spread over %d component(s)" % (len(gem_ids), len(gem_comps)))

def bbox(ids):
    xs=[me.vertices[i].co.x for i in ids]; ys=[me.vertices[i].co.y for i in ids]; zs=[me.vertices[i].co.z for i in ids]
    return (min(xs),max(xs),min(ys),max(ys),min(zs),max(zs))

for c in gem_comps:
    ids = comp[c]
    mm = defaultdict(int)
    for i in ids:
        for m in vmat[i]: mm[m]+=1
    bx = bbox(ids)
    print("\nCOMPONENT root=%d  size=%d verts" % (c, len(ids)))
    print("  bbox x[%.3f..%.3f] y[%.3f..%.3f] z[%.3f..%.3f]" % bx)
    print("  materials:")
    for m in sorted(mm, key=lambda k:-mm[k]):
        print("    %-16s %d" % (m, mm[m]))

# Enumerate ALL components that have any vertex inside the badge region
print("\n=== ALL components intersecting badge region (xz<=1.8*rad, y<=hy+0.09) ===")
R = rad*1.8
region_comps = defaultdict(int)   # comp root -> count of verts of that comp inside region
for i in range(n):
    p = me.vertices[i].co
    if ((p.x-hx)**2+(p.z-hz)**2)**0.5 <= R and p.y <= hy+0.09:
        region_comps[find(i)] += 1
for c in sorted(region_comps, key=lambda k:-region_comps[k]):
    ids = comp[c]
    mm = defaultdict(int)
    for i in ids:
        for m in vmat[i]: mm[m]+=1
    bx = bbox(ids)
    topmats = ",".join("%s:%d"%(m,mm[m]) for m in sorted(mm,key=lambda k:-mm[k])[:4])
    print("  comp=%-6d total=%5d  in_region=%4d  z[%.3f..%.3f] y[%.3f..%.3f]  {%s}" %
          (c, len(ids), region_comps[c], bx[4], bx[5], bx[2], bx[3], topmats))
