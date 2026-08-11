"""Full-body render of the current head parameters, for before/after review.

    blender -b --factory-startup --python render_full.py -- <outfile>
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

os.environ["OPUS_AUTORUN"] = "0"

import bpy  # noqa: E402

import opus2_build  # noqa: E402
import opus2_render  # noqa: E402

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
out = argv[0] if argv else os.path.join(HERE, "sweep", "full_new.png")

opus2_build.main(stage="full", outp=None, save=False)
bpy.context.scene.cycles.samples = 64
opus2_render.blender_pass(out)
print("FULL DONE", out)
