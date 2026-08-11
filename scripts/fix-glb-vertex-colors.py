"""Strip the broken all-black COLOR_0 vertex-color attribute from a GLB.

The Blender export of the Mimamo character wrote a COLOR_0 stream that is
entirely black. glTF multiplies COLOR_0 by baseColorFactor, so every material
rendered black in three.js. The per-material baseColorFactor values are correct,
so removing COLOR_0 restores the intended mint / white / pink palette.

Usage: python scripts/fix-glb-vertex-colors.py <input.glb> <output.glb>
"""

import json
import struct
import sys


def strip_color0(src: str, dst: str) -> None:
    data = open(src, "rb").read()
    json_len = struct.unpack("<I", data[12:16])[0]
    gltf = json.loads(data[20 : 20 + json_len].decode("utf-8"))
    bin_chunk = data[20 + json_len :]

    removed = 0
    for mesh in gltf.get("meshes", []):
        for prim in mesh.get("primitives", []):
            if prim.get("attributes", {}).pop("COLOR_0", None) is not None:
                removed += 1
    if removed == 0:
        raise SystemExit("COLOR_0 not found; nothing to do")

    new_json = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
    new_json += b" " * ((4 - len(new_json) % 4) % 4)

    out = bytearray()
    out += struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(new_json) + len(bin_chunk))
    out += struct.pack("<II", len(new_json), 0x4E4F534A) + new_json
    out += bin_chunk
    open(dst, "wb").write(out)
    print(f"removed COLOR_0 from {removed} primitives -> {dst} ({len(out)} bytes)")


if __name__ == "__main__":
    strip_color0(sys.argv[1], sys.argv[2])
