import bpy
ns=sorted(o.name for o in bpy.data.objects)
print(len(ns)); print(ns)
