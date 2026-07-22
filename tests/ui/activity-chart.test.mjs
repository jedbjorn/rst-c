// activity-chart.test.mjs — regression guard for the Activity tab's chart
// math in src/RST.UI/Assets/health_viewer.html (Telemetry v1 spec, Build
// Plan step 4).
//
// The spec mandates monotone cubic interpolation (Fritsch–Carlson), NOT a
// plain Catmull-Rom spline, precisely so the smoothed curve never
// overshoots below zero between points — a fast sync next to a slow one
// must not render as a negative duration. These tests extract the REAL
// shipped functions from the HTML (so they exercise production code, not
// a copy) and sample the emitted Bézier segments densely to prove the
// no-overshoot property, plus the sparse-state rules (<2 points → no
// curve). Zero deps — run with `node` or `./sc test`.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const htmlPath = path.resolve(here, '../../src/RST.UI/Assets/health_viewer.html');
const html = fs.readFileSync(htmlPath, 'utf8');

// --- Extract a top-level `function name(...) {...}` by brace-matching.
function extractFunction(name) {
  const marker = 'function ' + name + '(';
  const start = html.indexOf(marker);
  if (start < 0) throw new Error(name + ' not found in health_viewer.html');
  const braceOpen = html.indexOf('{', start);
  let depth = 0, i = braceOpen;
  for (; i < html.length; i++) {
    const ch = html[i];
    if (ch === '{') depth++;
    else if (ch === '}') { depth--; if (depth === 0) { i++; break; } }
  }
  return html.slice(start, i);
}

const block = ['monotoneTangents', 'monotoneCubicPath', 'chartMode', 'fmtDur', 'activityStatValues']
  .map(extractFunction).join('\n');
const { monotoneTangents, monotoneCubicPath, chartMode, fmtDur, activityStatValues } = new Function(
  block + '\n; return { monotoneTangents, monotoneCubicPath, chartMode, fmtDur, activityStatValues };')();

// --- Parse the emitted path into cubic Bézier segments and sample them.
function sampleBezierYs(pathStr, samplesPerSeg = 64) {
  const move = pathStr.match(/^M(-?[\d.]+) (-?[\d.]+)/);
  if (!move) throw new Error('path has no M: ' + pathStr);
  let x0 = parseFloat(move[1]), y0 = parseFloat(move[2]);
  const ys = [{ x: x0, y: y0 }];
  const segRe = /C((?:-?[\d.]+ ){5}-?[\d.]+)/g;
  let m;
  while ((m = segRe.exec(pathStr)) !== null) {
    const [c1x, c1y, c2x, c2y, x1, y1] = m[1].trim().split(' ').map(parseFloat);
    for (let s = 1; s <= samplesPerSeg; s++) {
      const t = s / samplesPerSeg, u = 1 - t;
      ys.push({
        x: u * u * u * x0 + 3 * u * u * t * c1x + 3 * u * t * t * c2x + t * t * t * x1,
        y: u * u * u * y0 + 3 * u * u * t * c1y + 3 * u * t * t * c2y + t * t * t * y1,
      });
    }
    x0 = x1; y0 = y1;
  }
  return ys;
}

const failures = [];
function check(name, cond) { if (!cond) failures.push(name); }

// --- No-overshoot: a spiky non-negative series must never dip below zero.
//     (A Catmull-Rom spline through this data undershoots between the 8
//     and the two 0.1s — the exact artifact the spec forbids.)
{
  const pts = [0, 0.1, 8, 0.1, 0.05, 9, 0].map((v, i) => ({ x: i * 100, y: v }));
  const sampled = sampleBezierYs(monotoneCubicPath(pts));
  const minY = Math.min(...sampled.map(p => p.y));
  check('curve never dips below zero on non-negative data (min=' + minY.toFixed(4) + ')',
    minY >= -1e-9);
}

// --- Segment-bounded: between any two adjacent points the curve stays
//     within their y-range (monotone interpolation, no overshoot above
//     either).
{
  const data = [2, 7, 7.2, 1, 1, 5];
  const pts = data.map((v, i) => ({ x: i * 50, y: v }));
  const sampled = sampleBezierYs(monotoneCubicPath(pts));
  let bounded = true;
  for (const p of sampled) {
    const i = Math.min(Math.floor(p.x / 50), data.length - 2);
    const lo = Math.min(data[i], data[i + 1]) - 1e-6;
    const hi = Math.max(data[i], data[i + 1]) + 1e-6;
    if (p.y < lo || p.y > hi) { bounded = false; break; }
  }
  check('curve stays within each segment\'s y-range', bounded);
}

// --- Flat data stays flat (Fritsch–Carlson zeroes tangents on zero secants).
{
  const pts = [5, 5, 5, 5].map((v, i) => ({ x: i * 10, y: v }));
  const sampled = sampleBezierYs(monotoneCubicPath(pts));
  check('flat series renders as a flat line',
    sampled.every(p => Math.abs(p.y - 5) < 1e-9));
}

// --- Tangent sign: interior tangent at a local extremum is zero.
{
  const m = monotoneTangents([0, 1, 2], [0, 10, 0]);
  check('tangent at a local maximum is 0', m[1] === 0);
}

// --- SC-031: equal-x points (two events in the same instant) must not
//     emit NaN — the equal-x pair zeroed a secant denominator at PR head.
//     They collapse to their mean y before interpolation.
{
  const p = monotoneCubicPath([{ x: 1, y: 1 }, { x: 1, y: 2 }, { x: 2, y: 3 }]);
  check('equal-x pair emits a NaN-free path', p !== '' && !p.includes('NaN'));
  check('equal-x pair collapses to its mean y (curve starts at 1.50)',
    p.startsWith('M1.00 1.50'));
  const sampled = p ? sampleBezierYs(p) : [];
  check('equal-x collapsed curve samples finite',
    sampled.length > 0 && sampled.every(q => isFinite(q.x) && isFinite(q.y)));
}
check('all points at a single x emit no path (dots only)',
  monotoneCubicPath([{ x: 5, y: 1 }, { x: 5, y: 2 }]) === '');

// --- Sparse states: <2 points → no curve; the mode helper gates rendering.
check('monotoneCubicPath with 1 point emits no path',
  monotoneCubicPath([{ x: 0, y: 3 }]) === '');
check('monotoneCubicPath with 0 points emits no path', monotoneCubicPath([]) === '');
check('chartMode: 0 points → empty', chartMode([]) === 'empty');
check('chartMode: 1 point → dots (no curve)', chartMode([{ x: 0, y: 1 }]) === 'dots');
check('chartMode: 2 points → curve', chartMode([{ x: 0, y: 1 }, { x: 1, y: 2 }]) === 'curve');

// --- Duration formatting used by dots/axis labels.
check('fmtDur 5.3s', fmtDur(5.3) === '5.3s');
check('fmtDur 45s', fmtDur(45) === '45s');
check('fmtDur 90s → 1m 30s', fmtDur(90) === '1m 30s');
check('fmtDur 5400s → 1h 30m', fmtDur(5400) === '1h 30m');
check('fmtDur null → —', fmtDur(null) === '—');

// --- Range pills shipped with the spec's four windows, 7D default.
check('range pills carry 7/30/90/180',
  ['data-days="7"', 'data-days="30"', 'data-days="90"', 'data-days="180"']
    .every(a => html.includes(a)));
check('7D pill is the default-active one',
  /class="act-pill active" data-days="7"/.test(html));

// --- Stat cards: all six ship in one row at the top of the Activity tab.
check('all six stat cards ship with their value hosts',
  ['Session Timer', 'Session Total', 'Longest Sync', 'Longest Open Time', 'Warning Count', 'File Size']
    .every(l => html.includes('>' + l + '<')) &&
  ['actSessionTime', 'statSessionTotal', 'statLongestSync', 'statLongestOpen', 'statWarnings', 'statFileSize']
    .every(id => html.includes('id="' + id + '"')));

// --- activityStatValues: range-scoped cards come from the same series the
//     charts plot; warnings/file size pass through point-in-time.
check('no model open → null (all cards render —)', activityStatValues(null) === null);
{
  const s = activityStatValues({
    perDayOpenHours: [{ date: '2026-07-20', hours: 2.5 }, { date: '2026-07-21', hours: 1.5 }, { date: '2026-07-22', hours: 0 }],
    openEvents: [{ ts: 'a', seconds: 12 }, { ts: 'b', seconds: 47.5 }, { ts: 'c', seconds: 30 }],
    syncEvents: [{ ts: 'd', seconds: 90 }, { ts: 'e', seconds: 8 }],
    warningsCount: 1234,
    fileSizeMb: 512,
  });
  check('session total sums the per-day hours in range', s.totalHours === 4);
  check('longest open is the max open-event duration', s.longestOpenSeconds === 47.5);
  check('longest sync is the max sync-event duration', s.longestSyncSeconds === 90);
  check('warning count passes through', s.warningsCount === 1234);
  check('file size converts MB → bytes for fmt.bytes', s.fileSizeBytes === 512 * 1024 * 1024);
}
{
  const s = activityStatValues({ perDayOpenHours: [], openEvents: [], syncEvents: [] });
  check('empty series → zero total, null maxes (cards render 0h / —)',
    s.totalHours === 0 && s.longestOpenSeconds === null && s.longestSyncSeconds === null);
  check('missing point-in-time fields stay null', s.warningsCount === null && s.fileSizeBytes === null);
}

if (failures.length) {
  console.error('FAIL — activity chart regression:');
  for (const f of failures) console.error('  ✗ ' + f);
  process.exit(1);
}
console.log('PASS — activity chart math (monotone cubic no-overshoot, sparse states, formatting)');
