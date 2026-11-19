"""Draws the PCHUB icon and writes every size each platform needs.

Run from the repo root:  python packaging/icon/make_icon.py
Needs Pillow. Outputs:
  packaging/icon/pchub-1024.png      master image
  src/PcHub/Assets/pchub.ico          Windows exe + window icon (16-256 px)
  src/PcHub/Assets/pchub-256.png      window icon on macOS / Linux
  packaging/macos/PCHUB.icns          macOS app bundle icon
  packaging/linux/pchub.png           Linux desktop entry icon (512 px)
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[2]
S = 2048  # drawn at 2x, then scaled down for smooth edges

BG_TOP = (33, 39, 49)
BG_BOTTOM = (14, 16, 20)
LINE = (38, 44, 54)
CHIP = (22, 26, 33)
ACCENT = (74, 222, 128)


def rounded_mask(size, box, radius):
    m = Image.new("L", size, 0)
    ImageDraw.Draw(m).rounded_rectangle(box, radius=radius, fill=255)
    return m


def draw() -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Background tile: rounded square with a soft vertical gradient.
    pad = int(S * 0.06)
    tile = (pad, pad, S - pad, S - pad)
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = y / (S - 1)
        c = tuple(int(BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * t) for i in range(3))
        gd.line([(0, y), (S, y)], fill=c + (255,))
    img.paste(grad, (0, 0), rounded_mask((S, S), tile, int(S * 0.2)))
    ImageDraw.Draw(img).rounded_rectangle(tile, radius=int(S * 0.2), outline=LINE + (255,), width=int(S * 0.008))

    # The chip with pins on every side.
    c0, c1 = int(S * 0.27), int(S * 0.73)
    pin_len, pin_w = int(S * 0.075), int(S * 0.045)
    d = ImageDraw.Draw(img)
    for i in range(4):
        p = c0 + (c1 - c0) * (i + 0.5) / 4
        for box in (
            (p - pin_w / 2, c0 - pin_len, p + pin_w / 2, c0 + 10),  # top
            (p - pin_w / 2, c1 - 10, p + pin_w / 2, c1 + pin_len),  # bottom
            (c0 - pin_len, p - pin_w / 2, c0 + 10, p + pin_w / 2),  # left
            (c1 - 10, p - pin_w / 2, c1 + pin_len, p + pin_w / 2),  # right
        ):
            d.rounded_rectangle(box, radius=pin_w / 2, fill=ACCENT + (255,))

    d.rounded_rectangle((c0, c0, c1, c1), radius=int(S * 0.06), fill=CHIP + (255,), outline=ACCENT + (255,), width=int(S * 0.028))

    # A live-graph pulse across the chip, with a soft glow.
    w = c1 - c0
    pts = [(0.12, 0.62), (0.32, 0.62), (0.43, 0.36), (0.55, 0.74), (0.66, 0.48), (0.88, 0.48)]
    pts = [(c0 + x * w, c0 + y * w) for x, y in pts]
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(glow).line(pts, fill=ACCENT + (170,), width=int(S * 0.05), joint="curve")
    img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(S * 0.02)))
    d = ImageDraw.Draw(img)
    d.line(pts, fill=ACCENT + (255,), width=int(S * 0.03), joint="curve")
    r = S * 0.026
    for x, y in (pts[0], pts[-1]):
        d.ellipse((x - r, y - r, x + r, y + r), fill=ACCENT + (255,))

    return img.resize((1024, 1024), Image.LANCZOS)


def main() -> None:
    master = draw()
    out = {
        "master": ROOT / "packaging/icon/pchub-1024.png",
        "ico": ROOT / "src/PcHub/Assets/pchub.ico",
        "png256": ROOT / "src/PcHub/Assets/pchub-256.png",
        "icns": ROOT / "packaging/macos/PCHUB.icns",
        "linux": ROOT / "packaging/linux/pchub.png",
    }
    for p in out.values():
        p.parent.mkdir(parents=True, exist_ok=True)

    master.save(out["master"])
    master.save(out["ico"], sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
    master.resize((256, 256), Image.LANCZOS).save(out["png256"])
    master.save(out["icns"])
    master.resize((512, 512), Image.LANCZOS).save(out["linux"])
    for name, p in out.items():
        print(f"{name}: {p.relative_to(ROOT)} ({p.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
