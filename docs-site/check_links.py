"""Offline link check for the built documentation site.

Usage: python docs-site/check_links.py site

Every internal href/src in the generated HTML (relative, root-relative, or an
absolute URL on the site's own host) must resolve to a file in the build
output, and every #fragment must match an id on the target page. The sitemap
and robots.txt must exist and name only pages that were built. External links
are not fetched (the check stays deterministic and offline).

It also enforces the privacy rule for the site: no page may load a script,
stylesheet, font, image or frame from another host at run time (links the
reader clicks are fine), and the theme's repository widget must stay disabled
(it calls api.github.com from the browser). Standard library only.
"""

from __future__ import annotations

import re
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urldefrag, urljoin, urlsplit
from xml.etree import ElementTree

SITE_HOST = "mangapixer.com"


class _Collector(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.ids: set[str] = set()
        self.links: list[str] = []
        self.loads: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = dict(attrs)
        for key in ("id", "name"):
            if values.get(key) and (key == "id" or tag == "a"):
                self.ids.add(values[key])
        for key in ("href", "src"):
            if values.get(key) and not (tag == "link" and values.get("rel") in ("canonical",)):
                self.links.append(values[key])
        # Resources the browser fetches on its own, without a click.
        if tag in ("script", "img", "iframe", "source", "video", "audio") and values.get("src"):
            self.loads.append(values["src"])
        if tag == "link" and values.get("href") and values.get("rel") not in ("canonical", "alternate", "next", "prev"):
            self.loads.append(values["href"])


def _target(site: Path, page: Path, link: str) -> tuple[Path, str] | None:
    """File and fragment an internal link points at, or None for an external link."""
    parts = urlsplit(link)
    if parts.scheme in ("mailto", "tel", "data", "javascript"):
        return None
    if parts.scheme in ("http", "https"):
        if parts.hostname != SITE_HOST:
            return None
        link = parts.path + (f"#{parts.fragment}" if parts.fragment else "")
    url, fragment = urldefrag(link)
    base = "/" + page.relative_to(site).as_posix()
    path = unquote(urlsplit(urljoin(base, url)).path) if url else base
    target = site / path.lstrip("/")
    if path.endswith("/") or target.is_dir():
        target = target / "index.html"
    return target, fragment


def main(site_dir: str) -> int:
    site = Path(site_dir)
    pages = sorted(site.rglob("*.html"))
    parsed: dict[Path, _Collector] = {}
    for page in pages:
        collector = _Collector()
        collector.feed(page.read_text(encoding="utf-8"))
        parsed[page] = collector

    errors: list[str] = []
    checked = 0
    for page, collector in parsed.items():
        # 404.html is served for any missing path, so its relative links are not checkable.
        if page.name == "404.html":
            continue
        for link in collector.links:
            resolved = _target(site, page, link)
            if resolved is None:
                continue
            checked += 1
            target, fragment = resolved
            where = page.relative_to(site).as_posix()
            if not target.is_file():
                errors.append(f"{where}: broken link {link}")
            elif fragment and target.suffix == ".html" and fragment not in parsed[target].ids:
                errors.append(f"{where}: missing anchor {link}")

        if 'data-md-component="source"' in page.read_text(encoding="utf-8"):
            errors.append(f"{page.relative_to(site).as_posix()}: repository widget calls api.github.com")
        for load in collector.loads:
            host = urlsplit(load).hostname
            if (host and host != SITE_HOST) or load.startswith("//"):
                errors.append(f"{page.relative_to(site).as_posix()}: loads a third-party resource {load}")

    for sheet in sorted(site.rglob("*.css")):
        for url in re.findall(r"(?:url\(|@import\s+)['\"]?((?:https?:)?//[^'\")\s]+)", sheet.read_text(encoding="utf-8")):
            if urlsplit(url).hostname != SITE_HOST:
                errors.append(f"{sheet.relative_to(site).as_posix()}: loads a third-party resource {url}")

    sitemap = site / "sitemap.xml"
    robots = site / "robots.txt"
    if not sitemap.is_file():
        errors.append("sitemap.xml missing")
    else:
        ns = {"s": "http://www.sitemaps.org/schemas/sitemap/0.9"}
        for loc in ElementTree.parse(sitemap).findall("s:url/s:loc", ns):
            resolved = _target(site, site / "index.html", loc.text or "")
            if resolved is None or not resolved[0].is_file():
                errors.append(f"sitemap.xml: {loc.text} is not a built page")
    if not robots.is_file() or f"Sitemap: https://{SITE_HOST}/sitemap.xml" not in robots.read_text():
        errors.append("robots.txt missing or without the Sitemap line")

    for error in errors:
        print(f"LINK ERROR {error}")
    print(f"Link check: {len(pages)} pages, {checked} internal links, {len(errors)} errors")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "site"))
