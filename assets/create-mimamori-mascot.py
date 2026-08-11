import bpy
import math
from mathutils import Vector
from pathlib import Path


ROOT = Path(r"C:\Users\msoga\OneDrive - Smart Designer\Projects\見守り隊")
OUTPUT = ROOT / "assets" / "line-mimamori-mascot.png"
BLEND = ROOT / "assets" / "line-mimamori-mascot.blend"


def material(name, color, roughness=0.72):
    mat = bpy.data.materials.new(name)
    mat.diffuse_color = (*color, 1.0)
    mat.use_nodes = True
    shader = next(
        (node for node in mat.node_tree.nodes if node.type == "BSDF_PRINCIPLED"),
        None,
    )
    if shader:
        shader.inputs["Base Color"].default_value = (*color, 1.0)
        shader.inputs["Roughness"].default_value = roughness
    return mat


def smooth(obj):
    if obj.type == "MESH":
        for polygon in obj.data.polygons:
            polygon.use_smooth = True
    return obj


def uv_sphere(name, location, scale, mat, segments=64, rings=32):
    bpy.ops.mesh.primitive_uv_sphere_add(
        segments=segments,
        ring_count=rings,
        location=location,
    )
    obj = bpy.context.object
    obj.name = name
    obj.scale = scale
    obj.data.materials.append(mat)
    return smooth(obj)


def cube(name, location, scale, mat, bevel=0.16, rotation=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(location=location, rotation=rotation)
    obj = bpy.context.object
    obj.name = name
    obj.scale = scale
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    modifier = obj.modifiers.new("Soft edges", "BEVEL")
    modifier.width = bevel
    modifier.segments = 5
    obj.data.materials.append(mat)
    return smooth(obj)


def cone(name, location, radius, depth, mat, rotation=(0, 0, 0), vertices=32):
    bpy.ops.mesh.primitive_cone_add(
        vertices=vertices,
        radius1=radius,
        radius2=0,
        depth=depth,
        location=location,
        rotation=rotation,
    )
    obj = bpy.context.object
    obj.name = name
    obj.data.materials.append(mat)
    return smooth(obj)


def heart(name, location, scale, mat):
    vertices = []
    for i in range(96):
        t = (2 * math.pi * i) / 96
        x = 16 * math.sin(t) ** 3
        z = (
            13 * math.cos(t)
            - 5 * math.cos(2 * t)
            - 2 * math.cos(3 * t)
            - math.cos(4 * t)
        )
        vertices.append((x / 16, 0, z / 16))

    mesh = bpy.data.meshes.new(f"{name}Mesh")
    mesh.from_pydata(vertices, [], [list(range(len(vertices)))])
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.location = location
    obj.scale = (scale, scale * 0.4, scale)
    obj.data.materials.append(mat)

    solidify = obj.modifiers.new("Plump depth", "SOLIDIFY")
    solidify.thickness = 0.72
    solidify.offset = 0
    bevel = obj.modifiers.new("Soft edge", "BEVEL")
    bevel.width = 0.16
    bevel.segments = 5
    return smooth(obj)


def look_at(obj, target):
    direction = Vector(target) - obj.location
    obj.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()


# Clean scene.
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
for datablocks in (
    bpy.data.meshes,
    bpy.data.curves,
    bpy.data.materials,
    bpy.data.cameras,
    bpy.data.lights,
):
    for block in list(datablocks):
        if block.users == 0:
            datablocks.remove(block)

# Warm, calm identity suitable for older adults and family members.
brown = material("Guardian brown", (0.31, 0.17, 0.10))
mid_brown = material("Warm feather", (0.55, 0.32, 0.18))
cream = material("Soft cream", (0.95, 0.82, 0.61))
white = material("Eye white", (0.99, 0.98, 0.93))
charcoal = material("Kind eyes", (0.055, 0.045, 0.04), 0.5)
amber = material("Beak and feet", (0.96, 0.53, 0.14))
sage = material("Mimamori green", (0.20, 0.52, 0.36))
sage_light = material("Mimamori green light", (0.55, 0.76, 0.58))
rose = material("Caring heart", (0.83, 0.18, 0.29), 0.56)
blush = material("Cheek blush", (0.95, 0.48, 0.48))

# Body and head.
uv_sphere("Body", (0, 0.05, 1.65), (1.34, 0.72, 1.48), mid_brown)
uv_sphere("Belly", (0, -0.59, 1.48), (0.94, 0.20, 1.04), cream)
uv_sphere("Head", (0, 0, 3.25), (1.29, 0.72, 1.04), mid_brown)

# Ear tufts create the owl silhouette.
cone("Left ear", (-0.83, 0.02, 4.13), 0.43, 1.12, mid_brown, rotation=(0.05, -0.20, -0.12))
cone("Right ear", (0.83, 0.02, 4.13), 0.43, 1.12, mid_brown, rotation=(0.05, 0.20, 0.12))

# Soft facial discs, eyes, highlights, and cheeks.
for side in (-1, 1):
    x = 0.53 * side
    uv_sphere(f"Face disc {side}", (x, -0.61, 3.30), (0.63, 0.18, 0.66), cream)
    uv_sphere(f"Eye {side}", (x, -0.78, 3.34), (0.255, 0.13, 0.30), charcoal)
    uv_sphere(f"Eye highlight {side}", (x - 0.075, -0.90, 3.47), (0.075, 0.05, 0.09), white, 32, 16)
    uv_sphere(f"Cheek {side}", (0.88 * side, -0.77, 3.02), (0.16, 0.055, 0.09), blush, 32, 16)

cone(
    "Beak",
    (0, -0.93, 3.02),
    0.24,
    0.55,
    amber,
    rotation=(math.radians(90), 0, math.radians(45)),
    vertices=4,
)

# Green scarf ties the mascot to health and reassurance.
bpy.ops.mesh.primitive_torus_add(
    major_radius=0.86,
    minor_radius=0.14,
    major_segments=64,
    minor_segments=20,
    location=(0, -0.43, 2.32),
    rotation=(math.radians(90), 0, 0),
)
scarf = bpy.context.object
scarf.name = "Green scarf"
scarf.scale = (1.0, 1.0, 0.65)
scarf.data.materials.append(sage)
smooth(scarf)
cube("Scarf tail", (0.60, -0.74, 1.94), (0.18, 0.08, 0.48), sage, 0.12, rotation=(0, 0, -0.24))

# Wings reach forward around the heart.
uv_sphere("Left wing", (-1.13, -0.20, 1.83), (0.52, 0.30, 1.05), brown)
uv_sphere("Right wing", (1.13, -0.20, 1.83), (0.52, 0.30, 1.05), brown)
for name, x, rotation in (
    ("Left hand", -0.52, -0.32),
    ("Right hand", 0.52, 0.32),
):
    wing_tip = uv_sphere(name, (x, -1.02, 1.78), (0.37, 0.20, 0.62), mid_brown)
    wing_tip.rotation_euler[1] = rotation

heart("Care heart", (0, -1.18, 1.50), 0.66, rose)

# Feet ground the character.
for side in (-1, 1):
    uv_sphere(f"Foot {side}", (0.52 * side, -0.35, 0.34), (0.50, 0.46, 0.22), amber, 48, 24)

# Small shield pin: a reassuring service mark rather than a medical symbol.
cube("Shield pin", (0, -1.02, 2.13), (0.19, 0.08, 0.21), sage_light, 0.09)
cube("Shield mark vertical", (0, -1.115, 2.13), (0.045, 0.025, 0.13), white, 0.025)
cube("Shield mark horizontal", (0, -1.12, 2.13), (0.13, 0.025, 0.045), white, 0.025)

# Camera and soft studio lighting.
bpy.ops.object.camera_add(location=(0, -12.8, 2.38))
camera = bpy.context.object
camera.name = "Mascot Camera"
camera.data.type = "ORTHO"
camera.data.ortho_scale = 5.75
look_at(camera, (0, 0, 2.2))
bpy.context.scene.camera = camera

def area_light(name, location, energy, size, color):
    bpy.ops.object.light_add(type="AREA", location=location)
    light = bpy.context.object
    light.name = name
    light.data.energy = energy
    light.data.shape = "DISK"
    light.data.size = size
    light.data.color = color
    look_at(light, (0, 0, 2.0))


area_light("Key", (-4.5, -6.5, 7.5), 900, 5.0, (1.0, 0.82, 0.65))
area_light("Fill", (4.0, -4.5, 4.0), 650, 4.0, (0.70, 0.88, 1.0))
area_light("Rim", (0, 2.0, 6.5), 1100, 3.0, (1.0, 0.74, 0.43))

scene = bpy.context.scene
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = 1024
scene.render.resolution_y = 1024
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA"
scene.render.film_transparent = True
scene.render.filepath = str(OUTPUT)
scene.render.resolution_percentage = 100
scene.render.image_settings.color_depth = "8"
scene.view_settings.look = "AgX - Medium High Contrast"

scene.world.color = (0.025, 0.025, 0.025)

bpy.ops.wm.save_as_mainfile(filepath=str(BLEND))
bpy.ops.render.render(write_still=True)
print({"mascot": str(OUTPUT), "blend": str(BLEND)})
