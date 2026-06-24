// required-addins.test.mjs — regression guard for computeRequiredAddins()
// in src/RST.UI/Assets/profile_builder.html.
//
// Bug it locks down: tools that are *built-in Revit features* (origin
// "Native") were being recorded as required third-party add-ins, because
// detection relied on a hard-coded BUILTIN_PANELS name allowlist that can
// never enumerate all of Revit's built-in panels. A profile placing the
// "Color Schemes" (panel "Room & Area") and "Color Fill Legend" (panel
// "Color Fill") native tools therefore loaded with two phantom
// "Not Installed" entries. Reproduced on the Windows test VM (Revit 2026):
//   load_profile QA: active=3 disabled=0 missing=2
//
// The fix makes the command's `origin` authoritative: a Native command is
// never a dependency, whatever panel it sits on.
//
// This test extracts the REAL shipped function body from the HTML (so it
// exercises production code, not a copy) and runs it against the profile
// that reproduced the bug. Zero deps — run with `node` or `./sc test`.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const htmlPath = path.resolve(here, '../../src/RST.UI/Assets/profile_builder.html');
const lookupPath = path.resolve(here, '../../src/RST.UI/Assets/lookup/addin_lookup.json');

// --- Extract `var BUILTIN_TABS ...` through the end of computeRequiredAddins,
//     by brace-matching from the function declaration (robust to line shifts).
function extractBlock(html) {
  const startMarker = 'var BUILTIN_TABS = new Set([';
  const start = html.indexOf(startMarker);
  if (start < 0) throw new Error('BUILTIN_TABS marker not found');
  const fnStart = html.indexOf('function computeRequiredAddins()', start);
  if (fnStart < 0) throw new Error('computeRequiredAddins not found');
  const braceOpen = html.indexOf('{', fnStart);
  let depth = 0, i = braceOpen;
  for (; i < html.length; i++) {
    const ch = html[i];
    if (ch === '{') depth++;
    else if (ch === '}') { depth--; if (depth === 0) { i++; break; } }
  }
  return html.slice(start, i);
}

const html = fs.readFileSync(htmlPath, 'utf8');
const ADDIN_LOOKUP = JSON.parse(fs.readFileSync(lookupPath, 'utf8'));
const block = extractBlock(html);

function buildComputeFn(catalog, profile) {
  // Inject the globals the function closes over, then hand back the function.
  return new Function('catalog', 'profile', 'ADDIN_LOOKUP',
    block + '\n; return computeRequiredAddins;')(catalog, profile, ADDIN_LOOKUP);
}

// --- Fixture: the profile that reproduced the bug on the VM. The two ID_*
//     commands are Revit built-ins; the CustomCtrl_% ones are third-party.
const profile = {
  panels: [
    { name: 'Color Tools', slots: [
      { type: 'tool', commandId: 'CustomCtrl_%CustomCtrl_%pyRevit%Analysis%ColorSplasher', sourceTab: 'pyRevit', sourcePanel: 'Analysis' },
      { type: 'tool', commandId: 'ID_SETTINGS_COLORFILLSCHEMES', sourceTab: 'Architecture', sourcePanel: 'Room & Area' },
      { type: 'tool', commandId: 'ID_OBJECTS_ROOM_FILL', sourceTab: 'Annotate', sourcePanel: 'Color Fill' },
    ]},
    { name: 'Kinship Library', slots: [
      { type: 'tool', commandId: 'CustomCtrl_%Add-Ins%Kinship%KinshipUploadThis2Library', sourceTab: 'Kinship' },
    ]},
    { name: 'Panel 3', slots: [
      { type: 'tool', commandId: 'CustomCtrl_%DiRootsOne%Data IO%OneParameter', sourceTab: 'DiRootsOne', sourcePanel: 'Data IO' },
    ]},
  ],
  stacks: {},
};

const catalog = [
  { id: 'ID_SETTINGS_COLORFILLSCHEMES', sourceTab: 'Architecture', sourcePanel: 'Room & Area', origin: 'Native' },
  { id: 'ID_OBJECTS_ROOM_FILL', sourceTab: 'Annotate', sourcePanel: 'Color Fill', origin: 'Native' },
  { id: 'CustomCtrl_%CustomCtrl_%pyRevit%Analysis%ColorSplasher', sourceTab: 'pyRevit', sourcePanel: 'Analysis', origin: 'Custom' },
  { id: 'CustomCtrl_%Add-Ins%Kinship%KinshipUploadThis2Library', sourceTab: 'Kinship', sourcePanel: null, origin: 'Custom' },
  { id: 'CustomCtrl_%DiRootsOne%Data IO%OneParameter', sourceTab: 'DiRootsOne', sourcePanel: 'Data IO', origin: 'Custom' },
];

// --- Assertions
const failures = [];
function check(name, cond) { if (!cond) failures.push(name); }

const tabs = buildComputeFn(catalog, profile)().map(r => r.tabName).sort();

check('excludes built-in panel "Room & Area"', !tabs.includes('Room & Area'));
check('excludes built-in panel "Color Fill"', !tabs.includes('Color Fill'));
check('keeps third-party "pyRevit"', tabs.includes('pyRevit'));
check('keeps third-party "Kinship"', tabs.includes('Kinship'));
check('keeps third-party "DiRootsOne"', tabs.includes('DiRootsOne'));
check('emits exactly the 3 real third-party tabs',
  JSON.stringify(tabs) === JSON.stringify(['DiRootsOne', 'Kinship', 'pyRevit']));

// Sanity: a Native command on a NON-allowlisted panel must still be skipped
// purely because of origin (the heart of the fix).
const nativeOnly = buildComputeFn(
  [{ id: 'ID_FOO', sourceTab: 'Manage', sourcePanel: 'Some Unlisted Panel', origin: 'Native' }],
  { panels: [{ slots: [{ type: 'tool', commandId: 'ID_FOO', sourceTab: 'Manage', sourcePanel: 'Some Unlisted Panel' }] }], stacks: {} },
)();
check('origin=Native short-circuits regardless of panel name', nativeOnly.length === 0);

if (failures.length) {
  console.error('FAIL — computeRequiredAddins regression:');
  for (const f of failures) console.error('  ✗ ' + f);
  console.error('got requiredAddins tabs:', JSON.stringify(tabs));
  process.exit(1);
}
console.log('PASS — computeRequiredAddins excludes built-in Revit tools (' + tabs.length + ' real deps:', tabs.join(', ') + ')');
