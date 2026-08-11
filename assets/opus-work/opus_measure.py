"""Measure the finished character and write assets/mimamo-opus-measurements.json.

Run:
    blender --background --factory-startup assets\\mimamo-robot-opus-working.blend ^
            --python assets\\opus-work\\opus_measure.py

Everything is reported in Blender world space (Z-up, soles on Z=0) and in
glTF space (Y-up, the axis convention produced by export_yup=True), together
with the fraction of total height, so the web layer never has to guess.
"""
import json
import os

import bpy

BLEND = bpy.data.filepath
ASSETS = os.path.dirname(BLEND)
OUT = os.path.join(ASSETS, "mimamo-opus-measurements.json")


def yup(p):
    """Blender (x, y, z) -> glTF (x, z, -y)."""
    return [round(p[0], 6), round(p[2], 6), round(-p[1], 6)]


def zup(p):
    return [round(p[0], 6), round(p[1], 6), round(p[2], 6)]


def group_points(ob, names, wmin=0.4):
    idx = {ob.vertex_groups[n].index for n in names if n in ob.vertex_groups}
    if not idx:
        return []
    mw = ob.matrix_world
    out = []
    for v in ob.data.vertices:
        for g in v.groups:
            if g.group in idx and g.weight > wmin:
                out.append(mw @ v.co)
                break
    return out


def bounds(pts):
    if not pts:
        return None
    xs = [p.x for p in pts]
    ys = [p.y for p in pts]
    zs = [p.z for p in pts]
    return {
        "min_zup": [round(min(xs), 6), round(min(ys), 6), round(min(zs), 6)],
        "max_zup": [round(max(xs), 6), round(max(ys), 6), round(max(zs), 6)],
        "centre_zup": [round(0.5 * (min(xs) + max(xs)), 6),
                       round(0.5 * (min(ys) + max(ys)), 6),
                       round(0.5 * (min(zs) + max(zs)), 6)],
        "size": [round(max(xs) - min(xs), 6), round(max(ys) - min(ys), 6),
                 round(max(zs) - min(zs), 6)],
    }


def main():
    body = bpy.data.objects["Mimamo"]
    rig = bpy.data.objects["MimamoRig"]

    dg = bpy.context.evaluated_depsgraph_get()
    ev = body.evaluated_get(dg)
    me = ev.to_mesh()
    mw = body.matrix_world
    pts = [mw @ v.co for v in me.vertices]
    n_v = len(me.vertices)
    n_t = len(me.loop_triangles) if me.loop_triangles else 0
    ev.to_mesh_clear()

    xs = [p.x for p in pts]
    ys = [p.y for p in pts]
    zs = [p.z for p in pts]
    H = max(zs) - min(zs)

    def ratio(z):
        return round((z - min(zs)) / H, 6)

    doc = {
        "_comment": "Measured from the built model, not from design intent. "
                    "Blender is Z-up with soles on Z=0; gltf/three.js is Y-up "
                    "(export_yup=True) with soles on Y=0.",
        "source_blend": os.path.basename(BLEND),
        "height_axis": {"blender": "Z", "gltf_threejs": "Y"},
        "totals": {
            "height_m": round(H, 6),
            "width_m": round(max(xs) - min(xs), 6),
            "depth_m": round(max(ys) - min(ys), 6),
            "feet_plane_z": round(min(zs), 8),
            "vertices": n_v,
            "triangles": n_t,
        },
        "bbox_blender_zup": {
            "min": [round(min(xs), 6), round(min(ys), 6), round(min(zs), 6)],
            "max": [round(max(xs), 6), round(max(ys), 6), round(max(zs), 6)],
        },
        "bbox_gltf_yup": {
            "min": [round(min(xs), 6), round(min(zs), 6), round(-max(ys), 6)],
            "max": [round(max(xs), 6), round(max(zs), 6), round(-min(ys), 6)],
        },
        "bones": {},
        "regions": {},
    }

    want = ["root", "body", "chest", "neck", "head", "jaw", "eye_L", "eye_R",
            "ear_L", "ear_R", "antenna01", "antenna02", "scarf", "chest_heart",
            "upperarm_L", "lowerarm_L", "hand_L",
            "upperarm_R", "lowerarm_R", "hand_R",
            "thigh_L", "shin_L", "foot_L", "thigh_R", "shin_R", "foot_R",
            "cape01", "cape02", "cape03"]
    amw = rig.matrix_world
    for bn in want:
        b = rig.data.bones.get(bn)
        if b is None:
            continue
        h = amw @ b.head_local
        t = amw @ b.tail_local
        doc["bones"][bn] = {
            "head_zup": zup(h), "tail_zup": zup(t),
            "head_yup": yup(h), "tail_yup": yup(t),
            "height_ratio": ratio(h.z),
        }

    regions = {
        "head_shell": ["head"],
        "eyes": ["eye_L", "eye_R"],
        "eye_L_screen_left": ["eye_L"],
        "eye_R_screen_right": ["eye_R"],
        "mouth_jaw": ["jaw"],
        "antenna": ["antenna01", "antenna02"],
        "hand_waving": ["hand_L"],
        "hand_phone": ["hand_R"],
        "ear_pods": ["ear_L", "ear_R"],
        "cape": ["cape01", "cape02", "cape03"],
    }
    for name, groups in regions.items():
        b = bounds(group_points(body, groups))
        if b is None:
            continue
        c = b["centre_zup"]
        b["centre_yup"] = [c[0], c[2], -c[1]]
        b["height_ratio_centre"] = ratio(c[2])
        doc["regions"][name] = b

    eyes = doc["regions"].get("eyes")
    head = doc["regions"].get("head_shell")
    if eyes and head:
        fc_z = eyes["centre_zup"]
        doc["threejs"] = {
            "_comment": "Use these measured values directly. Do NOT derive the "
                        "face centre from the antenna or from the whole-model "
                        "bounding box - the antenna adds ~15% of the height "
                        "above the crown and skews any midpoint estimate.",
            "face_centre_yup": [fc_z[0], round(fc_z[2], 6), round(-fc_z[1], 6)],
            "eye_centre_height_ratio": ratio(fc_z[2]),
            "head_bounds_yup": {
                "min": [head["min_zup"][0], head["min_zup"][2], -head["max_zup"][1]],
                "max": [head["max_zup"][0], head["max_zup"][2], -head["min_zup"][1]],
            },
            "head_width_m": head["size"][0],
            "head_height_m": head["size"][2],
            "model_height_m": round(H, 6),
            "feet_plane_y": 0.0,
            "recommended_camera_target_yup": [0.0, round(fc_z[2], 6), 0.0],
            "load_note": "Read the bounding box after two nested "
                         "requestAnimationFrame callbacks following "
                         "GLTFLoader.onLoad, so skinning and morph state are "
                         "resolved. Renderer is created with "
                         "preserveDrawingBuffer:false.",
        }

    with open(OUT, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=2, ensure_ascii=False)
    print("MEASURE height=%.5f feet_z=%.8f verts=%d" % (H, min(zs), n_v))
    print("MEASURE face_centre_yup", doc.get("threejs", {}).get("face_centre_yup"))
    print("WROTE", OUT)


main()
