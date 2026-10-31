---
title: Profile Builder
tags: [rst-c, builder, profiles, admin, export, import]
date: 2026-10-30
project: rst-c
purpose: Admin guide to building, managing, exporting, and importing profiles
---

# Profile Builder

[![Open in md-converter](https://img.shields.io/badge/Open%20in-md--converter-6b46c1?style=flat-square)](https://md-converter.designs-os.com/?url=https://github.com/jedbjorn/rst-c/blob/main/docs/profile-builder.md)

## Overview

The Builder is the admin counterpart to the Loader. It reads every command on every tab of the running Revit session (native Revit commands, add-in commands, pyRevit buttons: anything Revit's ribbon knows about) and presents them as a searchable catalogue you can drag into panels on a new profile tab. The finished profile is saved for the Loader and can be exported as a self-contained `.zip` that any rst-c install can import.

Live profile switching (no Revit restart) is part of the same system: the profile the Builder saves is what the Loader loads. Export and import are how profiles are distributed.

## Building a profile

![The Profile Builder window: the profile dropdown, Branding, Name and Tab along the top with Export ZIP, Save and Exit on the right; the Commands tree filtered by a search on the left; the profile's panel cards and their tool slots in the centre; the selected panel's Properties on the right; and the Live Preview of the ribbon tab along the bottom](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/builder-overview.png)

### Opening the Builder

Click **Builder** on the **RST** panel of the **Add-Ins** tab. The window, titled **RST — Profile Builder**, opens centred over Revit, belongs to Revit (no taskbar button of its own; Revit waits until you close it) and uses Revit's light or dark theme as it was when the window opened.

The Builder opens on the profile that is loaded in the Loader, ready to edit. With no profile loaded, or when the loaded one is no longer among the saved profiles, it starts on **+ New Profile**: a blank profile with one empty panel, "Panel 1".

### The window

- **Header:** the profile dropdown, "Profile Builder", **Branding ▾**, the **Name** and **Tab** boxes, then **Export ZIP**, **Save** and **Exit**.
- **Commands (left):** the command catalogue, with its size and a **Filter…** box.
- **Panels (centre):** **+ Add Panel** and one card per panel, in ribbon order.
- **Properties (right):** Profile, Selected Panel, RST Presets and Junk Cleanup.
- **Live Preview (bottom):** a read-only picture of the ribbon tab as it stands, updated as you edit.

### Name and tab title

Every profile has a **Name** (shown in the Loader's list) and a **Tab** (the ribbon tab's label). Both are required to save. The preview's tab label follows the Tab box as you type.

### The command catalogue

The catalogue is read from Revit when the Builder opens. It is a tree:

- **Autodesk Native** groups every built-in Revit tab. It starts collapsed.
- Below it, add-in tabs and vendor panels, in alphabetical order.
- Each level runs tab → panel → command. Groups show how many commands they hold, and commands show a short form of their command id (hover for the full id).

Typing in **Filter…** keeps the commands whose name or id contains the text (case does not matter), opens the groups that hold matches and updates their counts. "No matches for "<text>"." means nothing matched.

To add a command, **drag** it onto a panel, or **double-click** it to add it to the selected panel (a panel is created if there is none).

### Panels and tools

**+ Add Panel** adds "Panel N" in blue. Click a card to select it; the selected card has an accent ring and its settings appear under **Selected Panel**. Each card shows:

- a colour stripe in the panel's colour;
- the panel name, editable in place, and its slot count;
- **▲ ▼** to move it, and **×** to delete it. Deleting asks **Delete panel "<name>"?**, with **Cancel** as the default;
- its tools, one row each: a **Pick icon** button (the icon and its name, or "+ Icon"), the tool's name (editable; it is the ribbon button's label), the short command id, and **▲ ▼ ×** (× removes the tool without asking).

An empty card reads "Drop a command here, or click one in the Commands pane."

A row that places a [stack](#stacks) shows a **stack** tag instead of the icon button. Its name is read-only there, because the name is what links the row to the stack.

### Drag and drop

| Drag | Drop on | Result |
|---|---|---|
| A command from the catalogue | A panel card | Added at the end of that panel |
| A command from the catalogue | A tool row | Inserted before or after it, by which half of the row you drop on |
| A tool row | Another tool row | Moved there, in the same panel or another |
| A tool row | Another panel's card | Moved to the end of that panel |
| A panel's header | Another panel card | Panels reordered, before or after by which half you drop on |

While you drag, the source row dims, the target card is highlighted, and a green line marks where an item will land. You can drop over the name boxes too. A drag that starts inside a name box selects text instead of moving anything, and releasing outside a target changes nothing.

### Icons

Tool icons come from rst-c's built-in icon pack: 55 designs, each in seven colours. Click a tool's **Pick icon** button to open **Pick an icon**:

- **∅ none** (first) removes the icon.
- Clicking a design opens a row of its seven colours just below that design's row. Click a colour to use it.
- Arrow keys, Home and End move between the colours. **Escape** closes the colour row first and the picker on a second press. Clicking outside the picker closes it without a change.

The chosen icon shows on the tool's row, in the Live Preview and on the ribbon. A tool with no pack icon uses rst-c's default button icon on the ribbon (rst-c's own tools keep their own icons). There is no option to take an icon from the source command.

![The Pick an icon overlay over the Builder: the icon pack's designs in a grid with ∅ none first, and the house design's row of seven colours open below it, with the tooltip "house — purple"](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/builder-icon-picker.png)

### Stacks

A **stack** is two or three small buttons, one above the other, in the space of one large button: the same layout as Revit's own stacked buttons. Stacks hold tools only (Revit or add-in commands, and [URL buttons](custom-url-slots.md)). On the ribbon each stacked button has a 16 px icon and a one-line label; a tool without a pack icon gets rst-c's default icon (a dark ≡ on a light grey tile, like the pack icons).

> **The Builder keeps stacks but cannot author them yet.** You can't add, fill or rename a stack in the Builder in this version; Builder stack authoring is coming in a later release. For now, add stacks by editing the profile's JSON file (see below).

What the Builder does with a profile that already has stacks:

- **Keeps its stacks exactly as they are when you save.** The first save of a hand-edited file also tidies the rest of it, as for any profile: it adds the default Junk Cleanup targets if there are none, recomputes the required add-ins, drops the profile's own branding and reformats the file. After that, saving without edits writes the same file.
- **Shows each placed stack** as a row with a **stack** tag in its panel card. You can move it with **▲ ▼**, drag it, or remove it with **×**. Its name is read-only.
- **Removing a stack's row** leaves the stack unplaced. **Save** then drops the stack from the file, because a stack no panel places is never drawn. The Builder can't put a removed stack row back: to undo it, leave without saving: **Exit**, then **Discard & Exit**.
- **Draws the stacks** in the Live Preview as rows with the tools' 16 px icons, as the Loader's preview and the ribbon do.

**Adding a stack by hand.** A stack has two parts in the profile JSON: an entry under `"stacks"` with its tools, and a `{ "type": "stack", "name": … }` slot in a panel where it should appear. Each tool needs `"type": "tool"` and a `commandId` (a Revit or add-in command id, or `URL:` and an address); `sourceTab`, `sourcePanel` and `iconFile` are optional and work as on panel tools.

```json
"stacks": {
  "Datum": {
    "tools": [
      { "type": "tool", "name": "Level", "commandId": "ID_OBJECTS_LEVEL", "sourceTab": "Architecture", "sourcePanel": "Datum", "iconFile": "pack:level_green" },
      { "type": "tool", "name": "Grid", "commandId": "ID_OBJECTS_GRID", "sourceTab": "Architecture", "sourcePanel": "Datum" }
    ]
  }
},
"panels": [
  { "name": "Model", "color": "#4f8ef7", "slots": [ { "type": "stack", "name": "Datum" } ] }
]
```

To add one, close the Builder, open a profile's file from `%AppData%\RST\profiles\` in a text editor, add the two parts and save it. The Loader and the Builder show the stack the next time they open. To see it on the ribbon, select the profile in the Loader and click **Load Profile →** again (or restart Revit). A stack with fewer than two or more than three tools, or a tool without a `commandId`, fails validation and the profile is not listed (see [Notes & limits](#notes--limits)).

### Properties

- **Profile:** **Min RST Version**, the minimum RST version (default `0.1.0`; informational, see [Notes & limits](#notes--limits)) and **Panel Opacity**, from 10 to 100 in steps of 5. Opacity sets how strongly every panel's colour is drawn; users can override it in the Loader.
- **Selected Panel:** the panel's **Name**; its **Color**, as a swatch and a hex box (`#RRGGBB`, applied when you press Enter or leave the box). The swatch opens the colour picker, with a palette, recent colours, a hex box and a tool to pick a colour from anywhere on screen. **+ URL Button** opens a small editor with **Button label** and **URL** boxes: **Add** appends a link button to the panel (see [Custom URL Slots](custom-url-slots.md)) and **Cancel** discards it. Both boxes are required.
- **RST Presets:** a summary (for example "2 tabs hidden • Disable Unused: on") and **Configure**.
- **Junk Cleanup:** a summary ("5 of 6 targets enabled.") and **Configure**.

See [Appearance](appearance.md) for how colours and opacity render on the ribbon.

### RST Presets

**RST Presets** sets the defaults the Loader offers the first time a user selects this profile:

- **RSTify — Hide These Tabs:** one switch per ribbon tab on this machine, apart from the core tabs (Modify, Manage, View, Annotate, Add-Ins, Create) and the profile's own tab. Tabs the profile already hides but that are not on this ribbon are listed too.
- **Disable Unused Add-ins:** the starting position of the Loader's **Disable unused add-ins** switch.

**Apply** keeps the changes and **Cancel** discards them. The defaults are stored in the profile's `presets` field. When a user first selects the profile, the Loader asks whether to adopt them; after that, the user's own choices apply. See [Ribbon Tools](ribbon-tools.md).

![The RST Presets dialog: the "RSTify — Hide These Tabs" switch list, the Disable Unused Add-ins switch, and Cancel and Apply](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/builder-rst-presets.png)

### Junk Cleanup targets

**Junk Cleanup Targets** chooses which of the Health tool's cleanup targets this profile offers in **Clean Junk Files**. The paths are fixed presets for safety: Temp, the Revit package cache, journals, the collaboration cache, the recent-file list and the RST logs (see [Health Tool](health-tool.md#junk-file-cleanup) for the paths). You cannot add a path of your own. Each row has:

- an on/off switch;
- the name, which you can edit;
- the preset path, read-only;
- its kind;
- **×**, which removes it from this profile.

**Restore Defaults** brings back the full preset list. **Apply** keeps the changes; **Cancel** undoes every change made since the dialog opened. With every row removed, the list reads `No targets. Use "Restore Defaults".`

![The Junk Cleanup Targets dialog over the Builder: the help text, six rows each with an on/off switch, an editable name, the fixed preset path, its kind and ×, then Restore Defaults, Apply and Cancel](https://raw.githubusercontent.com/jedbjorn/rst-c/main/docs/images/builder-junk-cleanup.png)

A profile keeps its own cleanup targets when you edit and save it. Only a profile with no targets at all (one made before cleanup targets existed) is given the defaults.

### Required add-ins

When you save, the Builder works out which add-ins the profile depends on from the commands you used, and stores that list in the profile. The Loader checks it when the profile is loaded: it re-enables installed add-ins that are disabled and shows download links for missing ones. See [Ribbon Tools](ribbon-tools.md).

### Branding

**Branding ▾** opens a small panel for the company branding. It is a setting for this machine, not part of a profile. The dot on the button is filled when a logo or URL is set.

- **Logo:** **Pick…** chooses an image; rst-c resizes a PNG, JPG or BMP to 85×85 and stores it as `%AppData%\RST\branding.png`. The dialog also offers SVG, but an SVG is copied as picked, without resizing, and the ribbon may not display it. **×** clears it. The file name, or "(none)", is shown beside them.
- **URL:** a link kept with the branding and included in exported profiles. It is saved when you press Enter or leave the box.
- **Done**, a click outside the panel, or **Escape** closes it. The panel takes the keyboard focus when it opens, so Escape works straight away.

The branding appears at the leading edge of every profile tab on this machine. See [Appearance](appearance.md).

## Managing profiles

```linear
New profile :::class1 -> Name + assemble panels :::class2 -> Presets + cleanup targets :::class2 -> Save :::class3 -> Export ZIP :::class3 -> Distribute to users :::class4
```

### Saving

**Save** writes the profile to `%AppData%\RST\profiles\`:

- A new profile is saved under a new file ("Saved as <file>").
- An existing profile saved with the same name is overwritten.
- An existing profile saved with a **new name** is renamed: it keeps its id, the file under the old name is replaced, and users who have it loaded keep it. If another profile already has that name, the save stops and asks you to choose a different one. (Saving under a new name no longer makes a copy; a Duplicate action arrives with the Builder redesign, feature #18.)
- If the profile you saved is the loaded one, rst-c reloads it on the ribbon straight away ("Saved & ribbon reloaded.").

### Editing an existing profile

Choose a profile from the dropdown at the top left (the loaded one is marked "loaded" in the list; while it is open, a green dot follows its name on the button). Its panels, tools and settings load into the Builder. Your edits are made to a working copy, so the dropdown keeps showing the saved profiles until you save.

### Unsaved changes

If you have unsaved edits, the Builder asks before discarding them:

| You | Choices |
|---|---|
| Click **Exit**, or close the window with **✕** | **Cancel**, **Discard & Exit**, **Save & Exit** |
| Choose another profile in the dropdown | **Cancel**, **Discard & Switch**, **Save & Switch** |

**Cancel** keeps you where you were, with your edits. **Save & Exit** closes only if the save succeeds.

### Deleting a profile

Profiles are deleted in the Loader: select the profile and click **Delete Profile**. The Builder has no delete action.

## Export and import

### Exporting a profile

Click **Export ZIP**. The profile needs a name. Choose where to save it in the **Export RST Profile (.zip)** dialog; the Builder confirms with "Exported to <path>". The export is the profile as it stands in the Builder; save as well if you want your own copy to match. The `.zip` contains:

- `profile.json`: the full profile definition, including the branding URL if one is set;
- `assets/branding.png`: the branding logo, if one is set.

The zip is self-contained: the recipient does not need to set up branding, because the logo travels with the profile.

### Importing a profile

Import happens in the Loader: click **+ Import from File** and choose the `.zip`. rst-c installs the profile into `%AppData%\RST\profiles\` and its logo into `%AppData%\RST\<profile-id>\`, and the profile appears in the Loader's list, ready to load.

> [!class2]
> You never need to place or rename profile files by hand. Saving and importing put them in `%AppData%\RST\profiles\`.

## Notes & limits

- The catalogue is read from the running Revit session when the Builder opens. Open a project (even a blank one) first if you want add-in commands to appear. "No commands available." means the catalogue could not be read.
- An imported profile shows its own logo. Once you edit and save it in the Builder, it uses this machine's branding instead, because the Builder treats branding as a machine setting.
- A profile's `schemaVersion` keeps profiles compatible across versions. Older pyRevit-era profiles (no `schemaVersion`, treated as v0) can be loaded and saved again.
- **Min RST Version** (the minimum RST version, `min_version`) is informational: the Loader shows it on the profile's card and details ("Min RST"), but rst-c does not check or enforce it when a profile is loaded.
- Stacks hold push buttons only: Revit and add-in commands and URL buttons, two or three per stack. Pulldowns, split buttons, combo boxes, text boxes and stacks inside stacks are not supported, and the permanent RST panel on the Add-Ins tab has no stacks.
- A profile file whose stacks break the rules (for example a hand-edited stack of one or four tools, or a stack tool without a command id) fails validation, so neither the Loader nor the Builder lists it; the log says why. The ribbon would skip such a stack in any case, with a log line, and never draws a stack of one or four buttons.
- Profiles with stacks open in older versions of rst-c (v3.0.0 and earlier) without errors, but those versions do not draw the stacks on the ribbon.
