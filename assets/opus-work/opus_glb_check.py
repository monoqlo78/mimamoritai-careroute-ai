"""Parse a .glb container and report structural info without any Blender dependency."""
import json
import struct
import sys

path = sys.argv[1] if len(sys.argv) > 1 else r"..\mimamo-robot-opus-rigged.glb"

with open(path, "rb") as fh:
    magic, version, total = struct.unpack("<4sII", fh.read(12))
    assert magic == b"glTF", magic
    js = None
    bin_len = 0
    while fh.tell() < total:
        clen, ctype = struct.unpack("<I4s", fh.read(8))
        data = fh.read(clen)
        if ctype == b"JSON":
            js = json.loads(data.decode("utf-8"))
        else:
            bin_len += clen

print("file            ", path)
print("glb version     ", version)
print("total bytes     ", total)
print("bin bytes       ", bin_len)
print("meshes          ", len(js.get("meshes", [])))
print("materials       ", len(js.get("materials", [])))
print("skins           ", len(js.get("skins", [])))
for s in js.get("skins", []):
    print("   skin joints ", len(s.get("joints", [])))
print("animations      ", len(js.get("animations", [])))
for a in js.get("animations", []):
    print("   anim '%s' channels=%d samplers=%d"
          % (a.get("name", "?"), len(a.get("channels", [])), len(a.get("samplers", []))))
print("images          ", len(js.get("images", [])))

tot_v = 0
tot_t = 0
morphs = 0
for m in js.get("meshes", []):
    for p in m.get("primitives", []):
        acc = js["accessors"][p["attributes"]["POSITION"]]
        tot_v += acc["count"]
        if "indices" in p:
            tot_t += js["accessors"][p["indices"]]["count"] // 3
        morphs += len(p.get("targets", []) or [])
        print("   prim attrs  ", sorted(p["attributes"].keys()),
              "targets=", len(p.get("targets", []) or []))
print("total verts     ", tot_v)
print("total tris      ", tot_t)
print("morph targets   ", morphs)
names = (js.get("meshes", [{}])[0].get("extras", {}) or {}).get("targetNames")
if names:
    print("morph names     ", names)
