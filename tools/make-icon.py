# Cuts the ZeppieSays logo (rounded purple square, white sound bars) out of its dark backdrop and writes
# the app icon (.ico, all Windows sizes) plus a large PNG for the window.
# Usage: python tools/make-icon.py   (needs Pillow)
from pathlib import Path

from PIL import Image, ImageDraw

HERE = Path(__file__).parent
SOURCE = HERE / "zeppie-logo-source.webp"
ASSETS = HERE.parent / "src" / "ZeppieSays" / "Assets"

# Where the rounded square sits in the 1024 x 1024 source, and how round its corners are.
LEFT, TOP, SIZE, RADIUS = 68, 68, 888, 250
INSET = 2  # trim the anti-aliased rim so no dark halo shows on light backgrounds
SUPERSAMPLE = 4


def cut_out() -> Image.Image:
    logo = Image.open(SOURCE).convert("RGBA").crop((LEFT, TOP, LEFT + SIZE, TOP + SIZE))
    big = SIZE * SUPERSAMPLE
    mask = Image.new("L", (big, big), 0)
    edge = INSET * SUPERSAMPLE
    ImageDraw.Draw(mask).rounded_rectangle(
        (edge, edge, big - 1 - edge, big - 1 - edge), radius=RADIUS * SUPERSAMPLE, fill=255)
    logo.putalpha(mask.resize((SIZE, SIZE), Image.LANCZOS))
    return logo


def main() -> None:
    logo = cut_out()
    logo.resize((512, 512), Image.LANCZOS).save(ASSETS / "zeppie.png")
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    logo.resize((256, 256), Image.LANCZOS).save(ASSETS / "zeppie.ico", sizes=[(s, s) for s in sizes])
    print(f"Wrote {ASSETS / 'zeppie.png'} and {ASSETS / 'zeppie.ico'}")


if __name__ == "__main__":
    main()
