---
title: Appearance
tags: [rst-c, branding, colours, panels, appearance]
date: 2026-06-24
project: rst-c
purpose: Guide to branding and coloured panels on a profile tab
---

# Appearance

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/appearance.md)

## Overview

A profile tab carries two layers of visual identity: a **branding panel** at the leading edge that shows a company logo, and **coloured panels** that let admins group tools by hue so users can navigate by glance rather than by label.

## Branding

### What it is

Every profile tab can display an 85×85 pixel square branding image with rounded corners at the far left of the tab. It is typically a company or team logo. The panel is non-interactive by default; a URL can optionally be attached so clicking the logo opens a link (see [Custom URL Slots](custom-url-slots.md)).

The branding panel sits at the leading edge of the tab so the curated ribbon is visibly associated with the organisation that built it. This is intentional — a shared ribbon that carries no identity makes it harder for users to know they are working from a curated environment.

### Setting a logo

The logo is set in the Builder as a machine-wide default. Once set, it applies to all profiles created or edited on that machine from that point forward. Logos are stored at `%AppData%\RST\branding.png`. On first launch with no logo set, rst-c seeds the branding panel with the bundled RST default logo.

When you pick a new logo in the Builder, rst-c resizes it to 85×85 pixels and saves it to `%AppData%\RST\branding.png`. The original file is not modified.

### Logo in exported profiles

The branding image travels with the profile. When a profile is exported as a `.rstprofile` zip, rst-c bundles the resolved logo as `assets/branding.png` inside the archive. On import, the logo is extracted to `%AppData%\RST\<profile-id>\branding.png` on the receiving machine — separate from that machine's own global branding — so the imported profile renders with its original logo without touching the recipient's own branding setup.

> [!class2]
> An org can share profiles to users whose machines have a different logo set. The imported profile's logo is isolated per-profile and does not overwrite the recipient's global branding.

## Coloured panels

### What it is

Each panel on a profile tab can carry a custom background colour and an opacity between 10% and 100%. The colour is drawn as a rounded rectangle behind the panel's buttons and title strip, on top of Revit's native panel chrome. The intent is grouping by glance — a user who has worked with the profile a few times should find the "detailing" panel by colour before they read any label.

### Setting panel colours

Panel colours are set in the Builder per panel. You provide a hex colour code and an opacity value. The colour applies to the panel body and the title strip separately, each with ~5px rounded corners, matching the rounding that pyRevit's ribbon styling used.

```stats
:::class1
value: 85×85
label: Branding panel size
description: Square pixels, rendered with rounded corners
:::class2
value: 10–100%
label: Panel opacity range
description: Colour intensity per panel, set in the Builder
:::class3
value: 5px
label: Corner radius
description: Rounded corners on panel body and title strip
```

### How colours are applied

rst-c applies colours via the Autodesk.Windows (`AdWindows`) ribbon API, setting `CustomPanelBackground` and `CustomPanelTitleBarBackground` on each panel. The body and title strip receive separate brush instances so both render with correctly proportioned corners regardless of the panel's item count.

Panel width is estimated from item count (each large item is approximately 96px wide) because `AdWindows.RibbonPanel` does not expose `ActualWidth`. The corner radius is calculated as a relative fraction of the estimated dimensions to target the absolute 5px result.

The effect is purely visual — colour changes nothing about how commands behave.

## Notes & limits

- Colours are applied on the UI thread when the profile tab is built. They update when a new profile is applied live.
- If a hex colour is unparseable, rst-c falls back to a solid colour brush without the rounded corners rather than failing the whole apply.
- The branding panel logo is PNG only. Other image formats are not tested.
- Per-profile branding overrides in the profile JSON are supported for legacy and hand-edited profiles, but the Builder today writes the machine-wide default for all new profiles.
- Opacity applies to the colour fill; the panel's button icons and labels are not affected.
