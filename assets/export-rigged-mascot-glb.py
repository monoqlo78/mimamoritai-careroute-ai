"""Export the isolated rigged mascot project as a web-ready animated GLB."""

from pathlib import Path

import bpy


ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / "line-mimamori-mascot-rigged.blend"
OUTPUT = ROOT.parent / "src" / "MimamoriTai.Web" / "wwwroot" / "models" / "mimamori-owl-rigged.glb"

OUTPUT.parent.mkdir(parents=True, exist_ok=True)
bpy.ops.wm.open_mainfile(filepath=str(SOURCE))

bpy.ops.export_scene.gltf(
    filepath=str(OUTPUT),
    export_format="GLB",
    use_active_collection=False,
    export_animations=True,
    export_skins=True,
    export_morph=True,
    export_yup=True,
    export_apply=False,
)

print(f"EXPORTED_GLTF={OUTPUT}")
