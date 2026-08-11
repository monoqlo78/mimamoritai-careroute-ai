"""Head / face construction for the Mimamo opus rebuild (iteration 2).

Every landmark below is measured off the canonical reference poster and mapped
with  X(xr) = (xr-555)*0.0025 ,  Z(yr) = (1100-yr)*0.0025 .
"""
import math

import bpy
from mathutils import Euler, Vector

from opus_lib import (
    L,
    X,
    Z,
    add_modifier,
    bevel,
    ellipse_outline,
    head_half_width,
    heart_outline,
    inflate_outline,
    link,
    mesh_from,
    project_to_ellipsoid,
    project_to_head,
    quad_sphere,
    revolve,
    rounded_rect_outline,
    set_vertex_colors,
    smile_outline,
    subsurf,
    surface_band,
)

# ---- reference-derived head constants ------------------------------------- #
HEAD_C = (0.0, 0.0, 1.355)          # top 1.805 = ref y378, chin 0.905 = ref y738
HEAD_R = (0.5125, 0.470, 0.450)     # +-0.5125 = ref x 350 / 760

EYE_X = 0.22900      # lash-ring centre, ref x 466 / 644
EYE_Z = 1.22440      # lash-ring centre, ref y 601.5
EYE_A = 0.09450      # lash-ring half width
EYE_B = 0.10700      # lash-ring half height

BROW_X = 0.2740
BROW_Z = 1.4550     # ref y 522
BROW_HW = 0.0870

MOUTH_Z = 1.0688    # ref y 677.5 centre
MOUTH_HW = 0.07700  # ref x 524..585 = 61 px wide

BLUSH_X = 0.2925
BLUSH_Z = 1.1163

FH_Z = 1.6400       # forehead pink heart centre, ref y 444
CAP_Z0 = 1.5450  # crest lower edge at centre     # mint cap lower edge at the centre line (ref y 482)
CAP_Z1 = 1.4500  # crest tips     # mint cap lower edge at the silhouette (ref y 442)

EAR_X = 0.5560
EAR_Z = 1.2900
ANT_HEART_Z = 2.0250


def uv_grid(name, w, h, nx, ny, mat, coll, cx=0.0, cz=0.0, ellipse=False):
    verts, faces = [], []
    grid = []
    for j in range(ny + 1):
        row = []
        for i in range(nx + 1):
            u = i / nx * 2 - 1
            v = j / ny * 2 - 1
            if ellipse:
                r = math.hypot(u, v)
                if r > 1e-6:
                    k = min(1.0, 1.0 / max(abs(u), abs(v))) * min(1.0, 1.0 / r) * r
                    u, v = u * k / max(r, 1e-6), v * k / max(r, 1e-6)
            row.append(len(verts))
            verts.append((cx + u * w * 0.5, 0.0, cz + v * h * 0.5))
        grid.append(row)
    for j in range(ny):
        for i in range(nx):
            faces.append([grid[j][i], grid[j][i + 1], grid[j + 1][i + 1], grid[j + 1][i]])
    ob = mesh_from(name, verts, faces, mat, coll=coll)
    me = ob.data
    uvl = me.uv_layers.new(name="UVMap")
    for poly in me.polygons:
        for li in poly.loop_indices:
            vi = me.loops[li].vertex_index
            p = me.vertices[vi].co
            uvl.data[li].uv = ((p.x - cx) / w + 0.5, (p.z - cz) / h + 0.5)
    return ob


CREST_HX = 0.4620          # visor arc half-span (ref x 396..714 core, sweep to ears)
CREST_T = 0.0088           # HALF thickness of the thin mint visor stroke


def crest_mid(x):
    """Centre-line of the thin mint visor arc, measured from the reference:
    ref (396,455) (420,437) (456,427) (528,419) centre ~y413, mirrored."""
    ax = min(abs(x), CREST_HX)
    return (1.7020 - 0.30 * ax ** 2 - 1.55 * ax ** 4
            - 2.2 * max(0.0, ax - 0.36) ** 2)


def crest_low(x):
    return crest_mid(x) - CREST_T


def crest_up(x):
    return crest_mid(x) + CREST_T


def cap_edge_z(x):
    """Lower edge of the mint helmet crest, as a function of world x."""
    return crest_low(x)


def build_head(M, coll, tex):
    """Returns dict of named objects (each already in `coll`)."""
    out = {}
    cx, cy, cz = HEAD_C
    rx, ry, rz = HEAD_R

    def decal(ob, extra):
        # +DECAL_BIAS: Catmull-Clark shrinks the small decal shells more than the
        # big head shell, so without a global bias the head pokes through the
        # face plate near the forward pole (a bright gloss blob mid-face).
        return project_to_head(ob, head, HEAD_C, HEAD_R, extra=extra + 0.0075)

    # ---------------- helmet shell ---------------- #
    head = quad_sphere("HeadShell", radii=HEAD_R, loc=HEAD_C, n=22, power=2.18,
                       mat=M["white"], coll=coll)
    me = head.data
    for v in me.vertices:
        t = (v.co.z / rz)                      # -1 bottom .. +1 top
        k = 1.0 - 0.085 * max(0.0, -t) ** 1.35 - 0.030 * max(0.0, t) ** 2.6
        v.co.x *= k
        v.co.y *= k * (1.0 - 0.04 * max(0.0, -t))
        if v.co.y < 0:
            v.co.y *= 1.0 + 0.045 * max(0.0, t)
    me.update()
    subsurf(head, 2, 3)
    out["HeadShell"] = head

    # ---------------- face plate (large raised oval, ref x378..733 y478..706) -- #
    face = inflate_outline(
        "FacePlate",
        ellipse_outline(0.4438, 0.2850, 96, cx=0.0, cz=1.2700, squash_bot=1.05),
        depth=0.009, rings=8, power=0.42, mat=M["white_face"], coll=coll, flat_back=True,
    )
    decal(face, 0.0022)
    subsurf(face, 2, 2)
    out["FacePlate"] = face

    # thin soft rim around the face plate (reference shows a delicate outline)
    fr_out = ellipse_outline(0.4530, 0.2912, 96, cx=0.0, cz=1.2700, squash_bot=1.05)
    fr_in = ellipse_outline(0.4438, 0.2850, 96, cx=0.0, cz=1.2700, squash_bot=1.05)
    frim = inflate_outline("FacePlateRim", fr_out + list(reversed(fr_in)),
                           depth=0.006, rings=5, power=0.45, mat=M["face_rim"],
                           coll=coll, flat_back=True)
    decal(frim, 0.0012)
    subsurf(frim, 2, 2)
    out["FacePlateRim"] = frim

    # ---------------- thin mint visor arc (reference draws a ~5px stroke) ---- #
    NS = 120
    xs = [-CREST_HX + 2 * CREST_HX * i / NS for i in range(NS + 1)]

    def taper(x):
        return max(0.0, 1.0 - (min(abs(x), CREST_HX) / CREST_HX) ** 7) ** 0.5

    cr_pts = [(x, crest_mid(x) - CREST_T * taper(x)) for x in xs]
    cr_pts += [(x, crest_mid(x) + CREST_T * taper(x)) for x in reversed(xs)]
    cap = inflate_outline("HelmetCrest", cr_pts, depth=0.010, rings=4, power=0.55,
                          mat=M["mint"], coll=coll, flat_back=True)
    decal(cap, 0.0050)
    subsurf(cap, 2, 2)
    out["HelmetCrest"] = cap

    # ---------------- forehead heart badge (mint frame / cream / pink) ------ #
    def shield_pts(w, h, cz):
        # rounded diamond / shield, as drawn on the reference forehead
        hw, hh = w / 2.0, h / 2.0
        base = [(0.0, hh), (hw * 0.72, hh * 0.46), (hw, -hh * 0.05),
                (hw * 0.60, -hh * 0.72), (0.0, -hh),
                (-hw * 0.60, -hh * 0.72), (-hw, -hh * 0.05), (-hw * 0.72, hh * 0.46)]
        pts = []
        for i in range(len(base)):
            ax, az = base[i]
            bx, bz = base[(i + 1) % len(base)]
            for j in range(5):
                t = j / 5.0
                pts.append((ax + (bx - ax) * t, az + (bz - az) * t))
        for _ in range(3):
            nxt = []
            for i in range(len(pts)):
                ax, az = pts[i]
                bx, bz = pts[(i + 1) % len(pts)]
                nxt.append((ax * 0.75 + bx * 0.25, az * 0.75 + bz * 0.25))
                nxt.append((ax * 0.25 + bx * 0.75, az * 0.25 + bz * 0.75))
            pts = nxt
        return [(p[0], cz + p[1]) for p in pts]

    fh_stack = [
        ("ForeheadFrame", 0.3560, 0.2480, 1.6180, M["mint"], 0.0300, 0.014, "shield"),
        ("ForeheadRing", 0.3060, 0.2130, 1.6215, M["white_face"], 0.0385, 0.013, "shield"),
        ("ForeheadHeart", 0.2020, 0.1740, FH_Z, M["pink_vc"], 0.0455, 0.017, "heart"),
    ]
    for nm, w, h, zc, mat, ex, dep, kind in fh_stack:
        pts = (shield_pts(w, h, zc) if kind == "shield"
               else heart_outline(w, h, 128, cz=zc, plump=0.55))
        o = inflate_outline(nm, pts, depth=dep, rings=8,
                            power=0.60, mat=mat, coll=coll, flat_back=True)
        decal(o, ex)
        subsurf(o, 2, 2)
        out[nm] = o
    fz0, fz1 = FH_Z - 0.065, FH_Z + 0.065

    def fh_grad(world, local):
        t = min(1.0, max(0.0, (world.z - fz0) / (fz1 - fz0)))
        return (1.0, 0.365 + 0.42 * t, 0.400 + 0.38 * t, 1.0)

    set_vertex_colors(out["ForeheadHeart"], fh_grad)

    # ---------------- eyes ---------------- #
    def eye_stack(sx, tag):
        objs = {}
        px = sx * EYE_X
        inn = -sx      # +1 => toward the face centre line

        def eye_e(a, b, n=96, cxx=0.0, czz=0.0):
            return ellipse_outline(a, b, n, cx=px + cxx, cz=EYE_Z + czz,
                                   squash_bot=0.985)

        # dark lash: a thick arc over the top + outer corner flick (NOT a full ring)
        def arc_pt(theta, rr_a, rr_b=None):
            if rr_b is None:
                rr_b = rr_a
            return (px + sx * math.cos(theta) * EYE_A * rr_a,
                    EYE_Z + math.sin(theta) * EYE_B * rr_b)

        # Reference scanline profile (poster y 566..642) shows: a 3-8 px hairline
        # lash on the OUTER side, a 9-15 px WHITE sclera crescent just inside it,
        # a bold lash over the TOP, and a hairline under the eye.  The iris is
        # offset toward the INNER side, which is what creates that crescent.
        # th = 0 is OUTER, pi/2 TOP, pi INNER, 3pi/2 BOTTOM.
        NR = 160
        rim_pts = []

        def lash_w(th):
            return (0.045
                    + 0.330 * max(0.0, math.sin(th)) ** 0.85
                    + 0.020 * max(0.0, math.cos(th)) ** 2.00)

        for i in range(NR + 1):                       # outer edge
            th = 2.0 * math.pi * i / NR
            w = lash_w(th)
            rim_pts.append(arc_pt(th, 1.020 + w, 1.020 + w))
        for i in range(NR, -1, -1):                   # inner edge (eyeball border)
            th = 2.0 * math.pi * i / NR
            rim_pts.append(arc_pt(th, 1.012, 1.012))
        rim = inflate_outline(f"Eye{tag}_Rim", rim_pts, depth=0.013, rings=6, power=0.5,
                              mat=M["eye_rim"], coll=coll, flat_back=True)
        decal(rim, 0.0040)
        subsurf(rim, 2, 2)
        objs[rim.name] = rim

        sclera = inflate_outline(f"Eye{tag}_White", eye_e(EYE_A * 1.020, EYE_B * 1.018),
                                 depth=0.011, rings=7, power=0.5, mat=M["eye_white"],
                                 coll=coll, flat_back=True)
        decal(sclera, 0.0055)
        subsurf(sclera, 2, 2)
        objs[sclera.name] = sclera

        IR_A, IR_B = 0.08800, 0.10180
        IR_X = inn * 0.02110
        IR_Z = -0.00460
        iris = inflate_outline(f"Eye{tag}_Iris",
                               ellipse_outline(IR_A, IR_B, 96, cx=px + IR_X,
                                               cz=EYE_Z + IR_Z, squash_bot=0.985),
                               depth=0.014, rings=10, power=0.60, mat=M["iris"],
                               coll=coll, flat_back=True)
        decal(iris, 0.0072)
        subsurf(iris, 2, 2)
        icx = px + IR_X
        z0 = EYE_Z + IR_Z - IR_B
        z1 = EYE_Z + IR_Z + IR_B
        # measured: near-black over the top ~55%, emerald across the bottom third
        top = Vector((0.0000, 0.0130, 0.0210))
        mid = Vector((0.0000, 0.1050, 0.1180))
        bot = Vector((0.0450, 0.7400, 0.6200))
        glow = Vector((0.4200, 1.0000, 0.9000))

        def grad(world, local):
            t = min(1.0, max(0.0, (world.z - z0) / (z1 - z0)))
            if t < 0.34:
                c = bot.lerp(mid, (t / 0.34) ** 0.90)
            elif t < 0.60:
                c = mid.lerp(top, ((t - 0.34) / 0.26) ** 0.70)
            else:
                c = top
            rx = (world.x - icx) / IR_A
            rz = (world.z - EYE_Z - IR_Z) / IR_B
            # soft light pool low-outer (the poster's single diffuse bounce)
            d = math.hypot((rx + inn * 0.40) / 0.52, (rz + 0.50) / 0.40)
            k = max(0.0, 1.0 - d) ** 1.1
            if k > 0.0:
                c = c.lerp(glow, min(1.0, 0.55 * k))
            r = min(1.0, math.hypot(rx, rz))
            k = max(0.0, (r - 0.88) / 0.12) ** 1.20
            c = c.lerp(Vector((0.000, 0.012, 0.018)), 0.70 * k)
            return (c.x, c.y, c.z, 1.0)

        set_vertex_colors(iris, grad)
        objs[iris.name] = iris

        pupil = inflate_outline(f"Eye{tag}_Pupil",
                                ellipse_outline(0.03730, 0.04600, 96, cx=px + IR_X,
                                                cz=EYE_Z + IR_Z + 0.0086,
                                                squash_bot=0.985),
                                depth=0.012, rings=8, power=0.55, mat=M["pupil"],
                                coll=coll, flat_back=True)
        decal(pupil, 0.0150)
        subsurf(pupil, 2, 2)
        objs[pupil.name] = pupil

        # highlight measured off the reference: ref (488, 588) ~17 x 25 px
        specs = [
            ("Hi1", inn * 0.06010, 0.06400, 0.02340, 0.03340, M["hilite"]),
        ]
        for nm, ox, oz, a, b, mat in specs:
            h = inflate_outline(f"Eye{tag}_{nm}",
                                ellipse_outline(a, b, 44, cx=px + ox, cz=EYE_Z + oz),
                                depth=0.011, rings=6, power=0.55, mat=mat,
                                coll=coll, flat_back=True)
            decal(h, 0.0205)
            subsurf(h, 2, 2)
            objs[h.name] = h

        # NOTE: a dome "lens" shell veiled the eye grey in Cycles (shadow + specular
        # haze), so the gloss is expressed through the painted highlights instead.
        return objs

    for sx, tag in ((1, "L"), (-1, "R")):
        out.update(eye_stack(sx, tag))

    # ---------------- brows ---------------- #
    for sx, tag in ((1, "L"), (-1, "R")):
        pts = []
        n = 30
        for i in range(n + 1):
            t = -1 + 2 * i / n
            pts.append((sx * BROW_X + t * BROW_HW,
                        BROW_Z + 0.0330 * (1 - t * t) ** 0.58 + 0.0165 * t * sx))
        for i in range(n, -1, -1):
            t = -1 + 2 * i / n
            th = 0.0086 + 0.0128 * max(0.0, -t * sx) ** 0.75
            pts.append((sx * BROW_X + t * BROW_HW,
                        BROW_Z + 0.0330 * (1 - t * t) ** 0.58 + 0.0165 * t * sx - th))
        b = inflate_outline(f"Brow_{tag}", pts, depth=0.011, rings=5, power=0.5,
                            mat=M["brow"], coll=coll, flat_back=True)
        decal(b, 0.0042)
        subsurf(b, 2, 2)
        out[b.name] = b

    # ---------------- nose ---------------- #
    nose = inflate_outline("Nose", ellipse_outline(0.0118, 0.0110, 36, cz=1.1600),
                           depth=0.013, rings=7, power=0.6, mat=M["white_face"],
                           coll=coll, flat_back=True)
    decal(nose, 0.0034)
    subsurf(nose, 2, 2)
    out["Nose"] = nose

    # ---------------- mouth (open smile, ref y 663..691) ---------------- #
    def mouth_outline(hw, corner, top_dip, bottom, n=72):
        pts = []
        for i in range(n + 1):
            t = -1 + 2 * i / n
            pts.append((t * hw, corner * t * t + top_dip * (1 - t * t)))
        for i in range(n, -1, -1):
            t = -1 + 2 * i / n
            pts.append((t * hw, corner * t * t - bottom * (1 - t * t) ** 0.55))
        return pts

    m_o = [(x, MOUTH_Z + z) for (x, z) in
           mouth_outline(MOUTH_HW, 0.0275, 0.0100, 0.0350)]
    cavity = inflate_outline("MouthCavity", m_o, depth=0.026, rings=8, power=0.75,
                             mat=M["mouth"], coll=coll, flat_back=False, shrink=0.10)
    for v in cavity.data.vertices:
        v.co.y = abs(v.co.y) * 0.80
    cavity.data.update()
    decal(cavity, -0.0055)
    subsurf(cavity, 2, 2)
    out["MouthCavity"] = cavity

    t_o = [(x, MOUTH_Z + z - 0.0135) for (x, z) in
           mouth_outline(MOUTH_HW * 0.74, 0.0060, 0.0125, 0.0215)]
    tongue = inflate_outline("Tongue", t_o, depth=0.016, rings=7, power=0.62,
                             mat=M["tongue"], coll=coll, flat_back=True)
    decal(tongue, -0.0020)
    subsurf(tongue, 2, 2)
    out["Tongue"] = tongue

    teeth_pts = []
    _n = 48
    for i in range(_n + 1):
        t = -0.80 + 1.60 * i / _n
        teeth_pts.append((t * MOUTH_HW, MOUTH_Z + 0.0275 * t * t + 0.0100 * (1 - t * t)))
    for i in range(_n, -1, -1):
        t = -0.80 + 1.60 * i / _n
        teeth_pts.append((t * MOUTH_HW,
                          MOUTH_Z + 0.0275 * t * t + 0.0100 * (1 - t * t)
                          - 0.0060 - 0.0070 * (1 - t * t)))
    teeth = inflate_outline("Teeth", teeth_pts, depth=0.010, rings=5, power=0.5,
                            mat=M["white_face"], coll=coll, flat_back=True)
    decal(teeth, 0.0008)
    subsurf(teeth, 2, 2)
    out["Teeth"] = teeth

    lip_o = [(x, MOUTH_Z + z) for (x, z) in
             mouth_outline(MOUTH_HW * 1.085, 0.0300, 0.0128, 0.0400)]
    lip = inflate_outline("MouthRim", lip_o + list(reversed(m_o)), depth=0.009,
                          rings=5, power=0.5, mat=M["mouth_rim"], coll=coll,
                          flat_back=True)
    decal(lip, 0.0030)
    subsurf(lip, 1, 2)
    out["MouthRim"] = lip

    # ---------------- blush ---------------- #
    for sx, tag in ((1, "L"), (-1, "R")):
        bl = inflate_outline(
            f"Blush_{tag}",
            ellipse_outline(0.0975, 0.0670, 64, cx=sx * BLUSH_X, cz=BLUSH_Z),
            depth=0.0030, rings=10, power=0.38, mat=M["blush"], coll=coll,
            flat_back=True)

        def blush_col(wp, lp, _sx=sx):
            u = (lp.x - _sx * BLUSH_X) / 0.0975
            w = (lp.z - BLUSH_Z) / 0.0670
            d = min(1.0, math.sqrt(u * u + w * w))
            t = (1.0 - d) ** 1.30
            return (1.0, 1.0 - 0.42 * t, 1.0 - 0.34 * t, 1.0)

        set_vertex_colors(bl, blush_col)
        decal(bl, 0.0026)
        out[bl.name] = bl

    # ---------------- side mint bands (headset arc) ---------------- #
    for sx, tag in ((1, "L"), (-1, "R")):
        band = surface_band(
            f"SideBand_{tag}", HEAD_C, HEAD_R,
            theta_range=(sx * math.radians(70), sx * math.radians(154)),
            phi0_fn=lambda t: math.radians(44),
            phi_fn=lambda t: math.radians(100),
            nu=44, nv=10, offset=0.004, thickness=0.017, mat=M["mint"], coll=coll)
        out[band.name] = band

    # ---------------- ear pods (headset) ---------------- #
    for sx, tag in ((1, "L"), (-1, "R")):
        pod = quad_sphere(f"EarPod_{tag}", radii=(0.0640, 0.0960, 0.1060),
                          loc=(sx * EAR_X, 0.0, EAR_Z), n=16, power=2.45,
                          mat=M["mint_dark"], coll=coll)
        subsurf(pod, 2, 3)
        out[pod.name] = pod

        # reference ear pod: white outer ring around a paler mint centre
        disc = revolve(f"EarDisc_{tag}",
                       [(0.086, 0.0), (0.108, 0.0), (0.114, 0.013), (0.108, 0.026),
                        (0.088, 0.028)],
                       56, M["mint"], coll=coll)
        disc.rotation_euler = Euler((0, math.radians(90 * sx), 0), "XYZ")
        disc.location = (sx * (EAR_X + 0.022), 0.0, EAR_Z)
        out[disc.name] = disc

        gem = quad_sphere(f"EarGem_{tag}", radii=(0.034, 0.088, 0.088),
                          loc=(sx * (EAR_X + 0.040), 0.0, EAR_Z), n=12, power=2.1,
                          mat=M["mint_dark"], coll=coll)
        subsurf(gem, 2, 2)
        out[gem.name] = gem

    # ---------------- antenna ---------------- #
    stalk = revolve("AntennaStalk",
                    [(0.030, 0.0), (0.0245, 0.020), (0.0175, 0.048), (0.0155, 0.078), (0.0165, 0.092)],
                    32, M["mint"], loc=(0.0, 0.0, 1.7880), coll=coll)
    subsurf(stalk, 2, 2)
    out["AntennaStalk"] = stalk

    ball = quad_sphere("AntennaJoint", radii=(0.026, 0.026, 0.026),
                       loc=(0.0, 0.0, 1.8830), n=8, mat=M["mint_dark"], coll=coll)
    subsurf(ball, 2, 2)
    out["AntennaJoint"] = ball

    ah = inflate_outline("AntennaHeart",
                         heart_outline(0.2860, 0.2620, 160, cx=0.0250, cz=ANT_HEART_Z),
                         depth=0.070, rings=10, power=0.55, mat=M["pink_vc"], coll=coll)
    subsurf(ah, 1, 2)
    z0, z1 = ANT_HEART_Z - 0.1310, ANT_HEART_Z + 0.1310

    def hgrad(world, local):
        t = min(1.0, max(0.0, (world.z - z0) / (z1 - z0)))
        return (1.0, 0.330 + 0.40 * t, 0.370 + 0.36 * t, 1.0)

    set_vertex_colors(ah, hgrad)
    out["AntennaHeart"] = ah

    hi = inflate_outline("AntennaHeartHi",
                         ellipse_outline(0.034, 0.026, 32,
                                         cx=-0.0370, cz=ANT_HEART_Z + 0.0510),
                         depth=0.008, rings=6, power=0.5, mat=M["hilite"], coll=coll,
                         flat_back=True)
    for v in hi.data.vertices:
        v.co.y -= 0.0640
    hi.data.update()
    subsurf(hi, 2, 2)
    out["AntennaHeartHi"] = hi

    # ---------------- neck ---------------- #
    neck = revolve("Neck",
                   [(0.0, 0.0), (0.135, 0.0), (0.128, 0.045), (0.132, 0.090), (0.0, 0.100)],
                   40, M["mint_dark"], loc=(0.0, 0.0, 0.8150), coll=coll)
    subsurf(neck, 2, 2)
    out["Neck"] = neck
    return out
