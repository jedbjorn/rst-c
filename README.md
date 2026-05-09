# rst-c

A native Revit add-in that gives admins a way to build curated ribbon
toolbar profiles, and gives end users a one-click way to load them.
Architects, modellers, BIM coordinators, and trainees rarely need the same
ribbon — rst-c lets each role get the ribbon that fits the work, without
hunting through tabs or relearning where commands live.

It ships as a single signed managed add-in for Revit 2025 through 2027,
installs into the standard per-user add-in directory, and runs without
pyRevit, Python, or any other external runtime.

## Intention

The goal is a ribbon that respects the work in front of you. Revit's
native ribbon assumes every user wants every tool, and every add-in
vendor assumes their tab is the most important one. The result, on a
real machine with five or six add-ins installed, is a ribbon dense
enough that finding the right command is itself a task.

rst-c treats the ribbon as a curated surface. An admin assembles a
profile — a tab full of panels full of tools, drawn from anywhere in
Revit's command catalogue — and an end user loads that profile to get
exactly that surface and nothing else. Profiles are JSON, profiles are
shareable, profiles can be swapped live without restarting Revit, and
everything that runs runs in-process inside Revit's own .NET host. There
is no IPC, no second runtime, no GitHub clone step on the user's
machine. Install the add-in, drop in a profile, get back to drawing.

The port from the original pyRevit-based RST is what enables this. A
single managed assembly can be code-signed, installed by an MSI, and
shipped through normal IT channels — the operational story that pyRevit
extensions never quite had.

## Features

### Profile Loader

The Loader is the end-user face of rst-c. It opens as a WebView2 window
listing every profile installed on the machine, shows what's in each
one before you commit, and applies the chosen profile to the ribbon
with a single click. After Apply, the rst-c tab rebuilds with the
profile's panels and tools in place; nothing else on the ribbon is
touched. The Loader is meant to feel closer to picking a workspace than
to running an extension command — most users will only ever see this
one window.

### Profile Builder

The Builder is the admin counterpart to the Loader. It scans the live
Revit session for every command on every tab — native Revit commands,
add-in commands, pyRevit buttons, anything Revit's ribbon API knows
about — and presents them as a searchable catalogue you can drag into
panels on a new profile tab. Each tool gets a name, an icon (chosen
from a vendored 48-icon pack or auto-derived from the source command),
and a panel assignment. The Builder also handles per-profile metadata —
branding, colors, cleanup targets, required add-ins — and exports the
finished profile as a self-contained zip that any other rst-c install
can load.
Support for: 
- Giving tools a unique name on the built ribbon
- Naming Panels individually
- coloring panels, including opacity settings
- Tab and Profile naming

Profiles are saved to the users %AppData% folder at creation and transmitted profiles are copied to that location.
No need to save the zips anywhere, just import via the Loader. 

### Live Profile Switching

Switching profiles is done via the Loader tool and does not require a Revit restart. When the Loader
applies a profile, rst-c rebuilds the profile tab in place using
Revit's own ribbon and AdWindows APIs, on the next idle event. The
intent is to make profile switching cheap enough to do mid-session — an
architect moving from schematic design to construction documents, or a
trainer cycling through teaching profiles in front of a class, should
not have to close the model to change ribbons.

### Custom URL Slots

A profile button can point at a URL, a `mailto:` address, a file path,
or a UNC share, in addition to a Revit command. Bare hostnames
(`gmail.com`) and bare emails (`support@example.com`) are normalised so
they resolve cleanly through the user's default browser or mail client.
The intent is to let admins fold company resources — the wiki, the
SharePoint document library, the BIM standards PDF, the helpdesk inbox —
into the same ribbon people already use, instead of asking them to
remember a separate set of bookmarks.

### Branding Panel

Every profile tab can display an 85×85 square branding image with
rounded corners — typically a company or office logo. This is set in the Builder tool and the same branding will be applied to all future profiles created or edited after the branding image is set. The branding
panel is non-interactive and lives at the leading edge of the tab, so
the curated ribbon visibly belongs to the org that curated it. Logos
ship inside the profile zip, so a profile shared to another office
keeps its branding without extra setup.

### Colored Panels

Each panel on a profile tab can carry a custom hex color and an opacity
between 10% and 100%, with rounded corners drawn over Revit's native
panel chrome via an AdWindows adapter. The intent is grouping by glance
— a user who has used the profile twice should be able to find the
"detailing" panel by color before they read any tool labels. The effect
is purely visual; nothing about command behaviour changes.

### RSTify

RSTify is a profile-aware tab-hiding mode. When enabled, rst-c hides
every Revit and add-in tab the active profile didn't draw from, leaving
just the rst-c tab and any tabs the profile explicitly preserves. The
intent is to remove the temptation to "fall back" into the unfiltered
ribbon mid-task, which is the failure mode any curated ribbon system
has to defend against. RSTify is per-profile and per-user — the admin
sets a default in the Builder, the end user can override it from the
Loader, and the ribbon icon shows the current state.

### Required Add-ins

Profiles can declare the add-ins they depend on. On Apply, rst-c
checks the live Revit session against that list and either auto-enables
the missing ones for the session or prompts the user, depending on how
the profile is configured. The intent is to make a profile a complete
environment statement — "this profile expects these tools to exist" —
rather than a fragile collection of buttons that silently break when
the underlying add-in isn't loaded.

### Health Tool

Health is a one-click workstation and Revit-session snapshot: CPU, RAM,
GPU, disk, display, network, OS, the active model and its size, and
any warnings the session has accumulated. It also offers a Clean Junk
Files step for Temp folders, the package cache, journal files, and the
collaboration cache, with cleanup targets configurable per profile.
Everything runs locally; nothing is uploaded. The intent is to give an
end user something concrete to send a BIM lead when Revit is misbehaving,
without asking them to dig through Windows settings.

### Profile Export and Import

Profiles export as `.rstprofile` zip bundles that contain the profile
JSON, the branding image, and any other per-profile assets. A bundle
can be emailed, dropped on a share, or committed to a repo, and another
rst-c install can import it as-is. The intent is to make profile
sharing as low-friction as sharing a Revit family — no install steps,
no path fixing, no "works on my machine."

### Logging

rst-c writes Serilog rolling files under `%AppData%\RST\`. Both C#-side
and WebView2-side errors land in the same file via a `log_event`
bridge, so a single log captures the full picture when something
misbehaves. Session logs are capped so the directory does not grow
unbounded, and the active log is the first place to look when a profile
behaves unexpectedly.

## Targets

| Revit version | TFM               |
|---------------|-------------------|
| 2025          | `net8.0-windows`  |
| 2026          | `net8.0-windows`  |
| 2027          | `net10.0-windows` |

(2027 is provisional pending Autodesk's release.)

## Install

Pre-built bundles ship on `main` under `build/R<NN>/` — one folder per
Revit major. Close Revit, then copy the matching bundle into
`%AppData%\Autodesk\Revit\Addins\<version>\`:

```powershell
Copy-Item -Recurse -Force "build\R25\*" "$env:APPDATA\Autodesk\Revit\Addins\2025\"
```

Start Revit. The **rst-c** tab should appear with the Loader button.

## Build

Local builds need only the .NET 8 SDK — Revit API references resolve
from NuGet.

```bash
dotnet restore
dotnet build -c "Debug R25"     # Revit 2025
dotnet build -c "Debug R26"     # Revit 2026
dotnet build -c "Debug R27"     # Revit 2027 (needs .NET 10 SDK)
```

## License

TBD — to mirror the parent RST repo at release.
