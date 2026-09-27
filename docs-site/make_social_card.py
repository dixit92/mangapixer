"""Regenerate the social preview card (1200x630) from the existing artwork.

Usage (from the repository root, with Pillow installed):
    python docs-site/make_social_card.py

Writes docs-site/overrides/assets/social-card.png: the app icon, the name and
the README tagline beside the desktop home screenshot. The PNG is committed,
so the site build itself does not need Pillow. The same file can be uploaded
as the GitHub repository's social preview image.
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "docs-site" / "overrides" / "assets" / "social-card.png"
ICON = ROOT / "web" / "src" / "assets" / "icons" / "icon-circle.png"
SHOT = ROOT / "assets" / "screenshots" / "home-desktop.jpg"

WIDTH, HEIGHT = 1200, 630
BACKGROUND = (51, 51, 51)
TEXT = (242, 242, 242)
ACCENT = (179, 157, 255)
TAGLINE = ["Self-hosted, folder-native", "manga and comic server", "with a web reader"]


def main() -> None:
    card = Image.new("RGB", (WIDTH, HEIGHT), BACKGROUND)

    # Screenshot on the right, bleeding off the right edge, with a soft shadow.
    shot = Image.open(SHOT).convert("RGB")
    shot_h = 470
    shot = shot.resize((round(shot.width * shot_h / shot.height), shot_h), Image.LANCZOS)
    x, y = 560, (HEIGHT - shot_h) // 2
    shadow = Image.new("L", card.size, 0)
    ImageDraw.Draw(shadow).rounded_rectangle((x + 8, y + 14, x + shot.width + 8, y + shot_h + 14), 18, fill=150)
    card.paste((0, 0, 0), mask=shadow.filter(ImageFilter.GaussianBlur(16)))
    mask = Image.new("L", shot.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, shot.width, shot_h), 16, fill=255)
    card.paste(shot, (x, y), mask)

    # Icon, name and tagline on the left.
    icon = Image.open(ICON).convert("RGBA").resize((132, 132), Image.LANCZOS)
    card.paste(icon, (64, 92), icon)
    draw = ImageDraw.Draw(card)
    title = ImageFont.load_default(size=72)
    body = ImageFont.load_default(size=34)
    draw.text((62, 250), "MangaPixer", font=title, fill=TEXT, stroke_width=2, stroke_fill=TEXT)
    for line_no, line in enumerate(TAGLINE):
        draw.text((64, 356 + line_no * 46), line, font=body, fill=TEXT)
    draw.text((64, 520), "mangapixer.com", font=body, fill=ACCENT)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    card.save(OUT, optimize=True)
    print(f"wrote {OUT.relative_to(ROOT)} ({OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
