# Documentation site

The site at <https://mangapixer.com> is built from the pages in [`docs/`](../docs/README.md) with [MkDocs](https://www.mkdocs.org/) and the [Material](https://squidfunk.github.io/mkdocs-material/) theme ([`mkdocs.yml`](../mkdocs.yml) at the repository root). `docs/` stays the single source: write pages for GitHub as usual and the site follows.

- **New page:** add it to a table in [`docs/README.md`](../docs/README.md). The site nav and each page's search description come from those tables; a page missing from them fails the build.
- **Links:** link other docs pages relatively (`reader.md#image-quality`). Links to files outside `docs/` (`../CHANGELOG.md`) point at GitHub on the site, and `../assets/screenshots/` images are copied in.
- **Diagrams:** ```` ```mermaid ```` blocks render on the site with a pinned Mermaid copy served from the site itself.

## Build locally

With Python 3.12 (or in a `python:3.12-slim` container), from the repository root:

```bash
python -m pip install --require-hashes --no-deps -r docs-site/requirements.txt
mkdocs build --strict                  # output in site/ (git-ignored)
python docs-site/check_links.py site   # internal links, anchors, sitemap, no third-party loads
mkdocs serve                           # preview on http://127.0.0.1:8000
```

The first build downloads Mermaid from the npm registry, checks its hash and caches it in `docs-site/.cache/` (git-ignored).

## Files

| File | Purpose |
|---|---|
| `mkdocs.yml` | Site settings, theme, Markdown extensions, SEO values. |
| `docs-site/hooks.py` | Build-time adaptation of `docs/`: nav, outside links, images, descriptions, JSON-LD, `robots.txt`, Mermaid. |
| `docs-site/overrides/` | Theme overrides: social and structured-data meta, the landing page, the repository link, styles, the social preview image. |
| `docs-site/check_links.py` | Offline check of the built site. |
| `docs-site/make_social_card.py` | Regenerates `overrides/assets/social-card.png` from the app icon and a README screenshot (needs Pillow). |
| `docs-site/requirements.in` / `.txt` | Pinned tooling; `.txt` is the hash-locked output of `pip-compile --generate-hashes`. |
| `docs/CNAME` | The custom domain for GitHub Pages. |
| `.github/workflows/docs-site.yml` | Builds on `dev`, `main` and pull requests into `dev`; deploys from `main` only. |

Nothing here ships in the MangaPixer image or installer: the tooling runs at build time only.
