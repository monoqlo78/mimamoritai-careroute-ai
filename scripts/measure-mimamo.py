"""Measure Mimamo proportions directly from the mesh (front-view equivalents).

All values are normalised by body height (foot sole -> crown, antenna excluded)
so they can be compared 1:1 with landmark reads taken off the reference poster.

Run: blender -b <blend> -P scripts/measure-mimamo.py
"""

import bpy

GROUPS = {
    "head": ["head", "jaw", "eye_L", "eye_R", "eyebrow_L", "eyebrow_R"],
    "ears": ["ear_L", "ear_R"],
    "antenna": ["antenna01", "antenna02"],
    "torso": ["body", "chest"],
    "feet": ["foot_L", "foot_R"],
    "legs": ["thigh_L", "shin_L", "thigh_R", "shin_R"],
    "cape": ["cape01", "cape02", "cape03"],
    "arms": ["upperarm_L", "lowerarm_L", "hand_L", "upperarm_R", "lowerarm_R", "hand_R"],
}


def collect(obj):
    idx = {g.name: g.index for g in obj.vertex_groups}
    out = {}
    for label, names in GROUPS.items():
        wanted = {idx[n] for n in names if n in idx}
        pts = []
        for v in obj.data.vertices:
            w = sum(g.weight for g in v.groups if g.group in wanted)
            if w > 0.5:
                pts.append(obj.matrix_world @ v.co)
        out[label] = pts
    return out


def extent(pts, axis):
    if not pts:
        return 0.0, 0.0
    vals = [p[axis] for p in pts]
    return min(vals), max(vals)


def main():
    obj = bpy.data.objects["Mimamo"]
    parts = collect(obj)

    all_pts = [obj.matrix_world @ v.co for v in obj.data.vertices]
    sole = min(p.z for p in all_pts)
    antenna_pts = parts["antenna"]
    antenna_lo = min((p.z for p in antenna_pts), default=1e9)
    crown = max(p.z for p in all_pts if p.z < antenna_lo) if antenna_pts else max(p.z for p in all_pts)
    height = crown - sole
    print(f"HEIGHT {height:.4f}  sole {sole:.4f}  crown {crown:.4f}")

    def norm_span(label, axis=0):
        lo, hi = extent(parts[label], axis)
        return (hi - lo) / height

    def norm_z(label, pick):
        lo, hi = extent(parts[label], 2)
        z = lo if pick == "lo" else hi
        return (z - sole) / height

    head_lo, head_hi = extent(parts["head"], 2)
    print(f"head_h      {(head_hi - head_lo) / height:.4f}")
    print(f"head_w      {norm_span('head'):.4f}")
    print(f"ear_span    {norm_span('ears'):.4f}")
    print(f"feet_span   {norm_span('feet'):.4f}")
    print(f"cape_span   {norm_span('cape'):.4f}")
    print(f"torso_w     {norm_span('torso'):.4f}")
    print(f"arm_span    {norm_span('arms'):.4f}")
    print(f"chin_z      {norm_z('head', 'lo'):.4f}")
    print(f"crown_z     {norm_z('head', 'hi'):.4f}")
    print(f"hip_z       {norm_z('legs', 'hi'):.4f}")
    print(f"torso_lo_z  {norm_z('torso', 'lo'):.4f}")


main()
