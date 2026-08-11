"""Mimamo v2 -- body, limbs, cape and props, rebuilt from scratch.

Every number below was read off annotated reference grids (g_body.png,
g_upper.png) in poster pixels and converted with the shared mapping
X(xr) / Z(yr) / L(px).  Nothing is carried over from the v1 geometry.

Reference reads (poster px)
  scarf collar      x 437..617   y 707..750     bow  x 513..617  y 723..787
  torso             x 433..657   y 750..1003
  chest heart plate x 493..627   y 773..893     pink x 513..610  y 790..878
  belt              y 867..913   buckles x 430..487 and x 617..660
  legs split        y 923        leg half-width 52, centres dx +/-62
  boot ankle band   y 998..1027
  boots             y 1000..1103  half-width 66, toe forward
  L hand (waving)   x 243..364   y 654..757     watch centre (323, 757)
  phone             x 670..860   y 645..915, tilted ~10 deg
  cape L wing tip   (220, 998)   R wing tip (750, 1023)
"""

import math

import bpy
from mathutils import Vector

import opus_lib

from opus_lib import (
    X, Z, L, link, mesh_from, subsurf, shade, bevel, quad_sphere,
    ellipse_outline, heart_outline, rounded_rect_outline, inflate_outline,
    limb, capsule, set_vertex_colors, add_modifier, ring_on,
)

CX = 555.0


# --------------------------------------------------------------------------- #
def loft(name, rings, mat, coll, cap_top=True, cap_bot=True, seg=48,
         smooth=True):
    """Loft a stack of horizontal super-ellipse rings.

    rings: list of (yr, cx_px, cy_world, hw_px, hd_world, n)
        yr    reference row          -> z
        cx_px reference column       -> x centre
        cy    world y centre (depth offset, -Y is forward)
        hw    half width in ref px
        hd    half depth in world units
        n     super-ellipse exponent (2 = ellipse, 4 = squarish)
    """
    verts, faces = [], []
    seg = max(12, int(round(seg * opus_lib.DENSITY)))
    layers = []
    for (yr, cxp, cy, hw, hd, n) in rings:
        z = Z(yr)
        a = L(hw)
        idx = []
        for i in range(seg):
            th = 2.0 * math.pi * i / seg
            ct, st = math.cos(th), math.sin(th)
            sx = math.copysign(abs(ct) ** (2.0 / n), ct)
            sy = math.copysign(abs(st) ** (2.0 / n), st)
            idx.append(len(verts))
            verts.append((X(cxp) + a * sx, cy + hd * sy, z))
        layers.append(idx)
    for r in range(len(layers) - 1):
        a, b = layers[r], layers[r + 1]
        for i in range(seg):
            j = (i + 1) % seg
            faces.append([a[i], a[j], b[j], b[i]])
    if cap_bot:
        c = len(verts)
        yr, cxp, cy, hw, hd, n = rings[0]
        verts.append((X(cxp), cy, Z(yr)))
        for i in range(seg):
            faces.append([layers[0][(i + 1) % seg], layers[0][i], c])
    if cap_top:
        c = len(verts)
        yr, cxp, cy, hw, hd, n = rings[-1]
        verts.append((X(cxp), cy, Z(yr)))
        for i in range(seg):
            faces.append([layers[-1][i], layers[-1][(i + 1) % seg], c])
    ob = mesh_from(name, verts, faces, mat, smooth=smooth, coll=coll)
    return ob


def plate(name, outline, mat, coll, y_back, depth, rings=9, power=0.55):
    """Pillow decal that sits proud of a body surface (front is -Y)."""
    ob = inflate_outline(name, outline, depth, rings=rings, power=power,
                         mat=mat, coll=coll, flat_back=True)
    ob.location = (0.0, y_back, 0.0)
    return ob


def sphere(name, mat, coll, cxp, yr, hw, hd, hh, cy=0.0):
    ob = quad_sphere(name, radii=(L(hw), hd, L(hh)), n=20, mat=mat, coll=coll)
    ob.location = (X(cxp), cy, Z(yr))
    return ob


# --------------------------------------------------------------------------- #
# torso: shoulders y765, chest widest y850, waist tuck y890, hips y950
TORSO_RINGS = [
    (742.0, CX, 0.0,  58.0, 0.130, 2.4),
    (756.0, CX, 0.0,  86.0, 0.178, 2.5),
    (772.0, CX, 0.0, 101.0, 0.201, 2.6),
    (792.0, CX, 0.0, 109.0, 0.215, 2.7),
    (820.0, CX, 0.0, 113.0, 0.222, 2.8),
    (852.0, CX, 0.0, 113.0, 0.222, 2.8),
    (884.0, CX, 0.0, 108.0, 0.213, 2.7),
    (910.0, CX, 0.0, 110.0, 0.216, 2.6),
    (936.0, CX, 0.0, 114.0, 0.222, 2.5),
    (956.0, CX - 8.0, 0.0, 102.0, 0.199, 2.4),
    (972.0, CX - 12.0, 0.0, 82.0, 0.162, 2.3),
]


def build_torso(M, coll):
    ob = loft("Torso", TORSO_RINGS, M["white_shell"], coll)
    subsurf(ob, 1, 2)
    shade(ob)
    return [ob]


def build_neck(M, coll):
    rings = [
        (700.0, CX, 0.0, 54.0, 0.118, 2.4),
        (726.0, CX, 0.0, 58.0, 0.126, 2.4),
        (748.0, CX, 0.0, 64.0, 0.138, 2.4),
    ]
    ob = loft("Neck", rings, M["white_shell"], coll)
    subsurf(ob, 1, 2)
    shade(ob)
    return [ob]


# --------------------------------------------------------------------------- #
def build_chest_heart(M, coll):
    """Layered heart shield: mint outer shell, white plate, pale rim, pink heart.

    The torso half-depth around y820..890 is 0.222, so every layer has to sit
    forward of -0.222 or it disappears inside the body (iteration 1 bug).
    """
    from opus2_head import fit_px
    objs = []

    sh_o = fit_px(heart_outline(1.0, 1.0, 160, plump=0.22),
                  488.0, 632.0, 768.0, 899.0)
    shl = plate("ChestShield", sh_o, M["mint"], coll, -0.206, 0.040)
    subsurf(shl, 1, 2)
    shade(shl)
    objs.append(shl)

    plate_o = fit_px(heart_outline(1.0, 1.0, 160, plump=0.25),
                     499.0, 621.0, 777.0, 890.0)
    pl = plate("ChestPlate", plate_o, M["white_shell"], coll, -0.238, 0.030)
    subsurf(pl, 1, 2)
    shade(pl)
    objs.append(pl)

    rim_o = fit_px(heart_outline(1.0, 1.0, 160, plump=0.30),
                   506.0, 614.0, 784.0, 883.0)
    rim = plate("ChestRim", rim_o, M["mint_pale"], coll, -0.262, 0.018)
    subsurf(rim, 1, 2)
    shade(rim)
    objs.append(rim)

    ph_o = fit_px(heart_outline(1.0, 1.0, 160, plump=0.44),
                  513.0, 610.0, 790.0, 878.0)
    ph = plate("ChestHeart", ph_o, M["pink_heart"], coll, -0.276, 0.048,
               rings=13, power=0.62)
    cz, ch = Z(834.0), L(88.0)

    def col(w, l):
        v = (w.z - (cz - ch * 0.30)) / (ch * 0.8)
        k = 0.86 + 0.30 * max(0.0, min(1.0, v))
        spec = max(0.0, 1.0 - (((w.x + L(22.0)) / L(24.0)) ** 2 +
                               ((w.z - (cz + ch * 0.22)) / L(17.0)) ** 2))
        k = min(1.55, k + spec * 0.55)
        return (k, k, k, 1.0)

    set_vertex_colors(ph, col)
    subsurf(ph, 1, 2)
    shade(ph)
    objs.append(ph)
    return objs


# --------------------------------------------------------------------------- #
def build_belt(M, coll):
    """Mint band around the waist with two white buckle plates."""
    objs = []
    rings = []
    for yr, hw, hd in ((868.0, 110.5, 0.217), (877.0, 113.5, 0.223),
                       (901.0, 114.0, 0.224), (911.0, 110.0, 0.216)):
        rings.append((yr, CX, 0.0, hw, hd, 2.7))
    band = loft("Belt", rings, M["mint"], coll, cap_top=False, cap_bot=False)
    add_modifier(band, "SOLIDIFY", thickness=0.012, offset=1.0)
    subsurf(band, 1, 2)
    shade(band)
    objs.append(band)

    for sx, tag in ((1.0, "L"), (-1.0, "R")):
        o = rounded_rect_outline(L(56.0), L(48.0), L(14.0), 10,
                                 sx * L(90.0), Z(889.0))
        b = plate("Buckle_" + tag, o, M["white_shell"], coll, -0.234, 0.030)
        subsurf(b, 1, 2)
        shade(b)
        objs.append(b)

        o2 = rounded_rect_outline(L(34.0), L(28.0), L(9.0), 10,
                                  sx * L(90.0), Z(889.0))
        b2 = plate("BuckleIn_" + tag, o2, M["mint_pale"], coll, -0.262, 0.014)
        subsurf(b2, 1, 2)
        shade(b2)
        objs.append(b2)
    return objs


# --------------------------------------------------------------------------- #
def build_leg(M, coll, sx):
    tag = "L" if sx > 0 else "R"
    dx = sx * 62.0 - 22.0
    rings = [
        (906.0, CX + dx, 0.0, 50.0, 0.112, 2.4),
        (936.0, CX + dx, 0.0, 49.0, 0.110, 2.4),
        (966.0, CX + dx, 0.0, 47.0, 0.104, 2.4),
        (992.0, CX + dx, 0.0, 46.0, 0.100, 2.4),
        (1006.0, CX + dx, 0.0, 46.0, 0.098, 2.4),
    ]
    ob = loft("Leg_" + tag, rings, M["white_shell"], coll)
    subsurf(ob, 1, 2)
    shade(ob)
    return [ob]


def build_boot(M, coll, sx):
    """Chunky shoe: heel under the leg, toe swept forward and slightly out."""
    tag = "L" if sx > 0 else "R"
    dx = sx * 62.0 - 25.0
    objs = []

    # mint ankle band
    band_rings = [
        (988.0, CX + dx, 0.0, 62.0, 0.122, 2.4),
        (998.0, CX + dx, 0.0, 66.0, 0.130, 2.4),
        (1014.0, CX + dx, 0.0, 66.0, 0.130, 2.4),
        (1022.0, CX + dx, 0.0, 62.0, 0.122, 2.4),
    ]
    band = loft("BootBand_" + tag, band_rings, M["mint"], coll,
                cap_top=False, cap_bot=False)
    add_modifier(band, "SOLIDIFY", thickness=0.011, offset=1.0)
    subsurf(band, 1, 2)
    shade(band)
    objs.append(band)

    # shoe volume - each ring drifts forward (-Y) and outward as it descends
    shoe = []
    for (yr, hw, hd, fwd, out, slide) in (
        (1006.0, 56.0, 0.112, 0.000, 4.0, 0.0),
        (1026.0, 70.0, 0.140, 0.020, 16.0, -5.0),
        (1048.0, 82.0, 0.166, 0.046, 30.0, -12.0),
        (1070.0, 89.0, 0.181, 0.070, 41.0, -18.0),
        (1088.0, 88.0, 0.180, 0.080, 46.0, -22.0),
        (1100.0, 78.0, 0.162, 0.082, 46.0, -23.0),
    ):
        shoe.append((yr, CX + dx + sx * out + slide, -fwd, hw, hd, 2.6))
    sh = loft("Boot_" + tag, shoe, M["white_shell"], coll)
    subsurf(sh, 1, 2)
    shade(sh)
    objs.append(sh)

    sole = []
    for (yr, hw, hd, fwd, out, slide) in (
        (1086.0, 84.0, 0.174, 0.078, 44.0, -21.0),
        (1098.0, 85.0, 0.176, 0.082, 46.0, -23.0),
        (1106.0, 76.0, 0.158, 0.078, 44.0, -22.0),
    ):
        sole.append((yr, CX + dx + sx * out + slide, -fwd, hw, hd, 2.6))
    so = loft("BootSole_" + tag, sole, M["mint"], coll)
    subsurf(so, 1, 2)
    shade(so)
    objs.append(so)

    # small white buckle plate on the mint ankle strap
    bo = rounded_rect_outline(L(34.0), L(30.0), L(9.0), 10,
                              X(CX + dx + sx * 6.0), Z(1013.0))
    bk = plate("BootBuckle_" + tag, bo, M["white_shell"], coll, -0.118, 0.024)
    subsurf(bk, 1, 2)
    shade(bk)
    objs.append(bk)
    return objs


# --------------------------------------------------------------------------- #
SCARF_HW = 118.0

# Shallow wrap behind the band -- gives the kerchief volume from a 3/4 view
# without ever breaking the front silhouette (every ring is checked to sit
# inside scarf_outline() at its own row).
SCARF_RINGS = [
    # yr,    cx,  cy,     hw,   hd,    n
    (730.0, CX, -0.012,  66.0, 0.176, 2.4),
    (744.0, CX, -0.008,  86.0, 0.192, 2.5),
    (758.0, CX,  0.000,  98.0, 0.200, 2.5),
]


def scarf_outline(n=72):
    """Silhouette of the tied kerchief, in world coords.

    Measured off the poster with the saturated-mint mask (b-r, g-r > 0.09),
    per column, as offsets dx from the band centre (x 544):

        dx     0    12    24    36    48    60    72    84    96   108
        top  727   726   726   725   724   722   720   717   714   707
        bot  778   778   778   774   772   771   773   765   765   757

    So it is a band of roughly constant 50 px depth whose whole centreline
    sweeps UP toward the tips -- a cloth kerchief knotted at the throat.

    This has to be an explicit outline rather than a loft: a stack of
    horizontal rings can only ever produce dead-straight top and bottom
    edges, and that flat lower edge is exactly what read as a shelf lying
    across the chest in the previous two attempts.
    """
    def top(u):
        # iter-17: the rendered plate silhouette sits ~4 px above its outline
        # (the inflate rings push the boundary out), and measuring the poster's
        # hard mint edge under the chin gives y 726 against y 722 here.
        return 731.0 - 20.0 * (abs(u) ** 2.4)

    def bot(u):
        b = 778.0 - 21.0 * (u * u)
        # square-cut tips look like cardboard; taper the outer 15 %
        k = max(0.0, (abs(u) - 0.85) / 0.15)
        return b + (top(u) - b) * (min(1.0, k) ** 1.4) * 0.88

    up, dn = [], []
    for i in range(n + 1):
        u = -1.0 + 2.0 * i / n
        x = X(CX + u * SCARF_HW)
        up.append((x, Z(top(u))))
        dn.append((x, Z(bot(u))))
    return up + dn[::-1]


def build_scarf(M, coll):
    """Mint kerchief knotted at the throat.

    Iterations 14 and 15 both built this as a loft of horizontal rings, which
    renders with a razor-straight lower edge -- a shelf across the chest --
    plus a knot ball floating above it and two ribbon eggs floating below it,
    disconnected from everything.  Measuring the poster column by column shows
    there are no hanging ribbons at all down there: the mint below the band is
    the chest badge's own bezel.  So the band is now a single plate cut to the
    measured outline, with the knot as the only extra lump.
    """
    objs = []
    band = plate("ScarfCollar", scarf_outline(), M["mint"], coll,
                 -0.208, 0.052, rings=9, power=0.58)
    subsurf(band, 1, 2)
    shade(band)
    objs.append(band)

    wrap = loft("ScarfCollarWrap", SCARF_RINGS, M["mint"], coll, seg=56)
    subsurf(wrap, 1, 2)
    shade(wrap)
    objs.append(wrap)

    knot = sphere("ScarfKnot", M["mint"], coll, 538.0, 738.0, 27.0, 0.050,
                  21.0, cy=-0.252)
    subsurf(knot, 1, 2)
    shade(knot)
    objs.append(knot)
    return objs


# --------------------------------------------------------------------------- #
def build_arm(M, coll, sx, joints, r0, r1):
    tag = "L" if sx > 0 else "R"
    objs = []
    sh, el, wr = joints
    up = limb("UpperArm_" + tag, sh, el, r0, r1 * 1.06, M["white_shell"],
              coll, seg=26, cap_scale=1.0)
    lo = limb("ForeArm_" + tag, el, wr, r1 * 1.02, r1 * 0.90,
              M["white_shell"], coll, seg=26, cap_scale=1.0)
    for o in (up, lo):
        subsurf(o, 1, 2)
        shade(o)
    objs += [up, lo]

    # mint sleeve ring at the elbow
    e = Vector(el)
    d = (Vector(wr) - Vector(sh)).normalized()
    cuff = quad_sphere("Cuff_" + tag, radii=(r1 * 1.20, r1 * 1.20, r1 * 0.42),
                       n=18, mat=M["mint"], coll=coll)
    cuff.location = e
    ang = math.atan2(d.x, d.z)
    cuff.rotation_euler = (math.atan2(math.hypot(d.x, d.y), d.z), 0.0, -ang)
    subsurf(cuff, 1, 2)
    shade(cuff)
    objs.append(cuff)

    # mint ring where the arm meets the torso
    ring = ring_on("Shoulder_" + tag, sh, el, 0.10, r0 * 1.10, r0 * 0.52,
                   M["mint"], coll, seg=36)
    subsurf(ring, 1, 2)
    shade(ring)
    objs.append(ring)
    return objs


def build_hand(M, coll, sx, wrist, aim, spread, scale=1.0):
    """Open mitten hand: palm ellipsoid + four fingers + thumb."""
    tag = "L" if sx > 0 else "R"
    objs = []
    w = Vector(wrist)
    a = Vector(aim).normalized()
    side = Vector((a.z, 0.0, -a.x)).normalized()
    palm_c = w + a * (0.100 * scale)
    palm = quad_sphere("Palm_" + tag,
                       radii=(0.070 * scale, 0.050 * scale, 0.074 * scale),
                       n=18, mat=M["white_shell"], coll=coll)
    palm.location = palm_c
    subsurf(palm, 1, 2)
    shade(palm)
    objs.append(palm)

    for k in range(4):
        t = (k - 1.5) / 1.5
        d = (a + side * (t * spread)).normalized()
        ln = (0.070 - 0.009 * abs(t)) * scale
        base = palm_c + side * (t * 0.034 * scale) + a * (0.024 * scale)
        f = limb("Finger%s%d" % (tag, k), base, base + d * ln,
                 0.030 * scale, 0.026 * scale, M["white_shell"], coll, seg=14)
        subsurf(f, 1, 2)
        shade(f)
        objs.append(f)

    td = (a * 0.30 - side * 1.0).normalized()
    tb = palm_c - side * (0.048 * scale) - a * (0.012 * scale)
    th = limb("Thumb" + tag, tb, tb + td * (0.062 * scale),
              0.032 * scale, 0.026 * scale, M["white_shell"], coll, seg=14)
    subsurf(th, 1, 2)
    shade(th)
    objs.append(th)
    return objs


def build_watch(M, coll, wrist, aim):
    """Wearable on the waving wrist: ref box ~66 x 74 px centred (322, 766)."""
    objs = []
    w = Vector(wrist)
    a = Vector(aim).normalized()
    c = w + a * 0.020

    strap = quad_sphere("WatchStrap", radii=(0.090, 0.058, 0.064), n=18,
                        mat=M["mint"], coll=coll)
    strap.location = (X(324.0), c.y, Z(768.0))
    subsurf(strap, 1, 2)
    shade(strap)
    objs.append(strap)

    cuff = quad_sphere("WatchCuff", radii=(0.074, 0.056, 0.030), n=16,
                       mat=M["white_shell"], coll=coll)
    cuff.location = (X(330.0), c.y + 0.004, Z(806.0))
    subsurf(cuff, 1, 2)
    shade(cuff)
    objs.append(cuff)

    body_o = rounded_rect_outline(L(62.0), L(70.0), L(19.0), 12,
                                  X(322.0), Z(766.0))
    body = plate("WatchBody", body_o, M["dark_metal"], coll, -0.214, 0.026)
    subsurf(body, 1, 2)
    shade(body)
    objs.append(body)

    face_o = rounded_rect_outline(L(47.0), L(53.0), L(14.0), 12,
                                  X(322.0), Z(766.0))
    fc = plate("WatchFace", face_o, M["screen_teal"], coll, -0.238, 0.007)
    shade(fc)
    objs.append(fc)

    hb = fit_heart(302.0, 342.0, 750.0, 780.0)
    hrt = plate("WatchGlyph", hb, M["white_shell"], coll, -0.245, 0.005)
    shade(hrt)
    objs.append(hrt)
    return objs


def fit_heart(x0, x1, y0, y1, plump=0.34):
    from opus2_head import fit_px
    return fit_px(heart_outline(1.0, 1.0, 96, plump=plump), x0, x1, y0, y1)


# --------------------------------------------------------------------------- #
def build_phone(M, coll):
    """Rounded slab held in the right hand.  Ref body ~166 x 240 px, tilt 10 deg
    clockwise on screen, centred (749, 776)."""
    objs = []
    cx, cz = X(749.0), Z(776.0)
    cy = -0.322
    rot = math.radians(10.0)

    def place(ob, fwd, dz=0.0, dx=0.0):
        ob.location = (cx - fwd * math.cos(rot) + dx, cy - fwd,
                       cz + fwd * math.sin(rot) + dz)
        ob.rotation_euler = (0.0, rot, 0.0)

    body_o = rounded_rect_outline(L(166.0), L(240.0), L(24.0), 14, 0.0, 0.0)
    body = inflate_outline("Phone", body_o, 0.026, rings=7, power=0.40,
                           mat=M["dark_metal"], coll=coll)
    place(body, 0.0)
    bevel(body, 0.005, 3)
    shade(body)
    objs.append(body)

    scr_o = rounded_rect_outline(L(150.0), L(224.0), L(18.0), 14, 0.0, 0.0)
    scr = inflate_outline("PhoneScreen", scr_o, 0.005, rings=5, power=0.40,
                          mat=M["screen_teal"], coll=coll, flat_back=True)
    place(scr, 0.030)
    shade(scr)
    objs.append(scr)

    card_o = rounded_rect_outline(L(124.0), L(176.0), L(14.0), 14, 0.0, 0.0)
    card = inflate_outline("PhoneCard", card_o, 0.004, rings=5, power=0.40,
                           mat=M["screen"], coll=coll, flat_back=True)
    place(card, 0.036, dz=L(6.0))
    shade(card)
    objs.append(card)

    badge_o = ellipse_outline(L(29.0), L(29.0), 72, 0.0, 0.0)
    bd = inflate_outline("PhoneBadge", badge_o, 0.006, rings=6, power=0.50,
                         mat=M["screen_teal"], coll=coll, flat_back=True)
    place(bd, 0.042, dz=L(52.0))
    shade(bd)
    objs.append(bd)

    tick = [(L(v[0]), L(v[1])) for v in (
        (-16, 1), (-6, -9), (16, 15), (12, 20), (-6, -1), (-12, 8))]
    tk = inflate_outline("PhoneTick", tick, 0.004, rings=4, power=0.5,
                         mat=M["white_shell"], coll=coll, flat_back=True)
    place(tk, 0.048, dz=L(52.0))
    shade(tk)
    objs.append(tk)

    # headline (dark) and sub-line (teal), matching the poster's two text rows
    for i, (wpx, hpx, ypx, mat) in enumerate((
            (86.0, 15.0, -6.0, "dark_metal"),
            (96.0, 11.0, -34.0, "screen_teal"))):
        bo = rounded_rect_outline(L(wpx), L(hpx), L(hpx * 0.45), 6, 0.0,
                                  L(ypx))
        bar = inflate_outline("PhoneText%d" % i, bo, 0.003, rings=4, power=0.5,
                              mat=M[mat], coll=coll, flat_back=True)
        place(bar, 0.042)
        shade(bar)
        objs.append(bar)

    hb = [(p[0] * L(24.0), p[1] * L(24.0)) for p in
          heart_outline(1.0, 1.0, 72, plump=0.40)]
    hp = inflate_outline("PhoneHeart", hb, 0.004, rings=5, power=0.5,
                         mat=M["pink"], coll=coll, flat_back=True)
    place(hp, 0.042, dz=-L(72.0))
    shade(hp)
    objs.append(hp)
    return objs


def build_grip(M, coll, scale=1.0):
    """Fingers of the right hand wrapping the phone's near edge."""
    objs = []
    for k, zr in enumerate((812.0, 832.0, 852.0, 870.0)):
        base = Vector((X(852.0), -0.262, Z(zr - 6.0)))
        tip = Vector((X(790.0 + 5.0 * k), -0.368, Z(zr)))
        f = limb("FingerR%d" % k, base, tip, 0.026 * scale, 0.021 * scale,
                 M["white_shell"], coll, seg=14)
        subsurf(f, 1, 2)
        shade(f)
        objs.append(f)
    th = limb("ThumbR", Vector((X(842.0), -0.244, Z(792.0))),
              Vector((X(806.0), -0.348, Z(786.0))),
              0.027 * scale, 0.022 * scale, M["white_shell"], coll, seg=14)
    subsurf(th, 1, 2)
    shade(th)
    objs.append(th)
    return objs


# --------------------------------------------------------------------------- #
CAPE_HW0 = 132.0
CAPE_HW1 = 210.0


def cape_surface(u, v):
    """u 0(collar)..1(hem), v -1..1 across.  +Y is behind the character."""
    av = abs(v)
    # round the outer edge and fillet the bottom corner so the wing reads as
    # draped cloth rather than a flat spike
    taper = 1.0 - 0.30 * (max(0.0, av - 0.52) / 0.48) ** 2.0
    fillet = 1.0 - 0.24 * (max(0.0, u - 0.66) / 0.34) ** 2.2
    # the reference cape is asymmetric: the pink-lined wing (screen-left, v<0)
    # sweeps much wider than the mint wing on the right.  Up at the collar the
    # poster is very nearly symmetric, so ease the asymmetry in with u.
    side = 1.45 if v < 0.0 else 0.93
    side = 1.0 + (side - 1.0) * (u ** 0.65)
    hw = (CAPE_HW0 + 214.0 * (u ** 0.62)) * taper * fillet * side
    hem = 1062.0 - 140.0 * ((1.0 - av) ** 1.50) - 30.0 * math.exp(-(((av - 0.52) / 0.24) ** 2))
    # the poster shows the wings climbing past the jaw (mint beside the head as
    # high as y ~690) instead of starting on a flat line level with the collar
    y0 = 706.0 - 24.0 * (av ** 1.35)
    yr = y0 + (hem - y0) * (u ** 0.92)
    xr = CX + v * hw
    depth = 0.150 + 0.300 * (u ** 1.10)
    depth -= 0.155 * (av ** 1.9) * (u ** 0.85)
    depth += 0.050 * math.sin(v * 4.1) * (u ** 1.4)
    # soft forward fold on the character's right (screen-left) wing
    depth -= 0.135 * math.exp(-(((v + 0.62) / 0.30) ** 2)) * (u ** 1.4)
    return Vector((X(xr), depth, Z(yr)))


def build_cape(M, coll, nu=30, nv=42, thick=0.020):
    """Two-sided shell.  Mint everywhere except the pink lining panel that the
    reference shows on the character's right wing (screen-left)."""
    grid = [[cape_surface(i / nu, -1.0 + 2.0 * j / nv)
             for j in range(nv + 1)] for i in range(nu + 1)]

    def nrm(i, j):
        i0, i1 = max(0, i - 1), min(nu, i + 1)
        j0, j1 = max(0, j - 1), min(nv, j + 1)
        n = (grid[i1][j] - grid[i0][j]).cross(grid[i][j1] - grid[i][j0])
        if n.length < 1e-9:
            return Vector((0.0, 1.0, 0.0))
        return n.normalized()

    normals = [[nrm(i, j) for j in range(nv + 1)] for i in range(nu + 1)]
    stride = nv + 1
    nvert = (nu + 1) * stride
    verts = []
    for sgn in (1.0, -1.0):
        for i in range(nu + 1):
            for j in range(nv + 1):
                verts.append(grid[i][j] + normals[i][j] * (sgn * thick * 0.5))

    def lining(i, j):
        u = (i + 0.5) / nu
        v = -1.0 + 2.0 * (j + 0.5) / nv
        return u > 0.26 and -0.99 < v < -0.24

    faces, midx = [], []
    for i in range(nu):
        for j in range(nv):
            a = i * stride + j
            b, c, d = a + 1, a + stride, a + stride + 1
            lin = 1 if lining(i, j) else 0
            faces.append([a, c, d, b])
            midx.append(lin)
            faces.append([nvert + a, nvert + b, nvert + d, nvert + c])
            midx.append(lin)
    for i in range(nu):          # side rims
        for (j, fwd) in ((0, False), (nv, True)):
            a, c = i * stride + j, (i + 1) * stride + j
            q = [a, c, nvert + c, nvert + a]
            faces.append(q if fwd else q[::-1])
            midx.append(1 if (j == 0 and (i + 0.5) / nu > 0.26) else 0)
    for j in range(nv):          # hem rim
        a, b = nu * stride + j, nu * stride + j + 1
        faces.append([a, b, nvert + b, nvert + a])
        midx.append(1 if lining(nu - 1, j) else 0)

    ob = mesh_from("Cape", verts, faces, M["mint"], coll=coll)
    ob.data.materials.append(M["pink_lining"])
    for p, m in zip(ob.data.polygons, midx):
        p.material_index = m
    subsurf(ob, 1, 2)
    shade(ob)
    return [ob]


# --------------------------------------------------------------------------- #
# joint positions (world) read off the poster
L_SH = (X(452.0), -0.030, Z(778.0))
L_EL = (X(392.0), -0.115, Z(820.0))
L_WR = (X(336.0), -0.150, Z(772.0))
L_AIM = (-0.72, -0.20, 0.66)

R_SH = (X(658.0), -0.030, Z(782.0))
R_EL = (X(724.0), -0.150, Z(848.0))
R_WR = (X(818.0), -0.250, Z(856.0))
R_AIM = (0.30, -0.90, 0.32)


def build_body(M, coll):
    objs = []
    objs += build_neck(M, coll)
    objs += build_torso(M, coll)
    objs += build_chest_heart(M, coll)
    objs += build_belt(M, coll)
    for sx in (1.0, -1.0):
        objs += build_leg(M, coll, sx)
        objs += build_boot(M, coll, sx)
    objs += build_scarf(M, coll)

    objs += build_arm(M, coll, 1.0, (L_SH, L_EL, L_WR), 0.088, 0.062)
    objs += build_hand(M, coll, 1.0, L_WR, L_AIM, 0.55, scale=1.55)
    objs += build_watch(M, coll, L_WR, L_AIM)

    objs += build_arm(M, coll, -1.0, (R_SH, R_EL, R_WR), 0.088, 0.062)
    palm = quad_sphere("Palm_R", radii=(0.058, 0.044, 0.062), n=18,
                       mat=M["white_shell"], coll=coll)
    palm.location = (X(838.0), -0.276, Z(846.0))
    subsurf(palm, 1, 2)
    shade(palm)
    objs.append(palm)

    objs += build_phone(M, coll)
    objs += build_grip(M, coll)
    objs += build_cape(M, coll)
    return objs
