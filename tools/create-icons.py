"""Generate VADER Bridge icons from scalable drawing primitives. Requires Pillow."""
from pathlib import Path
import math
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "VaderBridge" / "Assets"
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
SCALE = 4


def curve(start, sections):
    points = [start]
    p0 = start
    for p1, p2, p3 in sections:
        for step in range(1, 33):
            t = step / 32
            u = 1 - t
            points.append(tuple(u**3*p0[i] + 3*u*u*t*p1[i] + 3*u*t*t*p2[i] + t**3*p3[i] for i in (0, 1)))
        p0 = p3
    return points


def render(status=None):
    n = 256 * SCALE
    image = Image.new("RGBA", (n, n))
    draw = ImageDraw.Draw(image)

    def box(coords):
        return tuple(round(c * SCALE) for c in coords)

    def line(points, fill, width):
        draw.line([(round(x*SCALE), round(y*SCALE)) for x, y in points], fill, round(width*SCALE), joint="curve")

    mask = Image.new("L", (n, n))
    ImageDraw.Draw(mask).rounded_rectangle(box((12, 12, 244, 244)), 53*SCALE, fill=255)
    base = Image.new("RGBA", (n, n))
    rows = ImageDraw.Draw(base)
    for y in range(n):
        t = y / (n - 1)
        rows.line((0, y, n, y), fill=(round(30-17*t), round(49-24*t), round(78-33*t), 255))
    image.paste(base, (0, 0), mask)
    draw.rounded_rectangle(box((14, 14, 242, 242)), 51*SCALE, outline="#344B66", width=2*SCALE)

    # A tilted orbit expresses motion and the bridge's sensor input.
    orbit = []
    rotation = math.radians(-24)
    for step in range(241):
        angle = math.radians(-160 + step * 290 / 240)
        x, y = 97*math.cos(angle), 59*math.sin(angle)
        orbit.append((128+x*math.cos(rotation)-y*math.sin(rotation), 125+x*math.sin(rotation)+y*math.cos(rotation)))
    line(orbit, "#2ACBEE", 7)
    dot = orbit[0]
    draw.ellipse(box((dot[0]-7, dot[1]-7, dot[0]+7, dot[1]+7)), fill="#7DEBFF")

    body = curve((89, 96), [
        ((69, 96), (58, 112), (51, 136)),
        ((46, 153), (42, 167), (40, 177)),
        ((37, 195), (54, 205), (67, 188)),
        ((75, 179), (83, 168), (90, 161)),
        ((112, 158), (144, 158), (166, 161)),
        ((173, 168), (181, 179), (189, 188)),
        ((202, 205), (219, 195), (216, 177)),
        ((214, 167), (210, 153), (205, 136)),
        ((198, 112), (187, 96), (167, 96)),
        ((147, 96), (109, 96), (89, 96)),
    ])
    shadow = [(x, y+5) for x, y in body]
    draw.polygon([(round(x*SCALE), round(y*SCALE)) for x, y in shadow], fill="#091626")
    draw.polygon([(round(x*SCALE), round(y*SCALE)) for x, y in body], fill="#EAF5FF")
    line(body + [body[0]], "#B5CADF", 2)

    # Bold controls remain readable at the 16-pixel taskbar size.
    draw.rounded_rectangle(box((76, 119, 87, 149)), 3*SCALE, fill="#213957")
    draw.rounded_rectangle(box((66, 129, 97, 140)), 3*SCALE, fill="#213957")
    for x, y in ((175, 117), (188, 130), (175, 143), (162, 130)):
        draw.ellipse(box((x-5, y-5, x+5, y+5)), fill="#087CBF")
    draw.rounded_rectangle(box((113, 112, 142, 118)), 3*SCALE, fill="#73ACC9")
    for x, y in ((110, 143), (146, 149)):
        draw.ellipse(box((x-9, y-9, x+9, y+9)), fill="#34516F")
        draw.ellipse(box((x-5, y-5, x+5, y+5)), fill="#1C334F")

    if status:
        draw.ellipse(box((178, 178, 247, 247)), fill="#0D1B2D")
        draw.ellipse(box((185, 185, 240, 240)), fill=status, outline="#F4FBFF", width=3*SCALE)
    return image.resize((256, 256), Image.Resampling.LANCZOS)


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    colors = {"Starting": "#29A9FF", "Ready": "#34D399", "Waiting": "#FBBF24", "Error": "#F87171"}
    base = render()
    base.save(ASSETS / "VADERBridge.ico", sizes=[(s, s) for s in SIZES])
    base.save(ASSETS / "VADERBridge.png")
    for state, color in colors.items():
        render(color).save(ASSETS / f"{state}.ico", sizes=[(s, s) for s in SIZES])

    # A review image also shows the actual small-size rasters on light and dark backgrounds.
    preview = Image.new("RGB", (560, 300), "#F2F5FA")
    preview.paste(base, (12, 20), base)
    canvas = ImageDraw.Draw(preview)
    canvas.rectangle((285, 12, 548, 152), fill="#101B2B")
    for y in (45, 190):
        for x, size in ((310, 16), (353, 24), (406, 32), (465, 48)):
            small = base.resize((size, size), Image.Resampling.LANCZOS)
            preview.paste(small, (x, y), small)
    for x, color in zip((304, 363, 422, 481), colors.values()):
        small = render(color).resize((32, 32), Image.Resampling.LANCZOS)
        preview.paste(small, (x, 244), small)
    preview.save(ASSETS / "preview.png")
    print(ASSETS)


if __name__ == "__main__":
    main()
