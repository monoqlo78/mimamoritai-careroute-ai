"""GLB size experiment: measure what actually costs bytes."""
import os
import bpy

WORK = os.path.dirname(os.path.abspath(__file__))


def export(tag, **over):
    out = os.path.join(WORK, "try_%s.glb" % tag)
    kw = dict(filepath=out, export_format="GLB", export_apply=True,
              export_yup=True, export_animations=True, export_skins=True,
              export_morph=True, export_morph_normal=False,
              export_morph_tangent=False, export_materials="EXPORT",
              export_optimize_animation_size=True,
              export_draco_mesh_compression_enable=True,
              export_draco_mesh_compression_level=6)
    kw.update(over)
    bpy.ops.export_scene.gltf(**kw)
    print("TRY %-10s %9d bytes" % (tag, os.path.getsize(out)))
    return os.path.getsize(out)


body = bpy.data.objects["Mimamo"]
me = body.data
print("COLOR ATTRS", [(a.name, a.domain, a.data_type) for a in me.color_attributes])

export("A_base")

# B: float colour -> byte colour
bpy.context.view_layer.objects.active = body
try:
    me.color_attributes.active_color_index = 0
    bpy.ops.geometry.color_attribute_convert(domain="CORNER", data_type="BYTE_COLOR")
except Exception as e:
    print("CONVERT CORNER FAILED", e)
    try:
        bpy.ops.geometry.color_attribute_convert(domain="POINT", data_type="BYTE_COLOR")
    except Exception as e2:
        print("CONVERT POINT FAILED", e2)
print("COLOR ATTRS NOW", [(a.name, a.domain, a.data_type) for a in me.color_attributes])
export("B_bytecol")

# C: byte colour + no draco (sanity)
export("C_nodraco", export_draco_mesh_compression_enable=False)
