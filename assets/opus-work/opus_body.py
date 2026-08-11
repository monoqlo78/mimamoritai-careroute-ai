"""Body / limbs / props / cape for the Mimamo opus rebuild."""
import math

import bpy
from mathutils import Euler, Matrix, Vector

from opus_lib import (
    add_modifier,
    aim_matrix,
    ellipse_outline,
    heart_outline,
    inflate_outline,
    limb,
    mesh_from,
    octagon_outline,
    quad_sphere,
    revolve,
    ring_on,
    rounded_rect_outline,
    set_vertex_colors,
    subsurf,
)
from opus_head import uv_grid

# ---- reference-derived body constants ------------------------------------- #
SH_L = (0.2750, -0.0200, 0.7550)     # character's left  (+X, viewer right) - phone
EL_L = (0.4500, -0.0850, 0.6050)
WR_L = (0.5050, -0.1650, 0.7000)
HD_L = (0.5550, -0.1950, 0.7350)

SH_R = (-0.2750, -0.0200, 0.7550)    # character's right (-X, viewer left) - wave
EL_R = (-0.4450, -0.0600, 0.7000)
WR_R = (-0.6250, -0.0900, 0.8500)
HD_R = (-0.6300, -0.1000, 0.9150)

HIP_Z = 0.4000
KNEE_Z = 0.2450
ANK_Z = 0.0950
LEG_X = 0.1850


def build_body(M, coll, tex):
    out = {}

    # ---------------- torso ---------------- #
    torso = quad_sphere("Torso", radii=(0.2780, 0.2500, 0.2350), loc=(0, 0, 0.6250),
                        n=16, power=2.15, mat=M["white"], coll=coll)
    for v in torso.data.vertices:
        t = v.co.z / 0.2350
        k = 1.0 - 0.205 * max(0.0, -t) ** 1.4 - 0.10 * max(0.0, t) ** 1.8
        v.co.x *= k
        v.co.y *= k
    torso.data.update()
    subsurf(torso, 2, 3)
    out["Torso"] = torso

    hips = quad_sphere("Hips", radii=(0.2480, 0.2230, 0.1420), loc=(0, 0, 0.4300),
                       n=14, power=2.2, mat=M["white"], coll=coll)
    subsurf(hips, 2, 3)
    out["Hips"] = hips

    # chest shield: mint frame -> cream plate -> pink heart (ref x492-638 / y775-915)
    def shield_outline(hw, hh, cz, r, n=24):
        """Rounded square with a softly pointed lower edge (reference chest plate)."""
        pts = []
        corners = [(hw - r, cz - hh + r, math.pi, 1.5 * math.pi),
                   (hw - r, cz + hh - r, 0.0, 0.5 * math.pi),
                   (-hw + r, cz + hh - r, 0.5 * math.pi, math.pi),
                   (-hw + r, cz - hh + r, 1.5 * math.pi, 2.0 * math.pi)]
        # right edge upward
        pts.append((hw, cz - hh + r))
        cx0, cz0, a0, a1 = corners[1]
        for i in range(n + 1):
            a = a0 + (a1 - a0) * i / n
            pts.append((cx0 + r * math.cos(a), cz0 + r * math.sin(a)))
        cx0, cz0, a0, a1 = corners[2]
        for i in range(n + 1):
            a = a0 + (a1 - a0) * i / n
            pts.append((cx0 + r * math.cos(a), cz0 + r * math.sin(a)))
        pts.append((-hw, cz - hh + r))
        # lower edge: gentle downward point
        m = 40
        for i in range(m + 1):
            u = -1.0 + 2.0 * i / m
            pts.append((u * hw * (1.0 - 0.06 * (1 - u * u)),
                        cz - hh + r - (r + 0.052) * (1 - u * u) ** 0.62))
        return pts

    def chest_push(ob, base, hw, amp=0.075):
        for v in ob.data.vertices:
            u = min(1.0, abs(v.co.x) / hw)
            k = 0.5 * (1.0 + math.cos(math.pi * u))   # C1-smooth dome, no centre ridge
            v.co.y -= base + amp * k
        ob.data.update()

    frame = inflate_outline(
        "ChestFrame", shield_outline(0.1880, 0.1770, 0.6380, 0.0640),
        depth=0.040, rings=10, power=0.55, mat=M["mint"], coll=coll, flat_back=True)
    chest_push(frame, 0.1950, 0.2350)
    subsurf(frame, 2, 3)
    out["ChestFrame"] = frame

    rim2 = inflate_outline(
        "ChestRim", shield_outline(0.1745, 0.1645, 0.6390, 0.0580),
        depth=0.040, rings=10, power=0.55, mat=M["white_face"], coll=coll, flat_back=True)
    chest_push(rim2, 0.2040, 0.2200)
    subsurf(rim2, 2, 3)
    out["ChestRim"] = rim2

    plate = inflate_outline(
        "ChestPlate", shield_outline(0.1600, 0.1500, 0.6395, 0.0520),
        depth=0.040, rings=10, power=0.55, mat=M["mint_pale"], coll=coll, flat_back=True)
    chest_push(plate, 0.2135, 0.2050)
    subsurf(plate, 2, 3)
    out["ChestPlate"] = plate

    plate2 = inflate_outline(
        "ChestPlate2", shield_outline(0.1490, 0.1395, 0.6400, 0.0480),
        depth=0.040, rings=10, power=0.55, mat=M["white_face"], coll=coll, flat_back=True)
    chest_push(plate2, 0.2230, 0.1950)
    subsurf(plate2, 2, 3)
    out["ChestPlate2"] = plate2

    heart = inflate_outline("ChestHeart",
                            heart_outline(0.2800, 0.2540, 128, cz=0.6520, plump=0.62),
                            depth=0.050, rings=12, power=0.56, mat=M["pink_vc"],
                            coll=coll, flat_back=True)
    chest_push(heart, 0.2560, 0.1900, amp=0.070)
    subsurf(heart, 2, 3)
    z0, z1 = 0.6520 - 0.1270, 0.6520 + 0.1270

    def hgrad(world, local):
        t = min(1.0, max(0.0, (world.z - z0) / (z1 - z0)))
        g = 0.235 + 0.36 * t
        b = 0.285 + 0.32 * t
        # soft inner sheen, painted (a separate mesh occluded the outer one)
        rr = math.hypot((world.x) / 0.1290, (world.z - 0.6520) / 0.1180)
        ki = max(0.0, min(1.0, (0.80 - rr) / 0.34))
        g += (0.150 * ki)
        b += (0.140 * ki)
        # painted specular bloom - avoids a floating highlight card
        d = math.hypot((world.x + 0.0560) / 0.0500, (world.z - 0.7060) / 0.0360)
        k = max(0.0, 1.0 - d) ** 0.75
        return (1.0, min(1.0, g + (1.0 - g) * k), min(1.0, b + (1.0 - b) * k), 1.0)

    set_vertex_colors(heart, hgrad)
    out["ChestHeart"] = heart

    # ---------------- collar / scarf ---------------- #
    collar = revolve("Collar",
                     [(0.0, 0.0), (0.190, 0.0), (0.246, 0.020), (0.276, 0.056),
                      (0.256, 0.092), (0.196, 0.110), (0.0, 0.114)],
                     56, M["mint"], loc=(0, 0, 0.7960), coll=coll)
    for v in collar.data.vertices:
        v.co.y *= 1.05
        v.co.z += 0.030 * (v.co.y / 0.26)
        # drape the shawl down over the shoulders
        v.co.z -= 0.115 * min(1.0, (abs(v.co.x) / 0.276)) ** 2.1
    collar.data.update()
    subsurf(collar, 2, 3)
    out["Collar"] = collar

    knot = quad_sphere("ScarfKnot", radii=(0.088, 0.062, 0.070),
                       loc=(-0.0850, -0.2240, 0.8760),
                       n=12, power=2.35, mat=M["mint"], coll=coll)
    subsurf(knot, 2, 3)
    out["ScarfKnot"] = knot

    knot2 = quad_sphere("ScarfKnotLoop", radii=(0.052, 0.046, 0.052),
                        loc=(-0.0320, -0.2210, 0.8960),
                        n=10, power=2.3, mat=M["mint"], coll=coll)
    subsurf(knot2, 2, 3)
    out["ScarfKnotLoop"] = knot2

    for sx, tag in ((1, "L"), (-1, "R")):
        tail_pts = []
        n = 22
        for i in range(n + 1):
            t = i / n
            w = 0.052 * (1.0 - 0.45 * t) * (1.0 + 0.5 * math.sin(t * 3.0))
            tail_pts.append((sx * (0.055 + 0.075 * t + 0.03 * math.sin(t * 4.2)) + w,
                             0.8600 - 0.235 * t))
        for i in range(n, -1, -1):
            t = i / n
            w = 0.052 * (1.0 - 0.45 * t) * (1.0 + 0.5 * math.sin(t * 3.0))
            tail_pts.append((sx * (0.055 + 0.075 * t + 0.03 * math.sin(t * 4.2)) - w,
                             0.8600 - 0.235 * t))
        tl = inflate_outline(f"ScarfTail_{tag}", tail_pts, depth=0.022, rings=7,
                             power=0.55, mat=M["mint"], coll=coll)
        for v in tl.data.vertices:
            v.co.y -= 0.2250
        tl.data.update()
        subsurf(tl, 2, 3)
        out[tl.name] = tl

    # ---------------- belt (ref y 858..915) ---------------- #
    belt = revolve("Belt",
                   [(0.0, 0.0), (0.2740, 0.0), (0.2880, 0.030), (0.2880, 0.112),
                    (0.2740, 0.1425), (0.0, 0.1425)],
                   56, M["mint"], loc=(0, 0, 0.4625), coll=coll)
    for v in belt.data.vertices:
        v.co.y *= 0.93
    belt.data.update()
    subsurf(belt, 2, 3)
    out["Belt"] = belt

    for nm, bx, by in (("BeltBuckle_L", 0.2570, -0.0660),
                       ("BeltBuckle_R", -0.2570, -0.0660)):
        bk = inflate_outline(nm,
                             rounded_rect_outline(0.0820, 0.0980, 0.0240, 8,
                                                  cx=bx, cz=0.5410),
                             depth=0.026, rings=6, power=0.55, mat=M["white_face"],
                             coll=coll, flat_back=True)
        for v in bk.data.vertices:
            v.co.y += by
        bk.data.update()
        subsurf(bk, 2, 2)
        out[nm] = bk

    # ---------------- arms ---------------- #
    def arm(sh, el, wr, tag):
        ua = limb(f"UpperArm_{tag}", sh, el, 0.0900, 0.0700, M["white"], coll, over=0.010)
        subsurf(ua, 2, 3)
        out[ua.name] = ua
        la = limb(f"LowerArm_{tag}", el, wr, 0.0700, 0.0620, M["white"], coll, over=0.010)
        subsurf(la, 2, 3)
        out[la.name] = la
        sj = quad_sphere(f"Shoulder_{tag}", radii=(0.078, 0.076, 0.076), loc=sh,
                         n=10, mat=M["white"], coll=coll)
        subsurf(sj, 2, 3)
        out[sj.name] = sj
        ej = quad_sphere(f"Elbow_{tag}", radii=(0.072, 0.072, 0.072), loc=el,
                         n=8, mat=M["white"], coll=coll)
        subsurf(ej, 2, 3)
        out[ej.name] = ej
        cuff = ring_on(f"Cuff_{tag}", el, wr, 0.94, 0.0700, 0.036, M["mint"], coll)
        subsurf(cuff, 2, 2)
        out[cuff.name] = cuff

    arm(SH_L, EL_L, WR_L, "L")
    arm(SH_R, EL_R, WR_R, "R")

    # ---------------- waving hand (character's right, viewer left) ---------- #
    palm_c = Vector((-0.6820, -0.1050, 0.9450))
    palm = quad_sphere("Palm_R", radii=(0.1030, 0.0620, 0.1120), loc=palm_c,
                       n=12, power=2.2, mat=M["white"], coll=coll)
    palm.rotation_euler = Euler((0, math.radians(-14), 0), "XYZ")
    subsurf(palm, 2, 3)
    out["Palm_R"] = palm

    finger_specs = [
        # (angle deg from +Z, length, radius)
        (-46.0, 0.138, 0.0428),
        (-19.0, 0.152, 0.0442),
        (6.0, 0.145, 0.0426),
        (30.0, 0.124, 0.0398),
    ]
    for i, (ang, ln, r) in enumerate(finger_specs):
        base = palm_c + Vector((math.sin(math.radians(ang)) * 0.074, 0.0,
                                math.cos(math.radians(ang)) * 0.080))
        tip = base + Vector((math.sin(math.radians(ang - 6)) * ln, -0.008,
                             math.cos(math.radians(ang - 6)) * ln))
        f = limb(f"Finger_R{i}", base, tip, r, r * 0.92, M["white"], coll, seg=20, over=0.010)
        subsurf(f, 2, 3)
        out[f.name] = f
    th_base = palm_c + Vector((-0.052, -0.030, -0.052))
    th_tip = th_base + Vector((-0.112, -0.034, 0.008))
    thumb = limb("Thumb_R", th_base, th_tip, 0.0480, 0.0425, M["white"], coll, seg=20, over=0.010)
    subsurf(thumb, 2, 3)
    out["Thumb_R"] = thumb

    # ---------------- smartwatch (wrist wearable) ---------------- #
    wm, wl = aim_matrix(WR_R, palm_c)
    band = revolve("WatchBand", [(0.0, 0.0), (0.074, 0.0), (0.080, 0.016),
                                 (0.080, 0.062), (0.074, 0.078), (0.0, 0.078)],
                   36, M["mint_dark"], coll=coll)
    band.matrix_world = wm @ Matrix.Translation((0, 0, -0.030))
    subsurf(band, 2, 2)
    out["WatchBand"] = band

    case = inflate_outline("WatchCase", rounded_rect_outline(0.1740, 0.1580, 0.052, 9),
                           depth=0.028, rings=7, power=0.6, mat=M["white_face"],
                           coll=coll, flat_back=True)
    case.data.materials.append(M["watch_screen"])
    ww, wh = 0.130, 0.116
    cm = case.data
    cuv = cm.uv_layers.new(name="UVMap") if not cm.uv_layers else cm.uv_layers[0]
    for poly in cm.polygons:
        c = poly.center
        if abs(c.x) <= ww * 0.5 and abs(c.z) <= wh * 0.5 and abs(poly.normal.y) > 0.55:
            poly.material_index = 1
    for li in range(len(cm.loops)):
        p = cm.vertices[cm.loops[li].vertex_index].co
        cuv.data[li].uv = (p.x / ww + 0.5, p.z / wh + 0.5)
    cm.update()
    case.matrix_world = (Matrix.Translation(Vector((-0.5900, -0.1620, 0.8320)))
                         @ Matrix.Rotation(math.radians(-18), 4, "Y"))
    subsurf(case, 2, 2)
    out["WatchCase"] = case

    wui = [
        inflate_outline("WatchHeart", heart_outline(0.0880, 0.0770, 48, cz=0.0165),
                        depth=0.006, rings=4, power=0.5, mat=M["white_face"], coll=coll,
                        flat_back=True),
        inflate_outline("WatchLine",
                        rounded_rect_outline(0.1080, 0.0130, 0.0058, 5, cz=-0.0400),
                        depth=0.005, rings=4, power=0.5, mat=M["white_face"], coll=coll,
                        flat_back=True),
    ]
    for u in wui:
        for v in u.data.vertices:
            v.co.y -= 0.0330
        u.data.update()
        u.matrix_world = case.matrix_world
        subsurf(u, 1, 2)
        out[u.name] = u

    # ---------------- phone hand (character's left, viewer right) ---------- #
    # The palm sits BEHIND the phone; only the fingertips wrap around its
    # viewer-left edge, matching the reference (no fingers across the screen).
    palm_l = Vector((0.4880, -0.1480, 0.6820))
    palm2 = quad_sphere("Palm_L", radii=(0.0800, 0.0640, 0.0850), loc=palm_l,
                        n=12, power=2.2, mat=M["white"], coll=coll)
    subsurf(palm2, 2, 3)
    out["Palm_L"] = palm2
    for i in range(4):
        base = palm_l + Vector((-0.052, -0.046, 0.054 - 0.037 * i))
        tip = base + Vector((-0.088, -0.106, 0.006))
        f = limb(f"Finger_L{i}", base, tip, 0.0255, 0.0230, M["white"], coll, seg=16)
        subsurf(f, 2, 3)
        out[f.name] = f
    th2 = limb("Thumb_L", palm_l + Vector((-0.028, -0.030, -0.070)),
               palm_l + Vector((-0.080, -0.106, -0.100)), 0.0300, 0.0265,
               M["white"], coll, seg=16)
    subsurf(th2, 2, 3)
    out["Thumb_L"] = th2

    # ---------------- smartphone ---------------- #
    ph_m = (Matrix.Translation(Vector((0.5308, -0.2450, 0.8135)))
            @ Matrix.Rotation(math.radians(-16), 4, "Y")
            @ Matrix.Rotation(math.radians(-7), 4, "X"))
    body = inflate_outline("PhoneBody", rounded_rect_outline(0.3620, 0.6240, 0.0620, 12),
                           depth=0.019, rings=6, power=0.42, mat=M["dark_body"], coll=coll)
    body.data.materials.append(M["phone_screen"])
    sw, sh = 0.3150, 0.5620
    bm = body.data
    uvl = bm.uv_layers.new(name="UVMap") if not bm.uv_layers else bm.uv_layers[0]
    scr_r = 0.055
    for poly in bm.polygons:
        c = poly.center
        ax, az = abs(c.x), abs(c.z)
        inx = ax <= sw * 0.5 and az <= sh * 0.5
        if inx and ax > sw * 0.5 - scr_r and az > sh * 0.5 - scr_r:
            dx = ax - (sw * 0.5 - scr_r)
            dz = az - (sh * 0.5 - scr_r)
            inx = (dx * dx + dz * dz) <= scr_r * scr_r
        if inx and abs(poly.normal.y) > 0.45:
            poly.material_index = 1
    for li in range(len(bm.loops)):
        p = bm.vertices[bm.loops[li].vertex_index].co
        uvl.data[li].uv = (p.x / sw + 0.5, p.z / sh + 0.5)
    bm.update()
    body.matrix_world = ph_m
    subsurf(body, 2, 2)
    out["PhoneBody"] = body

    # screen UI: mint check badge, two text bars, a row of small hearts
    def rot2(pts, ang, cx=0.0, cz=0.0):
        c, s = math.cos(ang), math.sin(ang)
        return [(cx + (x - cx) * c - (z - cz) * s,
                 cz + (x - cx) * s + (z - cz) * c) for x, z in pts]

    ui = []
    ui.append(inflate_outline("PhoneBadge", ellipse_outline(0.0640, 0.0640, 48, cz=0.1240),
                              depth=0.011, rings=6, power=0.5, mat=M["mint"], coll=coll,
                              flat_back=True))
    # checkmark: short down-right stroke + long up-right stroke
    ui.append(inflate_outline(
        "PhoneTickA",
        rot2(rounded_rect_outline(0.0292, 0.0123, 0.0058, 5, cx=-0.0210, cz=0.1148),
             math.radians(-52), -0.0210, 0.1148),
        depth=0.008, rings=4, power=0.5, mat=M["white_face"], coll=coll, flat_back=True))
    ui.append(inflate_outline(
        "PhoneTickB",
        rot2(rounded_rect_outline(0.0584, 0.0123, 0.0058, 5, cx=0.0100, cz=0.1296),
             math.radians(44), 0.0100, 0.1296),
        depth=0.008, rings=4, power=0.5, mat=M["white_face"], coll=coll, flat_back=True))
    for i, (bw, bz) in enumerate(((0.1860, 0.0192), (0.1478, -0.0392))):
        ui.append(inflate_outline(f"PhoneBar{i}",
                                  rounded_rect_outline(bw, 0.0187, 0.0092, 6, cz=bz),
                                  depth=0.007, rings=4, power=0.5, mat=M["mint_dark"],
                                  coll=coll, flat_back=True))
    for i, hx in enumerate((0.0,)):
        ui.append(inflate_outline(f"PhoneHeart{i}",
                                  heart_outline(0.0474, 0.0420, 48, cx=hx, cz=-0.1213),
                                  depth=0.006, rings=4, power=0.5, mat=M["pink"],
                                  coll=coll, flat_back=True))
    for u in ui:
        off = 0.0235 if u.name.startswith("PhoneTick") else 0.0165
        for v in u.data.vertices:
            v.co.y -= off
        u.data.update()
        u.matrix_world = ph_m
        subsurf(u, 1, 2)
        out[u.name] = u

    # ---------------- legs / boots ---------------- #
    for sx, tag in ((1, "L"), (-1, "R")):
        hip = (sx * LEG_X, 0.0, HIP_Z)
        knee = (sx * (LEG_X + 0.028), 0.0, KNEE_Z)
        ank = (sx * (LEG_X + 0.046), -0.010, ANK_Z)
        th = limb(f"Thigh_{tag}", hip, knee, 0.0955, 0.0760, M["white"], coll, over=0.012)
        subsurf(th, 2, 3)
        out[th.name] = th
        sh = limb(f"Shin_{tag}", knee, ank, 0.0760, 0.0735, M["white"], coll, over=0.012)
        subsurf(sh, 2, 3)
        out[sh.name] = sh
        kn = ring_on(f"KneeTrim_{tag}", hip, knee, 0.97, 0.0840, 0.052, M["mint"], coll)
        subsurf(kn, 2, 2)
        out[kn.name] = kn

        # boot: chunky rounded sneaker, toe pushed forward (-Y)
        bx = sx * 0.2140
        boot = quad_sphere(f"Boot_{tag}", radii=(0.1740, 0.2020, 0.1270),
                           loc=(bx, -0.0620, 0.1240), n=20, power=2.55,
                           mat=M["white"], coll=coll)
        for v in boot.data.vertices:
            t = v.co.z / 0.1050                 # -1 sole .. +1 ankle
            yy = v.co.y / 0.2120                # -1 toe .. +1 heel
            k = 1.0 - 0.30 * max(0.0, t) ** 1.15
            v.co.x *= k
            # taper the toe, keep the heel full
            v.co.x *= 1.0 - 0.20 * max(0.0, -yy) ** 2.2
            if yy < 0:                          # lift the toe box a little
                v.co.z += 0.030 * (-yy) ** 2.0
            v.co.y *= 1.0 - 0.22 * max(0.0, t) ** 1.4
            if v.co.z < -0.070:                 # flatten the sole
                v.co.z = -0.070 - (v.co.z + 0.070) * 0.16
        boot.data.update()
        boot.rotation_euler = Euler((0, 0, math.radians(-9 * sx)), "XYZ")
        subsurf(boot, 2, 3)
        out[boot.name] = boot

        sole = quad_sphere(f"Sole_{tag}", radii=(0.1720, 0.2010, 0.0410),
                           loc=(bx, -0.0620, 0.0430), n=16, power=2.9,
                           mat=M["mint"], coll=coll)
        for v in sole.data.vertices:
            if v.co.z < -0.016:
                v.co.z = -0.016 - (v.co.z + 0.016) * 0.14
        sole.data.update()
        sole.rotation_euler = Euler((0, 0, math.radians(-9 * sx)), "XYZ")
        subsurf(sole, 2, 3)
        out[sole.name] = sole

        strap = quad_sphere(f"BootTrim_{tag}", radii=(0.1585, 0.1830, 0.0320),
                            loc=(bx, -0.0330, 0.1930), n=18, power=2.7,
                            mat=M["mint"], coll=coll)
        strap.rotation_euler = Euler((0, 0, math.radians(-9 * sx)), "XYZ")
        subsurf(strap, 2, 3)
        out[strap.name] = strap

        buckle = inflate_outline(f"BootBuckle_{tag}",
                                 rounded_rect_outline(0.0760, 0.0620, 0.0190, 8,
                                                      cx=bx - sx * 0.0150, cz=0.1965),
                                 depth=0.022, rings=5, power=0.55,
                                 mat=M["white_face"], coll=coll, flat_back=True)
        for v in buckle.data.vertices:
            v.co.y -= 0.1880
        buckle.data.update()
        subsurf(buckle, 2, 2)
        out[buckle.name] = buckle
    return out


def build_cape(M, coll):
    """Flowing cape: mint outer shell (away from camera), pink lining inside.

    Built as an explicit double-sided sheet so the lining material is
    deterministic (Solidify's material_offset depends on winding).
    """
    nu, nv = 56, 48

    def surf(u, v):
        w = 0.250 + 0.400 * (v ** 0.62)
        x = u * w - 0.230 * (v ** 1.70)
        z = 0.8850 - v * (0.5900 + 0.0850 * (1.0 - u * u))
        # hem lifts on the viewer-right, drops on the swept viewer-left
        z += 0.135 * (v ** 1.80) * max(0.0, u)
        y = 0.2450 + 0.3350 * (v ** 1.15) - 0.3250 * (u * u) * (0.28 + 0.72 * v)
        # travelling folds, deepening toward the hem
        fold = (0.105 * math.sin(u * 4.2 + 0.42)
                + 0.046 * math.sin(u * 8.6 - 0.95)) * (v ** 1.10)
        y += fold
        z += (0.095 * math.cos(u * 4.2 + 0.42)
              + 0.038 * math.cos(u * 8.6 - 0.95)) * (v ** 1.70)
        # scalloped hem + gentle sideways sway
        z += 0.055 * math.sin(u * 5.1 + 1.15) * (v ** 2.20)
        x += (0.085 * math.sin(v * 3.0 + u * 1.05)) * v
        return Vector((x, y, z))

    def nrm(u, v):
        d = 1.0 / 96.0
        du = surf(min(1.0, u + d), v) - surf(max(-1.0, u - d), v)
        dv = surf(u, min(1.0, v + d)) - surf(u, max(0.0, v - d))
        n = du.cross(dv)
        if n.length < 1e-9:
            return Vector((0.0, 1.0, 0.0))
        n.normalize()
        if n.y < 0:
            n = -n
        return n

    T = 0.0135
    outer, inner = [], []
    for j in range(nv + 1):
        v = j / nv
        for i in range(nu + 1):
            u = -1.0 + 2.0 * i / nu
            p = surf(u, v)
            n = nrm(u, v)
            outer.append(tuple(p + n * T))
            inner.append(tuple(p - n * T))
    no = len(outer)
    verts = outer + inner
    faces_mint, faces_pink = [], []
    for j in range(nv):
        for i in range(nu):
            a = j * (nu + 1) + i
            faces_mint.append([a, a + nu + 1, a + nu + 2, a + 1])
            b = no + a
            faces_pink.append([b, b + 1, b + nu + 2, b + nu + 1])
    # rim strip closing the two sheets (mint)
    rim = []
    for i in range(nu):                       # hem
        a = nv * (nu + 1) + i
        rim.append([a, a + 1, no + a + 1, no + a])
    for j in range(nv):                       # left + right edges
        a = j * (nu + 1)
        rim.append([a, no + a, no + a + nu + 1, a + nu + 1])
        b = j * (nu + 1) + nu
        rim.append([b, b + nu + 1, no + b + nu + 1, no + b])
    cape = mesh_from("Cape", verts, faces_mint + rim + faces_pink,
                     M["mint"], coll=coll)
    cape.data.materials.append(M["pink_lining"])
    n_out = len(faces_mint)
    n_mint = len(faces_mint) + len(rim)
    # Outer (back) sheet stays mint everywhere: where the cape curls it reads as
    # the mint shell rim that frames the pink lining in the reference.
    for k, poly in enumerate(cape.data.polygons):
        if k < n_mint:
            poly.material_index = 0
        else:
            j = (k - n_mint) // nu
            i = (k - n_mint) % nu
            u = abs(-1.0 + 2.0 * i / nu)
            v = j / nv
            poly.material_index = 0 if (u > 0.940 or v < 0.155 or v > 0.945) else 1
    cape.data.update()
    subsurf(cape, 2, 3)
    return {"Cape": cape}
