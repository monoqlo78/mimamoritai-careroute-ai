"""A/B sweep round 2: separate the outer head width from the jaw taper.

    blender -b --factory-startup --python sweep_cheek2.py
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

# name, HEAD_N_BOT, FACE_N, FACE_HW, HEAD_HW
VARIANTS = [
    ("e_w215_n220", 2.20, 2.00, 164.0, 215.0),
    ("f_w215_n230", 2.30, 1.95, 158.0, 215.0),
    ("g_w220_n220", 2.20, 1.90, 155.0, 220.0),
    ("h_w220_n210", 2.10, 1.95, 160.0, 220.0),
]

OUT = os.path.join(HERE, "sweep")
os.makedirs(OUT, exist_ok=True)

for name, n_bot, face_n, face_hw, head_hw in VARIANTS:
    os.environ["MIMAMO_HEAD_N_BOT"] = str(n_bot)
    os.environ["MIMAMO_FACE_N"] = str(face_n)
    os.environ["MIMAMO_FACE_HW"] = str(face_hw)
    os.environ["MIMAMO_HEAD_HW"] = str(head_hw)
    opus2_build.main(stage="head", outp=None, save=False)
    bpy.context.scene.cycles.samples = 32
    path = os.path.join(OUT, name + ".png")
    opus2_render.blender_pass(path)
    print("VARIANT", name, n_bot, face_n, face_hw, head_hw, "->", path)
