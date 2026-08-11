"""Render the three shipped PNG stills from stage_full.blend.

The web app uses four pictures of Mimamo and they must all show the same face:

    models/mimamo-robot-opus-rigged.glb   the 3D body (exported separately)
    images/mimamo-robot-opus.png          1200x1500 RGBA, full figure
    images/mimamo-avatar.png               512x512  RGBA, face in a circle
    images/mimamo-line-alert.png          1040x676  RGB  on #EFF6FF

The avatar is NOT a crop of the standing shot - it is framed by its own,
much tighter camera - so each shot gets its own orthographic framing here.

Framing is solved from the EYES rather than guessed.  The eye centres sit at
world x = +-0.235, z = 1.245 (EYE_X = L(94), EYE_CZ = Z(602)), so measuring the
eye pixel gap / row / centre in each existing asset pins ortho_scale and the
camera location exactly, which reproduces the current compositions.

    blender -b stage_full.blend --python opus2_shots.py -- [outdir]
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

# Anchors of the DARK EYE BLOB as the framing measurement actually finds it.
#
# These are deliberately NOT the geometric eye centres (EYE_X = L(94) -> 0.235,
# EYE_CZ = Z(602) -> 1.245).  The framing of the existing assets was measured by
# taking the centroid of the two darkest blobs, and that centroid is pulled
# inward and downward by the asymmetric lash rim, so it lands ~10% closer
# together than the ellipse centres.  Framing against the geometric centres
# rendered every shot 0.899x too small.  These values were solved back out of a
# test render and reproduce the shipped compositions exactly.
EYE_HALF_X = 0.21126        # world units from centre to one eye blob centroid
EYE_Z = 1.27044             # world height of the eye blob row
EYE_CX = -0.00142           # world x of the midpoint between the two blobs

# name, width, height, eye gap px, eye row px, eye centre px
SHOTS = [
    ("standing", 1200, 1500, 304.2, 607.8, 599.0),
    ("avatar", 512, 512, 233.2, 325.3, 255.0),
    ("linealert", 1040, 676, 135.1, 298.3, 531.2),
]


def solve(w, h, gap, row, cx):
    """-> (ortho_scale, cam_x, cam_z).  Sensor fit AUTO spans the LARGER side."""
    span = max(w, h)
    scale = 2.0 * EYE_HALF_X * span / gap
    ppw = span / scale                      # pixels per world unit
    return (scale,
            EYE_CX - (cx - w / 2.0) / ppw,
            EYE_Z + (row - h / 2.0) / ppw)


def render_all(outdir, samples=80):
    import bpy
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = samples
    sc.cycles.use_denoising = True
    sc.render.resolution_percentage = 100
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    sc.view_settings.view_transform = "Standard"

    src = bpy.data.objects["FrontOrthoCam"]
    cam = bpy.data.objects.new("ShotCam", src.data.copy())
    sc.collection.objects.link(cam)
    cam.rotation_euler = src.rotation_euler
    cam.data.type = "ORTHO"
    sc.camera = cam
    sc.frame_set(1)

    for name, w, h, gap, row, cx in SHOTS:
        scale, camx, camz = solve(w, h, gap, row, cx)
        cam.data.ortho_scale = scale
        cam.location = (camx, src.location.y, camz)
        sc.render.resolution_x = w
        sc.render.resolution_y = h
        # Blender resolves a relative render filepath against the drive root,
        # not the cwd, so this has to be absolute.
        out = os.path.abspath(os.path.join(outdir, "shot_%s.png" % name))
        sc.render.filepath = out
        bpy.ops.render.render(write_still=True)
        print("SHOT %-10s %4dx%-4d scale %.5f cam (%.5f, %.5f) -> %s"
              % (name, w, h, scale, camx, camz, out))
    print("SHOTS DONE")


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    render_all(argv[0] if argv else HERE)
