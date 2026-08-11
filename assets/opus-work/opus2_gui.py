import bpy

def go():
    sc = bpy.context.scene
    cam = bpy.data.objects.get("FrontOrthoCam")
    if cam:
        sc.camera = cam
        cam.data.ortho_scale = 2.25
        cam.location.z = 1.05
    for ob in bpy.data.objects:
        if "Reference" in ob.name:
            ob.hide_viewport = False
            ob.hide_set(False)
    vl = bpy.context.view_layer
    for lc in vl.layer_collection.children:
        if "REFERENCE" in lc.name:
            lc.exclude = False
            lc.hide_viewport = False
    for a in bpy.context.window_manager.windows[0].screen.areas:
        if a.type == "VIEW_3D":
            sp = a.spaces[0]
            sp.region_3d.view_perspective = "CAMERA"
            sp.shading.type = "MATERIAL"
            sp.overlay.show_overlays = True
            sp.show_region_ui = True
    print("GUI READY")
    return None

bpy.app.timers.register(go, first_interval=4.0)
