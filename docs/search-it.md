---
title: Search-It
tags: [rst-c, search, ribbon, tools]
date: 2026-11-17
project: rst-c
purpose: Guide to Search-It, the RST panel's tool finder, covering search, chips, the tools listed for the open file, badges, running a tool and how the window closes
---

# Search-It

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/search-it.md)

## Overview

You know a Revit tool exists, but not which tab it is on. **Search-It** finds it. Click **Search-It** on the **RST** panel of the Add-Ins tab, type part of the tool's name, and click the tool in the list (or press Down, then Enter) to run it. Each result also shows the tab and panel the tool lives on, so you learn where it is for next time.

Search-It lists the tools for the file and view you are in. In a family you get family tools; in a project, project tools. A tool that cannot run in this view but runs in another view of the same file is listed with a **Must Run In** badge that names those views, so you know where to go.

Search-It is the fifth button on the RST panel, after Builder, Loader, RSTify and Health. It comes with every install.

## Opening it

The window opens in the middle of the Revit window, ready to type: the cursor is already in the search box, so the first key you press after clicking the button goes into the search. No extra click is needed.

## Searching

Start typing; the list updates with every letter.

- **You search tool names**, as the ribbon shows them: "Align", "Wall: Architectural", "Aligned Dimension".
- **Typos and skipped letters are fine.** "algn", "aling" and "allign" all find Align. Upper and lower case do not matter.
- **The best matches come first:** the exact name, then names that start with what you typed, then names with a word that starts with it, then names that contain it anywhere, then the near misses.
- **Each result is a card** with the tool's name and, under it in smaller text, where it lives: `Tab › Panel`, for example `Modify › Modify`. A tool on the family editor's own panel (Load into Project, for example) shows `Family Editor`.
- **A tool that sits in more than one place appears once**, with a note such as "+2 more places". Revit sometimes gives the same tool a different name in each place ("Tag Room" on Architecture, "Room Tag" on Annotate). You can search by either name; the card shows the name you matched and the place that uses it.
- **Eight results show at a time.** Scroll for the rest. The line under the search box says how many tools match, for example "12 of 1,204 tools". In a family it says "family tools" instead.
- **Badges** sit at the right of a card: **Must Run In** and **NOT IN GUI** (see [Badges](#badges)).

With nothing typed, the list holds every tool in alphabetical order: first the tools that run here, then the Must Run In ones.

## Chips: narrowing a search

Press **Enter** in the search box to keep what you typed as a **chip**. A chip is a filter: only tools whose name contains that text stay in the list. Then type again to search inside what is left.

- **Chips are exact.** A chip keeps a tool only if the tool's name contains the chip's text (upper and lower case do not matter). Chips do not forgive typos; only the text in the box does.
- **Several chips all apply.** With the chips `wall` and `struct`, only names that contain both remain.
- **The text in the box still searches loosely**, inside the tools the chips kept.
- **To remove a chip**, click its **×**, or press **Backspace** while the box is empty to remove the last one. Holding Backspace down to clear the box stops at the empty box: press it again to remove a chip.

## Tools for the open file

Each time the window opens, Search-It looks at what Revit is showing for the active file and view, and lists what can run there.

- **A family lists family tools; a project lists project tools.** A project template (`.rte`) opened directly counts as a project. With a family and a project both open, the list follows the window you are in.
- **Tabs and panels that are not showing for this file are left out**, so Wall does not appear in a family and Extrusion does not appear in a project. The one exception is a tab **you** chose to hide in a project (the Structure tab, for example): its tools stay listed. Most of them still run; a tool that is only on hidden tabs and that Revit does not let add-ins start (see [Running a tool](#running-a-tool)) shows the amber message instead.
- **A tool that cannot run in this view but runs in another view of the same file** is listed with **Must Run In**. Level in a floor plan, for example, shows **Must Run In Elevation, Section**.
- **A tool that runs in no view of this file is left out.** HVAC Zone is not listed in a project, and Electrical Connector is not listed in a Casework family (it runs in lighting, equipment and other MEP families). Search-It does not point you to another kind of family.
- **A greyed add-in button is left out.** A Revit tool that needs something selected follows Revit's own check when the window opens, so select first, then open Search-It.

The list is set when the window opens. If you switch views, close Search-It and open it again.

## Badges

| Badge | Means | A click |
|---|---|---|
| **Must Run In** *views* | The tool cannot run in this view. It runs in the views named, in this same file. Up to two names show, then `+N` for the rest, for example **Must Run In Floor Plan, Ceiling Plan +3**. Hover over the badge for the full list. The card is dimmed and comes after the cards that run with an equally good match. | Does nothing. The window stays open. Switch to one of the named views and open Search-It again. |
| **NOT IN GUI** (amber) | The tool runs here, but in this file Revit draws no button for it on the ribbon. Site Settings in a project is one. The card shows the place where Revit keeps it. | Runs it. |

Badges apply to Revit's own tools in Revit 2026 and 2027 (see [Notes & limits](#notes--limits)).

## Running a tool

**With the mouse:** click a result.

**With the keyboard:** press **Down** to move from the search box to the first result, **Up** and **Down** to move through the results (the list scrolls to follow), and **Enter** to run the highlighted one.

Either way, RST first checks with Revit that the tool can run right now (a tool that is only on tabs you hid in a project is started without that check, if Revit lets add-ins start it). If it can, the window closes and Revit starts the tool, as if you had clicked it on the ribbon. Tools that Revit does not let add-ins start, such as Site Settings, are started through the ribbon's own command, so they run from Search-It too (they still cannot go on a profile toolbar).

If the tool cannot run right now, nothing starts and the window stays open with an amber message, for example: "This tool can't be run with RST here: Level (Architecture › Datum)." This can happen when the view or selection changed since the window opened.

If the tool still cannot start after the window has closed, RST tells you in a small dialog, the same way a profile button does when its add-in is not installed or is switched off.

## Keys

What **Enter** does depends on where you are.

| Where | Key | What it does |
|---|---|---|
| Search box | Enter | Keeps the text as a chip. It never runs a tool. |
| Search box | Down | Moves to the first result. |
| Search box | Backspace (box empty) | Removes the last chip. |
| A result | Up / Down | Moves to the result above or below. Up on the first result goes back to the search box. |
| A result | Enter | Runs that tool (a Must Run In card does nothing). |
| A result | Any letter, number or symbol | Goes back to the search box and types it there. |
| A result | Backspace | Goes back to the search box. |
| Anywhere | Esc | Closes the window without running anything. |

The result you are on is highlighted.

## The window

Search-It stays out of your way. It is a small window that floats above Revit, and you can keep working in the view behind it:

- **Pan, zoom and orbit** with the middle mouse button and the wheel. The window stays open. When you let go of the middle button, Search-It takes the keyboard back, so you can go on typing your search.
- **Esc** closes it (while it has the keyboard).
- **A left or right click anywhere outside it** closes it.
- **Switching to another program** (Alt+Tab) closes it.
- **Drag its edges** to make it bigger or smaller. The size is not remembered: it opens at its usual size next time. It has no minimize or maximize button.
- **Only one opens at a time.** If you click the Search-It button while the window is open, that click is a click outside it: the window closes and a fresh one opens. A double-click on the button leaves you with one window, ready to type. If you start Search-It from a keyboard shortcut instead, the window that is already open comes to the front with the cursor in the search box.

It follows Revit's light or dark theme.

## What you can search

Search-It searches Revit's own ribbon, including the family editor's tabs, and the buttons of your installed add-ins, under the names the ribbon shows. From that, it lists the tools for the open file and view (see [Tools for the open file](#tools-for-the-open-file)). Tools your administrator has blocked are never listed.

It finds more than the [Profile Builder](profile-builder.md)'s Commands list: family-editor tools, and Revit tools that add-ins cannot start, which Search-It runs through the ribbon. A profile toolbar can hold only the Builder's tools, so the Builder's list is unchanged.

## The first search of a session

The first time you open Search-It, the Builder or the Loader after starting Revit, RST reads the whole ribbon; the three share what it finds. This takes a few seconds. If Search-It is the one doing that first read, its window says **Finding Revit's tools…** while it happens. You can start typing straight away; clicks made during the wait are ignored, so a stray click cannot start a tool. After that, Search-It opens straight away, with its results, until you close Revit.

## Notes & limits

- **Names only.** Search-It does not search tooltips, keyboard shortcuts or command ids.
- **Names as your Revit shows them.** Tools are listed under the names on your ribbon. Accents count: "ecole" does not find "École".
- **Contextual tools.** The tools Revit adds when something is selected ("Modify | Walls") are not listed.
- **Badges need Revit 2026 or 2027.** They come from RST's [Ribbon Tool Census](../census/README.md) ([browse the command index](../census/COMMANDS.md)), a record of where each stock Revit tool runs, made by starting every tool in the project views and the stock family templates. A view the census did not run uses its nearest match (an area plan uses the floor plan's record). In Revit 2025, and in a file or view with no match, Search-It lists what Revit says is available and shows no badges.
- **If RST cannot read the ribbon's state**, Search-It falls back to the whole list, without badges. In a family this can include project tools, and the count line says "tools".
- **Revit's own tools only.** Add-in buttons never get a badge; a greyed one is left out.
- **English Revit.** In another language, a tool Revit does not draw may be shown without its NOT IN GUI badge.
- **Custom tools** (the web and email links a profile can hold) are not tools Revit runs, so Search-It does not list them.
- **You can put Search-It on a profile toolbar** like the other RST tools: it is in the Builder's Commands list under RST.
