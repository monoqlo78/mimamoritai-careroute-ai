import bpy, sys
sc = bpy.context.scene
# make sure the reference collection is visible in the viewport
for area in bpy.context.screen.areas:
    if area.type == "VIEW_3D":
        for sp in area.spaces:
            if sp.type == "VIEW_3D":
                sp.region_3d.view_perspective = "CAMERA"
                sp.shading.type = "MATERIAL"
                sp.overlay.show_overlays = True
cam = bpy.data.objects.get("FrontOrthoCam")
if cam:
    sc.camera = cam
print("GUI SETUP OK")
