// failed-disables.test.mjs — regression guard for failedDisableMessage()
// in src/RST.UI/Assets/profile_loader.html.
//
// Bug it locks down (flag #15, doc #4 addendum): the bridge reports
// add-ins the disable pass could not rename as { fileName: ... }, but the
// UI mapped f.name — so the list always filtered to empty and the failure
// message degenerated. Combined with a 3-second auto-dismiss toast racing
// the loader's auto-close, 5 failed disables shipped as "all clear".
//
// This test extracts the REAL shipped function body from the HTML (so it
// exercises production code, not a copy). Zero deps — run with `node` or
// `./sc test`.

import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const htmlPath = path.resolve(here, '../../src/RST.UI/Assets/profile_loader.html');

// --- Extract failedDisableMessage by brace-matching from its declaration.
function extractFunction(html, name) {
  const fnStart = html.indexOf('function ' + name + '(');
  if (fnStart < 0) throw new Error(name + ' not found in ' + htmlPath);
  const braceOpen = html.indexOf('{', fnStart);
  let depth = 0, i = braceOpen;
  for (; i < html.length; i++) {
    const ch = html[i];
    if (ch === '{') depth++;
    else if (ch === '}') { depth--; if (depth === 0) { i++; break; } }
  }
  return html.slice(fnStart, i);
}

const html = fs.readFileSync(htmlPath, 'utf8');
const failedDisableMessage = new Function(
  extractFunction(html, 'failedDisableMessage') + '\n; return failedDisableMessage;')();

let failures = 0;
function check(label, fn) {
  try { fn(); console.log('  ok - ' + label); }
  catch (e) { failures++; console.error('  FAIL - ' + label + '\n    ' + e.message); }
}

console.log('failed-disables.test.mjs');

check('empty / missing list → null (nothing to surface)', () => {
  assert.equal(failedDisableMessage([]), null);
  assert.equal(failedDisableMessage(null), null);
  assert.equal(failedDisableMessage(undefined), null);
});

check('bridge shape { fileName } is read (the flag #15 mapping bug)', () => {
  const msg = failedDisableMessage([{ fileName: 'BatchPrint.addin' }]);
  assert.ok(msg && msg.includes('BatchPrint.addin'), 'file name must appear in the message: ' + msg);
});

check('multiple failures list every file name', () => {
  const failed = ['BatchPrint', 'eTransmit', 'TotalCarbonAnalysis', 'WorksharingMonitor', 'FormItConverter']
    .map(n => ({ fileName: n + '.addin' }));
  const msg = failedDisableMessage(failed);
  for (const f of failed) {
    assert.ok(msg.includes(f.fileName), f.fileName + ' missing from: ' + msg);
  }
});

check('legacy shape { name } still surfaces', () => {
  const msg = failedDisableMessage([{ name: 'Old.addin' }]);
  assert.ok(msg && msg.includes('Old.addin'), 'got: ' + msg);
});

check('unknown entry shape still produces a message (never silent)', () => {
  const msg = failedDisableMessage([{ unexpected: true }]);
  assert.ok(msg && msg.length > 0, 'a failure with no readable name must still surface');
});

if (failures > 0) {
  console.error(failures + ' failure(s)');
  process.exit(1);
}
console.log('all passed');
