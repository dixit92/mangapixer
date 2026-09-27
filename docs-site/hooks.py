"""MkDocs hooks for the MangaPixer documentation site.

docs/ is written for GitHub first and stays the single source. These hooks
adapt it for the site at build time without changing the Markdown files:

- the nav is read from the tables in docs/README.md (the docs index);
- links to repository files outside docs/ (../CHANGELOG.md, ../LICENSE, ...)
  point at the file on GitHub; ../assets/ images are copied into the site;
- each page gets a meta description from its first paragraph;
- the home page uses the landing template and carries the JSON-LD block;
- robots.txt allows crawling and names the sitemap.
"""

from __future__ import annotations

import json
import posixpath
import re
from pathlib import Path

from mkdocs.config.defaults import MkDocsConfig
from mkdocs.structure.files import File, Files
from mkdocs.structure.pages import Page

REPO_ROOT = Path(__file__).resolve().parent.parent
GITHUB_BLOB = "https://github.com/dixit92/mangapixer/blob/main/"
GITHUB_TREE = "https://github.com/dixit92/mangapixer/tree/main/"
INDEX_PAGE = "README.md"

# Files outside docs/ that the site serves: site path -> repository path.
EXTRA_FILES = {
    "assets/icons/icon-circle.png": "web/src/assets/icons/icon-circle.png",
    "assets/icons/apple-touch-icon.png": "web/src/assets/icons/apple-touch-icon.png",
    "assets/icons/favicon.ico": "web/src/favicon.ico",
}
SCREENSHOT_DIR = "assets/screenshots"

# [text](../target) and ![alt](../target), with an optional #anchor.
_PARENT_LINK = re.compile(r"(\]\()\.\./([^)\s#]+)(#[^)\s]*)?(\))")
_SECTION = re.compile(r"^##\s+(.+?)\s*$")
_INDEX_ROW = re.compile(r"^\|\s*\[([^\]]+)\]\(([^)#\s]+\.md)\)\s*\|")
_FENCE = re.compile(r"^(```|~~~)")


def _version() -> str:
    text = (REPO_ROOT / "Version.props").read_text(encoding="utf-8")
    text = re.sub(r"<!--.*?-->", "", text, flags=re.S)
    parts = [
        re.search(rf"<MangaPixerVersion{p}>([^<]*)<", text).group(1).strip()
        for p in ("Major", "Minor", "Patch", "Prerelease")
    ]
    version = ".".join(parts[:3])
    return f"{version}-{parts[3]}" if parts[3] else version


def _nav_from_index(docs_dir: Path) -> list:
    """Home, then one section per '## Heading' with the pages its table links."""
    nav: list = [{"Home": INDEX_PAGE}]
    section: list | None = None
    for line in (docs_dir / INDEX_PAGE).read_text(encoding="utf-8").splitlines():
        heading = _SECTION.match(line)
        if heading:
            section = []
            nav.append({heading.group(1): section})
            continue
        row = _INDEX_ROW.match(line)
        if row and section is not None:
            section.append({row.group(1): row.group(2)})
    return [entry for entry in nav if next(iter(entry.values()))]


def on_config(config: MkDocsConfig) -> MkDocsConfig:
    config.nav = _nav_from_index(Path(config.docs_dir))
    version = _version()
    config.extra["version"] = version
    seo = config.extra["seo"]
    data = {
        "@context": "https://schema.org",
        "@type": "SoftwareApplication",
        "name": config.site_name,
        "description": config.site_description,
        "url": config.site_url,
        "image": config.site_url + seo["image"],
        "applicationCategory": "MultimediaApplication",
        "applicationSubCategory": "Comic and manga server",
        "operatingSystem": "Linux (Docker), Unraid, Windows",
        "softwareVersion": version,
        "license": seo["license"],
        "isAccessibleForFree": True,
        "offers": {"@type": "Offer", "price": "0", "priceCurrency": "USD"},
        "downloadUrl": seo["download_url"],
        "installUrl": config.site_url + "install-docker/",
        "releaseNotes": GITHUB_BLOB + "CHANGELOG.md",
        "sameAs": [config.repo_url],
    }
    # Escaped for a <script> element: no '<', '>' or '&' can close it early.
    config.extra["jsonld"] = (
        json.dumps(data, ensure_ascii=False, indent=2)
        .replace("<", "\\u003c").replace(">", "\\u003e").replace("&", "\\u0026")
    )
    return config


def on_files(files: Files, config: MkDocsConfig) -> Files:
    extra = dict(EXTRA_FILES)
    for image in sorted((REPO_ROOT / SCREENSHOT_DIR).iterdir()):
        if image.is_file():
            extra[f"{SCREENSHOT_DIR}/{image.name}"] = f"{SCREENSHOT_DIR}/{image.name}"
    for site_path, repo_path in extra.items():
        files.append(File.generated(config, site_path, abs_src_path=str(REPO_ROOT / repo_path)))
    return files


def _rewrite_parent_links(markdown: str) -> str:
    def replace(match: re.Match) -> str:
        target, anchor = match.group(2), match.group(3) or ""
        if target.startswith("assets/"):
            return f"{match.group(1)}{target}{anchor}{match.group(4)}"
        if target.endswith("/"):
            return f"{match.group(1)}{GITHUB_TREE}{target}{anchor}{match.group(4)}"
        return f"{match.group(1)}{GITHUB_BLOB}{target}{anchor}{match.group(4)}"

    out, in_fence = [], False
    for line in markdown.splitlines(keepends=True):
        if _FENCE.match(line.lstrip()):
            in_fence = not in_fence
        out.append(line if in_fence else _PARENT_LINK.sub(replace, line))
    return "".join(out)


def _description(markdown: str, limit: int = 160) -> str | None:
    """Plain text of the first paragraph, cut at a word boundary."""
    in_fence = False
    for block in re.split(r"\n\s*\n", markdown):
        stripped = block.strip()
        if _FENCE.match(stripped):
            in_fence = not in_fence
        if in_fence or not stripped or stripped[0] in "#|<>!-*`" or stripped[0].isdigit():
            continue
        text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", stripped)
        text = re.sub(r"[*_`]", "", text)
        text = " ".join(text.split())
        if len(text) > limit:
            text = text[: limit - 1].rsplit(" ", 1)[0].rstrip(",.;:") + "…"
        return text
    return None


def on_page_markdown(markdown: str, page: Page, config: MkDocsConfig, files: Files) -> str:
    # docs/ pages all sit at the top level, so ../ always means the repository root.
    if posixpath.dirname(page.file.src_uri):
        raise ValueError(f"{page.file.src_uri}: nested docs pages need a ../ rewrite rule")
    markdown = _rewrite_parent_links(markdown)
    if page.is_homepage:
        page.meta.setdefault("template", "home.html")
        page.meta.setdefault("description", config.site_description)
    else:
        description = _description(markdown)
        if description:
            page.meta.setdefault("description", description)
    return markdown


def on_post_build(config: MkDocsConfig) -> None:
    site_dir = Path(config.site_dir)
    (site_dir / "robots.txt").write_text(
        "User-agent: *\nAllow: /\n\nSitemap: " + config.site_url + "sitemap.xml\n",
        encoding="utf-8",
    )
