"""Builds the application icon, the UI logos and the installer image from assets/brand/*.png.
Run from the repository root:  python3 tools/branding/make-icons.py
The printer badge marks this as the label-printer tool of the Flow Poshak brand."""
from PIL import Image, ImageDraw
import pathlib

root = pathlib.Path(__file__).resolve().parents[2]
brand = root / "assets" / "brand"
SS = 4  # supersampling for the drawn badge


def badge(size: int, fg, bg) -> Image.Image:
    """Printer glyph inside a round badge, drawn at SSx and reduced for clean edges."""
    s = size * SS
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.ellipse([0, 0, s - 1, s - 1], fill=bg)
    r = int(s * 0.045)
    d.ellipse([r, r, s - 1 - r, s - 1 - r], outline=fg, width=max(2, int(s * 0.035)))
    u = s / 100.0

    def rr(x0, y0, x1, y1, rad, **kw):
        d.rounded_rectangle([x0 * u, y0 * u, x1 * u, y1 * u], radius=rad * u, **kw)

    rr(33, 22, 67, 42, 3, fill=fg)            # paper in the tray
    rr(20, 40, 80, 68, 7, fill=fg)            # body
    rr(30, 58, 70, 82, 3, fill=fg, outline=bg, width=max(1, int(1.6 * u)))  # printed label
    d.line([36 * u, 66 * u, 64 * u, 66 * u], fill=bg, width=max(1, int(2 * u)))
    d.line([36 * u, 72 * u, 58 * u, 72 * u], fill=bg, width=max(1, int(2 * u)))
    d.ellipse([68 * u, 46 * u, 74 * u, 52 * u], fill=bg)  # status light
    return im.resize((size, size), Image.LANCZOS)


def with_badge(logo: Image.Image, size: int, fg, bg) -> Image.Image:
    base = logo.resize((size, size), Image.LANCZOS)
    b = badge(int(size * 0.46), fg, bg)
    off = int(size * 0.54)
    out = base.copy()
    out.alpha_composite(b, (off, off))
    return out


dark = Image.open(brand / "logo-dark.png").convert("RGBA")
light = Image.open(brand / "logo-light.png").convert("RGBA")
gold = dark.getpixel((300, 200))[:3] + (255,)
gold = (242, 185, 59, 255) if sum(gold[:3]) < 200 else gold
black = (10, 10, 10, 255)

# Application icon: dark (gold on black) variant, readable at 16 px.
sizes = [16, 24, 32, 48, 64, 128, 256]
big = with_badge(dark, 512, gold, black)
big.save(root / "src" / "LabelPrinter.Agent" / "app.ico", sizes=[(n, n) for n in sizes])

ui = root / "src" / "LabelPrinter.Agent" / "Ui"
with_badge(dark, 192, gold, black).save(ui / "icon.png", optimize=True)
dark.resize((160, 160), Image.LANCZOS).save(ui / "logo-dark.png", optimize=True)
light.resize((160, 160), Image.LANCZOS).save(ui / "logo-light.png", optimize=True)

# Installer wizard small image (Inno scales it by DPI): logo on white.
w = Image.new("RGB", (110, 110), (255, 255, 255))
l = light.resize((110, 110), Image.LANCZOS)
w.paste(l, (0, 0), l)
w.save(root / "installer" / "wizard-small.bmp")
print("ok")
