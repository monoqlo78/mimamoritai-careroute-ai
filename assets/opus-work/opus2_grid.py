import sys
from PIL import Image, ImageDraw
REF = r"C:\Users\msoga\.copilot\workspaces\fe9aca11-79ab-4d6d-a028-c44b6544089c\attachments\09f6fd54-74b5-4ae0-90b4-b88b0e31fccf-ChatGPT Image 2026" + "\u5e74" + "8" + "\u6708" + "9" + "\u65e5" + " 19_57_40.png"
x0,y0,x1,y1,zoom,out = int(sys.argv[1]),int(sys.argv[2]),int(sys.argv[3]),int(sys.argv[4]),float(sys.argv[5]),sys.argv[6]
step = int(sys.argv[7]) if len(sys.argv)>7 else 20
im = Image.open(REF).convert("RGB").crop((x0,y0,x1,y1))
w,h = im.size
im = im.resize((int(w*zoom),int(h*zoom)), Image.LANCZOS)
d = ImageDraw.Draw(im)
for xr in range(x0 - x0%step, x1+1, step):
    px = (xr-x0)*zoom
    major = (xr % (step*5) == 0)
    d.line([(px,0),(px,im.size[1])], fill=(255,0,0) if major else (255,170,170), width=2 if major else 1)
    if major: d.text((px+2,2), str(xr), fill=(200,0,0))
for yr in range(y0 - y0%step, y1+1, step):
    py = (yr-y0)*zoom
    major = (yr % (step*5) == 0)
    d.line([(0,py),(im.size[0],py)], fill=(0,0,255) if major else (170,170,255), width=2 if major else 1)
    if major: d.text((2,py+2), str(yr), fill=(0,0,200))
im.save(out)
print("saved", out, im.size)
