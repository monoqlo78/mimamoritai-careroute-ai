"""Reshape Mimamo to match the reference poster proportions.

Targets were measured off the poster (height = crown->sole = 695 px):
    head_h 0.424 | head_w 0.547 | ear_span 0.659 | feet_span 0.492 | chin_z 0.576

The current model measured 0.507 / 0.588 / 0.867 / 0.392 / 0.524, so the head is
too tall, the ear pods stick out far too far and the stance is too narrow.

The edit is weight-blended off the existing vertex groups and mirrored onto the
armature edit bones so the rig keeps deforming correctly.

Run: blender -b <blend> -P scripts/reshape-mimamo.py -- <out.blend>
"""

import sys

import bpy
from mathutils import Vector

HEAD_CORE = ["head", "jaw", "eye_L", "eye_R", "eyebrow_L", "eyebrow_R",
             "antenna01", "antenna02"]
EARS = ["ear_L", "ear_R"]
LOWER = {"foot_L": 1.0, "foot_R": 1.0, "shin_L": 0.65, "shin_R": 0.65,
         "thigh_L": 0.25, "thigh_R": 0.25}

HEAD_SZ = 0.88      # vertical squash of the head assembly
HEAD_SXY = 0.98     # slight narrowing of the head
HEAD_LIFT = 0.1019  # extra body height, gained purely from longer legs
EAR_SHRINK = 0.85   # pod shrink about its own centre
LEG_NARROW = 0.78   # slim the thigh/shin band so the two legs read separately
EAR_SPAN_T = 0.659  # normalised target
FEET_SPAN_T = 0.492

# detail-pass gains, from the crown-aligned overlay residuals (poster px ratios)
EYE_SX, EYE_SZ = 84 / 78, 94 / 82        # eyes read a touch small
HEART_SX, HEART_SZ = 1.0, 1.0            # chest heart is sculpted into the torso
                                          # shell (pink gem nested in a MI_Mint-
                                          # outlined recess); sliding/scaling it
                                          # tears the gem out of its nest and
                                          # exposes the empty white recess as a
                                          # 2nd heart, so we leave it as designed
ANT_SX, ANT_SZ = 113 / 94, 93 / 54       # antenna heart is badly squashed
HEAD_SHELL_SX = 1.0                      # v13: shell x-scale REVERTED to no-op.
#   The 0.85 (=380/447) came from a width that could not be reproduced (the
#   reference white shell sits on a white background and cannot be segmented).
#   Aligned-space silhouette vs the confirmed landmarks shows the shell is NOT
#   measurably too wide: at the shell row y=470 v11 gives L374/R737 vs ref
#   372/752 (dL+2/dR-15) -- a near match -- while 0.85 pulled it to L400/R711
#   (dL+28/dR-41), i.e. too NARROW. It also left the teal visor at full width
#   (441->443px), producing an overhang. Per "do not sculpt on an unmeasurable
#   quantity", the shell scale is disabled until a valid target ratio exists.
EYE_DY = EYE_OUT = HEART_DY = 0.0        # solved from `height` at runtime


def group_weight(vert, index_map, names):
    if isinstance(names, dict):
        wanted = {index_map[n]: s for n, s in names.items() if n in index_map}
    else:
        wanted = {index_map[n]: 1.0 for n in names if n in index_map}
    total = sum(g.weight * wanted[g.group] for g in vert.groups if g.group in wanted)
    return min(1.0, max(0.0, total))


def lerp(a, b, t):
    return a + (b - a) * t


def main():
    out = sys.argv[sys.argv.index("--") + 1]
    obj = bpy.data.objects["Mimamo"]
    mesh = obj.data
    idx = {g.name: g.index for g in obj.vertex_groups}

    # per-vertex material tags, so the chest heart badge can be moved as one
    # unit (pink gem + mint rim) without dragging the white torso shell with it.
    mat_names = [m.name for m in mesh.materials]
    vmat = [set() for _ in mesh.vertices]
    for poly in mesh.polygons:
        mn = mat_names[poly.material_index]
        for vi in poly.vertices:
            vmat[vi].add(mn)
    BADGE_MATS = {"MI_PinkHeart", "MI_MintPale", "MI_PinkLining"}

    # Connected-component (island) map.  The chest badge is not a single
    # material: probing the mesh shows it is four concentric *separate* islands
    # stacked in depth -- pink gem (MI_PinkHeart), mint-pale rim (MI_MintPale),
    # white backing frame (a MI_WhiteShell island, NOT the torso shell) and a
    # mint backing plate (a MI_Mint island, NOT the scarf).  A material-name
    # gate moved only the pink gem and left the white frame stranded, so the
    # badge is selected geometrically by island centroid instead (see below).
    ncc = len(mesh.vertices)
    _par = list(range(ncc))

    def _find(a):
        while _par[a] != a:
            _par[a] = _par[_par[a]]
            a = _par[a]
        return a

    for e in mesh.edges:
        ra, rb = _find(e.vertices[0]), _find(e.vertices[1])
        if ra != rb:
            _par[ra] = rb
    vcomp = [_find(i) for i in range(ncc)]
    comp_members = {}
    for i, r in enumerate(vcomp):
        comp_members.setdefault(r, []).append(i)

    w_head = [group_weight(v, idx, HEAD_CORE) for v in mesh.vertices]
    w_ear = [group_weight(v, idx, EARS) for v in mesh.vertices]
    w_low = [group_weight(v, idx, LOWER) for v in mesh.vertices]
    w_up = [min(1.0, w_head[i] + w_ear[i]) for i in range(len(mesh.vertices))]

    zs = [v.co.z for v in mesh.vertices]
    sole = min(zs)
    chin = min(v.co.z for i, v in enumerate(mesh.vertices) if w_head[i] > 0.5)
    cx = sum(v.co.x for v in mesh.vertices) / len(mesh.vertices)

    # Lengthen the legs instead of stretching the whole body: only the band
    # between the top of the boots and the hip is scaled, so the torso, chest
    # heart and belt keep their authored shape and simply ride upward.
    foot_i = {idx[n] for n in ("foot_L", "foot_R") if n in idx}
    thigh_i = {idx[n] for n in ("thigh_L", "thigh_R") if n in idx}
    band_lo = max(v.co.z for v in mesh.vertices
                  if any(g.group in foot_i and g.weight > 0.5 for g in v.groups))
    band_hi = max(v.co.z for v in mesh.vertices
                  if any(g.group in thigh_i and g.weight > 0.5 for g in v.groups))
    stretch = 1.0 + HEAD_LIFT / (band_hi - band_lo)
    dz = HEAD_LIFT
    print(f"PIVOT sole {sole:.4f} chin {chin:.4f} cx {cx:.4f} lift {dz:.4f}")
    print(f"LEGBAND {band_lo:.4f}..{band_hi:.4f} stretch {stretch:.4f}")

    ear_pts = [v.co.x for i, v in enumerate(mesh.vertices) if w_ear[i] > 0.5]
    ear_ctr_r = sum(x for x in ear_pts if x > cx) / max(1, len([x for x in ear_pts if x > cx]))

    shin_i = {idx[n] for n in ("thigh_L", "thigh_R", "shin_L", "shin_R") if n in idx}
    w_shin = [min(1.0, sum(g.weight for g in v.groups if g.group in shin_i))
              for v in mesh.vertices]
    leg_pts = [v.co.x for i, v in enumerate(mesh.vertices) if w_shin[i] > 0.5]
    right = [x for x in leg_pts if x > cx] or [cx]
    leg_ctr_r = sum(right) / len(right)
    print(f"LEGCTR {leg_ctr_r:.4f}")

    def raise_z(z):
        if z <= band_lo:
            return z
        if z >= band_hi:
            return z + dz
        return band_lo + (z - band_lo) * stretch

    def warp(co, wh, we, wl, ws=0.0):
        p = Vector(co)
        up = min(1.0, wh + we)
        z_body = raise_z(p.z)
        z_head = chin + dz + (p.z - chin) * HEAD_SZ
        p.z = lerp(z_body, z_head, up)
        # lateral / depth
        s = lerp(1.0, HEAD_SXY, wh)
        p.x = cx + (p.x - cx) * s
        p.y = p.y * lerp(1.0, HEAD_SXY, wh)
        if we > 0.0:
            side = ear_ctr_r if p.x > cx else -ear_ctr_r + 2 * cx
            p.x = lerp(p.x, side + (p.x - side) * EAR_SHRINK, we)
        if ws > 0.0:
            side = leg_ctr_r if p.x > cx else -leg_ctr_r + 2 * cx
            p.x = lerp(p.x, side + (p.x - side) * LEG_NARROW, ws)
        if wl > 0.0:
            p.x = p.x + (1 if p.x >= cx else -1) * FEET_OFF * wl
        return p

    # first pass with no lateral offsets so spans can be solved exactly
    global FEET_OFF
    FEET_OFF = 0.0
    EAR_OFF = 0.0
    base = [warp(v.co, w_head[i], w_ear[i], w_low[i], w_shin[i])
            for i, v in enumerate(mesh.vertices)]
    height = max(p.z for p in base) - min(p.z for p in base)
    # crown excludes the antenna, so recompute using the head shell only
    ant = {idx[n] for n in ("antenna01", "antenna02") if n in idx}
    ant_lo = min((base[i].z for i, v in enumerate(mesh.vertices)
                  if any(g.group in ant and g.weight > 0.5 for g in v.groups)), default=1e9)
    crown = max(p.z for p in base if p.z < ant_lo)
    sole_n = min(p.z for p in base)
    height = crown - sole_n

    ex = [base[i].x for i in range(len(base)) if w_ear[i] > 0.5]
    fx = [base[i].x for i in range(len(base)) if w_low[i] > 0.9]
    ear_span = max(ex) - min(ex)
    feet_span = max(fx) - min(fx)
    EAR_OFF = (EAR_SPAN_T * height - ear_span) / 2.0
    feet_off = (FEET_SPAN_T * height - feet_span) / 2.0
    FEET_OFF = 0.0  # warp() stays offset-free; offsets are applied explicitly
    print(f"height {height:.4f} ear_span {ear_span:.4f}->{EAR_SPAN_T * height:.4f} "
          f"feet_span {feet_span:.4f}->{FEET_SPAN_T * height:.4f}")
    print(f"OFFSETS ear {EAR_OFF:.4f} feet {feet_off:.4f}")

    def offset(p, we, wl):
        if we > 0.0:
            p.x = p.x + (1 if p.x >= cx else -1) * EAR_OFF * we
        if wl > 0.0:
            p.x = p.x + (1 if p.x >= cx else -1) * feet_off * wl
        return p

    for i, v in enumerate(mesh.vertices):
        offset(base[i], w_ear[i], w_low[i])

    # ---- detail pass ----------------------------------------------------
    # Overlaying the render on the poster (aligned crown->sole) left the head
    # shell and boots on the mark but showed the internal features riding too
    # high and the two hearts undersized.  These deltas are the measured
    # residuals in poster pixels, converted with `u`.
    u = height / 696.0
    w_eye = [group_weight(v, idx, ["eye_L", "eye_R"]) for v in mesh.vertices]
    w_brow = [group_weight(v, idx, ["eyebrow_L", "eyebrow_R"]) for v in mesh.vertices]
    w_heart = [group_weight(v, idx, ["chest_heart"]) for v in mesh.vertices]
    w_ant = [group_weight(v, idx, ["antenna02"]) for v in mesh.vertices]

    eye_pts = [base[i] for i in range(len(base)) if w_eye[i] > 0.5]
    eye_rx = sum(abs(p.x - cx) for p in eye_pts) / len(eye_pts)
    eye_cz = sum(p.z for p in eye_pts) / len(eye_pts)
    heart_pts = [base[i] for i in range(len(base)) if w_heart[i] > 0.5]
    heart_cx = sum(p.x for p in heart_pts) / len(heart_pts)
    heart_cz = sum(p.z for p in heart_pts) / len(heart_pts)
    heart_cy = sum(p.y for p in heart_pts) / len(heart_pts)
    heart_rad = max(((p.x - heart_cx) ** 2 + (p.z - heart_cz) ** 2) ** 0.5
                    for p in heart_pts)
    ant_lo = min(base[i].z for i in range(len(base)) if w_ant[i] > 0.5)

    # Badge mask: select the four concentric badge islands geometrically.  A
    # vertex is part of the badge when its whole connected component is a small
    # island (<= BADGE_MAX_ISLAND verts) whose centroid sits right on the chest
    # heart (within BADGE_CTR_TOL in x/z).  This captures pink gem + mint rim +
    # white frame + mint backing and RIGIDLY moves them together, while the
    # torso shell, scarf mint (centroid above the heart) and belt trim (centroid
    # below) are excluded because their island centroids are far from the heart.
    BADGE_MAX_ISLAND = 520
    BADGE_CTR_TOL = 0.045
    comp_ctr = {}
    for r, mem in comp_members.items():
        if len(mem) > BADGE_MAX_ISLAND:
            continue
        cx_ = sum(base[i].x for i in mem) / len(mem)
        cz_ = sum(base[i].z for i in mem) / len(mem)
        comp_ctr[r] = (cx_, cz_)
    badge_comps = {r for r, (cxx, czz) in comp_ctr.items()
                   if ((cxx - heart_cx) ** 2 + (czz - heart_cz) ** 2) ** 0.5 <= BADGE_CTR_TOL}

    def badge_weight(i):
        return 1.0 if vcomp[i] in badge_comps else 0.0

    w_badge = [badge_weight(i) for i in range(len(mesh.vertices))]
    _bmats = {}
    for i in range(len(mesh.vertices)):
        if w_badge[i] > 0.5:
            for m in vmat[i]:
                _bmats[m] = _bmats.get(m, 0) + 1
    print(f"BADGE islands {sorted(badge_comps)} mats {_bmats}")
    print(f"DETAIL u {u:.5f} eye_rx {eye_rx:.4f} eye_cz {eye_cz:.4f} "
          f"heart {heart_cx:.4f},{heart_cz:.4f} rad {heart_rad:.4f} "
          f"badge_n {sum(1 for w in w_badge if w > 0.5)} ant_lo {ant_lo:.4f}")

    # ---- scarf tail shorten (task A) -----------------------------------
    # The scarf's long drape is one MI_Mint island hanging from the knot.  The
    # head lift raised the knot while HEART_DY lowered the badge, so the drape
    # was exposed as a vertical teal band down the white belly that the
    # reference poster does not have (there the tails are short and flare to the
    # sides, with the badge nesting just under the knot).  Select that island
    # geometrically -- a small centre-column mint island whose lower end reaches
    # well below the knot base -- and z-scale it about its own top so it
    # terminates just under the knot.  A material-name gate alone is unsafe
    # (the knot and both tails share MI_Mint), hence the geometric z_min gate.
    TIE_SZ = 0.45
    TIE_ZMIN_CUT = 0.60
    tie_comps = set()
    for r, mem in comp_members.items():
        n = len(mem)
        if not (400 <= n <= 900) or r in badge_comps:
            continue
        cxx = sum(base[i].x for i in mem) / n
        if abs(cxx - cx) > 0.15:
            continue
        zmin = min(base[i].z for i in mem)
        if zmin >= TIE_ZMIN_CUT:
            continue
        matc = {}
        for i in mem:
            for m in vmat[i]:
                matc[m] = matc.get(m, 0) + 1
        if not matc or max(matc, key=matc.get) != "MI_Mint":
            continue
        tie_comps.add(r)
    w_tie = [1.0 if vcomp[i] in tie_comps else 0.0 for i in range(len(mesh.vertices))]
    tie_top = max((base[i].z for i in range(len(base)) if w_tie[i] > 0.5),
                  default=0.0)
    tie_bot = min((base[i].z for i in range(len(base)) if w_tie[i] > 0.5),
                  default=0.0)
    print(f"TIE islands {sorted(tie_comps)} n {sum(1 for w in w_tie if w > 0.5)} "
          f"tie_top {tie_top:.4f} tie_bot {tie_bot:.4f} "
          f"new_bot {tie_top - (tie_top - tie_bot) * TIE_SZ:.4f}")

    # ---- head shell x-narrow (task C) ----------------------------------
    # The crown-aligned overlay measured the white helmet shell 17.6% wider than
    # the reference (447 vs 380 px) while the ear pods matched (453 vs 458), so a
    # uniform head scale would break the ears.  The head is not one island but a
    # nest of concentric material shells (MI_WhiteShell > MI_FaceRim > MI_White
    # Face) with the ear pods, eyes, brows, jaw and antenna as separate islands.
    # Narrow ONLY the central shell islands in x about the body centre; every
    # face feature and both ear pods keep their x, so the matching ear span and
    # the eye residuals are preserved.  Selection is per-island (not per-material)
    # because MI_FaceRim also skins the ear pods -- the |centroid_x| gate keeps
    # the ear FaceRim islands (xc ~ +/-0.43) out while admitting the central
    # head shells (xc ~ 0).
    SHELL_MATS = {"MI_WhiteShell", "MI_WhiteFace", "MI_FaceRim"}
    SHELL_XC_TOL = 0.10
    print("SHELL candidates (central shell-material islands):")
    for r, mem in comp_members.items():
        matc = {}
        for i in mem:
            for m in vmat[i]:
                matc[m] = matc.get(m, 0) + 1
        if not matc or max(matc, key=matc.get) not in SHELL_MATS:
            continue
        cxx = sum(base[i].x for i in mem) / len(mem)
        if abs(cxx - cx) > SHELL_XC_TOL:
            continue
        zc = sum(base[i].z for i in mem) / len(mem)
        zlo = min(base[i].z for i in mem)
        zhi = max(base[i].z for i in mem)
        hw = max(abs(base[i].x - cx) for i in mem)
        hfrac = sum(1 for i in mem if w_head[i] > 0.5) / len(mem)
        dom = max(matc, key=matc.get)
        print(f"  id{r} n{len(mem)} xc{cxx:+.3f} hw{hw:.3f} "
              f"z[{zlo:.3f},{zhi:.3f}] zc{zc:.3f} hf{hfrac:.2f} "
              f"badge{r in badge_comps} {dom}")
    shell_comps = set()
    for r, mem in comp_members.items():
        if r in badge_comps:
            continue
        matc = {}
        for i in mem:
            for m in vmat[i]:
                matc[m] = matc.get(m, 0) + 1
        if not matc or max(matc, key=matc.get) not in SHELL_MATS:
            continue
        cxx = sum(base[i].x for i in mem) / len(mem)
        if abs(cxx - cx) > SHELL_XC_TOL:
            continue
        zc = sum(base[i].z for i in mem) / len(mem)
        zlo = min(base[i].z for i in mem)
        # central helmet shells sit above the neck (zlo>0.60) with their centroid
        # in the helmet band (0.85<zc<1.15); this keeps torso/limb white islands
        # (lower zlo) and the antenna (zc~1.26) out without a bone-weight gate,
        # which wrongly drops the neck-skinned outer WhiteShell.
        if zlo < 0.60 or not (0.85 < zc < 1.15):
            continue
        shell_comps.add(r)
    w_shell = [1.0 if vcomp[i] in shell_comps else 0.0
               for i in range(len(mesh.vertices))]
    shell_x = [abs(base[i].x - cx) for i in range(len(base)) if w_shell[i] > 0.5]
    print(f"SHELL islands {sorted(shell_comps)} n {len(shell_x)} "
          f"half_w {max(shell_x) if shell_x else 0.0:.4f} "
          f"-> {(max(shell_x) if shell_x else 0.0) * HEAD_SHELL_SX:.4f} "
          f"(SX {HEAD_SHELL_SX:.4f})")


    def detail(p, we, wb, wht, wa, wt, ws=0.0):
        if we > 0.0:
            side = 1.0 if p.x >= cx else -1.0
            nx = cx + side * (eye_rx + (abs(p.x - cx) - eye_rx) * EYE_SX + EYE_OUT)
            nz = eye_cz + (p.z - eye_cz) * EYE_SZ - EYE_DY
            p.x, p.z = lerp(p.x, nx, we), lerp(p.z, nz, we)
        if wb > 0.0:
            p.z = p.z - EYE_DY * wb
        if wht > 0.0:
            nx = heart_cx + (p.x - heart_cx) * HEART_SX
            nz = heart_cz + (p.z - heart_cz) * HEART_SZ - HEART_DY
            p.x, p.z = lerp(p.x, nx, wht), lerp(p.z, nz, wht)
        if wa > 0.0:
            nx = cx + (p.x - cx) * ANT_SX
            nz = ant_lo + (p.z - ant_lo) * ANT_SZ
            p.x, p.z = lerp(p.x, nx, wa), lerp(p.z, nz, wa)
        if wt > 0.0:
            nz = tie_top - (tie_top - p.z) * TIE_SZ
            p.z = lerp(p.z, nz, wt)
        if ws > 0.0:
            p.x = lerp(p.x, cx + (p.x - cx) * HEAD_SHELL_SX, ws)
        return p

    global EYE_DY, EYE_OUT, HEART_DY
    EYE_DY, EYE_OUT, HEART_DY = 34 * u, 7 * u, 63 * u   # chest badge rides 63px high

    for i, v in enumerate(mesh.vertices):
        v.co = detail(base[i], w_eye[i], w_brow[i], w_badge[i], w_ant[i],
                      w_tie[i], w_shell[i])
    mesh.update()

    # shape keys carry their own coordinates and take priority over mesh.vertices
    keys = mesh.shape_keys
    if keys:
        for block in keys.key_blocks:
            for i, pt in enumerate(block.data):
                p = offset(warp(pt.co, w_head[i], w_ear[i], w_low[i], w_shin[i]),
                           w_ear[i], w_low[i])
                pt.co = detail(p, w_eye[i], w_brow[i], w_badge[i], w_ant[i],
                               w_tie[i], w_shell[i])
        print(f"SHAPEKEYS warped {[b.name for b in keys.key_blocks]}")

    # mirror the same warp onto the rig so animation still lines up
    rig = bpy.data.objects["MimamoRig"]
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    for bone in rig.data.edit_bones:
        name = bone.name
        wh = 1.0 if name in HEAD_CORE else 0.0
        we = 1.0 if name in EARS else 0.0
        wl = LOWER.get(name, 0.0)
        ws = 1.0 if name in ("thigh_L", "thigh_R", "shin_L", "shin_R") else 0.0
        d = (1.0 if name in ("eye_L", "eye_R") else 0.0,
             1.0 if name in ("eyebrow_L", "eyebrow_R") else 0.0,
             1.0 if name == "chest_heart" else 0.0,
             1.0 if name == "antenna02" else 0.0,
             0.0)
        for attr in ("head", "tail"):
            p = offset(warp(getattr(bone, attr), wh, we, wl, ws), we, wl)
            setattr(bone, attr, detail(p, *d))
    bpy.ops.object.mode_set(mode="OBJECT")

    bpy.ops.wm.save_as_mainfile(filepath=out)
    print(f"SAVED {out}")


main()
