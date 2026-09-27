"""MkDocs hooks for the MangaPixer documentation site.

docs/ is written for GitHub first and stays the single source. These hooks
adapt it for the site at build time without changing the Markdown files:

- the nav is read from the tables in docs/README.md (the docs index);
- links to repository files outside docs/ (../CHANGELOG.md, ../LICENSE, ...)
  point at the file on GitHub; ../assets/ images are copied into the site;
- each page's meta description is its "What it covers" text from that index;
- mermaid diagrams render with a pinned, hash-checked Mermaid copy served from
  the site itself (the theme would otherwise load it from a CDN);
- the home page uses the landing template and carries the JSON-LD block;
- robots.txt allows crawling and names the sitemap.
"""

from __future__ import annotations

import base64
import hashlib
import io
import json
import posixpath
import re
import tarfile
import urllib.request
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

# Mermaid for the diagrams in docs/, fetched once at build time from the npm
# registry, checked against the package's published integrity hash, cached in
# docs-site/.cache (git-ignored) and served from the site. Stay on 11.x: the
# theme's diagram integration is written for the Mermaid 11 API.
MERMAID_VERSION = "11.17.2"
MERMAID_INTEGRITY = (
    "sha512-V6K3C8EBdEsPFZXSKMJe6ppQOENxuHARr9GvHX4hh47lAbhMRD9qf4oEK7LoaRQxULMa80/qt5gHO73aCleBBg=="
)
MERMAID_SCRIPT = f"assets/javascripts/mermaid-{MERMAID_VERSION}.min.js"
CACHE_DIR = REPO_ROOT / "docs-site" / ".cache"

# [text](../target) and ![alt](../target), with an optional #anchor.
_PARENT_LINK = re.compile(r"(\]\()\.\./([^)\s#]+)(#[^)\s]*)?(\))")
_SECTION = re.compile(r"^##\s+(.+?)\s*$")
_INDEX_ROW = re.compile(r"^\|\s*\[([^\]]+)\]\(([^)#\s]+\.md)\)\s*\|\s*(.*?)\s*\|\s*$")
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


def _read_index(docs_dir: Path) -> tuple[list, dict[str, str]]:
    """Nav (Home, then one section per '## Heading' with the pages its table
    links) and each page's "What it covers" text."""
    nav: list = [{"Home": INDEX_PAGE}]
    summaries: dict[str, str] = {}
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
            summaries[row.group(2)] = _plain(row.group(3))
    return [entry for entry in nav if next(iter(entry.values()))], summaries


def _plain(markdown: str) -> str:
    text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", markdown)
    return " ".join(re.sub(r"[*_`]", "", text).split())


def _mermaid_script() -> Path:
    """The cached Mermaid bundle, downloaded and verified on first use."""
    cached = CACHE_DIR / f"mermaid-{MERMAID_VERSION}"
    script, licence = cached / "mermaid.min.js", cached / "LICENSE"
    if script.is_file() and licence.is_file():
        return cached
    url = f"https://registry.npmjs.org/mermaid/-/mermaid-{MERMAID_VERSION}.tgz"
    with urllib.request.urlopen(url, timeout=60) as response:
        tarball = response.read()
    algorithm, expected = MERMAID_INTEGRITY.split("-", 1)
    actual = base64.b64encode(hashlib.new(algorithm, tarball).digest()).decode()
    if actual != expected:
        raise ValueError(f"mermaid {MERMAID_VERSION}: integrity mismatch for {url}")
    cached.mkdir(parents=True, exist_ok=True)
    with tarfile.open(fileobj=io.BytesIO(tarball), mode="r:gz") as archive:
        for member, target in (("package/dist/mermaid.min.js", script), ("package/LICENSE", licence)):
            target.write_bytes(archive.extractfile(member).read())
    return cached


def on_config(config: MkDocsConfig) -> MkDocsConfig:
    config.nav, config.extra["summaries"] = _read_index(Path(config.docs_dir))
    config.extra["mermaid_script"] = MERMAID_SCRIPT
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
    if any("```mermaid" in Path(f.abs_src_path).read_text(encoding="utf-8")
           for f in files.documentation_pages()):
        mermaid = _mermaid_script()
        files.append(File.generated(config, MERMAID_SCRIPT, abs_src_path=str(mermaid / "mermaid.min.js")))
        files.append(File.generated(config, MERMAID_SCRIPT.replace(".min.js", ".LICENSE.txt"),
                                    abs_src_path=str(mermaid / "LICENSE")))
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


def on_page_markdown(markdown: str, page: Page, config: MkDocsConfig, files: Files) -> str:
    # docs/ pages all sit at the top level, so ../ always means the repository root.
    if posixpath.dirname(page.file.src_uri):
        raise ValueError(f"{page.file.src_uri}: nested docs pages need a ../ rewrite rule")
    markdown = _rewrite_parent_links(markdown)
    if page.is_homepage:
        page.meta.setdefault("template", "home.html")
        page.meta.setdefault("description", config.site_description)
    elif page.file.src_uri in config.extra["summaries"]:
        page.meta.setdefault("description", config.extra["summaries"][page.file.src_uri])
    return markdown


def on_post_build(config: MkDocsConfig) -> None:
    site_dir = Path(config.site_dir)
    (site_dir / "robots.txt").write_text(
        "User-agent: *\nAllow: /\n\nSitemap: " + config.site_url + "sitemap.xml\n",
        encoding="utf-8",
    )
