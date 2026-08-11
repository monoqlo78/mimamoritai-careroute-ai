"""A/B sweep for the head silhouette / face-plate knobs.

    blender -b --factory-startup --python sweep_cheek.py

Builds the head stage once per variant and renders a front orthographic frame
so the cheek line can be compared against the poster.
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

VARIANTS = [
    ("a_current", 3.00, 2.12, 168.0),
    ("b_nbot240", 2.40, 2.12, 168.0),
    ("c_nbot220", 2.20, 2.00, 164.0),
    ("d_nbot210", 2.10, 1.95, 160.0),
]

OUT = os.path.join(HERE, "sweep")
os.makedirs(OUT, exist_ok=True)

for name, n_bot, face_n, face_hw in VARIANTS:
    os.environ["MIMAMO_HEAD_N_BOT"] = str(n_bot)
    os.environ["MIMAMO_FACE_N"] = str(face_n)
    os.environ["MIMAMO_FACE_HW"] = str(face_hw)
    opus2_build.main(stage="head", outp=None, save=False)
    sc = bpy.context.scene
    sc.cycles.samples = 32
    path = os.path.join(OUT, name + ".png")
    opus2_render.blender_pass(path)
    print("VARIANT", name, n_bot, face_n, face_hw, "->", path)
