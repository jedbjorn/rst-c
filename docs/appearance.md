---
title: Appearance
tags: [rst-c, branding, colours, panels, appearance]
date: 2026-10-31
project: rst-c
purpose: Guide to branding and coloured panels on a profile tab
---

# Appearance

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/appearance.md)

## Overview

A profile tab carries two layers of visual identity: a **branding panel** at the leading edge that shows a company logo, and **coloured panels** that let admins group tools by hue so users can navigate by glance rather than by label.

rst-c's own windows (the Loader, the Builder and Health) follow Revit's light or dark theme; see [Notes & limits](#notes--limits).

## Branding

### What it is

Every profile tab can display an 85×85 pixel square branding image with rounded corners at the far left of the tab. It is typically a company or team logo. The panel is display-only: it shows the logo and nothing else.

The branding panel sits at the leading edge of the tab so the curated ribbon is visibly associated with the organisation that built it. This is intentional: a shared ribbon that carries no identity makes it harder for users to know they are working from a curated environment.

![A profile tab on the Revit ribbon showing branding and panel colours together: the logo at the leading edge, followed by panels each tinted a different colour, one of them holding stacked buttons beside large ones](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/ribbon-profile-loaded.png)

### Setting a logo

Branding is a setting for the machine (per Windows user), not part of a profile. Set it in the Builder: click **⚙** (Settings for this machine), then **Branding…**, then **Pick…** and choose an image. rst-c resizes a PNG, JPG or BMP to 85×85 pixels and saves it as `%AppData%\RST\branding.png`; the original file is not changed. The dialog also offers SVG, but an SVG is copied as picked, without resizing, and the ribbon may not display it. **×** clears it.

The logo appears on every profile tab on this machine that does not carry its own logo (see below), the next time the tab is built. On first launch with no logo set, rst-c seeds `branding.png` with the bundled RST logo.

The Branding dialog also has a **URL** box. The URL is stored with the branding (`%AppData%\RST\branding.json`) and included in exported profiles. The ribbon's branding panel does not open it.

### Logo in exported profiles

The branding travels with the profile. When you export a profile with **Export ZIP**, rst-c puts the logo in the `.zip` as `assets/branding.png` and the URL in `profile.json`. On import, the logo is extracted to `%AppData%\RST\<profile-id>\branding.png` on the receiving machine, separate from that machine's own branding, so the imported profile renders with its original logo without touching the recipient's setup.

> [!class2]
> An organisation can share profiles with users whose machines have a different logo set. The imported profile's logo is kept per profile and does not overwrite the recipient's own branding.

If the recipient later edits and saves that profile in their Builder, it switches to their machine's branding: the Builder treats branding as a machine setting and saves no per-profile logo.

## Coloured panels

### What it is

Each panel on a profile tab can carry its own background colour, and the profile sets how strongly those colours are drawn: an opacity from 10% to 100%. The colour is drawn as a rounded rectangle behind the panel's buttons and title strip, on top of Revit's native panel chrome. The intent is grouping by glance: a user who has worked with the profile a few times should find the "detailing" panel by colour before they read any label.

### Setting panel colours

In the Builder, click the colour swatch in a panel card's header to open the colour picker: a palette, recent colours, a hex box (`#RRGGBB`), or pick a colour from anywhere on screen. The panel's stripe and swatch take the new colour. The profile's **Panel colour strength** (the Profile pane's **General** tab) runs from 10% to 100% in steps of 5 and applies to every panel. Users can override it for themselves in the Loader with **Override Panel Opacity**.

The Builder's Live Preview and the Loader's Tab Preview draw the panels with these colours, on a light or dark background to match Revit's theme.

```stats
:::class1
value: 85×85
label: Branding panel size
description: Square pixels, rendered with rounded corners
:::class2
value: 10–100%
label: Panel opacity range
description: Per profile (Panel colour strength in the Builder); users can override it in the Loader
:::class3
value: 5px
label: Corner radius
description: Rounded corners on panel body and title strip
```

### How colours are applied

rst-c applies colours through the Autodesk.Windows (`AdWindows`) ribbon API, setting `CustomPanelBackground` and `CustomPanelTitleBarBackground` on each panel. The body and the title strip get separate brushes, each with ~5px rounded corners, so both render with correctly proportioned corners whatever the panel's item count. This matches the rounding pyRevit's ribbon styling used.

Panel width is estimated from the item count (each large item is about 96px wide), because `AdWindows.RibbonPanel` does not expose `ActualWidth`. The corner radius is calculated as a fraction of the estimated size to land on 5px.

The effect is purely visual: colour changes nothing about how commands behave.

## Notes & limits

- Colours are applied when the profile tab is built, and update when a new profile is loaded.
- If a hex colour cannot be read, rst-c falls back to a plain colour without rounded corners rather than failing the whole load.
- The Builder converts the picked logo to an 85×85 PNG. If it cannot read the image (any SVG, or a damaged file), it stores the file as picked, unresized, and the ribbon may not be able to show it; PNG is the safest choice.
- Per-profile branding in the profile JSON comes from imported (or hand-edited) profiles. The Builder itself never writes per-profile branding.
- Opacity applies to the colour fill; the panel's icons and labels are not affected.
- **rst-c's windows** take Revit's UI theme (Options → Colors → UI active theme, including "Use system setting") when they open. Change Revit's theme while one is open and it keeps its theme until you reopen it.
