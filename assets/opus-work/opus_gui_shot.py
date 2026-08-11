import bpy, os
BASE = os.path.dirname(os.path.dirname(bpy.data.filepath))
OUT = os.path.join(BASE, "assets", "mimamo-opus-blender-reference-setup.png")

def setup():
    sc = bpy.context.scene
    cam = bpy.data.objects.get("FrontOrthoCam")
    if cam:
        sc.camera = cam
    for win in bpy.context.window_manager.windows:
        for area in win.screen.areas:
            if area.type == "VIEW_3D":
                for sp in area.spaces:
                    if sp.type == "VIEW_3D":
                        sp.region_3d.view_perspective = "CAMERA"
                        sp.shading.type = "MATERIAL"
                        sp.overlay.show_overlays = True
    print("SETUP DONE")
    return None

def shoot():
    wm = bpy.context.window_manager
    win = wm.windows[0]
    scr = win.screen
    area = next((a for a in scr.areas if a.type == "VIEW_3D"), scr.areas[0])
    region = next((r for r in area.regions if r.type == "WINDOW"), None)
    ok = False
    try:
        with bpy.context.temp_override(window=win, screen=scr, area=area, region=region):
            bpy.ops.screen.screenshot(filepath=OUT)
        ok = os.path.exists(OUT)
        print("FULL SHOT", ok, OUT)
    except Exception as e:
        print("FULL ERR", repr(e))
    if not ok:
        try:
            with bpy.context.temp_override(window=win, screen=scr, area=area, region=region):
                bpy.ops.screen.screenshot_area(filepath=OUT)
            print("AREA SHOT", os.path.exists(OUT))
        except Exception as e:
            print("AREA ERR", repr(e))
    bpy.app.timers.register(lambda: bpy.ops.wm.quit_blender(), first_interval=3.0)
    return None

bpy.app.timers.register(setup, first_interval=5.0)
bpy.app.timers.register(shoot, first_interval=14.0)
