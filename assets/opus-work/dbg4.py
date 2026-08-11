import bpy
o=bpy.data.objects["Mimamo"]; me=o.data
mi={m.name:i for i,m in enumerate(me.materials)}
def rng(idx, xf=None):
    pts=[me.vertices[v].co for p in me.polygons if p.material_index==idx for v in p.vertices]
    if xf: pts=[p for p in pts if xf(p)]
    if not pts: return None
    return (min(p.x for p in pts),max(p.x for p in pts),min(p.y for p in pts),max(p.y for p in pts),min(p.z for p in pts),max(p.z for p in pts))
print("DEV_phoneside", rng(mi["MI_Device"], lambda p: p.x>0.15))
print("SCREEN", rng(mi["MI_PhoneScreen"]))
print("origin", tuple(round(v,4) for v in o.matrix_world.translation))
b=rng(mi["MI_Blush"])
print("BLUSH", b)
img=bpy.data.images.get("opus_blush.png")
print("blushimg", img.size[:] if img else None)
m=bpy.data.materials["MI_Blush"]
for n in m.node_tree.nodes: print("  node",n.type,[ (i.name, getattr(i,'default_value',None)) for i in n.inputs][:3] if n.type=='BSDF_PRINCIPLED' else '')
