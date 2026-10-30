# F18 Builder UX Redesign: mockup v2

Open `builder.html` in Firefox (`firefox builder.html`). It is a throwaway design artifact, not product code.
The purple strip at the top is the mockup's own control bar, not part of the design.

## v2 (2026-10-30)

The FnB's picks are now the defaults: **B · Tabs**, **Tiles**, **In profile menu** (decision #22). Settings is an icon-only ⚙ at the far left, before the profile dropdown (v2.1, decision #23). The other options stay in the strip for reference.

## Options (purple strip; the choice is kept in the URL, so quote it in a redline)

| Switch | Choices |
|---|---|
| Profile pane | **A · Sections** (one scroll; Loader defaults and Junk cleanup open in dialogs) or **B · Tabs** (General, Loader defaults, Junk cleanup, all inline) |
| Panel layout | **Rows** (a list, like today) or **Tiles** (laid out like the ribbon) |
| Rename / Duplicate | **In profile menu** (header dropdown) or **In Profile pane** (edit the name in place; a Duplicate button) |
| Header colour | **Tint** (the theme's AccentSoft) or **Neutral** (the theme's Pressed grey), so the header reads as a header; **v1** is the old SurfaceAlt for comparison |
| Theme | Light or Dark (Revit follows either) |

**Reset data** puts the sample profiles back.

## Things to try

- **Stacks:** drag a tool's ⠿ handle onto the middle of another tool to stack them. Drag onto the top or bottom edge to reorder instead. Drag a tool out of a 2-tool stack and the stack dissolves. A 4th tool is refused (red outline). **+ New Stack** on a panel opens the picker (2–3 tools). The ✎ and × on a stack edit it or un-stack it; its tools stay on the panel.
- **Custom Tools:** **+ Create new** makes a URL or email tool on the selected panel. Hover a Custom Tools entry for ✎ (edit everywhere it is used) and × (remove from every profile). The list is built from all three sample profiles: Wiki and Help appear in two.
- **Tools on panels:** click a name to rename it for this panel only. Click the icon to change it. × removes it from this panel only. ✎ shows on URL and email tools.
- **Panels:** the colour swatch, the name, **+ New Stack**, and (on hover) ▲ ▼ ×. Drag the ⠿ handle to reorder.
- **Header:** switch profiles from the dropdown (with unsaved changes it asks first). Export ZIP sits beside it. **⚙** (far left) holds Branding (this machine).

## Redlines

Mark up any screenshot (the `shots/` folder holds v1 renders) and drop it in `shared/redlines/`.
