---
rendered_by: super-coder
source: db
edit: changes here are overwritten — author via the shell or localhost GUI
feature: rst-c documentation: feature /docs pages + README md-converter conversion
roadmap_status: next
frozen: false
title: rst-c documentation plan
tags: [docs, md-converter, readme, planning]
date: 2026-06-24
project: Documentation
purpose: Feature /docs pages + README conversion
---

## Overview

rst-c inherited a strong single-file `README.md` when the super-coder engine
was brought into the repo, but it never got real product documentation — the
`/docs` folder holds only `integration-runner.md`. This feature rewrites the
README in themed-markdown and turns the README's per-feature prose into
thorough, browsable, **md-converter-compatible** `/docs` pages: seven grouped
pages, linked from the README by GitHub URL.

### What "md-converter compatible" means

md-converter is `md-converter.designs-os.com` — the themed-markdown renderer
the GUI opens docs in. Compatible authoring means: H2s become tabs; only the
allowed constructs (callouts, stat cards, Mermaid, `linear`, GFM tables, images
with absolute URLs, bare video URLs); **no raw HTML, no H4–H6**; and an
"Open in md-converter" badge in each committed file's preamble.

Video: a bare video URL **alone on its own line** renders as a player.
`github.com/user-attachments/assets/<id>` URLs work directly (md-converter
follows the 302 to the signed S3 at play time). Don't wrap in `![]()` or `[]()`.

## Scope

Eleven README features become seven `/docs` pages. Three embed videos (bare
GitHub attachment URLs).

### Page groupings

| Page | Features | Has video |
|---|---|---|
| Profile Loader | Profile Loader | yes |
| Profile Builder | Profile Builder · Live Profile Switching · Export & Import | no |
| Custom URL Slots | Custom URL Slots | yes |
| Appearance | Branding Panel · Colored Panels | no |
| Ribbon Tools | RSTify · Required Add-ins | no |
| Health Tool | Health Tool | yes |
| Logging | Logging | no |

Each page is authored as themed-markdown with H2 tabs (e.g. `Overview` ·
`Using it` · `Configuration` · `Notes & limits`). "More thorough" means mining
the **actual source** per feature for config knobs, file paths, edge cases, and
failure modes — not just re-flowing README prose.

The README is rewritten to themed-markdown: one-paragraph feature summaries
linking to each `/docs` page by GitHub URL, plus a `Docs` index section.

## Decisions

### Locked

- **Files live in `/docs` as plain committed repo files**, not engine
  `documents`/`docs_sc/`. These are product docs; precedent is the existing
  `docs/integration-runner.md`. Authored on a branch → PR.
- **Each committed page carries the "Open in md-converter" badge** in its
  preamble (between H1 and first H2, so GitHub shows it, the render drops it).
- **README is rewritten as themed-markdown** — same convention as `/docs`,
  same badge. Not kept GitHub-first; the badge is the entry point for the
  full render.
- **Granularity: 7 pages** with the groupings above.
- **Video: bare URL on its own line** — md-converter renders a native player.
  GitHub user-attachments URLs work as-is (followed at play time). No thumbnail
  fallback needed.

## Plan and sequence

```linear
Sync pln1 branch to origin/main :::class1 -> Branch docs/feature-pages :::class1 -> Mine source per feature for detail :::class2 -> Author 7 /docs pages (themed-markdown + badge) :::class2 -> Rewrite README: summaries + Docs index (themed-markdown + badge) :::class2 -> Verify all pages render in md-converter :::class3 -> PR, stop at FnB merge gate :::class4
```

## Notes

### GitHub attachment URLs

`github.com/user-attachments/assets/<uuid>` 302-redirects to a short-lived
signed S3 URL (`X-Amz-Expires=300`, content-type `video/mp4`). md-converter's
video player follows the redirect at play time — the signed URL is fetched fresh
on each play, so expiry is not an issue for the viewer.

The `x-frame-options: deny` on the github.com response blocks `<iframe>` — a
`<video>` element is the correct primitive, which is what md-converter uses.
