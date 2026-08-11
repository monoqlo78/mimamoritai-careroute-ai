import bpy
def go():
    sc = bpy.context.scene
    cam = bpy.data.objects.get("FrontOrthoCam")
    if cam:
        sc.camera = cam
        cam.data.ortho_scale = 3.10
        cam.location.z = 1.16
    for ob in bpy.data.objects:
        if "Reference" in ob.name:
            ob.hide_viewport = False
            ob.hide_set(False)
    vl = bpy.context.view_layer
    for lc in vl.layer_collection.children:
        if "REFERENCE" in lc.name:
            lc.exclude = False
            lc.hide_viewport = False
    for win in bpy.context.window_manager.windows:
        for area in win.screen.areas:
            if area.type == "VIEW_3D":
                sp = area.spaces[0]
                sp.region_3d.view_perspective = "CAMERA"
                sp.shading.type = "MATERIAL"
                sp.overlay.show_overlays = True
    return None
bpy.app.timers.register(go, first_interval=5.0)
