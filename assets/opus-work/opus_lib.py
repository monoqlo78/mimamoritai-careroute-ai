"""Geometry / material helpers for the Mimamo opus rebuild.

All measurements originate from the canonical reference poster (1122x1402 px).
Poster pixel -> world mapping (front orthographic, Z up, character faces -Y):

    x_world = (x_ref - 555) * S
    z_world = (1100 - y_ref) * S     with S = 0.0025
"""
import math

import bmesh
import bpy
from mathutils import Euler, Matrix, Vector

S = 0.0025
FACE_CX = 555.0
FLOOR_Y = 1100.0

# Global tessellation multiplier. 1.0 = the authored density. Lowering it
# thins out every generated primitive at once - same silhouette, fewer control
# points - so the exported GLB stays inside its size budget.
DENSITY = 1.0


def set_density(d):
    global DENSITY
    DENSITY = float(d)


def _d(n, floor=6):
    return max(floor, int(round(n * DENSITY)))


def X(xr):
    return (xr - FACE_CX) * S


def Z(yr):
    return (FLOOR_Y - yr) * S


def L(px):
    return px * S


# --------------------------------------------------------------------------- #
# scene helpers
# --------------------------------------------------------------------------- #
def wipe_scene():
    for coll in list(bpy.data.collections):
        bpy.data.collections.remove(coll)
    for ob in list(bpy.data.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    for blk in (
        bpy.data.meshes,
        bpy.data.materials,
        bpy.data.armatures,
        bpy.data.actions,
        bpy.data.cameras,
        bpy.data.lights,
        bpy.data.images,
        bpy.data.curves,
        bpy.data.node_groups,
    ):
        for it in list(blk):
            try:
                blk.remove(it)
            except Exception:
                pass


def ensure_collection(name, parent=None, color=None):
    c = bpy.data.collections.get(name)
    if c is None:
        c = bpy.data.collections.new(name)
    p = parent or bpy.context.scene.collection
    if c.name not in [x.name for x in p.children]:
        p.children.link(c)
    if color:
        c.color_tag = color
    return c


def link(obj, coll):
    for c in list(obj.users_collection):
        c.objects.unlink(obj)
    coll.objects.link(obj)
    return obj


# --------------------------------------------------------------------------- #
# materials
# --------------------------------------------------------------------------- #
def make_material(
    name,
    base=(1, 1, 1),
    rough=0.35,
    metallic=0.0,
    coat=0.0,
    coat_rough=0.05,
    emission=None,
    emission_strength=1.0,
    alpha=1.0,
    subsurf=0.0,
    subsurf_color=None,
    use_vcol=False,
    image=None,
    ior=1.45,
    blend=False,
    backface_cull=False,
):
    mat = bpy.data.materials.get(name)
    if mat is None:
        mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    out.location = (600, 0)
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.location = (200, 0)
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])

    def setv(key, val):
        if key in bsdf.inputs:
            bsdf.inputs[key].default_value = val

    setv("Base Color", (*base, 1.0))
    setv("Roughness", rough)
    setv("Metallic", metallic)
    setv("IOR", ior)
    setv("Alpha", alpha)
    setv("Coat Weight", coat)
    setv("Coat Roughness", coat_rough)
    if subsurf:
        setv("Subsurface Weight", subsurf)
        setv("Subsurface Radius", (0.12, 0.06, 0.05))
        if subsurf_color:
            setv("Subsurface Radius", subsurf_color)
    if emission:
        setv("Emission Color", (*emission, 1.0))
        setv("Emission Strength", emission_strength)

    if use_vcol:
        ca = nt.nodes.new("ShaderNodeVertexColor")
        ca.location = (-260, 60)
        ca.layer_name = "Col"
        mix = nt.nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        mix.blend_type = "MULTIPLY"
        mix.location = (-40, 40)
        mix.inputs["Factor"].default_value = 1.0
        mix.inputs[6].default_value = (*base, 1.0)
        nt.links.new(ca.outputs["Color"], mix.inputs[7])
        nt.links.new(mix.outputs[2], bsdf.inputs["Base Color"])

    if image is not None:
        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.location = (-360, -40)
        tex.image = image
        tex.interpolation = "Cubic"
        tex.extension = "CLIP"
        nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        nt.links.new(tex.outputs["Alpha"], bsdf.inputs["Alpha"])

    if blend:
        try:
            mat.surface_render_method = "BLENDED"
        except Exception:
            pass
        for attr, val in (("blend_method", "BLEND"), ("shadow_method", "NONE")):
            if hasattr(mat, attr):
                try:
                    setattr(mat, attr, val)
                except Exception:
                    pass
    mat.use_backface_culling = backface_cull
    return mat


# --------------------------------------------------------------------------- #
# mesh construction
# --------------------------------------------------------------------------- #
def mesh_from(name, verts, faces, mat=None, smooth=True, coll=None):
    me = bpy.data.meshes.new(name)
    me.from_pydata([Vector(v) for v in verts], [], faces)
    me.validate(clean_customdata=False)
    me.update()
    ob = bpy.data.objects.new(name, me)
    (coll or bpy.context.scene.collection).objects.link(ob)
    if mat:
        me.materials.append(mat)
    if smooth:
        for p in me.polygons:
            p.use_smooth = True
    return ob


def add_modifier(ob, kind, **kw):
    m = ob.modifiers.new(kw.pop("name", kind.title()), kind)
    for k, v in kw.items():
        setattr(m, k, v)
    return m


def subsurf(ob, levels=2, render=3):
    m = add_modifier(ob, "SUBSURF", name="Subsurf")
    m.levels = levels
    m.render_levels = render
    m.use_limit_surface = True
    return m


# ---- primitive: quad sphere (cube -> sphere, pole free) --------------------- #
def quad_sphere_data(n=16, radii=(1, 1, 1), power=2.0):
    """Cube-sphere: n subdivisions per cube face."""
    verts = []
    faces = []
    index = {}

    def key(p):
        return (round(p[0], 6), round(p[1], 6), round(p[2], 6))

    def cube_point(axis, sign, u, v):
        a, b = (u * 2 - 1), (v * 2 - 1)
        if axis == 0:
            return (sign, a, b)
        if axis == 1:
            return (a, sign, b)
        return (a, b, sign)

    def add(p):
        # map cube point onto superellipsoid of given power
        x, y, z = p
        d = (abs(x) ** power + abs(y) ** power + abs(z) ** power) ** (1.0 / power)
        x, y, z = x / d, y / d, z / d
        k = key((x, y, z))
        if k not in index:
            index[k] = len(verts)
            verts.append((x * radii[0], y * radii[1], z * radii[2]))
        return index[k]

    for axis in (0, 1, 2):
        for sign in (-1, 1):
            for i in range(n):
                for j in range(n):
                    us = [i / n, (i + 1) / n]
                    vs = [j / n, (j + 1) / n]
                    quad = [
                        add(cube_point(axis, sign, us[0], vs[0])),
                        add(cube_point(axis, sign, us[1], vs[0])),
                        add(cube_point(axis, sign, us[1], vs[1])),
                        add(cube_point(axis, sign, us[0], vs[1])),
                    ]
                    if len(set(quad)) < 4:
                        continue
                    # consistent winding
                    p0 = Vector(verts[quad[0]])
                    p1 = Vector(verts[quad[1]])
                    p2 = Vector(verts[quad[2]])
                    nrm = (p1 - p0).cross(p2 - p0)
                    if nrm.dot(p0) < 0:
                        quad.reverse()
                    faces.append(quad)
    return verts, faces


def quad_sphere(name, radii=(1, 1, 1), loc=(0, 0, 0), n=14, power=2.0, mat=None, coll=None):
    v, f = quad_sphere_data(_d(n, 5), radii, power)
    ob = mesh_from(name, v, f, mat, coll=coll)
    ob.location = loc
    return ob


# ---- primitive: outline inflate (pillow / puffy solid from a 2D outline) ---- #
def inflate_outline(
    name,
    outline,
    depth,
    rings=9,
    power=0.55,
    mat=None,
    loc=(0, 0, 0),
    rot=(0, 0, 0),
    coll=None,
    flat_back=False,
    shrink=0.0,
):
    """Build a pillow solid: 2D outline (list of (x, z)) inflated along +/-Y."""
    if DENSITY < 0.999 and len(outline) > 24:
        keep = max(24, int(round(len(outline) * DENSITY)))
        step = len(outline) / float(keep)
        outline = [outline[min(len(outline) - 1, int(round(i * step)))]
                   for i in range(keep)]
    rings = max(5, int(round(rings * (DENSITY ** 0.5))))
    n = len(outline)
    verts = []
    faces = []
    cx = sum(p[0] for p in outline) / n
    cz = sum(p[1] for p in outline) / n
    ring_ts = [i / (rings - 1) for i in range(rings)]  # 0..1 -> -1..1
    layers = []
    for t in ring_ts:
        w = t * 2 - 1  # -1 (back) .. +1 (front)
        if flat_back and w < 0:
            scale = 1.0 if w < -0.999 else 1.0
            y = 0.0
        else:
            scale = max(1e-4, (1.0 - abs(w) ** 2.0) ** power)
            y = -w * depth
        if flat_back:
            scale = max(1e-4, (1.0 - max(0.0, w) ** 2.0) ** power) if w > 0 else 1.0
            y = -max(0.0, w) * depth
        sh = 1.0 - shrink * (1.0 - scale)
        idx = []
        for (px, pz) in outline:
            vx = cx + (px - cx) * scale * sh
            vz = cz + (pz - cz) * scale * sh
            idx.append(len(verts))
            verts.append((vx, y, vz))
        layers.append(idx)
    for r in range(rings - 1):
        a, b = layers[r], layers[r + 1]
        for i in range(n):
            j = (i + 1) % n
            faces.append([a[i], a[j], b[j], b[i]])
    # caps
    for ring, sgn in ((layers[0], -1), (layers[-1], 1)):
        c = len(verts)
        verts.append((cx, verts[ring[0]][1], cz))
        for i in range(n):
            j = (i + 1) % n
            faces.append([ring[i], ring[j], c][:: sgn])
    ob = mesh_from(name, verts, faces, mat, coll=coll)
    ob.location = loc
    ob.rotation_euler = Euler(rot, "XYZ")
    return ob


def ellipse_outline(a, b, n=96, cx=0.0, cz=0.0, squash_top=1.0, squash_bot=1.0):
    n = _d(n, 28)
    pts = []
    for i in range(n):
        t = i / n * 2 * math.pi
        x = math.cos(t) * a
        z = math.sin(t) * b
        z *= squash_top if z >= 0 else squash_bot
        pts.append((cx + x, cz + z))
    return pts


def heart_outline(w, h, n=128, cx=0.0, cz=0.0, plump=0.0):
    n = _d(n, 32)
    pts = []
    for i in range(n):
        t = i / n * 2 * math.pi
        x = 16 * math.sin(t) ** 3
        z = 13 * math.cos(t) - 5 * math.cos(2 * t) - 2 * math.cos(3 * t) - math.cos(4 * t)
        xn, zn = x / 16.0, z / 17.0
        if plump > 0.0:
            k = max(0.0, -zn) ** 1.25
            th = math.atan2(zn, xn)
            f = plump * k
            xn = xn * (1 - f) + math.cos(th) * 0.94 * f
            zn = zn * (1 - f) + math.sin(th) * 0.94 * f
        pts.append((cx + xn * (w / 2), cz + zn * (h / 2)))
    return pts


def octagon_outline(w, h, cut=0.30, cx=0.0, cz=0.0, smooth=2, dens=4):
    hw, hh = w / 2.0, h / 2.0
    cw, ch = hw * cut, hh * cut
    base = [(hw, hh - ch), (hw - cw, hh), (-(hw - cw), hh), (-hw, hh - ch),
            (-hw, -(hh - ch)), (-(hw - cw), -hh), (hw - cw, -hh), (hw, -(hh - ch))]
    pts = []
    for i in range(len(base)):
        ax, az = base[i]
        bx, bz = base[(i + 1) % len(base)]
        for j in range(dens):
            t = j / dens
            pts.append((ax + (bx - ax) * t, az + (bz - az) * t))
    for _ in range(smooth):                       # Chaikin corner rounding
        nxt = []
        for i in range(len(pts)):
            ax, az = pts[i]
            bx, bz = pts[(i + 1) % len(pts)]
            nxt.append((ax * 0.75 + bx * 0.25, az * 0.75 + bz * 0.25))
            nxt.append((ax * 0.25 + bx * 0.75, az * 0.25 + bz * 0.75))
        pts = nxt
    return [(cx + p[0], cz + p[1]) for p in pts]


def rounded_rect_outline(w, h, r, n_corner=10, cx=0.0, cz=0.0):
    n_corner = _d(n_corner, 4)
    r = min(r, w / 2 - 1e-4, h / 2 - 1e-4)
    hw, hh = w / 2 - r, h / 2 - r
    pts = []
    for (ox, oz, a0) in ((hw, hh, 0), (-hw, hh, 90), (-hw, -hh, 180), (hw, -hh, 270)):
        for i in range(n_corner + 1):
            a = math.radians(a0 + 90 * i / n_corner)
            pts.append((cx + ox + math.cos(a) * r, cz + oz + math.sin(a) * r))
    return pts


def smile_outline(half_w, up, down, n=80):
    """Open smile ('D' shape): flat-ish top arc, deep bottom arc."""
    n = _d(n, 24)
    top, bot = [], []
    for i in range(n + 1):
        t = -1 + 2 * i / n
        x = t * half_w
        k = max(0.0, 1 - t * t)
        top.append((x, up * (k ** 0.62)))
    for i in range(n + 1):
        t = 1 - 2 * i / n
        x = t * half_w
        k = max(0.0, 1 - t * t)
        bot.append((x, -down * (k ** 0.60)))
    return top[:-1] + bot[:-1]


# ---- conform a flat XZ feature onto the head ellipsoid --------------------- #
def project_to_ellipsoid(ob, center, radii, extra=0.0, bulge=1.0):
    """Keep the front-view (XZ) projection identical, ride the ellipsoid in Y."""
    cx, cy, cz = center
    rx, ry, rz = radii
    me = ob.data
    mw = ob.matrix_world.copy()
    inv = mw.inverted()
    for v in me.vertices:
        p = mw @ v.co
        u = (p.x - cx) / rx
        w = (p.z - cz) / rz
        k = 1.0 - u * u - w * w
        k = max(k, 0.0025)
        ysurf = cy - ry * math.sqrt(k) * bulge
        p.y = ysurf + p.y - extra
        v.co = inv @ p
    me.update()
    return ob


def surface_band(
    name,
    center,
    radii,
    theta_range,
    phi_fn,
    phi0_fn=None,
    nu=64,
    nv=10,
    offset=0.012,
    thickness=0.018,
    mat=None,
    coll=None,
):
    """Conforming band patch on an ellipsoid.

    theta = azimuth measured from -Y (front) around +Z; phi = polar from +Z.
    """
    cx, cy, cz = center
    rx, ry, rz = radii
    verts, faces = [], []
    grid_out, grid_in = [], []
    for i in range(nu + 1):
        t = theta_range[0] + (theta_range[1] - theta_range[0]) * i / nu
        p_hi = phi0_fn(t) if phi0_fn else 0.0
        p_lo = phi_fn(t)
        ro, ri = [], []
        for j in range(nv + 1):
            phi = p_hi + (p_lo - p_hi) * j / nv
            sx = math.sin(phi) * math.sin(t)
            sy = -math.sin(phi) * math.cos(t)
            sz = math.cos(phi)
            for off, dst in ((offset + thickness, ro), (offset, ri)):
                dst.append(len(verts))
                verts.append(
                    (
                        cx + sx * (rx + off),
                        cy + sy * (ry + off),
                        cz + sz * (rz + off),
                    )
                )
        grid_out.append(ro)
        grid_in.append(ri)
    for i in range(nu):
        for j in range(nv):
            faces.append([grid_out[i][j], grid_out[i + 1][j], grid_out[i + 1][j + 1], grid_out[i][j + 1]])
            faces.append([grid_in[i][j], grid_in[i][j + 1], grid_in[i + 1][j + 1], grid_in[i + 1][j]])
    for i in range(nu):  # rims along phi extremes
        for (g, jj, rev) in ((grid_out, 0, False), (grid_out, nv, True)):
            a, b = g[i][jj], g[i + 1][jj]
            c, d = grid_in[i + 1][jj], grid_in[i][jj]
            f = [a, b, c, d]
            faces.append(f[::-1] if rev else f)
    for j in range(nv):  # side caps
        for (i, rev) in ((0, True), (nu, False)):
            f = [grid_out[i][j], grid_out[i][j + 1], grid_in[i][j + 1], grid_in[i][j]]
            faces.append(f[::-1] if rev else f)
    return mesh_from(name, verts, faces, mat, coll=coll)


def revolve(name, profile, segments=64, mat=None, loc=(0, 0, 0), rot=(0, 0, 0), coll=None, cap=True):
    """Revolve a profile [(radius, z), ...] around Z."""
    segments = _d(segments, 10)
    verts, faces = [], []
    rings = []
    for (r, z) in profile:
        ring = []
        if r <= 1e-6:
            ring = [len(verts)] * segments
            verts.append((0, 0, z))
        else:
            for i in range(segments):
                a = i / segments * 2 * math.pi
                ring.append(len(verts))
                verts.append((math.cos(a) * r, math.sin(a) * r, z))
        rings.append(ring)
    for k in range(len(rings) - 1):
        a, b = rings[k], rings[k + 1]
        for i in range(segments):
            j = (i + 1) % segments
            q = [a[i], a[j], b[j], b[i]]
            q = list(dict.fromkeys(q))
            if len(q) >= 3:
                faces.append(q)
    if cap:
        for ring, rev in ((rings[0], True), (rings[-1], False)):
            if len(set(ring)) < 3:
                continue
            f = list(dict.fromkeys(ring))
            faces.append(f[::-1] if rev else f)
    ob = mesh_from(name, verts, faces, mat, coll=coll)
    ob.location = loc
    ob.rotation_euler = Euler(rot, "XYZ")
    return ob


def capsule(name, r0, r1, length, loc=(0, 0, 0), rot=(0, 0, 0), mat=None, coll=None, seg=32, cap_scale=1.0):
    prof = []
    steps = max(4, int(round(8 * (DENSITY ** 0.5))))
    for i in range(steps + 1):
        a = math.pi / 2 * i / steps
        prof.append((r0 * math.sin(a), -r0 * math.cos(a) * cap_scale))
    for i in range(1, steps + 1):
        a = math.pi / 2 * i / steps
        prof.append((r1 * math.cos(a), length + r1 * math.sin(a) * cap_scale))
    prof.insert(len(prof) - steps, (r1, length))
    return revolve(name, prof, seg, mat, loc, rot, coll)


def aim_matrix(head, tail, roll=0.0):
    """Matrix placing an object at `head` with its local +Z pointing at `tail`."""
    head, tail = Vector(head), Vector(tail)
    d = (tail - head)
    ln = d.length
    z = d.normalized()
    up = Vector((0, 0, 1))
    if abs(z.dot(up)) > 0.999:
        up = Vector((0, 1, 0))
    x = up.cross(z).normalized()
    y = z.cross(x).normalized()
    m = Matrix(((x.x, y.x, z.x, head.x), (x.y, y.y, z.y, head.y), (x.z, y.z, z.z, head.z), (0, 0, 0, 1)))
    if roll:
        m = m @ Matrix.Rotation(roll, 4, "Z")
    return m, ln


def limb(name, head, tail, r0, r1, mat, coll, seg=28, roll=0.0, cap_scale=1.0, over=0.0):
    m, ln = aim_matrix(head, tail, roll)
    ob = capsule(name, r0, r1, ln + over, mat=mat, coll=coll, seg=seg, cap_scale=cap_scale)
    ob.matrix_world = m
    return ob


def ring_on(name, head, tail, t, r, width, mat, coll, seg=40, taper=1.0):
    """Trim ring placed at parameter t along a limb axis."""
    m, ln = aim_matrix(head, tail)
    prof = [
        (r * 0.86, -width / 2),
        (r, -width / 2 * 0.55),
        (r * taper, width / 2 * 0.55),
        (r * 0.86 * taper, width / 2),
    ]
    ob = revolve(name, prof, seg, mat, coll=coll)
    ob.matrix_world = m @ Matrix.Translation((0, 0, ln * t))
    return ob


def set_vertex_colors(ob, fn, name="Col"):
    me = ob.data
    if name not in me.color_attributes:
        me.color_attributes.new(name=name, type="FLOAT_COLOR", domain="POINT")
    ca = me.color_attributes[name]
    me.color_attributes.active_color = ca
    mw = ob.matrix_world
    for i, v in enumerate(me.vertices):
        ca.data[i].color = fn(mw @ v.co, v.co)
    return ob


def shade(ob, angle=None):
    for p in ob.data.polygons:
        p.use_smooth = True
    if angle is not None:
        try:
            ob.data.use_auto_smooth = True
            ob.data.auto_smooth_angle = angle
        except Exception:
            add_modifier(ob, "SMOOTH_BY_ANGLE") if "SMOOTH_BY_ANGLE" in dir() else None
    return ob


def bevel(ob, width=0.006, segments=3, angle=math.radians(45)):
    m = add_modifier(ob, "BEVEL", name="Bevel")
    m.width = width
    m.segments = segments
    m.limit_method = "ANGLE"
    m.angle_limit = angle
    m.harden_normals = False
    return m


def apply_all(ob):
    bpy.context.view_layer.objects.active = ob
    for m in list(ob.modifiers):
        try:
            bpy.ops.object.modifier_apply(modifier=m.name)
        except Exception:
            ob.modifiers.remove(m)
    return ob


def join(objs, name):
    objs = [o for o in objs if o and o.name in bpy.data.objects]
    if not objs:
        return None
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    ob.name = name
    ob.data.name = name
    return ob


# ---- exact projection onto the real head shell (ray-cast) ------------------ #
def _head_ray(head, dg, xw, zw):
    import bpy
    hinv = head.matrix_world.inverted()
    o = hinv @ Vector((xw, -4.0, zw))
    d = (hinv.to_3x3() @ Vector((0.0, 1.0, 0.0)))
    d.normalize()
    try:
        hit, loc, nor, idx = head.ray_cast(o, d, distance=9.0, depsgraph=dg)
    except TypeError:
        hit, loc, nor, idx = head.ray_cast(o, d, distance=9.0)
    if not hit:
        return None
    return (head.matrix_world @ loc).y


def project_to_head(ob, head, center, radii, extra=0.0):
    """Keep the front-view (XZ) projection, ride the *actual* head surface in Y."""
    import bpy
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    cx, cy, cz = center
    rx, ry, rz = radii
    me = ob.data
    mw = ob.matrix_world.copy()
    inv = mw.inverted()
    cache = {}
    for v in me.vertices:
        p = mw @ v.co
        key = (round(p.x, 5), round(p.z, 5))
        ys = cache.get(key)
        if ys is None:
            ys = _head_ray(head, dg, p.x, p.z)
            if ys is None:
                u = (p.x - cx) / rx
                w = (p.z - cz) / rz
                k = max(1.0 - u * u - w * w, 0.0025)
                ys = cy - ry * math.sqrt(k)
            cache[key] = ys
        p.y = ys + p.y - extra
        v.co = inv @ p
    me.update()
    return ob


def head_half_width(head, zw, xmax=0.70):
    """Silhouette half-width of the real head shell at world height zw."""
    import bpy
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    if _head_ray(head, dg, 0.0, zw) is None:
        return 0.0
    lo, hi = 0.0, xmax
    for _ in range(26):
        mid = (lo + hi) * 0.5
        if _head_ray(head, dg, mid, zw) is None:
            hi = mid
        else:
            lo = mid
    return lo
