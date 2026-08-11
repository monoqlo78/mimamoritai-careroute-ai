"""Mimamo v2 - head + face, rebuilt from scratch off fresh reference reads.

Every constant below was re-measured on the canonical poster (1122x1402) with a
20/10 px annotated grid.  Nothing is inherited from the v1 head.

Poster pixel -> world:  x = (xr-555)*0.0025 ,  z = (1100-yr)*0.0025
"""
import math
import os

import bpy
from mathutils import Euler, Vector

import opus_lib
from opus_lib import (X, Z, L, ellipse_outline, heart_outline, inflate_outline,
                      link, mesh_from, project_to_head, quad_sphere,
                      set_vertex_colors, shade, subsurf)

# --------------------------------------------------------------------------- #
# measured reference landmarks
# --------------------------------------------------------------------------- #
# head silhouette (re-measured from the annotated grid g_tiara3.png):
#   x 347..762, y 378..735, centre (554.5, 556.5).
#   Half-width read directly off the grid: 120.5 @ y400, 143.5 @ y420,
#   184.5 @ y460  ->  n_top = 2.30 (the earlier 2.80 was far too boxy and was
#   the single biggest silhouette error in the 50/50 overlay).


def _tune(name, default):
    """Silhouette tuning knob, overridable from the environment for A/B renders."""
    try:
        return float(os.environ[name])
    except (KeyError, ValueError):
        return default


def _tune_hw():
    # The poster's helmet is wider than the 207.5 first read off the grid: at
    # 207.5 the rim between the face plate and the outer edge came out too thin,
    # which is what made the cheeks read as bulging.
    return _tune("MIMAMO_HEAD_HW", 215.0)


HEAD_CZ = Z(556.5)          # 1.35875
HEAD_HW = L(_tune_hw())     # 0.51875 at the measured 207.5
HEAD_HH = L(178.5)          # 0.44625
HEAD_HD = 0.4550            # depth (not measurable from a front view)
HEAD_N_TOP = 2.30           # superellipse exponent, crown


# The lower half is the cheek/jaw curve.  n=3.0 was far too boxy: it held the
# head at almost full width all the way down to the chin, so the face plate was
# pushed out into a rounded rectangle and the cheeks read as fat next to the
# poster's egg-shaped taper.  2.30 matches the poster in a 50/50 overlay.
HEAD_N_BOT = _tune("MIMAMO_HEAD_N_BOT", 2.30)

# face plate: superellipse n=3.0, x 372..748, y 445..715, centre (560, 580)
FACE_CZ = Z(586.0)          # measured white oval centre  y 470..705
FACE_HW = L(_tune("MIMAMO_FACE_HW", 158.0))   # measured x 393..718 at the widest
FACE_HH = L(122.0)
FACE_N = _tune("MIMAMO_FACE_N", 1.95)  # near-true ellipse: the poster face is NOT a squircle

# eyes - re-measured 1:1 against ref_poster.png with the exact projection
# mapping (px = 2*xr - 350, py = 2*yr - 460), screen-left eye, reference px:
#
#   black rim, OUTER edge   x 424..508  y 555..649  -> c(466.0, 602.0) half(42.5, 47.5)
#   pearl socket (the hole) x 429..506  y 571..649  -> c(467.0, 610.0) half(38.5, 38.5)
#   teal lens               x 445..506  y 570..648  -> c(475.5, 609.0) half(31.0, 39.5)
#
# The decisive finding: the socket is a CIRCLE that is dropped 8 px BELOW the
# rim ellipse centre.  That single offset is what makes the black read 17 px at
# the lash line, 5 px on the outer side, 3 px on the inner side and only 1 px
# under the lens - a crescent open at the bottom.  The previous version used
# two near-concentric ellipses of similar aspect, which can only ever produce
# the even black donut the reference never has.
EYE_X = L(94.0)             # 555 -/+ 94 = 461 ; rim centre is 6 px inward
EYE_CZ = Z(602.0)           # rim ellipse centre
EYE_A = L(42.5)
EYE_B = L(47.5)
SOC_R = L(38.5)             # socket is round ...
SOC_DZ = L(8.0)             # ... and sunk 8 px below the rim centre
IRIS_A = L(31.0)
IRIS_B = L(39.5)
IRIS_DX = L(14.5)           # lens centre, inward of EYE_X
IRIS_CZ = Z(609.0)

# brows - re-measured: ref L x451..488 y517..529 c(469.5, 523.0),
#                      ref R x635..672 y526..539 c(653.5, 532.5).
# Mean centre x offset 92, mean apex centre-line y 524.  The reference brow is
# a *comma*: the apex sits well toward the INNER end (u ~ 0.72) and the stroke
# droops 8 px at the outer tip and 2.5 px at the inner tip, staying ~7 px thick
# through the middle and only blunting at the very ends.
BROW_X = L(92.0)
BROW_Z = Z(524.0)           # apex centre-line; the droop table subtracts from it
BROW_HW = L(19.0)           # ref arc is 38 px long
BROW_T = L(7.0)             # max stroke thickness
# droop below the apex, u = 0 outer .. 1 inner (reference px)
BROW_V = [(0.00, 8.0), (0.12, 5.0), (0.25, 3.2), (0.38, 1.6), (0.51, 0.7),
          (0.64, 0.2), (0.76, 0.0), (0.88, 0.7), (1.00, 2.5)]

# mouth - row-by-row extents off the poster.  The opening's outer silhouette is
# x 527..581 (w54), so MOUTH_HW / MOUTH_TOP were already right; the "narrow top"
# the first pass saw was a *specular sheen* on the inner upper lip, not geometry.
# Corners: left (527, 664), right (581, 667).  Dark cavity slivers survive down
# to y 684 beside the tongue, and the tongue itself runs to y 690.
MOUTH_HW = L(27.0)
MOUTH_TOP = Z(663.0)
MOUTH_COR = Z(665.5)
MOUTH_BOT = Z(690.0)        # bowl closes where the tongue ends
# tongue: ref runs y675..690, widest 534..570 -> c(552, 682.5) half(18.5, 8)
TONGUE_A = L(18.5)
TONGUE_B = L(8.0)
TONGUE_Z = Z(682.5)

# nose - from the brightness profile down x 548..566: the reference highlight
# peaks at y 634.5 and its shadow bottoms at y 647.5, so the bump is centred at
# y ~641 and is only ~14 px tall.  The CG had it at y ~655 (14 px too low, right
# on the upper lip) and twice as deep, which read as a white ball.
# Second pass: the measured CG highlight still came out c(560.5, 646) 22x33 px
# against the reference's c(560.5, 638) 6x9 px, so raise 8 px and shrink hard.
NOSE_Z = Z(633.0)

# blush: sits directly under each eye, centre dx 100, y 665, 56 x 26 px
BLUSH_X = L(100.0)
BLUSH_Z = Z(647.0)

# forehead badge: pink heart x505..608, y415..478
BADGE_W = L(103.0)
BADGE_BOT = Z(478.0)

# mint crest heart band (re-measured, g_crest2.png):
#   outer heart  full-w 340,  bottom tip y 516, lobe top y 384
#   inner heart  full-w 272,  bottom tip y 496, lobe top y 418
CREST_O_W = L(340.0)
CREST_O_BOT = Z(516.0)
CREST_O_TOP = Z(384.0)
CREST_I_W = L(272.0)
CREST_I_BOT = Z(496.0)
CREST_I_TOP = Z(418.0)

# glass helmet dome rim - a thin bright ring inset ~14 px from the silhouette
DOME_INSET = L(14.0)

# ear pods, re-measured 1:1 against the poster on the aligned window:
#   the cup only just clears the shell - outer silhouette edge sits 219 px from
#   the head centre and the whole visible cup is 58 x 135 px.  The previous
#   245/55 pushed the collar out to 305 px, which made the head 19 % too wide.
EAR_X = L(200.0)
EAR_Z = Z(563.0)
EAR_HW = L(30.0)
EAR_HH = L(66.0)

# antenna heart: x500..610, y250..345
ANT_W = L(110.0)
ANT_BOT = Z(345.0)

BIAS = 0.0035               # global forward clearance for every face decal


# --------------------------------------------------------------------------- #
# small shape helpers (new)
# --------------------------------------------------------------------------- #
def super_r(t, n):
    """Half-width factor of a superellipse at normalised height t in [-1,1]."""
    t = max(-1.0, min(1.0, t))
    return max(0.0, (1.0 - abs(t) ** n)) ** (1.0 / n)


def super_outline(a, b, n_top, n_bot, npts=192, cx=0.0, cz=0.0):
    """Closed outline of a superellipse with separate top / bottom exponents."""
    pts = []
    half = npts // 2
    for i in range(half + 1):          # right side, top -> bottom
        t = 1.0 - 2.0 * i / half
        n = n_top if t >= 0 else n_bot
        pts.append((cx + super_r(t, n) * a, cz + t * b))
    for i in range(1, half):           # left side, bottom -> top
        t = -1.0 + 2.0 * i / half
        n = n_top if t >= 0 else n_bot
        pts.append((cx - super_r(t, n) * a, cz + t * b))
    return pts


def piping(name, outer, inner, depth, mat, coll, layers=5):
    """Rounded raised band between two matching outlines (a piped trim)."""
    n = len(outer)
    verts, faces = [], []
    rows = []
    for li in range(layers):
        s = li / (layers - 1)
        y = -depth * math.sin(math.pi * s) ** 0.75
        row = []
        for i in range(n):
            ox, oz = outer[i]
            ix, iz = inner[i]
            row.append(len(verts))
            verts.append((ox + (ix - ox) * s, y, oz + (iz - oz) * s))
        rows.append(row)
    for li in range(layers - 1):
        a, b = rows[li], rows[li + 1]
        for i in range(n):
            j = (i + 1) % n
            faces.append([a[i], a[j], b[j], b[i]])
    # flat back cap
    back = []
    for i in range(n):
        ox, oz = outer[i]
        back.append(len(verts))
        verts.append((ox, 0.0, oz))
    back2 = []
    for i in range(n):
        ix, iz = inner[i]
        back2.append(len(verts))
        verts.append((ix, 0.0, iz))
    for i in range(n):
        j = (i + 1) % n
        faces.append([back[j], back[i], back2[i], back2[j]])
    for i in range(n):
        j = (i + 1) % n
        faces.append([rows[0][i], rows[0][j], back[j], back[i]])
        faces.append([back2[i], back2[j], rows[-1][j], rows[-1][i]])
    return mesh_from(name, verts, faces, mat, coll=coll)


def scaled(outline, fx, fz, cx, cz):
    return [(cx + (p[0] - cx) * fx, cz + (p[1] - cz) * fz) for p in outline]


def fit_px(outline, xr0, xr1, yr0, yr1):
    """Rescale/translate an outline so its bbox exactly fills a REFERENCE
    pixel rectangle.  Removes all guesswork about heart_outline's odd
    parametric extents."""
    xs = [p[0] for p in outline]
    zs = [p[1] for p in outline]
    x0, x1, z0, z1 = min(xs), max(xs), min(zs), max(zs)
    tx0, tx1 = X(xr0), X(xr1)
    tz0, tz1 = Z(yr1), Z(yr0)
    fx = (tx1 - tx0) / (x1 - x0) if x1 > x0 else 1.0
    fz = (tz1 - tz0) / (z1 - z0) if z1 > z0 else 1.0
    return [(tx0 + (p[0] - x0) * fx, tz0 + (p[1] - z0) * fz) for p in outline]


# --------------------------------------------------------------------------- #
def head_shell(mat, coll, nu=112, nv=76):
    """Superellipse-lathe helmet: narrow crown, full cheeks, wide round chin."""
    # the head silhouette is the single most important curve in the model, so
    # it thins out more slowly than the rest of the mesh
    k = opus_lib.DENSITY ** 0.7
    nu = max(40, int(round(nu * k)) // 2 * 2)
    nv = max(28, int(round(nv * k)))
    verts, faces = [], []
    rows = []
    for j in range(nv + 1):
        t = 1.0 - 2.0 * j / nv                       # +1 top .. -1 bottom
        n = HEAD_N_TOP if t >= 0 else HEAD_N_BOT
        f = super_r(t, n)
        z = HEAD_CZ + t * HEAD_HH
        row = []
        if f < 1e-5:
            row.append(len(verts))
            verts.append((0.0, 0.0, z))
            rows.append((row, True))
            continue
        for i in range(nu):
            th = i / nu * 2.0 * math.pi              # 0 = front (-Y)
            sx = math.sin(th)
            sy = -math.cos(th)
            # flatten the face plane a little, keep the back round
            flat = 1.0 - 0.075 * max(0.0, -sy) ** 2
            row.append(len(verts))
            verts.append((sx * HEAD_HW * f, sy * HEAD_HD * f * flat, z))
        rows.append((row, False))
    for j in range(nv):
        a, pa = rows[j]
        b, pb = rows[j + 1]
        if pa:
            for i in range(nu):
                faces.append([a[0], b[(i + 1) % nu], b[i]])
        elif pb:
            for i in range(nu):
                faces.append([a[i], a[(i + 1) % nu], b[0]])
        else:
            for i in range(nu):
                k = (i + 1) % nu
                faces.append([a[i], a[k], b[k], b[i]])
    ob = mesh_from("HeadShell", verts, faces, mat, coll=coll)
    shade(ob)
    return ob


# --------------------------------------------------------------------------- #
def decal(ob, head, extra, dome=0.0):
    project_to_head(ob, head, (0.0, 0.0, HEAD_CZ),
                    (HEAD_HW, HEAD_HD, HEAD_HH), extra + BIAS)
    return ob


def flat_shape(name, outline, mat, coll, depth=0.004, rings=7, power=0.55):
    return inflate_outline(name, outline, depth, rings=rings, power=power,
                           mat=mat, coll=coll, flat_back=True)


# --------------------------------------------------------------------------- #
def build_dome_rim(head, M, coll):
    """Thin bright ring marking the edge of the glass helmet dome.

    In the reference this ring sits ~14 px inside the head silhouette and is
    the single strongest cue that the white shell is a transparent helmet
    rather than a bare ball.  Only the part below the crest is visible.
    """
    o = super_outline(HEAD_HW - DOME_INSET, HEAD_HH - DOME_INSET,
                      HEAD_N_TOP, HEAD_N_BOT, 224, 0.0, HEAD_CZ)
    i = super_outline(HEAD_HW - DOME_INSET - L(7.0),
                      HEAD_HH - DOME_INSET - L(7.0),
                      HEAD_N_TOP, HEAD_N_BOT, 224, 0.0, HEAD_CZ)
    ring = piping("DomeRim", o, i, 0.011, M["face_rim"], coll, layers=5)
    decal(ring, head, 0.0060)
    return [ring]


# --------------------------------------------------------------------------- #
def build_face_plate(head, M, coll):
    out = []
    rim_o = super_outline(FACE_HW + L(5.0), FACE_HH + L(5.0), FACE_N, FACE_N,
                          192, 0.0, FACE_CZ)
    rim = flat_shape("FaceRim", rim_o, M["face_rim"], coll, depth=0.010)
    decal(rim, head, 0.0030)
    out.append(rim)

    plate_o = super_outline(FACE_HW, FACE_HH, FACE_N, FACE_N, 192, 0.0, FACE_CZ)
    plate = flat_shape("FacePlate", plate_o, M["white_face"], coll, depth=0.014,
                       rings=9, power=0.62)
    decal(plate, head, 0.0045)

    def face_shade(w, l):
        # soft vertical warmth + gentle side shading, keeps the visor from
        # reading as a flat white disc
        u = min(1.0, abs(w.x) / FACE_HW)
        v = (w.z - FACE_CZ) / FACE_HH
        k = 1.0 - 0.055 * u ** 2.4 - 0.030 * max(0.0, -v) ** 2
        warm = 1.0 + 0.020 * max(0.0, -v)
        return (k * warm, k * (1.0 - 0.004), k * (1.0 - 0.012 * max(0.0, -v)), 1.0)

    set_vertex_colors(plate, face_shade)
    out.append(plate)
    return out


# --------------------------------------------------------------------------- #
def build_eye(head, M, coll, sx):
    """sx = +1 -> viewer right (character left).  Returns list of objects."""
    tag = "L" if sx > 0 else "R"
    cx = sx * EYE_X
    inward = -sx                                    # +x is inward for the left eye
    objs = []

    # Measured 1:1 off the poster (screen-left eye, reference pixels):
    #   rim outer ellipse  c(466.0, 602.0)  half(42.5, 47.5)
    #   pearl socket       c(467.0, 610.0)  half(38.5, 38.5)   <- ROUND, 8 px down
    #   teal lens          c(475.5, 609.0)  half(31.0, 39.5)
    # Subtracting a round socket that is offset downward from an ellipse that is
    # taller than it is wide gives 17 px of black at the lash line, 5 px outer,
    # 3 px inner and 1 px underneath: the reference's crescent.  The z-order is
    # deliberately generous so the socket never lets the dark plate poke through
    # at its own rim (that was the "even donut" artefact).
    # 1. dark lash oval (whole eye silhouette) - kept deliberately shallow
    soc_cx = cx + inward * L(6.0)
    soc_cz = EYE_CZ
    dark_o = ellipse_outline(EYE_A, EYE_B, 128, soc_cx, soc_cz,
                             squash_top=1.0, squash_bot=0.985)
    dark = flat_shape("EyeDark_" + tag, dark_o, M["eye_dark"], coll, depth=0.0035,
                      rings=7, power=0.55)
    decal(dark, head, 0.0240)
    objs.append(dark)

    # 1b. pearl socket - a CIRCLE dropped below the rim centre.  This is the
    #     piece that carves the crescent; it is face-coloured (very slightly
    #     shaded) so it reads as the white sliver between lash line and lens.
    soc_o = ellipse_outline(SOC_R, SOC_R, 112, soc_cx, soc_cz - SOC_DZ)
    # kept almost perfectly flat on purpose: as a raised pillow its own shaded
    # rim read as ~4 px of extra black at the lash line and made the socket look
    # like a separate white pearl sitting behind the lens
    socket = flat_shape("EyeSocket_" + tag, soc_o, M["eye_socket"], coll,
                        depth=0.0015, rings=7, power=0.58)
    decal(socket, head, 0.0280)
    objs.append(socket)

    # 2. iris - the glossy teal lens, seated inward-and-down in the socket
    ir_cx = cx + inward * IRIS_DX
    ir_cz = IRIS_CZ
    ir_a, ir_b = IRIS_A, IRIS_B
    ir_o = ellipse_outline(ir_a, ir_b, 128, ir_cx, ir_cz,
                           squash_top=0.965, squash_bot=1.0)
    # flatter than before: the reference lens is a disc with one crisp catch
    # light, the old dome caught a broad sheen that washed the teal out to grey
    iris = flat_shape("Iris_" + tag, ir_o, M["iris"], coll, depth=0.0045,
                      rings=13, power=0.66)
    decal(iris, head, 0.0305)

    def iris_col(w, l):
        v = (w.z - ir_cz) / ir_b                    # +1 top .. -1 bottom
        uin = (w.x - ir_cx) / ir_a * inward         # +1 toward the nose
        # Reference lens: dark teal over the upper two thirds, luminous mint
        # only in the bottom third.  The old ramp turned bright at mid-height.
        t = max(0.0, min(1.0, (0.10 - v) / 1.10))
        c = (0.055 + 0.210 * t ** 1.3,
             0.300 + 0.600 * t ** 0.85,
             0.310 + 0.510 * t ** 0.85)
        # Soft near-black iris core.  Measured x 460..492 / y 588..618, which in
        # normalised lens space is centred at (uin 0.02, v 0.15) with radii
        # (0.52, 0.38) at full strength.  A gradient - not a hard-edged pupil
        # disc - is what the poster actually shows.
        e = ((uin - 0.05) / 0.86) ** 2 + ((v - 0.14) / 0.82) ** 2
        k = max(0.0, min(1.0, 1.45 - e)) ** 0.35
        core = (0.010, 0.045, 0.056)
        c = tuple(c[i] * (1.0 - k) + core[i] * k for i in range(3))
        # cool rim light along the lower-outer edge
        rim = max(0.0, 1.0 - ((uin * 0.85 + 0.34) ** 2 + (v + 0.78) ** 2) * 2.3)
        c = tuple(min(1.0, c[i] + rim * 0.14) for i in range(3))
        return (c[0], c[1], c[2], 1.0)

    set_vertex_colors(iris, iris_col)
    objs.append(iris)

    # 5. catch-lights: measured big disc centre (484, 582) r 7.7 -> 10.5 px
    #    inward and 25 px above the lens centre; small bounce spot low-outward
    hi_o = ellipse_outline(L(7.7), L(7.7), 56, ir_cx + inward * L(10.5),
                           ir_cz + L(25.0))
    hi = flat_shape("Spec_" + tag, hi_o, M["highlight"], coll, depth=0.006,
                    rings=7, power=0.58)
    decal(hi, head, 0.0375)
    objs.append(hi)

    hi2_o = ellipse_outline(L(3.4), L(2.7), 40, ir_cx - inward * L(11.0),
                            ir_cz - L(27.0))
    hi2 = flat_shape("Spec2_" + tag, hi2_o, M["highlight2"], coll, depth=0.003,
                     rings=5, power=0.58)
    decal(hi2, head, 0.0368)
    objs.append(hi2)

    # 6. outer lash spike - a thin flick off the top-outer rim, not a bean
    lash_top, lash_bot = [], []
    n = 24
    for i in range(n + 1):
        u = i / n
        th = math.radians(128.0) + math.radians(56.0) * u       # around the oval
        ex = soc_cx - sx * math.cos(th) * EYE_A
        ez = soc_cz + math.sin(th) * EYE_B
        t = L(5.5) * (math.sin(math.pi * min(1.0, u * 1.06)) ** 0.8) * (1.0 - 0.45 * u)
        nx, nz = -sx * math.cos(th), math.sin(th)
        ln = math.hypot(nx, nz) or 1.0
        lash_top.append((ex + nx / ln * t, ez + nz / ln * t))
        lash_bot.append((ex, ez))
    lash_o = lash_top + lash_bot[::-1]
    lash = flat_shape("Lash_" + tag, lash_o, M["eye_dark"], coll, depth=0.006,
                      rings=5, power=0.55)
    decal(lash, head, 0.0244)
    objs.append(lash)
    return objs


# --------------------------------------------------------------------------- #
def build_brow(head, M, coll, sx):
    tag = "L" if sx > 0 else "R"
    cx = sx * BROW_X
    top, bot = [], []
    n = 48

    def droop(u):
        """Linear interpolation of the measured BROW_V table (reference px)."""
        for i in range(len(BROW_V) - 1):
            u0, d0 = BROW_V[i]
            u1, d1 = BROW_V[i + 1]
            if u <= u1:
                f = 0.0 if u1 == u0 else (u - u0) / (u1 - u0)
                return d0 + (d1 - d0) * f
        return BROW_V[-1][1]

    for i in range(n + 1):
        u = i / n                                    # 0 = outer, 1 = inner
        x = cx + sx * (u - 0.5) * -2.0 * BROW_HW     # outer -> inner
        # measured comma profile: apex at u ~ 0.76, 8 px droop at the outer tip
        zc = BROW_Z - L(droop(u))
        # single smooth bell instead of the old two-piece ramp, whose join at
        # u = 0.36 produced a visible kink and a needle-thin inner tip
        th = BROW_T * math.sin(math.pi * u) ** 0.30
        top.append((x, zc + th * 0.55))
        bot.append((x, zc - th * 0.45))
    outline = top + bot[::-1]
    ob = flat_shape("Brow_" + tag, outline, M["brow"], coll, depth=0.010,
                    rings=7, power=0.6)
    decal(ob, head, 0.0260)
    return [ob]


# --------------------------------------------------------------------------- #
def build_nose(head, M, coll):
    # the poster's nose is a faint crease ~14 px tall, not a ball; the old
    # depth-0.019 dome 14 px lower rendered as a white sphere on the upper lip
    o = ellipse_outline(L(4.5), L(5.0), 64, 0.0, NOSE_Z)
    ob = flat_shape("Nose", o, M["white_face"], coll, depth=0.0035, rings=9,
                    power=0.70)
    decal(ob, head, 0.0230)

    def col(w, l):
        v = (w.z - NOSE_Z) / L(5.0)
        k = 1.0 + 0.016 * max(0.0, v) - 0.030 * max(0.0, -v) ** 1.4
        return (k, k * 0.995, k * 0.985, 1.0)

    set_vertex_colors(ob, col)
    return [ob]


# --------------------------------------------------------------------------- #
def mouth_outline(hw, top_z, cor_z, bot_z, n=72, grow=0.0):
    """Open smile: shallow arc on top, deep bowl below.

    `grow` is tapered to zero at the corners (|t| -> 1).  Adding it to x and z
    independently, as the previous version did, left a blunt vertical segment
    at each corner; the reference mouth comes to a point.
    """
    top, bot = [], []
    for i in range(n + 1):
        t = -1.0 + 2.0 * i / n
        k = max(0.0, 1.0 - t * t)
        g = grow * k ** 0.45
        top.append((t * (hw + g), cor_z + (top_z - cor_z) * k ** 0.55 + g))
    for i in range(n + 1):
        t = 1.0 - 2.0 * i / n
        k = max(0.0, 1.0 - t * t)
        g = grow * k ** 0.45
        bot.append((t * (hw + g), cor_z + (bot_z - cor_z) * k ** 0.62 - g))
    return top[:-1] + bot[:-1]


def build_mouth(head, M, coll):
    objs = []
    # NOTE: the rim must stay FLATTER than the cavity is forward, or its dome
    # pokes through and the mouth renders white (v2-iter1 bug).
    rim_o = mouth_outline(MOUTH_HW, MOUTH_TOP, MOUTH_COR, MOUTH_BOT, grow=L(2.5))
    rim = flat_shape("MouthRim", rim_o, M["mouth_rim"], coll, depth=0.005,
                     rings=7, power=0.6)
    decal(rim, head, 0.0224)
    objs.append(rim)

    cav_o = mouth_outline(MOUTH_HW, MOUTH_TOP, MOUTH_COR, MOUTH_BOT)
    cav = flat_shape("MouthCavity", cav_o, M["mouth_cavity"], coll, depth=0.005,
                     rings=9, power=0.55)
    decal(cav, head, 0.0262)

    def cav_col(w, l):
        v = (w.z - MOUTH_BOT) / (MOUTH_TOP - MOUTH_BOT)
        v = max(0.0, min(1.0, v))
        t = min(1.0, abs(w.x) / MOUTH_HW)
        # The poster shows a glossy sheen on the *inner upper lip* (bright band
        # x547..567 at y664..670) with deep shadow at the corners and down in
        # the bowl.  The previous ramp was inverted - brightest at the bottom.
        k = (0.52 + 0.70 * v ** 1.9) * (1.0 - 0.30 * t ** 2)
        # localised specular band; without it the CG opening reads as one solid
        # dark slab from y665 down, where the reference is split in two by it
        sheen = math.exp(-((v - 0.88) / 0.14) ** 2) * \
            math.exp(-(t / 0.42) ** 2)
        k *= 1.0 + 2.1 * sheen
        return (k, k * 0.80, k * 0.82, 1.0)

    set_vertex_colors(cav, cav_col)
    objs.append(cav)

    # tongue - ref x 534..570 (w37) y 675..690 (h16); the old 50 x 25 px ellipse
    # was ~35 % oversized and filled the whole opening
    tz = TONGUE_Z
    tongue_o = ellipse_outline(TONGUE_A, TONGUE_B, 96, 0.0, tz,
                               squash_top=0.92, squash_bot=1.10)
    tongue = flat_shape("Tongue", tongue_o, M["tongue"], coll, depth=0.015,
                        rings=11, power=0.66)
    decal(tongue, head, 0.0280)

    def t_col(w, l):
        v = (w.z - tz) / TONGUE_B
        k = 1.0 + 0.16 * max(0.0, v) - 0.20 * max(0.0, -v)
        return (k, k * 0.96, k * 0.96, 1.0)

    set_vertex_colors(tongue, t_col)
    objs.append(tongue)
    return objs


# --------------------------------------------------------------------------- #
def build_blush(head, M, coll, sx):
    tag = "L" if sx > 0 else "R"
    cx = sx * BLUSH_X
    a, b = L(28.0), L(13.0)
    o = ellipse_outline(a, b, 96, cx, BLUSH_Z)
    ob = flat_shape("Blush_" + tag, o, M["blush"], coll, depth=0.004, rings=5,
                    power=0.5)
    decal(ob, head, 0.0215)

    def col(w, l):
        u = (w.x - cx) / a
        v = (w.z - BLUSH_Z) / b
        d = min(1.0, math.hypot(u, v))
        f = (1.0 - d) ** 1.5
        return (1.0, 1.0 - 0.32 * f, 1.0 - 0.28 * f, 1.0)

    set_vertex_colors(ob, col)
    return [ob]


# --------------------------------------------------------------------------- #
def head_hw_px(yr):
    """Half-width of the head silhouette, in reference px, at reference row yr."""
    t = (yr - 556.5) / 178.5
    if abs(t) >= 1.0:
        return 0.0
    n = HEAD_N_TOP if t < 0 else HEAD_N_BOT
    return 207.5 * (1.0 - abs(t) ** n) ** (1.0 / n)


# Lower edge of the mint tiara.
# Iter-9 used a deep centre dip (487) -> band too tall; iter-10 used the
# g_crown reading (403) -> band a thin flap, because the pale area at the
# upper-left of the poster is a SPECULAR STREAK on glossy mint, not white
# material.
#
# Iter-13: measured column-by-column with the b-r discriminator and verified by
# drawing the detected edge back onto both images (hoodmark.png).  The detector
# lands exactly on the boundary in both, and the shapes are plainly different:
# the reference lower edge is a DOME on each side - it climbs to a plateau at
# y ~436 over dx 100..120 and falls away both outward (y 480 at dx 180) and
# inward toward the heart - whereas the CG traced an almost straight diagonal.
# Folded left-against-right (the poster's head is turned, so the two sides
# disagree by 10-20 px through dx 50..90; the average is the best a symmetric
# model can do):
#   dx   40  48  56  64  72  80  90 100 110 120 130 140 150 160 170 180
#   y   481 480 475 470 463 454 442 438 436 437 441 447 455 463 468 480
# NOTE the inner readings (dx < 60) are the mint HEART SURROUND, not the band -
# CrestDrop supplies those separately.  Following them here as well produced
# two pointed mint wings either side of the badge.
# Holding the inner section flat at the measured y ~458..461 was also wrong: the
# render then carried opaque mint right across the forehead, and a per-row mint
# scan showed the CG band reaching 68..142 px further toward the centre than the
# poster's between y438 and y470 - exactly the "hat covers the face too much"
# complaint.  The poster's own inner edge cannot be read directly because the
# badge bezel occludes it, but bare white face is visible at dx 47..80 by y438,
# so the band must have receded by then.  The inner section is therefore carried
# flat at y ~431..435, continuing the outer curve instead of dipping 25 px below
# it, and CrestDrop supplies the mint that dips lower in the centre.
# The outer end is pinned to the helmet silhouette - head_hw_px(484) - 10 -
# so the lower edge meets the outer edge with no sliver (iter-8 bug).
# (yr, dx-from-centre), outer end first.
HOOD_V = [(484.0, 184.0), (470.0, 170.0), (463.0, 160.0), (455.0, 150.0),
          (447.0, 140.0), (441.0, 130.0), (437.0, 120.0), (436.0, 110.0),
          (435.0, 100.0), (434.0, 90.0), (433.0, 80.0), (432.0, 70.0),
          (431.0, 58.0), (431.0, 40.0), (431.0, 0.0)]
HOOD_INSET = 10.0          # thin white helmet rim outside the mint
HOOD_CX = 554.5


def hood_lower(t):
    """Interpolate the chevron: t 0..1 from the outer end to the centre tip."""
    n = len(HOOD_V) - 1
    f = t * n
    i = min(n - 1, int(f))
    a, b = HOOD_V[i], HOOD_V[i + 1]
    u = f - i
    return (a[0] + (b[0] - a[0]) * u, a[1] + (b[1] - a[1]) * u)


def crest_outline(steps=30):
    """Mint tiara band.

    Outer edge tracks the helmet silhouette inset 10 px (leaving the thin
    white rim visible in the poster); the lower edge is the measured shallow
    chevron.  The white heart badge sits on top of its centre.
    """
    right, left = [], []
    y0, y1 = 379.0, HOOD_V[0][0]
    for i in range(steps + 1):
        yr = y0 + (y1 - y0) * i / steps
        dx = max(0.0, head_hw_px(yr) - HOOD_INSET)
        right.append((HOOD_CX + dx, yr))
        left.append((HOOD_CX - dx, yr))
    for i in range(1, 41):
        yr, dx = hood_lower(i / 40.0)
        right.append((HOOD_CX + dx, yr))
        left.append((HOOD_CX - dx, yr))
    pts = right[::-1] + left[1:-1]        # up the right edge, down the left
    return [(X(a), Z(b)) for a, b in pts]


def hood_trim_outline(inner=13.0, outer=1.0):
    """Thin silver piping that follows the tiara's lower chevron.

    Clamped to the mint hood: the strip must never reach past the helmet
    silhouette or it renders as bright slivers outside the head (iter-8 bug).
    """
    up, dn = [], []
    for sgn in (1.0, -1.0):
        seq = range(0, 61) if sgn > 0 else range(60, -1, -1)
        for i in seq:
            t = i / 60.0
            yr, dx = hood_lower(t)
            lim = max(0.0, head_hw_px(yr) - HOOD_INSET - 3.0)
            dx = min(dx, lim)
            taper = min(1.0, (1.0 - t) * 6.0 + 0.15) if t > 0.84 else 1.0
            taper = min(1.0, t * 8.0) if t < 0.12 else taper
            up.append((HOOD_CX + sgn * dx, yr - inner * taper))
            dn.append((HOOD_CX + sgn * dx, yr + outer * taper))
    pts = up + dn[::-1]
    return [(X(a), Z(b)) for a, b in pts]


# --------------------------------------------------------------------------- #
def build_crest(head, M, coll):
    """Mint tiara hood + white heart bezel + pink heart badge."""
    objs = []
    hood = flat_shape("Crest", crest_outline(), M["mint_glass"], coll,
                      depth=0.014, rings=8, power=0.5)
    decal(hood, head, 0.0170)
    objs.append(hood)

    # NOTE: an explicit silver piping strip along the hood edge rendered as a
    # hard straight seam across the crown (iter-11).  The poster has no such
    # line -- the bright edge there is the mint's own specular -- so it is gone.

    # mint heart surround: hangs BELOW the hood arc, behind the white badge.
    # This is the deep central "V" the poster reads at small scale.
    # Iter-13: the whole badge stack was 15 % oversized.  Anchor measurement -
    # the pink heart's bounding box on the poster is x 537..596 y 421..466
    # (60 x 46 px, centre y 443.5), against 69 x 54 in the CG.  Every layer is
    # therefore scaled by 0.86 about (HOOD_CX, y 444).  (The poster's heart is
    # centred at x 566.5 rather than 554.5 because the head is turned; a
    # symmetric model cannot follow that, so the centre stays on HOOD_CX.)
    #
    # Iter-14: drawing the measured hood edge back onto both images
    # (hoodmark2.png) showed the band itself is now correct, but the CG still
    # read wrong because the mint SURROUND was far too narrow, leaving the
    # band's inner tips sticking out as two bare blades.  Measured with the
    # (b-r, g-r) mint mask, the poster's surround spans x 478..659 (181 px
    # wide, half 90) x y ~400..494, against 107 x 101 here - i.e. the poster's
    # is a WIDE, flat heart that merges into the band, not a narrow tall one.
    # Iter-16: measuring the mint tone either side of the badge gives the
    # poster (0.511, 0.767, 0.714) against (0.367, 0.728, 0.716) here, i.e. the
    # CG surround is far too dark in red.  The poster's surround is simply the
    # same light mint as the band -- there is no deep-teal layer at all -- so
    # mint_deep is gone and both layers now read as one soft mint pillow.
    drop_o = fit_px(heart_outline(1.0, 1.0, 144, plump=0.28),
                    466.0, 643.0, 396.0, 494.0)
    drop = flat_shape("CrestDrop", drop_o, M["mint"], coll,
                      depth=0.012, rings=8, power=0.52)
    decal(drop, head, 0.0250)
    objs.append(drop)

    drop_rim_o = fit_px(heart_outline(1.0, 1.0, 144, plump=0.28),
                        474.0, 635.0, 401.0, 489.0)
    drop_rim = flat_shape("CrestDropRim", drop_rim_o, M["mint"], coll,
                          depth=0.010, rings=8, power=0.52)
    decal(drop_rim, head, 0.0262)
    objs.append(drop_rim)

    # white bezel plate.  Isolating the pink with (r-g, r-b) > 0.02 and taking
    # the largest blob puts the poster's heart at x 530..602 y 417..475
    # (72 x 58, widest 36 % down) against 60 x 46 widest 27 % down here -- so
    # the badge was 20 % too narrow, 26 % too short, sat 5 px high and was too
    # top-heavy.  Scanning y 446 across the badge, the poster reads mint to
    # x 483, white bezel x 511..530, pink from x 531, about a centre of x 566:
    # bezel outer half 55, pink half 36.  Every layer below is rebuilt to that.
    plate_o = fit_px(heart_outline(1.0, 1.0, 144, plump=0.30),
                     499.5, 609.5, 405.0, 484.0)
    plate = flat_shape("BadgePlate", plate_o, M["white_shell"], coll,
                       depth=0.009, rings=8, power=0.55)
    decal(plate, head, 0.0300)
    objs.append(plate)

    # Iter-17: scanning y 444 across the poster badge, the white bezel occupies
    # half-radius 43..54 (x 512..523 left of centre x 566) and there is a 6 px
    # DARK teal ring at half-radius 36..43 (x 524..529) between the bezel and
    # the pink heart.  The CG had white running straight into the pink, which
    # cost the badge all of its definition, so this layer is now the dark ring
    # (BadgePlate above already supplies the white bezel out to half 55).
    rim_o = fit_px(heart_outline(1.0, 1.0, 144, plump=0.44),
                   511.0, 597.0, 411.5, 480.5)
    rim = flat_shape("BadgeRim", rim_o, M["mint_deep"], coll,
                     depth=0.006, rings=8, power=0.55)
    decal(rim, head, 0.0385)
    objs.append(rim)

    ph_o = fit_px(heart_outline(1.0, 1.0, 144, plump=0.56),
                  518.5, 590.5, 417.0, 475.0)
    bcz = Z(446.0)
    bh = L(58.0)
    ph = flat_shape("BadgeHeart", ph_o, M["pink_heart"], coll, depth=0.018,
                    rings=11, power=0.62)
    decal(ph, head, 0.0440)

    def ph_col(w, l):
        v = (w.z - (bcz - bh * 0.35)) / (bh * 0.8)
        k = 0.90 + 0.28 * max(0.0, min(1.0, v))
        spec = max(0.0, 1.0 - (((w.x + L(14.0)) / L(14.0)) ** 2 +
                               ((w.z - (bcz + bh * 0.20)) / L(10.0)) ** 2))
        k += spec * 0.55
        k = min(1.55, k)
        return (k, k, k, 1.0)

    set_vertex_colors(ph, ph_col)
    objs.append(ph)
    return objs


# --------------------------------------------------------------------------- #
def build_ear(M, coll, sx):
    """Headphone cup: silver collar -> deep-teal cup -> bright teal lens.

    Reference: a large glossy cup protruding sideways, x 250..365 (left),
    y 497..648, bright cyan-teal outer lens with a strong specular, a darker
    teal band on its inboard side and a bright silver collar behind it.
    """
    tag = "L" if sx > 0 else "R"
    objs = []
    cx = sx * EAR_X

    collar = quad_sphere("EarCollar_" + tag,
                         (EAR_HW * 1.10, 0.132, EAR_HH * 1.06),
                         (cx, 0.0, EAR_Z),
                         n=18, power=2.4, mat=M["face_rim"], coll=coll)
    subsurf(collar, 1, 2)
    objs.append(collar)

    cup = quad_sphere("EarPod_" + tag,
                      (EAR_HW * 0.94, 0.180, EAR_HH * 0.90),
                      (cx, 0.0, EAR_Z),
                      n=20, power=2.4, mat=M["mint_deep"], coll=coll)
    subsurf(cup, 1, 2)
    objs.append(cup)

    lens = quad_sphere("EarGem_" + tag,
                       (EAR_HW * 0.56, 0.178, EAR_HH * 0.54),
                       (cx + sx * EAR_HW * 0.16, 0.0, EAR_Z - EAR_HH * 0.06),
                       n=16, power=2.2, mat=M["mint"], coll=coll)
    subsurf(lens, 1, 2)
    objs.append(lens)

    core = quad_sphere("EarCore_" + tag,
                       (EAR_HW * 0.22, 0.176, EAR_HH * 0.21),
                       (cx + sx * EAR_HW * 0.22, 0.0, EAR_Z + EAR_HH * 0.20),
                       n=12, power=2.2, mat=M["mint_pale"], coll=coll)
    subsurf(core, 1, 2)
    objs.append(core)

    # slim mint arm tying the cup back into the helmet
    arm = quad_sphere("EarArm_" + tag, (EAR_HW * 1.00, 0.095, EAR_HH * 0.26),
                      (cx - sx * EAR_HW * 0.80, 0.020, EAR_Z + EAR_HH * 0.70),
                      n=14, power=2.6, mat=M["mint"], coll=coll)
    objs.append(arm)
    return objs


# --------------------------------------------------------------------------- #
def build_antenna(M, coll):
    objs = []
    base = quad_sphere("AntBase", (0.088, 0.088, 0.048), (0.0, 0.0, 1.7980),
                       n=16, power=2.6, mat=M["white_shell"], coll=coll)
    objs.append(base)
    stalk = quad_sphere("AntStalk", (0.026, 0.026, 0.040),
                        (0.0, 0.0, 1.8620), n=12, power=2.6,
                        mat=M["white_shell"], coll=coll)
    objs.append(stalk)
    knob = quad_sphere("AntKnob", (0.020, 0.020, 0.020), (0.0, 0.0, 1.8930),
                       n=12, power=2.4, mat=M["dark_metal"], coll=coll)
    objs.append(knob)

    o = fit_px(heart_outline(1.0, 1.0, 144, plump=0.42),
               478.0, 636.0, 246.0, 334.0)
    cz = Z(290.0)
    h = L(88.0)
    heart = inflate_outline("AntHeart", o, 0.105, rings=15, power=0.70,
                            mat=M["pink_heart"], coll=coll)

    def col(w, l):
        v = (w.z - (cz - h * 0.35)) / (h * 0.8)
        k = 0.88 + 0.30 * max(0.0, min(1.0, v))
        spec = max(0.0, 1.0 - (((w.x + L(22.0)) / L(19.0)) ** 2 +
                               ((w.z - (cz + h * 0.20)) / L(14.0)) ** 2))
        k = min(1.6, k + spec * 0.62)
        return (k, k, k, 1.0)

    set_vertex_colors(heart, col)
    objs.append(heart)
    return objs


# --------------------------------------------------------------------------- #
FACE_DENSITY_BOOST = 1.36  # keep eyes / mouth finer than the rest of the body


def build_head(M, coll):
    head = head_shell(M["white_shell"], coll)
    parts = [head]
    parts += build_dome_rim(head, M, coll)
    parts += build_face_plate(head, M, coll)
    parts += build_crest(head, M, coll)
    base_d = opus_lib.DENSITY
    for sx in (1, -1):
        opus_lib.set_density(min(1.0, base_d * FACE_DENSITY_BOOST))
        parts += build_eye(head, M, coll, sx)
        parts += build_brow(head, M, coll, sx)
        opus_lib.set_density(base_d)
        parts += build_blush(head, M, coll, sx)
        parts += build_ear(M, coll, sx)
    parts += build_nose(head, M, coll)
    opus_lib.set_density(min(1.0, base_d * FACE_DENSITY_BOOST))
    parts += build_mouth(head, M, coll)
    opus_lib.set_density(base_d)
    parts += build_antenna(M, coll)
    return head, parts
