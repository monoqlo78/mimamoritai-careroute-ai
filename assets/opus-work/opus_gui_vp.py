import bpy, os
BASE = os.path.dirname(os.path.dirname(bpy.data.filepath))
OUT = os.path.join(BASE, "assets", "opus-work", "gui_viewport.png")
log = open(os.path.join(BASE, "assets", "opus-work", "gui_log.txt"), "w", encoding="utf-8")

def go():
    sc = bpy.context.scene
    cam = bpy.data.objects.get("FrontOrthoCam")
    if cam:
        sc.camera = cam
    # make the reference plane visible in the viewport for the proof shot
    for ob in bpy.data.objects:
        if "Reference" in ob.name or "REFERENCE" in ob.name:
            ob.hide_viewport = False
            ob.hide_set(False)
            log.write("ref obj %s hide_render=%s\n" % (ob.name, ob.hide_render))
    vl = bpy.context.view_layer
    for lc in vl.layer_collection.children:
        if "REFERENCE" in lc.name:
            lc.exclude = False
            lc.hide_viewport = False
            log.write("ref coll %s exclude=%s\n" % (lc.name, lc.exclude))
    win = bpy.context.window_manager.windows[0]
    scr = win.screen
    area = next((a for a in scr.areas if a.type == "VIEW_3D"), None)
    region = next((r for r in area.regions if r.type == "WINDOW"), None)
    sp = area.spaces[0]
    sp.region_3d.view_perspective = "CAMERA"
    sp.shading.type = "MATERIAL"
    sp.overlay.show_overlays = True
    sc.render.filepath = OUT
    sc.render.image_settings.file_format = "PNG"
    sc.render.resolution_x = 1660
    sc.render.resolution_y = 1040
    if cam:
        cam.data.ortho_scale = 3.10
        cam.location.z = 1.16
    sc.render.resolution_percentage = 100
    try:
        with bpy.context.temp_override(window=win, screen=scr, area=area, region=region):
            bpy.ops.render.opengl(animation=False, write_still=True, view_context=True)
        log.write("opengl ok %s\n" % os.path.exists(OUT))
    except Exception as e:
        log.write("opengl err %r\n" % (e,))
    log.close()
    bpy.app.timers.register(lambda: bpy.ops.wm.quit_blender(), first_interval=2.0)
    return None

bpy.app.timers.register(go, first_interval=6.0)
