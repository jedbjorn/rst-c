# Developing rst-c

Notes for working on rst-c itself. This file covers the shared WPF kit, its theme
keys, and the screenshot pipeline (feature #14, spec #16).

## Vendoring policy (the family kit)

rst-c, [gantt-it](https://github.com/jedbjorn/gantt-it) and
[splash-it](https://github.com/jedbjorn/splash-it) are one family of Revit add-ins (MIT,
same owner). rst-c's WPF windows use the same kit. It is **vendored**: the files are
copied into this repo, with no shared package. Splash-it vendored it from gantt-it first,
so rst-c copies from splash-it.

- **Source commits:** splash-it `ba86a71`, and gantt-it `a96d0a1` for the original files
  (the kit files did not change in gantt-it between `aa83f35` and `a96d0a1`).
- **Provenance header:** every copied or adapted file starts with one, for example:

  ```text
  // Copied from splash-it src/SplashIt.UI/Theming/ThemePalette.cs (splash-it commit ba86a71;
  // originally gantt-it src/GanttIt.Revit/UI/Theming/ThemePalette.cs, gantt-it commit a96d0a1), MIT, same owner.
  // Changes: namespaces (SplashIt.* to RST.*); reads RstTheme where splash-it reads SplashItTheme.
  ```

  The header says **Adapted from** when the behavior changed. XAML and `.resx` files
  carry the same text in a `<!-- -->` comment. rst-c's own new files say
  `rst-c's own (not vendored)` instead.
- **Renames:** `SplashIt.*` became `RST.*` (`RST.Core.Theming`, `RST.UI.Theming`, …).
  `SplashItTheme` became `RstTheme`. The env vars are `RST_THEME` (Debug builds only:
  forces the detected theme) and `RST_SCREENSHOTS`. The kit's `Trace` calls became
  Serilog `Log`.
- **To update the kit:** re-copy from the sister repo's `origin/main`. Update the commit
  in each header, then re-apply the `Changes:` line. Do not edit the sister repos from
  here.

| Where | What |
|---|---|
| `src/RST.Core/Theming/UiPalette.cs` | the light and dark palettes, plus the WCAG `Requirements` |
| `src/RST.Core/Colors/`, `Picking/` | `Rgb`, `RecentColors`, `ScreenSampling` (for the color picker) |
| `src/RST.Tests/Theming/`, `Picking/`, `Colors/` | `UiPaletteTests` (scans `src/RST.UI/**/*.xaml`, finds the repo by `RST-C.sln`), plus the sampling and recent-colors tests |
| `src/RST.UI/Theme.xaml` | the theme. Each window merges `/RST.UI;component/Theme.xaml` |
| `src/RST.UI/Theming/` | `ThemePalette`, `DarkTitleBar`, `RstTheme` |
| `src/RST.UI/Mvvm/` | `ObservableObject`, `RelayCommand`, the converters, `Behaviors` (`HasError`, `CommitOnEnter`) |
| `src/RST.UI/Dialogs/` | `IDialogService`/`DialogService`, `MessageDialog`, `MonitorWorkArea`, `NativeColorDialog`, and rst-c's `WindowPlacement` |
| `src/RST.UI/Swatches/` | `ColorPicker`, `ColorPickerDialog`, the eyedropper (`ScreenColorPicker`, `ScreenPickOverlay`, `NativeScreen`) |
| `src/RST.UI/Resources/UiStrings.*` | English strings, used as `{x:Static res:UiStrings.Key}` |
| `src/RST.UI/Controls/` | rst-c's own: `TransientMessage`, `BusyState`, `CardState` |
| `src/RST.Engine/Theming/RevitThemeDetector.cs` | reads Revit's theme. `ApplyToWindows()` = `RstTheme.Apply(Detect())` |

**RST.UI stays Revit-free.** `UiProjectTests` fails if RST.UI references RevitAPI,
RevitAPIUI, Autodesk.*, AdWindows or RST.Engine.

## Opening a window (for the window lanes)

```csharp
RevitThemeDetector.ApplyToWindows();               // Engine: before the window is built
var dialogs = new DialogService();
var window = new ProfileLoaderWindow(new ProfileLoaderViewModel(..., dialogs));
dialogs.Owner = window;
WindowPlacement.Own(window, commandData.Application.MainWindowHandle); // CenterOwner, or CenterScreen with no owner
window.ShowDialog();
```

Each window:

- merges Theme.xaml;
- calls `DarkTitleBar.Attach(this)` in its constructor;
- calls `WindowPlacement.CapToWorkArea(this)` from `OnSourceInitialized`;
- uses `Background="{DynamicResource WindowBrush}"`, `FontFamily="Segoe UI"`, `FontSize="12"`,
  `UseLayoutRounding="True"` and `TextOptions.TextFormattingMode="Display"`.

The theme is read when Theme.xaml loads, so a window keeps the theme it opened with.

## Theme keys

The kit's keys are unchanged from splash-it. The kit's styles:

- **Implicit:** Button, ContextMenu, MenuItem, TextBox, ComboBox, CheckBox, ListBox,
  ToolTip, ScrollBar, Slider.
- **Keyed:** `HeadingText`, `SectionText`, `LabelText`, `MutedText`, `ErrorText`,
  `ColumnHeaderText`, `FocusRing`, `PrimaryButton`, `DangerButton`, `DangerFilledButton`,
  `IconButton`, `Panel`, `InfoBanner`, `WarningBanner`, `ErrorBanner`, `TextBoxContextMenu`,
  `IconFont`.
- **Converters:** `BoolToVisible`, `BoolToCollapsed`, `NullToCollapsed`, `InverseBool`,
  `DoubleToGridLength`.

rst-c adds the following. Every pairing they draw is a `ContrastRequirement` in
`UiPalette.RstRequirements`, checked by `UiPaletteTests`.

### New palette brushes

| Key | Purpose |
|---|---|
| `SuccessBrush` | Success text and strokes: the "● Loaded" badge, the active card's border, the success pill's text |
| `SuccessSoftBrush` | Success fill behind text: the success banner, toast and pill |
| `SuccessBorderBrush` | The success banner's and pill's outline (decorative) |
| `ScrimBrush` | The translucent wash behind the busy overlay (decorative; never under text) |

### New styles and resources

| Key | Target | Purpose |
|---|---|---|
| `SuccessText` | TextBlock | Success-colored text ("● Loaded") |
| `SuccessBanner` | Border | A success banner, alongside the kit's Info/Warning/Error banners |
| `Pill` | ContentControl | A neutral rounded label: counts (Profiles count, tree match counts), the "Native" status. Usage: `<ContentControl Style="{StaticResource Pill}" Content="12" />` |
| `InfoPill` / `SuccessPill` / `WarningPill` / `ErrorPill` | ContentControl | Status pills (Installed / Loaded / Disabled / Not Found) |
| `ToggleSwitch` | CheckBox | The rst-toggle switch. Filled with the check-box accent when on; off is an outlined track. Disabled is drawn at 45 % opacity |
| *(implicit)* | TabControl, TabItem | A flat header strip over a divider. The selected tab shows body text and a 2 px accent underline. Top placement only |
| `TreeExpander` | ToggleButton | The tree's chevron, used by the implicit TreeViewItem |
| *(implicit)* | TreeView, TreeViewItem | Virtualizing (recycling, pixel scroll). Hover and selected rows (AccentSoft). Headers stretch, so a header template can dock a `Pill` count to the right |
| *(implicit)* | ListBoxItem | Plain list rows: hover, and selected (AccentSoft), instead of the Windows system blue |
| `CardListBox` | ListBox | A transparent, borderless card list with no horizontal scroll that uses `CardListBoxItem` |
| `CardListBoxItem` | ListBoxItem | A selectable card. Normal: 1 px border. Hover: hover fill and strong border. Selected: 2 px accent ring. Active (`controls:CardState.IsActive="True"`): 2 px success ring, which wins over selected |
| `VerticalGridSplitter` / `HorizontalGridSplitter` | GridSplitter | A 7 px grab strip with a 1 px divider line. The line turns strong-border under the pointer and accent while dragging. Put the splitter in its own column or row |
| `BusyIndicator` | ProgressBar | An indeterminate thin bar with a sliding accent segment |
| `BusySpinner` | ProgressBar | A 16 px turning accent arc. Inside a `PrimaryButton`, set `Foreground="{StaticResource OnAccentBrush}"` |
| `BusyOverlay` | ContentControl | The `RSTmsgWait` overlay. Content: a `BusyState`. A scrim, then a centred panel with the spinner, `Title` and `Message`. Put it last in the root grid, spanning everything. The view-model also disables its commands while busy |
| `TransientBanner` | ContentControl | The `RSTmsgInform` / `showToast` toast. Content: a `TransientMessage`. Info/Success/Warning/Error colors by `Kind`, an optional bold `Title`, top-centre, click-through. Put it last in the root grid |

### Models (RST.UI.Controls)

- `TransientMessage.Show(text, kind, duration, title)`. The default duration is
  `InformDuration` (3 s). Pass `ToastDuration` (2.4 s) for the Builder toasts. A new
  `Show` replaces the message and restarts the timer. Tests pass a fake scheduler.
- `BusyState.Show(title, message)` and `Hide()`.
- `CardState.IsActive`, an attached property for the card's active state.

## Runtime tests and screenshots

`src/RST.RuntimeTests` holds the WPF tests. They need Windows but not Revit, so they run
in CI only. Locally, build them with `-p:EnableWindowsTargeting=true`.

- **Support:**
  - `Support/Screenshots`: `RunSta`, `Render`, `Save` to
    `<repo>/TestResults/screenshots/<name>.png` (or `$RST_SCREENSHOTS`), `PixelAt`,
    `DistinctPixels`.
  - `Support/WindowCapture`: `ShowOffScreen` (shows the window at -32000, not activated),
    `Client` (the client area only, without the DWM title bar), and **`Content`**.
    `Content(window, size)` renders the window's content at a chosen size without clipping
    it to the window. The CI desktop is about 1024 × 768, and Windows caps a window at the
    desktop size, so use `Content` for the 1450 × 900 Loader and Builder. The content must
    paint its own `WindowBrush` background. The gallery run writes
    `capture-environment.txt` with the CI screen size.
  - `Support/Fakes`: `FakeDialogs`, a scripted `IDialogService`, and `TempFolder`.
- **Theme:** call `RstTheme.Apply(theme)` before building a window. Window tests use the
  `WpfThemeCollection`, which runs them one at a time because the theme is process-wide.
  Name the files `<what>-light|dark`.
- **The tests:**
  - `Gallery/StyleGalleryScreenshotTests`: `style-gallery-light|dark`, every style with the
    real confirm dialog drawn in; `message-dialog-confirm|message-light|dark`; and
    `color-picker-dialog-light|dark`. Each run also samples the pixels of the new styles'
    states.
  - `ScreenshotPipelineTests`: `smoke-palette`.
  - `UiProjectTests` and `UiStringsTests`.

To get the screenshots from CI, open the PR's run. The `Test (runtime, Windows)` job uploads
the artifact **`RST-screenshots`**, which holds every PNG and `capture-environment.txt`. It
is uploaded even when a test fails. If that job runs as a per-major matrix, only the R25 leg
uploads. Download it with:

```sh
gh run download <run-id> -n RST-screenshots -D <folder>
```

## Health window additions (WU2)

The Health window (`src/RST.UI/Health/HealthWindow.xaml`, view-model `HealthViewModel`) adds:

| Key | Target | Purpose |
|---|---|---|
| `CautionTextBrush` | palette | The soft-warning severity (the HTML's warn-soft), between OK (`SuccessBrush`) and `WarningTextBrush`. Checked by `UiPalette.HealthRequirements` |
| `SectionExpander` | Expander | A collapsible bordered section: the header (`HeaderTemplate`) with a chevron on the right that turns down when open, then a divider and the body. Collapsed unless `IsExpanded` |
| `SectionExpanderHeader` | ToggleButton | The expander's header row (used by `SectionExpander`) |
| `RangePillGroup` / `RangePill` | Border / RadioButton | A segmented toggle: an outlined track of `RadioButton` pills (one `GroupName` per group); the checked pill is accent-tinted and outlined |

- **Severity colors:** `HealthBrushes.KeyFor(severity, fallback)` maps OK, WarnSoft, Warn, Danger and Info to
  `SuccessBrush`, `CautionTextBrush`, `WarningTextBrush`, `ErrorBrush` and `AccentForegroundBrush`.
- **`controls:ActivityChart`** draws an `RST.Core.Health.ActivityChartLayout` (760 × 190 logical, scaled to its
  width): gridlines with y labels, date ticks, the monotone curve as a `Path` of `BezierSegment`s and dots with
  tooltips. Set `LineBrush`, `GridBrush`, `LabelBrush` and `DotStrokeBrush`.
- **RST.Core** holds the rules: `HealthSeverityRules`, `HealthFormat` (`fmt`, `fmtDur`, `fmtHoursShort`,
  `fmtElapsed`, and `HoursAxis`/`DurAxis`, which keep the four gridline labels distinct for small values), `ActivityChartMath` (Fritsch–Carlson tangents, the curve, `chartMode`), `ActivityChartLayout`,
  `ActivityStats` (`activityStatValues`) and `ActivityRanges`. `tests/ui/activity-chart.test.mjs` is ported to
  `src/RST.Tests/Health/`.
- **Opening it:** `HealthLauncher.Show(ownerHwnd, context, telemetryToggled, liveSessionStartUtc)`, after
  `RevitThemeDetector.ApplyToWindows()`. Scan and Clean run off the UI thread (`HealthViewModelOptions.RunInBackground`);
  while either runs, every command and input is disabled.
- **Screenshots** (`src/RST.RuntimeTests/Health/HealthWindowScreenshotTests`): `health-sections`, `health-empty`,
  `health-scanning`, `junk-modal`, `junk-modal-empty`, `health-cleaning`, `cleanup-result`, `activity-curves`, `activity-small`
  and `activity-sparse`, each `-light` and `-dark`. The sections and curve captures are 900 wide and as tall as
  their content (the real window, 800 tall, scrolls); the others are 900 × 800.

## The Profile Loader (WU3)

The native Profile Loader replaces `profile_loader.html`. The Builder stays on WebView2 until
WU5, so `LoaderWindow` now always shows `profile_builder.html`.

- **Logic in RST.Core (`RST.Core.Loader`, tested on Linux in `RST.Tests/Loader`):** the ported
  JS rules (`RequiredAddinRules`, `RstifyRules`, `FailedDisableMessages`, `RibbonPreviewRules`)
  and the services moved out of `LoaderBridge` (`ProfileLibraryService`, `ProfileLoadService`,
  `LoaderPrefsService`, `AddinMaintenanceService`, `AddinLookupService`). `LoaderBridge` calls the
  same services for the Builder's `get_profiles`, `get_active_profile`, `load_profile` and
  `get_addin_lookup`. The services log through `ILoaderLog` (Serilog in RST.UI).
- **The window (`RST.UI/Loader`):** `ProfileLoaderLauncher.Show(owner, …)` →
  `ProfileLoaderWindow` → `ProfileLoaderViewModel`, which talks to `IProfileLoaderBackend` (the
  services) and `ILoaderDialogs` (`ConfirmAddinChangesWindow`, `AddinCheckWindow`). The load's
  add-in renames run on the thread pool (`ProfileLoadService.Begin` / `RunAddinPasses` / `Finish`).
- **`RibbonPreview` (`RST.UI/Controls`):** the shared preview. Set `Profile`, optionally
  `OverrideOpacity`, `Mode` (`Loader` or `Builder`: tool labels by name, "(empty)" stacks,
  "No panels configured.") and `TopBarContent`; bump `Revision` or call `Refresh()` after editing
  the profile in place.
- **Theme.xaml "Loader" block:** `LoaderLink` (Hyperlink: accent text, underlined on hover; its
  contrast is in `UiPalette.Requirements`), `LoaderStrip` (header and footer strips), `LoaderRow`
  (list rows with a divider).
- **Tests:** `RST.RuntimeTests/Loader` (view-model flows with fakes, screenshots `loader-*`, and
  AC6 over the v2.2.0 fixtures in `src/RST.Tests/Fixtures/V220`).

## The Profile Builder shell (WU4)

The native Profile Builder (`RST.UI/Builder`) is built and tested, but **not wired yet**:
`BuilderCommand` keeps opening the WebView2 `profile_builder.html` until WU5 adds drag and drop,
the icon picker and the RST Presets / Junk Cleanup modals. `ProfileBuilderLauncher.Show(owner, …)`
is the entry point WU5 will call.

- **Logic in RST.Core (`RST.Core.Builder`, tested on Linux in `RST.Tests/Builder`):**
  `CatalogCommand` (get_catalog's shape and host-tab promotion), `CatalogTree` (the catalogue index
  and filter: built once, a filter only computes per-node match counts), `RequiredAddinDetector`
  (computeRequiredAddins, the port of `tests/ui/required-addins.test.mjs` is
  `RequiredAddinDetectorTests`), `BuilderRules` (shortId, normalizeUrl via `UrlNormalizer`,
  normalizeHex, the slot shapes and summaries), `ProfileEditing` (startFresh, loadIntoProfile, the
  dirty snapshot, rename-as-copy) and `ProfileSaveService` (save_profile and export_profile's writes).
- **The window:** `ProfileBuilderWindow` → `ProfileBuilderViewModel`, which talks to
  `IProfileBuilderBackend` and `IDialogService`. The catalogue tree is a virtualizing `TreeView`
  over `CatalogNodeViewModel`s built once; a filter updates counts and each node's
  `FilteredCollection` of visible children (small changes item by item, big ones as one Reset).
  Panel and slot edits write straight into the profile and bump `PreviewRevision`, which redraws
  the shared `RibbonPreview` (Builder mode).
- **Dialogs:** `IDialogService.Choose` is the three-way prompt (MessageDialog's optional
  `SecondaryText` button) used for Save & Exit / Discard & Exit / Cancel. Every Builder window and
  dialog sets `ShowInTaskbar="False"`.
- **Tests:** `RST.RuntimeTests/Builder`: view-model flows with fakes, screenshots `builder-*`,
  AC6 (`ProfileBuilderDataCompatibilityTests`, over the v2.2.0 fixtures and the captured v2.2.0 save
  payloads in `src/RST.Tests/Fixtures/V220/builder`), and AC7 (`ProfileBuilderPerformanceTests`: 823
  commands, median keystroke under 100 ms, and far fewer realized rows than commands).
