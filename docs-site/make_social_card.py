"""Regenerate the social preview card (1200x630) from the existing artwork.

Usage (from the repository root, with Pillow installed):

    python docs-site/make_social_card.py

Writes docs-site/overrides/assets/social-card.png: the app icon, the name and
the README tagline beside a DRAWN library grid (blank coloured cards, one with
the (i) badge). The card contains no cover art on purpose (owner, 1.27.0): link
previews copy and cache it on other sites, where publishers' covers do not
belong. The PNG is committed, so the site build itself does not need Pillow.
The same file can be uploaded as the GitHub repository's social preview image.
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "docs-site" / "overrides" / "assets" / "social-card.png"
ICON = ROOT / "web" / "src" / "assets" / "icons" / "icon-circle.png"

WIDTH, HEIGHT = 1200, 630
BACKGROUND = (51, 51, 51)
PANEL = (30, 30, 38)
TEXT = (242, 242, 242)
ACCENT = (179, 157, 255)
CAPTION = (90, 90, 100)
TAGLINE = ["Self-hosted, folder-native", "manga and comic server", "with a web reader"]
CARD_COLOURS = [(122, 104, 196), (88, 140, 196), (196, 120, 104), (98, 168, 132),
                (176, 150, 86), (150, 100, 170), (90, 160, 170), (190, 110, 140)]


def font(size: int) -> ImageFont.ImageFont:
    return ImageFont.load_default(size=size)


def draw_info_badge(card: Image.Image, cx: int, cy: int, radius: int = 14) -> None:
    """The (i) badge as geometry (dot + stem), drawn 4x larger and scaled down so it stays centred and smooth."""
    k = 4
    badge = Image.new("RGBA", (2 * radius * k, 2 * radius * k), (0, 0, 0, 0))
    d = ImageDraw.Draw(badge)
    d.ellipse((0, 0, 2 * radius * k - 1, 2 * radius * k - 1), fill=TEXT + (255,))
    m = radius * k
    d.ellipse((m - 2.4 * k, m - 8.2 * k, m + 2.4 * k, m - 3.4 * k), fill=BACKGROUND + (255,))
    d.rounded_rectangle((m - 2.0 * k, m - 1.6 * k, m + 2.0 * k, m + 8.0 * k), 1.2 * k, fill=BACKGROUND + (255,))
    badge = badge.resize((2 * radius, 2 * radius), Image.LANCZOS)
    card.paste(badge, (cx - radius, cy - radius), badge)


def main() -> None:
    card = Image.new("RGB", (WIDTH, HEIGHT), BACKGROUND)
    draw = ImageDraw.Draw(card)

    # Drawn library grid on the right, bleeding off the right and bottom edges, with a soft shadow.
    gx, gy = 590, 70
    panel = (gx - 20, gy - 20, WIDTH + 40, HEIGHT - 30)
    shadow = Image.new("L", card.size, 0)
    ImageDraw.Draw(shadow).rounded_rectangle((panel[0] + 8, panel[1] + 14, panel[2] + 8, panel[3] + 14), 18, fill=150)
    card.paste((0, 0, 0), mask=shadow.filter(ImageFilter.GaussianBlur(16)))
    draw.rounded_rectangle(panel, 18, fill=PANEL)
    cw, ch, gap = 118, 170, 18
    for row in range(3):
        for col in range(5):
            x, y = gx + col * (cw + gap), gy + row * (ch + 44)
            colour = CARD_COLOURS[(row * 5 + col) % len(CARD_COLOURS)]
            draw.rounded_rectangle((x, y, x + cw, y + ch), 8, fill=colour)
            draw.rounded_rectangle((x + 10, y + ch - 30, x + cw - 48, y + ch - 18), 4,
                                   fill=tuple(min(255, v + 60) for v in colour))
            draw.rounded_rectangle((x, y + ch + 8, x + cw - 20, y + ch + 20), 5, fill=CAPTION)
    draw_info_badge(card, gx + cw - 10 - 14, gy + ch - 10 - 14)

    # Icon, name and tagline on the left.
    icon = Image.open(ICON).convert("RGBA").resize((120, 120), Image.LANCZOS)
    card.paste(icon, (64, 86), icon)
    draw.text((62, 236), "MangaPixer", font=font(70), fill=TEXT, stroke_width=2, stroke_fill=TEXT)
    for line_no, line in enumerate(TAGLINE):
        draw.text((64, 340 + line_no * 44), line, font=font(33), fill=TEXT)
    draw.text((64, 522), "mangapixer.com", font=font(33), fill=ACCENT)

    OUT.parent.mkdir(parents=True, exist_ok=True)
    card.save(OUT, optimize=True)
    print(f"wrote {OUT.relative_to(ROOT)} ({OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
