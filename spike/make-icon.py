from PIL import Image, ImageDraw, ImageFilter
import os

S = 4096
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))

def vgrad(box, top, bottom, radius):
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    g = Image.new("RGBA", (w, h))
    gd = ImageDraw.Draw(g)
    for y in range(h):
        gd.line([(0, y), (w, y)], fill=lerp(top, bottom, y / max(1, h - 1)))
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, w - 1, h - 1], radius=radius, fill=255)
    img.paste(g, (x0, y0), mask)

# background squircle
pad = int(S * 0.035)
vgrad((pad, pad, S - pad, S - pad), (26, 38, 84, 255), (7, 10, 24, 255), int(S * 0.21))

# soft glow behind center
glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
gd = ImageDraw.Draw(glow)
gd.ellipse([S * 0.18, S * 0.18, S * 0.82, S * 0.82], fill=(70, 100, 255, 95))
glow = glow.filter(ImageFilter.GaussianBlur(S * 0.09))
img.alpha_composite(glow)

# connector cross (behind tiles)
cx = cy = S // 2
line = (245, 248, 255, 90)
lw = int(S * 0.018)
tile = int(S * 0.30)
gap = int(S * 0.06)
left = int((S - (2 * tile + gap)) / 2)
top = left
positions = [(left, top), (left + tile + gap, top), (left, top + tile + gap), (left + tile + gap, top + tile + gap)]
for ox, oy in [(-1, -1), (1, -1), (-1, 1), (1, 1)]:
    tx = cx + ox * (tile + gap) // 2
    ty = cy + oy * (tile + gap) // 2
    d.line([(tx, ty), (cx, cy)], fill=line, width=lw)
    r = lw // 2
    d.ellipse([tx - r, ty - r, tx + r, ty + r], fill=line)
colors = [
    ((142, 172, 255, 255), (74, 102, 245, 255)),
    ((202, 156, 255, 255), (124, 58, 237, 255)),
    ((86, 231, 255, 255), (8, 145, 178, 255)),
    ((96, 235, 180, 255), (5, 150, 105, 255)),
]
radius = int(tile * 0.27)
for (x, y), (c0, c1) in zip(positions, colors):
    # shadow
    sh = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle([x + 8, y + 16, x + tile + 8, y + tile + 16], radius=radius, fill=(0, 0, 0, 110))
    img.alpha_composite(sh.filter(ImageFilter.GaussianBlur(S * 0.018)))
    vgrad((x, y, x + tile, y + tile), c0, c1, radius)
    # top highlight
    hl = Image.new("RGBA", (tile, tile), (0, 0, 0, 0))
    ImageDraw.Draw(hl).rounded_rectangle([int(tile*0.04), int(tile*0.04), int(tile*0.96), int(tile*0.40)], radius=radius, fill=(255, 255, 255, 34))
    img.alpha_composite(hl, (x, y))

# center hub
r = int(S * 0.118)
ring = int(S * 0.030)
d.ellipse([cx - r - ring, cy - r - ring, cx + r + ring, cy + r + ring], fill=(12, 18, 38, 255))
d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(248, 250, 255, 255))
# inner dot
ir = int(r * 0.34)
d.ellipse([cx - ir, cy - ir, cx + ir, cy + ir], fill=(30, 45, 110, 255))
# small accent glint
gr = int(r * 0.16)
d.ellipse([cx - ir * 0.9 - gr, cy - ir * 0.9 - gr, cx - ir * 0.9 + gr, cy - ir * 0.9 + gr], fill=(160, 190, 255, 255))

os.makedirs(r"C:\SDemo\DesktopAssembly\src\SDemo.Hub.App\Assets", exist_ok=True)
png = img.resize((1024, 1024), Image.LANCZOS)
png.save(r"C:\SDemo\DesktopAssembly\src\SDemo.Hub.App\Assets\sdemo-hub.png")

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
png.save(r"C:\SDemo\DesktopAssembly\src\SDemo.Hub.App\Assets\sdemo-hub.ico", sizes=[(s, s) for s in sizes])

# previews for inspection
for s in (16, 24, 32, 48, 64, 128):
    img.resize((s, s), Image.LANCZOS).resize((s * 8, s * 8), Image.NEAREST).save(rf"C:\SDemo\DesktopAssembly\spike\icon-{s}.png")
print("done")

