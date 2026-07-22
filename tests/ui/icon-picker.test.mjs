// icon-picker.test.mjs — headless behavior tests for the colored icon
// pack picker in src/RST.UI/Assets/profile_builder.html (spec #9).
//
// The picker's REAL shipped code is extracted from the HTML by marker
// slicing (the "ICON PICKER" section) and evaluated against a minimal
// zero-dep DOM stub, so the tests exercise production JS — open-without
// -mutation, commit-on-color-click, exact persisted value, exact
// thumbnail paths, None, Cancel, outside click, keyboard navigation,
// selected-state restore, rapid design switching — not a copy of it.
//
// Run with `node` or via `./sc test` (wired into tests/ui/package.json).

import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const htmlPath = path.resolve(here, '../../src/RST.UI/Assets/profile_builder.html');

// --- Extract the whole picker section between its marker comments, so
// the module-level state (iconPackCache, popdownState, PACK_COLORS) and
// the functions over it come across exactly as shipped.
function extractPickerSection(html) {
  const startMark = '// ── ICON PICKER';
  const endMark = '// Best-effort JS → Serilog';
  const start = html.indexOf(startMark);
  const end = html.indexOf(endMark);
  if (start < 0 || end < 0 || end <= start) {
    throw new Error('picker section markers not found in ' + htmlPath);
  }
  return html.slice(start, end);
}

// --- Minimal DOM stub: just enough surface for the picker code.
class El {
  constructor(tag) {
    this.tagName = tag.toUpperCase();
    this.children = [];
    this.parentNode = null;
    this.ownerDocument = null;
    this._classes = new Set();
    this.dataset = {};
    this.style = {};
    this.attributes = {};
    this.textContent = '';
    this.disabled = false;
    this.onclick = null;
    this.onkeydown = null;
    this.onerror = null;
    this.type = '';
    this.title = '';
    this.alt = '';
    this.src = '';
    // Layout numbers the positioning code reads; tests tweak per case.
    this.offsetTop = 0;
    this.offsetLeft = 0;
    this.offsetWidth = 86;
    this.offsetHeight = 70;
    this.scrollTop = 0;
    this.clientWidth = 640;
    this.clientHeight = 400;
  }
  set className(v) { this._classes = new Set(String(v).split(/\s+/).filter(Boolean)); }
  get className() { return [...this._classes].join(' '); }
  get classList() {
    const s = this._classes;
    return {
      add: (...c) => c.forEach((x) => s.add(x)),
      remove: (...c) => c.forEach((x) => s.delete(x)),
      contains: (c) => s.has(c),
    };
  }
  set innerHTML(v) { this._innerHTML = v; if (v === '') this.children = []; }
  get innerHTML() { return this._innerHTML || ''; }
  appendChild(c) { c.parentNode = this; this.children.push(c); return c; }
  insertBefore(c, ref) {
    if (ref == null) return this.appendChild(c);
    const i = this.children.indexOf(ref);
    if (i < 0) throw new Error('insertBefore: reference node is not a child');
    c.parentNode = this;
    this.children.splice(i, 0, c);
    return c;
  }
  get nextSibling() {
    if (!this.parentNode) return null;
    return this.parentNode.children[this.parentNode.children.indexOf(this) + 1] || null;
  }
  remove() {
    if (!this.parentNode) return;
    const i = this.parentNode.children.indexOf(this);
    if (i >= 0) this.parentNode.children.splice(i, 1);
    this.parentNode = null;
  }
  setAttribute(k, v) { this.attributes[k] = String(v); }
  getAttribute(k) { return this.attributes[k]; }
  focus() { this.ownerDocument.activeElement = this; }
  click() { if (!this.disabled && this.onclick) this.onclick({ target: this }); }
  _walk(out) { for (const c of this.children) { out.push(c); c._walk(out); } return out; }
  _matches(sel) {
    // Only the selectors the picker actually queries.
    if (sel === '.icon-variant') return this._classes.has('icon-variant');
    if (sel === '.icon-variant:not(:disabled)') return this._classes.has('icon-variant') && !this.disabled;
    if (sel === '.icon-tile.popen') return this._classes.has('icon-tile') && this._classes.has('popen');
    throw new Error('unsupported selector: ' + sel);
  }
  querySelectorAll(sel) { return this._walk([]).filter((e) => e._matches(sel)); }
  querySelector(sel) { return this.querySelectorAll(sel)[0] || null; }
}

function makeWorld({ catalogue = [], bridgeError = null, noBridge = false } = {}) {
  const doc = {
    activeElement: null,
    _listeners: {},
    createElement(tag) { const el = new El(tag); el.ownerDocument = doc; return el; },
    getElementById(id) { return doc._byId[id] || null; },
    addEventListener(type, fn) { (doc._listeners[type] ||= []).push(fn); },
    removeEventListener(type, fn) {
      doc._listeners[type] = (doc._listeners[type] || []).filter((f) => f !== fn);
    },
    querySelectorAll(sel) { return doc._root.querySelectorAll(sel); },
    // Test helper: deliver a key to every registered keydown listener.
    dispatchKey(key) {
      for (const fn of (doc._listeners.keydown || []).slice()) {
        fn({ key, stopPropagation() {}, preventDefault() {} });
      }
    },
    _byId: {},
    _root: null,
  };

  // Static picker DOM: overlay > card > (head, grid).
  const overlay = doc.createElement('div');
  overlay.id = 'iconPickerOverlay';
  const card = doc.createElement('div');
  const grid = doc.createElement('div');
  grid.id = 'iconPickerGrid';
  overlay.appendChild(card);
  card.appendChild(grid);
  doc._byId = { iconPickerOverlay: overlay, iconPickerGrid: grid };
  doc._root = overlay;

  const logs = [];
  const world = {
    document: doc,
    window: {
      pywebview: noBridge ? undefined : {
        api: {
          list_iconpack: () => bridgeError
            ? Promise.reject(new Error(bridgeError))
            : Promise.resolve(catalogue),
        },
      },
    },
    rstLog: (level, message, payload) => logs.push({ level, message, payload }),
    logs,
    overlay,
    grid,
  };
  return world;
}

const section = extractPickerSection(fs.readFileSync(htmlPath, 'utf8'));

// Fresh picker instance per scenario — module state (iconPackCache,
// popdownState) must not leak between tests.
function makePicker(world) {
  return new Function('document', 'window', 'rstLog',
    section + `
    function __state() { return { cache: iconPackCache, pop: popdownState }; }
    return { parsePackIcon, packIconUrl, refreshSlotIconBtn, ensureIconPack,
             openIconPicker, closeIconPicker, showPopdown, hidePopdown, __state };`
  )(world.document, world.window, world.rstLog);
}

const tick = () => new Promise((r) => setImmediate(r));

const COLORS = ['light_grey', 'dark_grey', 'blue', 'purple', 'green', 'orange', 'red'];
function catalogueEntry(name) { return { name, colors: COLORS.slice() }; }
const CATALOGUE = ['apple', 'move', 'link_external'].map(catalogueEntry);

// Find the design tile for a catalogue name (grid child 0 is None).
function tileFor(grid, name) {
  return grid.children.find((c) => c.title === name) || null;
}
function variantFor(picker, color) {
  return picker.__state().pop.el.querySelectorAll('.icon-variant')
    .find((b) => b.dataset.color === color) || null;
}

let failures = 0;
async function check(label, fn) {
  try { await fn(); console.log('  ok - ' + label); }
  catch (e) { failures++; console.error('  FAIL - ' + label + '\n    ' + e.message); }
}

const run = async () => {
console.log('icon-picker.test.mjs');

// ── parsePackIcon mirror of the Core contract ─────────────────────────
await check('parsePackIcon: bare, explicit, underscore names, case, whitespace', async () => {
  const { parsePackIcon } = makePicker(makeWorld());
  assert.deepEqual(parsePackIcon('pack:move'), { name: 'move', color: null });
  assert.deepEqual(parsePackIcon('pack:move_green'), { name: 'move', color: 'green' });
  assert.deepEqual(parsePackIcon('pack:link_external'), { name: 'link_external', color: null });
  assert.deepEqual(parsePackIcon('pack:link_external_purple'), { name: 'link_external', color: 'purple' });
  assert.deepEqual(parsePackIcon('PACK:Move_GREEN'), { name: 'Move', color: 'green' });
  assert.deepEqual(parsePackIcon('  pack:move_blue  '), { name: 'move', color: 'blue' });
  assert.deepEqual(parsePackIcon('pack:move_Light_Grey'), { name: 'move', color: 'light_grey' });
  // Unknown suffix is not a color — the whole remainder stays the name.
  assert.deepEqual(parsePackIcon('pack:move_fuchsia'), { name: 'move_fuchsia', color: null });
});

await check('parsePackIcon: rejects malformed and path-like values like Core', async () => {
  const { parsePackIcon } = makePicker(makeWorld());
  for (const bad of [null, undefined, '', '   ', 'pack:', 'pack:   ', 'pack:_blue',
                     'move', 'packs:move', 'pack:a/b', 'pack:a\\b', 'pack:../x',
                     'pack:a..b', 'pack:/abs', 'pack:C:\\x', 'pack:move|green']) {
    assert.equal(parsePackIcon(bad), null, JSON.stringify(bad));
  }
});

await check('packIconUrl: bare → blue alias, explicit → variant file', async () => {
  const { packIconUrl } = makePicker(makeWorld());
  assert.equal(packIconUrl('move', null), 'icons/pack/32_move.png');
  assert.equal(packIconUrl('move', 'green'), 'icons/pack/32_move_green.png');
  assert.equal(packIconUrl('link_external', 'purple'), 'icons/pack/32_link_external_purple.png');
});

// ── slot button: exact thumbnail, logical-name label ──────────────────
await check('slot button shows exact variant thumbnail and logical name', async () => {
  const { refreshSlotIconBtn } = makePicker(makeWorld());
  const world = makeWorld();
  const btn = world.document.createElement('button');
  refreshSlotIconBtn(btn, { iconFile: 'pack:move_red' });
  const img = btn.children.find((c) => c.tagName === 'IMG');
  assert.equal(img.src, 'icons/pack/32_move_red.png');
  assert.equal(btn.children.at(-1).textContent, 'move');

  refreshSlotIconBtn(btn, { iconFile: 'pack:move' });
  assert.equal(btn.children.find((c) => c.tagName === 'IMG').src, 'icons/pack/32_move.png');

  refreshSlotIconBtn(btn, { iconFile: null });
  assert.equal(btn.children[0].textContent, '+', 'no icon → placeholder');
  assert.equal(btn.children.at(-1).textContent, 'Icon');
});

// ── open without mutation ─────────────────────────────────────────────
await check('opening the picker and a popdown mutates nothing', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  const slot = { iconFile: 'pack:move_green' };
  let applied = 0;
  picker.openIconPicker(slot, () => applied++);
  await tick();
  assert.ok(world.overlay.classList.contains('visible'));
  tileFor(world.grid, 'move').click();
  assert.equal(slot.iconFile, 'pack:move_green', 'popdown open must not write');
  assert.equal(applied, 0, 'onApply must not fire before a color click');
  assert.equal(world.grid.querySelectorAll('.icon-variant').length, 7);
});

// ── commit on color click: exact persisted value ──────────────────────
await check('color click persists the explicit value and closes', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  const slot = { iconFile: null };
  let applied = 0;
  picker.openIconPicker(slot, () => applied++);
  await tick();
  tileFor(world.grid, 'move').click();
  variantFor(picker, 'red').click();
  assert.equal(slot.iconFile, 'pack:move_red');
  assert.equal(applied, 1);
  assert.ok(!world.overlay.classList.contains('visible'), 'picker closed after commit');
});

await check('blue click persists explicit _blue (not a bare legacy value)', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  const slot = { iconFile: 'pack:move' };   // legacy bare
  picker.openIconPicker(slot, () => {});
  await tick();
  tileFor(world.grid, 'move').click();
  variantFor(picker, 'blue').click();
  assert.equal(slot.iconFile, 'pack:move_blue');
});

// ── None ──────────────────────────────────────────────────────────────
await check('None writes null immediately and closes', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  const slot = { iconFile: 'pack:move_green' };
  let applied = 0;
  picker.openIconPicker(slot, () => applied++);
  await tick();
  world.grid.children[0].click();   // None tile is always first
  assert.equal(slot.iconFile, null);
  assert.equal(applied, 1);
  assert.ok(!world.overlay.classList.contains('visible'));
});

// ── Cancel / outside click / Escape ───────────────────────────────────
await check('Cancel closes without mutating', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  const slot = { iconFile: 'pack:move_green' };
  let applied = 0;
  picker.openIconPicker(slot, () => applied++);
  await tick();
  tileFor(world.grid, 'move').click();
  picker.closeIconPicker();
  assert.equal(slot.iconFile, 'pack:move_green');
  assert.equal(applied, 0);
  assert.ok(!world.overlay.classList.contains('visible'));
});

await check('outside click closes; inside click does not', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  const slot = { iconFile: 'pack:move_green' };
  let applied = 0;
  picker.openIconPicker(slot, () => applied++);
  await tick();
  world.overlay.onclick({ target: world.grid });            // inside
  assert.ok(world.overlay.classList.contains('visible'), 'inside click keeps the picker open');
  world.overlay.onclick({ target: world.overlay });         // backdrop
  assert.ok(!world.overlay.classList.contains('visible'), 'outside click closes');
  assert.equal(slot.iconFile, 'pack:move_green', 'outside click must not clear or mutate');
  assert.equal(applied, 0, 'outside click must not fire onApply (no dirtying)');
});

await check('Escape closes the popdown first, then the picker', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  const slot = { iconFile: 'pack:move_green' };
  picker.openIconPicker(slot, () => {});
  await tick();
  const tile = tileFor(world.grid, 'move');
  tile.click();
  assert.ok(picker.__state().pop, 'popdown open');
  world.document.dispatchKey('Escape');
  assert.equal(picker.__state().pop, null, 'first Escape closes only the popdown');
  assert.ok(world.overlay.classList.contains('visible'), 'picker still open');
  assert.equal(world.document.activeElement, tile, 'focus returns to the design tile');
  world.document.dispatchKey('Escape');
  assert.ok(!world.overlay.classList.contains('visible'), 'second Escape closes the picker');
  assert.equal(slot.iconFile, 'pack:move_green', 'no mutation on the way out');
});

// ── keyboard navigation ───────────────────────────────────────────────
await check('focus lands on the selected variant; arrows rove in palette order', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  picker.openIconPicker({ iconFile: 'pack:move_green' }, () => {});
  await tick();
  tileFor(world.grid, 'move').click();
  const pop = picker.__state().pop.el;
  assert.equal(world.document.activeElement, variantFor(picker, 'green'), 'selected color focused');

  const press = (key) => pop.onkeydown({ key, preventDefault() {} });
  press('ArrowRight');
  assert.equal(world.document.activeElement, variantFor(picker, 'orange'));
  press('ArrowLeft');
  assert.equal(world.document.activeElement, variantFor(picker, 'green'));
  press('ArrowLeft');
  assert.equal(world.document.activeElement, variantFor(picker, 'purple'));
  press('End');
  assert.equal(world.document.activeElement, variantFor(picker, 'red'));
  press('ArrowRight');   // wraps to first
  assert.equal(world.document.activeElement, variantFor(picker, 'light_grey'));
  press('Home');
  assert.equal(world.document.activeElement, variantFor(picker, 'light_grey'));
});

// ── selected-state restore ────────────────────────────────────────────
await check('explicit value restores selected design + color', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  picker.openIconPicker({ iconFile: 'pack:move_green' }, () => {});
  await tick();
  assert.ok(tileFor(world.grid, 'move').classList.contains('selected'));
  assert.ok(!tileFor(world.grid, 'apple').classList.contains('selected'));
  tileFor(world.grid, 'move').click();
  assert.ok(variantFor(picker, 'green').classList.contains('selected'));
  assert.ok(!variantFor(picker, 'red').classList.contains('selected'));
});

await check('legacy bare value restores as blue without rewriting it', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  const slot = { iconFile: 'pack:move' };
  picker.openIconPicker(slot, () => {});
  await tick();
  assert.ok(tileFor(world.grid, 'move').classList.contains('selected'));
  tileFor(world.grid, 'move').click();
  assert.ok(variantFor(picker, 'blue').classList.contains('selected'));
  picker.closeIconPicker();
  assert.equal(slot.iconFile, 'pack:move', 'open + close must not rewrite the bare value');
});

await check('mixed-case stored value restores selection like the runtime resolver', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  const slot = { iconFile: 'PACK:Move_Blue' };   // valid for the case-insensitive runtime
  picker.openIconPicker(slot, () => {});
  await tick();
  assert.ok(tileFor(world.grid, 'move').classList.contains('selected'),
    'mixed-case design highlights its canonical tile');
  tileFor(world.grid, 'move').click();
  assert.ok(variantFor(picker, 'blue').classList.contains('selected'),
    'mixed-case color highlights its canonical variant');
  picker.closeIconPicker();
  assert.equal(slot.iconFile, 'PACK:Move_Blue', 'stored string preserved untouched');
});

await check('unknown color or design highlights nothing; string preserved', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  const slot = { iconFile: 'pack:move_fuchsia' };   // parses bare, not in catalogue
  picker.openIconPicker(slot, () => {});
  await tick();
  for (const tile of world.grid.children) {
    assert.ok(!tile.classList.contains('selected'), tile.title + ' must not be selected');
  }
  picker.closeIconPicker();
  assert.equal(slot.iconFile, 'pack:move_fuchsia');
});

// ── rapid design switching ────────────────────────────────────────────
await check('rapid design clicks end with one popdown and no mutation', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  const slot = { iconFile: null };
  picker.openIconPicker(slot, () => {});
  await tick();
  tileFor(world.grid, 'apple').click();
  tileFor(world.grid, 'move').click();
  tileFor(world.grid, 'link_external').click();
  const pops = world.grid._walk([]).filter((e) => e.classList.contains('icon-popdown'));
  assert.equal(pops.length, 1, 'exactly one popdown open');
  assert.ok(tileFor(world.grid, 'link_external').classList.contains('popen'),
    'the last clicked design owns the popdown');
  assert.equal(pops[0].querySelectorAll('.icon-variant').length, 7);
  assert.equal(slot.iconFile, null, 'no profile mutation');
});

await check('clicking the same design toggles its popdown shut', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  picker.openIconPicker({ iconFile: null }, () => {});
  await tick();
  const tile = tileFor(world.grid, 'move');
  tile.click();
  assert.ok(picker.__state().pop);
  tile.click();
  assert.equal(picker.__state().pop, null);
});

// ── broken variant images ─────────────────────────────────────────────
await check('broken variant is disabled and logged, never applied', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  picker.openIconPicker({ iconFile: null }, () => {});
  await tick();
  tileFor(world.grid, 'move').click();
  const btn = variantFor(picker, 'orange');
  const img = btn.children.find((c) => c.tagName === 'IMG');
  img.onerror();
  assert.ok(btn.disabled, 'broken variant disabled');
  assert.ok(world.logs.some((l) => l.level === 'error' && l.payload?.name === 'move' && l.payload?.color === 'orange'),
    'failure logged');
  btn.click();   // a disabled button cannot commit
  assert.equal(btn.disabled, true);
});

// ── bridge failure / missing pack ─────────────────────────────────────
await check('bridge rejection caches an empty list; picker shows only None', async () => {
  const world = makeWorld({ bridgeError: 'COM exploded' });
  const picker = makePicker(world);
  picker.openIconPicker({ iconFile: null }, () => {});
  await tick();
  assert.equal(world.grid.children.length, 1, 'only the None tile renders');
  assert.equal(world.grid.children[0].title, 'No icon (default)');
  assert.ok(world.logs.some((l) => l.level === 'error' && l.message === 'iconpack list failed'));
  assert.deepEqual(picker.__state().cache, [], 'empty catalogue cached for the session');
});

await check('malformed catalogue entries are filtered out', async () => {
  const world = makeWorld({
    catalogue: [catalogueEntry('move'), { name: 'broken' }, { colors: [] }, null, 'junk'],
  });
  const picker = makePicker(world);
  picker.openIconPicker({ iconFile: null }, () => {});
  await tick();
  assert.equal(world.grid.children.length, 2, 'None + the one well-formed entry');
});

// ── popdown placement: in-flow row, never overlapping (spec #9) ───────
await check('popdown inserts in-flow right after its tile, with no absolute positioning', async () => {
  const world = makeWorld({ catalogue: CATALOGUE });
  const picker = makePicker(world);
  picker.openIconPicker({ iconFile: null }, () => {});
  await tick();
  const tile = tileFor(world.grid, 'move');
  tile.click();
  const pop = picker.__state().pop.el;
  const kids = world.grid.children;
  assert.equal(kids[kids.indexOf(tile) + 1], pop, 'popdown is the tile’s next grid sibling');
  assert.equal(pop.style.top, undefined, 'no absolute top offset');
  assert.equal(pop.style.left, undefined, 'no absolute left offset');

  // Reopening on another design moves the row with its tile.
  const other = tileFor(world.grid, 'link_external');
  other.click();
  const kids2 = world.grid.children;
  assert.equal(kids2[kids2.indexOf(other) + 1], picker.__state().pop.el);
});

await check('CSS pins the popdown as an in-flow full-width row (no overlay)', async () => {
  const html = fs.readFileSync(htmlPath, 'utf8');
  const rule = html.match(/\.icon-popdown\s*\{([^}]*)\}/);
  assert.ok(rule, '.icon-popdown rule present');
  assert.match(rule[1], /grid-column:\s*1\s*\/\s*-1/, 'spans the full grid width');
  assert.ok(!/position:\s*absolute/.test(rule[1]), 'never absolutely positioned over tiles');
  const gridRule = html.match(/\.icon-picker-grid\s*\{([^}]*)\}/);
  assert.match(gridRule[1], /grid-auto-flow:\s*dense/, 'split row backfills densely');
});

// ── catalogue order in the popdown ────────────────────────────────────
await check('variants render in canonical palette order regardless of bridge order', async () => {
  const world = makeWorld({
    catalogue: [{ name: 'move', colors: COLORS.slice().reverse() }],
  });
  const picker = makePicker(world);
  picker.openIconPicker({ iconFile: null }, () => {});
  await tick();
  tileFor(world.grid, 'move').click();
  const rendered = picker.__state().pop.el.querySelectorAll('.icon-variant')
    .map((b) => b.dataset.color);
  assert.deepEqual(rendered, COLORS);
});

if (failures > 0) {
  console.error(failures + ' failure(s)');
  process.exit(1);
}
console.log('all passed');
};

run();
