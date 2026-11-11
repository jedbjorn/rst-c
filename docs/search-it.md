---
title: Search-It
tags: [rst-c, search, ribbon, tools]
date: 2026-11-11
project: rst-c
purpose: Guide to Search-It, the RST panel's tool finder, covering search, chips, running a tool and how the window closes
---

# Search-It

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/search-it.md)

## Overview

You know a Revit tool exists, but not which tab it is on. **Search-It** finds it. Click **Search-It** on the **RST** panel of the Add-Ins tab, type part of the tool's name, and click the tool in the list to run it. Each result also shows the tab and panel the tool lives on, so you learn where it is for next time.

Search-It is the fifth button on the RST panel, after Builder, Loader, RSTify and Health. It comes with every install.

## Searching

The cursor is already in the search box when the window opens. Start typing; the list updates with every letter.

- **You search tool names**, as the ribbon shows them: "Align", "Wall: Architectural", "Aligned Dimension".
- **Typos and skipped letters are fine.** "algn", "aling" and "allign" all find Align.
- **The best matches come first:** the exact name, then names that start with what you typed, then names with a word that starts with it, then names that contain it anywhere, then the near misses.
- **Each result is a card** with the tool's name and, under it in smaller text, where it lives: `Tab › Panel`, for example `Modify › Modify`. A tool that sits in more than one place appears once, with its first place and a note such as "+2 more places".
- **Eight results show at a time.** Scroll for the rest. The line under the search box says how many tools match, for example "12 of 1,204 tools".

With nothing typed, the list holds every tool in alphabetical order.

## Chips: narrowing a search

Press **Enter** to keep what you typed as a **chip**. A chip is a filter: only tools whose name contains that text stay in the list. Then type again to search inside what is left.

- **Chips are exact.** A chip keeps a tool only if the tool's name contains the chip's text (upper and lower case do not matter). Chips do not forgive typos; only the text in the box does.
- **Several chips all apply.** With the chips `wall` and `struct`, only names that contain both remain.
- **The text in the box still searches loosely**, inside the tools the chips kept.
- **To remove a chip**, click its **×**, or press **Backspace** while the box is empty to remove the last one.

Enter never runs a tool. It only makes a chip.

## Running a tool

**Click a result.** The window closes and Revit starts the tool, as if you had clicked it on the ribbon.

If Revit cannot start it, RST tells you why in a small message, the same way a profile button does: the add-in the tool belongs to is not installed or is switched off, or the tool is one Revit does not let add-ins start.

## The window

Search-It stays out of your way. It is a small window that floats above Revit, and you can keep working in the view behind it:

- **Pan, zoom and orbit** with the middle mouse button and the wheel. The window stays open.
- **Esc** closes it (while it has the keyboard).
- **A left or right click anywhere outside it** closes it.
- **Switching to another program** (Alt+Tab) closes it.
- **Drag its edges** to make it bigger or smaller. The size is not remembered: it opens at its usual size next time.
- **Clicking Search-It again** while it is open brings you back to the search box. A second window never opens.

It follows Revit's light or dark theme.

## What you can search

Search-It lists every tool that RST can put on a profile toolbar: exactly the tools the [Profile Builder](profile-builder.md)'s Commands list offers. That is Revit's own ribbon and the buttons of your installed add-ins. Tools RST knows will not run from a button are left out of both: tools that only work inside the family editor or another editing mode, tools Revit does not let add-ins start, and any your administrator has blocked.

So if a tool is in the list, it will run. The two lists come from the same place and can never disagree.

## The first search of a session

The first time you open Search-It (or the Builder) after starting Revit, RST reads the whole ribbon. This takes a few seconds, and the window says **Finding Revit's tools…** while it happens. After that, Search-It opens straight away until you close Revit.

## Notes & limits

- **Names only.** Search-It does not search tooltips, keyboard shortcuts or command ids.
- **English names as your Revit shows them.** Tools are listed under the names on your ribbon.
- **Contextual tools.** A tool that needs something selected, or a particular kind of view, starts the same way it would from the ribbon: Revit may tell you what it needs first.
- **Custom tools** (the web and email links a profile can hold) are not tools Revit runs, so Search-It does not list them.
- **You can put Search-It on a profile toolbar** like the other RST tools: it is in the Builder's Commands list under RST.
