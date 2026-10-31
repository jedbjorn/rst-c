---
title: Profile Builder
tags: [rst-c, builder, profiles, admin, custom-tools, stacks, export, import]
date: 2026-10-31
project: rst-c
purpose: Admin guide to building and managing profiles
---

# Profile Builder

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/profile-builder.md)

## Overview

The Builder is the admin counterpart to the Loader. It reads every command on every tab of the running Revit session (native Revit commands, add-in commands, pyRevit buttons: anything Revit's ribbon knows about) and presents them as a searchable catalogue you can drag into panels on a new profile tab. Next to the catalogue it keeps your **Custom Tools**: the URL and email buttons used in your profiles, ready to reuse. The finished profile is saved for the Loader and can be exported as a self-contained `.zip` that any rst-c install can import.

Live profile switching (no Revit restart) is part of the same system: the profile the Builder saves is what the Loader loads. Export and import are how profiles are distributed.

Each setting lives in one place, at the scope it governs:

```linear
This machine: ⚙ at the far left :::class2 -> Which profile: the profile menu :::class1 -> The tab's panels and tools: the centre cards :::class1 -> The profile's settings: the Profile pane :::class1 -> How it looks: the Live Preview :::class3
```

## Building a profile

![The Profile Builder window: along the tinted header, the ⚙ machine settings button, the profile menu showing the loaded profile with its green dot, Export ZIP and the title, with Save and Exit on the right; the Commands pane on the left with Filter, Create new and the Custom Tools group above the Revit command catalogue; the profile's panels in the centre as cards of tool tiles, some holding stacks; the Profile pane on the right on its General tab; and the Live Preview of the ribbon tab along the bottom](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/builder-overview.png)

### Opening the Builder

Click **Builder** on the **RST** panel of the **Add-Ins** tab. The window, titled **RST — Profile Builder**, opens centred over Revit, belongs to Revit (no taskbar button of its own; Revit waits until you close it) and uses Revit's light or dark theme as it was when the window opened.

The Builder opens on the profile the Loader has loaded, ready to edit. With nothing loaded, or when the loaded profile is not among the saved profiles or cannot be opened, it starts on a new, unnamed profile ("New profile") with one empty panel, "Panel 1". To start a new profile, choose **+ New profile** from the [profile menu](#the-profile-menu); to edit another saved profile, choose it there. A loaded profile made by an older version, or one whose stacks need a repair, opens with "Unsaved changes"; saving it once clears that.

### The window

- **Header** (tinted, so it reads apart from the panes), left to right: **⚙** (settings for this machine), the profile menu button with the profile's name (a long name is shortened; hover it for the full name) and a green dot ("Loaded") when it is the one the Loader has loaded, **Export ZIP**, then a divider in line with the edge of the Commands pane and the title "Profile Builder". On the right: "Unsaved changes" with a dot while there are any, **Save** (enabled only when there are changes) and **Exit**.
- **Commands (left):** the number of commands and custom tools, **Filter…** with **+ Create new**, the **Custom Tools** group, then the Revit command catalogue.
- **Panels (centre):** "Panels on tab" and the tab name, **+ Add Panel**, and one card per panel, in ribbon order.
- **Profile (right):** "Profile · editing <name>", with three tabs: **General**, **Loader defaults** and **Junk cleanup**.
- **Live Preview (bottom):** a read-only picture of the ribbon tab as it stands, updated as you edit.

**Escape** closes the Builder the way **Exit** does (asking first if there are unsaved changes), unless a menu, a dialog, the icon picker or a drag has it first.

### The profile menu

Click the profile button in the header. Under "Switch profile" it lists your saved profiles: the one in the editor is ticked, and each shows its tab name; the loaded one reads *loaded · tab "<tab>"*. Below them:

- **+ New profile:** a new, unnamed profile with one empty panel.
- **Rename…** (same profile, new name): see [Rename and Duplicate](#rename-and-duplicate).
- **Duplicate…** (a copy; original kept): only for a saved profile.

Choosing another profile, or **+ New profile**, with unsaved changes asks first (see [Unsaved changes](#unsaved-changes)). Your edits stay in the Builder until you save, so the menu keeps showing the profiles as saved.

### The command catalogue

The catalogue is read from Revit when the Builder opens. It is a tree:

- **Autodesk Native** groups every built-in Revit tab. It starts collapsed.
- Below it, add-in tabs and vendor panels, in alphabetical order.
- Each level runs tab → panel → command. Groups show how many commands they hold, and commands show a short form of their command id (hover for the full id).

Typing in **Filter…** keeps the commands whose name or id contains the text (case does not matter), opens the groups that hold matches and updates their counts. It filters the Custom Tools too. "No matches for "<text>"." means nothing matched.

To add a command, **drag** it onto a panel card or a tile, or **double-click** it to add it to the end of the selected panel (a panel is created if there is none).

### Custom Tools

The **Custom Tools** group sits first in the Commands pane, set apart and expanded, with the note "URL and email tools found in your profiles. Drag onto a panel." Click its header to fold it. Each row shows the tool's icon, its label and a **URL** or **EMAIL** tag; hover it (or move to it with the keyboard) for **✎** (edit everywhere) and **×** (remove everywhere). Its tooltip shows the link and "Used in: …", the profiles that hold it.

The list is not stored anywhere: it is built from every URL or email button in your saved profiles, plus the profile you are editing. Drag a row onto a panel, or double-click it, to place the tool there; each placement is a new button. **+ Create new** makes a new one. See [Custom Tools](custom-url-slots.md) for creating, editing and removing them.

### Panels

**+ Add Panel** adds "Panel N" in blue. Click a card to select it (an accent ring); double-clicked commands and Custom Tools go to the selected card. Each card shows:

- a colour stripe in the panel's colour;
- a header with a drag handle (**⠿**: drag the header to reorder panels), the **colour swatch** (it opens the colour picker, with a palette, recent colours, a hex box and a tool to pick a colour from anywhere on screen), the **panel name**, edited in place, and its tool count ("3 tools", stacked tools included);
- **+ New Stack** (see [Stacks](#stacks));
- **▲ ▼** (move left or right on the ribbon) and **×** (delete), shown on hover, with the keyboard, or on the selected card. Deleting asks **Delete panel "<name>"?** first, and takes the panel's stacks with it;
- its tools, as tiles and stacks, in ribbon order.

An empty card reads "Drag commands or custom tools here, or double-click one in Commands." The pane header's tab name ("Panels on tab <name>", or "(no tab name)") takes you to the **Ribbon tab name** box on the General tab when you click it.

### Tools

Each loose tool is a **tile**:

- its **32 px icon**: click it to pick an icon (a dashed frame means no icon);
- its **name**, edited in place: this panel's label for the tool, the text on its ribbon button;
- a muted line saying where it comes from: "tab · panel" for a command, the address for a custom tool;
- on hover, **✎** (custom tools only: edit it everywhere) and **×** (remove it from this panel, without asking);
- a tooltip with the full command id or link.

### Drag and drop

| Drag | Drop on | Result |
|---|---|---|
| A command or a Custom Tools row | A panel card, outside its tiles | Added at the end of that panel |
| A command, a Custom Tools row or a tile | The leading or trailing edge of a tile or a stack | Inserted before or after it |
| A command, a Custom Tools row or a tile | The middle of a loose tile | A new stack of the two, in the target's place; the dropped tool goes second |
| A command, a Custom Tools row or a tile | The middle of a stack, or one of its rows | The tool joins the stack (before or after that row), if it has room |
| A stack row | Anywhere outside its stack | The tool leaves the stack; a 2-tool stack dissolves and its other tool stays in its place |
| A stack, by its header | A tile, a stack or a panel card | The whole stack moves; it never goes inside another stack |
| A panel's header | Another panel card | Panels reordered, before or after by which half you drop on |

A tile drags by its icon or by any part of it outside the name box; a click on the icon without moving opens the icon picker. While you drag, the source fades and a marker shows where the item will land. A drag that starts inside a name box selects text instead, and releasing outside a target changes nothing. A full stack shows a red refusal marker with "A stack holds 2–3 tools. This one is full." and the drop is refused with the same message.

### Stacks

A **stack** is two or three small buttons, one above the other, in the space of one large button: the same layout as Revit's own stacked buttons. Stacks hold tools only (Revit or add-in commands, and [custom tools](custom-url-slots.md)). On the ribbon each stacked button has a 16 px icon and a one-line label; a tool without a pack icon gets rst-c's default icon (a dark ≡ on a light grey tile). See [Ribbon Tools](ribbon-tools.md#stacks-on-the-ribbon) for how they are drawn.

On a card a stack shows as a group headed "STACK · 2/3" (or 3/3), with **✎** (edit stack) and **×** (remove stack; its tools stay on the panel), then one row per tool: a 16 px icon (click to change it), the name, edited in place, and on hover **✎** (custom tools only) and **×** (remove that tool from this panel; a stack left with one tool becomes that tool).

There are three ways to make one:

- **Drop one tool onto the middle of another** on a panel (see the table above).
- **+ New Stack** on the card (enabled when the panel has at least two loose tools) opens **New stack on "<panel>"**. Tick 2 or 3 of the panel's loose tools and order them with ▲ ▼; **Create stack** is enabled at 2. The stack takes the place of the first tool you ticked.
- Drop a command or a Custom Tools row onto an existing stack with room.

**✎** on a stack opens the same dialog as **Edit stack on "<panel>"**: its tools are ticked, in stack order, and the panel's loose tools are listed too. Reorder, untick a tool to put it back on the panel (right after the stack), or tick a loose tool to add it. Saving with fewer than 2 ticked removes the stack and leaves its tools on the panel. **×** on a stack un-stacks it: its tools go back on the panel in order, in its place.

The Builder names stacks itself ("Stack 1", "Stack 2", … in the profile file) and never shows the names. Removing a stack never removes its tools.

When the Builder opens a saved profile, it can make two repairs to its stacks, each logged: a stack that two places on the panels share gets a copy for each place, and a stack that no panel places is dropped. The toast "This profile's stacks were repaired when it opened (N). Save to keep the repair." says so, and the profile counts as changed. Any other broken stack (an empty, one-tool or 4+ stack, or a place that names a missing stack) makes the file fail validation when it is read, so neither the Builder nor the Loader lists it (see [Notes & limits](#notes--limits)).

### Icons

Tool icons come from rst-c's built-in icon pack: 55 designs, each in seven colours. Click a tool's icon to open **Pick an icon**:

- **∅ none** (first) removes the icon.
- Clicking a design opens a row of its seven colours just below that design's row. Click a colour to use it.
- Arrow keys, Home and End move between the colours. **Escape** closes the colour row first and the picker on a second press. Clicking outside the picker closes it without a change.

The chosen icon shows on the tile, in the Live Preview and on the ribbon. A tool with no pack icon uses rst-c's default button icon on the ribbon (rst-c's own tools keep their own icons). There is no option to take an icon from the source command.

![The Pick an icon overlay over the Builder: the icon pack's designs in a grid with ∅ none first, and one design's row of seven colours open below it](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/builder-icon-picker.png)

## The Profile pane

The right-hand pane edits the profile as a whole. Every edit on its three tabs is an unsaved change, saved with **Save** like any other edit.

### General

- **Profile name:** read-only, with **Rename…** beside it (the same dialog as the profile menu's). A new profile reads "Not named yet: use Rename…".
- **Ribbon tab name:** the ribbon tab's label. The Live Preview's tab follows it as you type.
- **Panel colour strength:** 10% to 100% in steps of 5, the strength every panel's colour is drawn at. "One value for every panel. Users can override it in the Loader." See [Appearance](appearance.md#coloured-panels).
- **Min RST version:** the minimum RST version this profile expects (the `min_version` field, default `0.1.0`). Informational: rst-c does not enforce it (see [Notes & limits](#notes--limits)).

### Loader defaults

The defaults the Loader offers the first time a user selects this profile ("They can still tweak per load."):

- **Disable unused add-ins:** the starting position of the Loader's **Disable unused add-ins** switch.
- **Hide these tabs (RSTify):** one switch per ribbon tab on this machine, apart from the core tabs (Modify, Manage, View, Annotate, Add-Ins, Create) and the profile's own tab. Tabs the profile already hides but that are not on this ribbon are listed too. "No tabs available to hide on this machine." means there are none.

The defaults are stored in the profile's `presets` field. When a user first selects the profile, the Loader asks whether to adopt them; after that, the user's own choices apply. See [Ribbon Tools](ribbon-tools.md#how-admin-defaults-work).

![The Profile Builder with the Profile pane on its Loader defaults tab: the Disable unused add-ins switch and the "Hide these tabs (RSTify)" switch list, some tabs switched on](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/builder-rst-presets.png)

### Junk cleanup

The **Junk cleanup** tab chooses which of the Health tool's cleanup targets this profile offers in **Clean Junk Files**. The paths are fixed presets for safety: Temp, the Revit package cache, journals, the collaboration cache, the recent-file list and the RST logs (see [Health Tool](health-tool.md#junk-file-cleanup) for the paths). You cannot add a path of your own. Each row has:

- an on/off switch;
- the name, which you can edit (hover it for the preset path);
- **×**, which removes it from this profile.

**Restore defaults** brings back the full preset list. With every row removed, the tab reads `No targets. Use "Restore defaults".`

![The Profile Builder with the Profile pane on its Junk cleanup tab: the help text, then one row per cleanup target with an on/off switch, an editable name and ×, and Restore defaults below](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/builder-junk-cleanup.png)

A profile keeps its own cleanup targets when you edit and save it. Only a profile with no targets at all (one made before cleanup targets existed) is given the defaults.

## Machine settings

**⚙** at the far left of the header ("Settings for this machine") opens a menu headed "This machine — not saved in profiles". It holds **Branding…** (logo + link on every tab), which opens **Company Tab Branding**: "Shown on every RST tab on this machine. Not saved in profiles."

- **Logo:** **Pick…** chooses an image; rst-c resizes a PNG, JPG or BMP to 85×85 and stores it as `%AppData%\RST\branding.png`. The dialog also offers SVG, but an SVG is copied as picked, without resizing, and the ribbon may not display it. **×** clears it. The file name, or "(none)", is shown beside them.
- **URL:** a link kept with the branding and included in exported profiles. It is saved when you press Enter, leave the box or close the dialog.
- **Done** or **Escape** closes it. Changes apply straight away; there is nothing to cancel.

The branding appears at the leading edge of every profile tab on this machine. See [Appearance](appearance.md).

## Managing profiles

```linear
New profile :::class1 -> Rename… to name it, set the tab :::class2 -> Panels, tools and stacks :::class2 -> Loader defaults, Junk cleanup :::class2 -> Save :::class3 -> Export ZIP :::class3 -> Distribute to users :::class4
```

### Saving

**Save** writes the profile to `%AppData%\RST\profiles\` under its own id:

- A profile needs a name and a tab name: "Profile name required." and "Tab name required." say which is missing. Name a new profile with **Rename…**.
- A new profile is saved under a new file ("Saved as <file>").
- An existing profile is overwritten in place, also when you renamed it: it keeps its id, the file under the old name is replaced, and users who have it loaded keep it.
- A profile file with no id (made by an older version) gets one on its first save, and the old file is replaced. Such a profile counts as changed as soon as it opens, so opening it and clicking **Save** is enough.
- A stack of more than three tools cannot be saved: "Panel "<name>" has a stack of N tools. A stack holds 2–3 tools; move some out before saving."
- If the profile you saved is the loaded one, rst-c reloads it on the ribbon when you close the Builder ("Saved. The ribbon updates when you close the Builder."): Revit cannot rebuild the ribbon while the Builder is open.

### Required add-ins

When you save, the Builder works out which add-ins the profile depends on from the commands you used, and stores that list in the profile. The Loader checks it when the profile is loaded: it re-enables installed add-ins that are disabled and shows download links for missing ones. See [Ribbon Tools](ribbon-tools.md).

### Rename and Duplicate

**Rename…** (profile menu, or the General tab) opens **Rename profile**: "Same profile, new name. Users who have it loaded keep it." The name is required and must not be used by another saved profile (case does not matter). It changes only the name in the Builder, so it is an unsaved change: **Save** writes it under the same id, and the loaded profile stays loaded.

**Duplicate…** (profile menu, saved profiles only) opens **Duplicate profile** with "<name> copy" as the name (then "<name> copy 2", and so on, if taken). "A new profile with its own id, made from what you see now, unsaved edits included. The original stays as last saved." **Duplicate** writes the copy at once and switches the Builder to it; the toast reads "Duplicated as "<name>"; the original is kept." Any unsaved edits go with the copy; the original's file is not touched.

### Unsaved changes

If you have unsaved edits, the Builder asks before discarding them:

| You | Choices |
|---|---|
| Click **Exit**, press **Escape**, or close the window with **✕** | **Cancel**, **Discard & Exit**, **Save & Exit** |
| Choose another profile, or **+ New profile**, in the profile menu | **Cancel**, **Discard & Switch**, **Save & Switch** |

**Cancel** keeps you where you were, with your edits. **Save & Exit** closes only if the save succeeds.

### Deleting a profile

Profiles are deleted in the Loader: select the profile and click **Delete Profile**. The Builder has no delete action.

## Export and import

### Exporting a profile

Click **Export ZIP**. The profile needs a name. Choose where to save it in the **Export RST Profile (.zip)** dialog; the Builder confirms with "Exported to <path>". The export is the profile as it stands in the Builder; save as well if you want your own copy to match. The `.zip` contains:

- `profile.json`: the full profile definition, including its custom tools and the branding URL if one is set;
- `assets/branding.png`: the branding logo, if one is set.

The zip is self-contained: the recipient does not need to set up branding, because the logo travels with the profile.

### Importing a profile

Import happens in the Loader: click **+ Import from File** and choose the `.zip`. rst-c installs the profile into `%AppData%\RST\profiles\` and its logo into `%AppData%\RST\<profile-id>\`, and the profile appears in the Loader's list, ready to load. Its URL and email buttons appear in the Builder's Custom Tools from then on.

> [!class2]
> You never need to place or rename profile files by hand. Saving and importing put them in `%AppData%\RST\profiles\`.

## Notes & limits

- The catalogue is read from the running Revit session when the Builder opens. Open a project (even a blank one) first if you want add-in commands to appear. "No commands available." means Revit's commands could not be read: restart Revit and open the Builder again (the RST log has the details).
- An imported profile shows its own logo. Once you edit and save it in the Builder, it uses this machine's branding instead, because the Builder treats branding as a machine setting.
- A profile's `schemaVersion` keeps profiles compatible across versions. Older pyRevit-era profiles (no `schemaVersion`, treated as v0) can be loaded and saved again.
- **Min RST version** (the `min_version` field) is informational: the Loader shows it on the profile's card and details ("Min RST"), but rst-c does not check or enforce it when a profile is loaded.
- Stacks hold push buttons only: Revit and add-in commands and custom tools, two or three per stack. Pulldowns, split buttons, combo boxes, text boxes and stacks inside stacks are not supported, and the permanent RST panel on the Add-Ins tab has no stacks.
- A profile file whose stacks break the rules (for example a hand-edited stack of one or four tools, or a stack tool without a command id) fails validation, so neither the Loader nor the Builder lists it; the log says why. The Builder never writes such a file.
- Panels are reordered with ▲ ▼ or by dragging; tools are reordered only by dragging, or within a stack with its dialog.
- Profiles with stacks open in older versions of rst-c (v3.0.0 and earlier) without errors, but those versions do not draw the stacks on the ribbon.
