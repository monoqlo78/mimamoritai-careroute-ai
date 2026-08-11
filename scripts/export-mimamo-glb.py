"""Re-export the Mimamo GLB with base colours baked into COLOR_0.

The authored Blender materials are `authored_colour x vertex_colour("Col")`.
glTF applies COLOR_0 to every primitive of a mesh, so the per-material mix
cannot survive a naive export - and the original export additionally corrupted
"Col" into an all-black BYTE_COLOR layer, which multiplied the whole character
to black.

This bakes the final base colour (authored x Col, or authored alone for
materials that do not read the vertex layer) into a CORNER FLOAT_COLOR layer,
resets every baseColorFactor to white, and exports. The result renders
identically to the authored Cycles look.

Run: blender -b assets/mimamo-robot-opus-working.blend -P scripts/export-mimamo-glb.py -- <out.glb>
"""

import os
import sys

import bpy

ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = os.path.abspath(ARGS[0]) if ARGS else os.path.abspath("mimamo-robot-opus-rigged.glb")

BAKED = "BakedCol"


def material_color(mat):
    """Return (authored_rgb, uses_vertex_layer)."""
    bsdf = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if bsdf is None:
        return (1.0, 1.0, 1.0), False
    slot = bsdf.inputs["Base Color"]
    if not slot.is_linked:
        return tuple(slot.default_value[:3]), False
    node = slot.links[0].from_node
    if node.type != "MIX":
        return tuple(slot.default_value[:3]), False
    rgb = (1.0, 1.0, 1.0)
    uses_vertex = False
    for inp in node.inputs:
        if inp.type != "RGBA":
            continue
        if inp.is_linked:
            if inp.links[0].from_node.type == "VERTEX_COLOR":
                uses_vertex = True
        else:
            rgb = tuple(inp.default_value[:3])
    return rgb, uses_vertex


def bake(obj):
    mesh = obj.data
    source = mesh.color_attributes.get("Col")
    if source is None:
        raise SystemExit("source colour attribute 'Col' missing")

    # Snapshot the authored per-vertex tint before touching the layer stack.
    if source.domain == "POINT":
        tint = [tuple(d.color[:3]) for d in source.data]
        per_vertex = True
    else:
        tint = [tuple(d.color[:3]) for d in source.data]
        per_vertex = False

    palette = [material_color(slot.material) for slot in obj.material_slots]

    existing = mesh.color_attributes.get(BAKED)
    if existing:
        mesh.color_attributes.remove(existing)
    baked = mesh.color_attributes.new(name=BAKED, type="FLOAT_COLOR", domain="CORNER")

    for poly in mesh.polygons:
        rgb, uses_vertex = palette[poly.material_index]
        for loop_index in poly.loop_indices:
            if uses_vertex:
                idx = mesh.loops[loop_index].vertex_index if per_vertex else loop_index
                t = tint[idx]
                color = (rgb[0] * t[0], rgb[1] * t[1], rgb[2] * t[2])
            else:
                color = rgb
            baked.data[loop_index].color = (color[0], color[1], color[2], 1.0)

    mesh.color_attributes.remove(source)
    mesh.color_attributes.active_color_index = mesh.color_attributes.find(BAKED)
    mesh.color_attributes.render_color_index = mesh.color_attributes.find(BAKED)

    # COLOR_0 now carries the full colour, so every material multiplies by white.
    for slot in obj.material_slots:
        mat = slot.material
        bsdf = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if bsdf is None:
            continue
        base = bsdf.inputs["Base Color"]
        for link in list(base.links):
            mat.node_tree.links.remove(link)
        base.default_value = (1.0, 1.0, 1.0, 1.0)
        vertex_node = mat.node_tree.nodes.new("ShaderNodeVertexColor")
        vertex_node.layer_name = BAKED
        mat.node_tree.links.new(vertex_node.outputs["Color"], base)


def main():
    body = bpy.data.objects.get("Mimamo")
    if body is None:
        raise SystemExit("object 'Mimamo' not found")
    bake(body)

    bpy.ops.export_scene.gltf(
        filepath=OUT,
        export_format="GLB",
        export_apply=False,
        export_yup=True,
        export_animations=True,
        export_animation_mode="ACTIONS",
        export_skins=True,
        export_materials="EXPORT",
        export_optimize_animation_size=True,
        # The web app loads this with a plain GLTFLoader (no DRACOLoader), so
        # the mesh must stay uncompressed or the model silently fails to load.
        export_draco_mesh_compression_enable=False,
    )
    print("GLB DONE", OUT, os.path.getsize(OUT))


main()
