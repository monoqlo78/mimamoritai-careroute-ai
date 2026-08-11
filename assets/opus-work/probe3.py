import bpy, sys
S = 0.0025


def xr(x):
    return x / S + 555.0


def yr(z):
    return 1100.0 - z / S


names = [n.strip() for n in sys.argv[sys.argv.index("--") + 1:]] if "--" in sys.argv else []
OUT = []
for ob in sorted(bpy.data.objects, key=lambda o: o.name):
    if ob.type != "MESH":
        continue
    if names and not any(n.lower() in ob.name.lower() for n in names):
        continue
    dg = bpy.context.evaluated_depsgraph_get()
    ev = ob.evaluated_get(dg)
    me = ev.to_mesh()
    if not me.vertices:
        ev.to_mesh_clear()
        continue
    ws = [ob.matrix_world @ v.co for v in me.vertices]
    x0 = min(w.x for w in ws); x1 = max(w.x for w in ws)
    z0 = min(w.z for w in ws); z1 = max(w.z for w in ws)
    y0 = min(w.y for w in ws); y1 = max(w.y for w in ws)
    OUT.append("%-16s xr %7.1f..%7.1f  yr %7.1f..%7.1f  y %6.3f..%6.3f" %
               (ob.name, xr(x0), xr(x1), yr(z1), yr(z0), y0, y1))
    ev.to_mesh_clear()

open("probe3.txt", "w", encoding="utf-8").write("\n".join(OUT))
