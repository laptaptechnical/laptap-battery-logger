// Battery-Logger/v14/tests/logic.test.js
// node --test tests/   (no npm installs). Every case comes from fixtures/rules.json, which HelperTests.cs also runs.
'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const L = require('../src/web/logic.js');
const F = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'rules.json'), 'utf8'));

const near = (a, b, msg) => (a == null || b == null) ? assert.equal(a, b, msg) : assert.ok(Math.abs(a - b) < 1e-9, `${msg}: ${a} vs ${b}`);

// Same point sets HelperTests.cs builds for the etaToAlarm fixtures.
function etaPoints(kind) {
  const p = [];
  if (kind.startsWith('linear')) for (let e = 0; e <= 900; e += 60) p.push({ e, pct: 50 - e / 120 });
  else if (kind.startsWith('four')) for (let e = 0; e < 240; e += 60) p.push({ e, pct: 90 - e / 60 });
  else for (let e = 0; e <= 900; e += 60) p.push({ e, pct: 80 });
  return p;
}

test('intervalFor at 100/21/20/18/17/15 (alarm 15) and when idle', () => {
  for (const [pct, alarm, running, want] of F.intervalFor) assert.equal(L.intervalFor(pct, alarm, running), want, `${pct}% alarm ${alarm}`);
});
test('detectGap around interval + 90 s', () => {
  for (const [a, b, i, want] of F.detectGap) assert.equal(L.detectGap(a, b, i), want, `${a}→${b} @${i}s`);
});
test('canStart at 95/96/99/100', () => {
  for (const [p, want] of F.canStart) assert.equal(L.canStart(p), want, String(p));
});
for (const c of F.segmentCases) test(`segments: ${c.name}`, () => {
  const s = L.segments(c.events);
  assert.equal(s.length, c.segments);
  near(L.countedTime(s), c.countedTime, 'countedTime');
  near(L.countedDrop(s), c.countedDrop, 'countedDrop');
  near(L.countedEnergy(s), c.countedEnergy, 'countedEnergy');
  near(L.estFull(s), c.estFull, 'estFull');
  near(L.avgW(s), c.avgW, 'avgW');
  near(L.estFullEnergy(c.fullMwh, L.avgW(s)), c.estFullEnergy, 'estFullEnergy');
});
test('the audit example does not use start% - end% (83)', () => {
  const s = L.segments(F.segmentCases[0].events);
  assert.equal(L.countedDrop(s), 113);
  assert.notEqual(L.countedDrop(s), 98 - 15);
});
test('etaToAlarm: linear fit over the last 15 counted minutes', () => {
  for (const c of F.etaToAlarm) near(L.etaToAlarm(etaPoints(c.points), c.alarm), c.expect, c.points);
});
test('health: raw kept, shown capped at 100, relative capacity = null', () => {
  for (const [full, design, raw, shown] of F.health) {
    const h = L.health(full, design);
    assert.equal(h.raw, raw, `${full}/${design} raw`); assert.equal(h.shown, shown, `${full}/${design} shown`);
  }
});
test('fmtElapsed / fmtHm', () => {
  for (const [s, want] of F.fmtElapsed) assert.equal(L.fmtElapsed(s), want);
  for (const [s, want] of F.fmtHm) assert.equal(L.fmtHm(s), want);
});
test('isoLocal gives ISO 8601 with offset (+08:00)', () => {
  for (const [ms, off, want] of F.isoLocal) assert.equal(L.isoLocal(ms, off), want);
});
test('csvRow: RFC 4180 escaping', () => { for (const [cells, want] of F.csvRow) assert.equal(L.csvRow(cells), want); });
test('escapeHtml on <script> in a model name', () => { for (const [i, want] of F.escapeHtml) assert.equal(L.escapeHtml(i), want); });
test('fillReport: escapes values, keeps *Html raw, leaves no slot', () => {
  for (const [tpl, data, want] of F.fillReport) { const out = L.fillReport(tpl, data); assert.equal(out, want); assert.doesNotMatch(out, /\{\{/); }
});
test('sanitizeSerial / isPlaceholderSerial', () => {
  for (const [i, want] of F.sanitizeSerial) assert.equal(L.sanitizeSerial(i), want, i);
  for (const [i, want] of F.isPlaceholderSerial) assert.equal(L.isPlaceholderSerial(i), want, i);
});
test('validSample: sanity bounds give null, never a guess', () => {
  for (const [s, full, want] of F.validSample) {
    const o = L.validSample(s, full);
    for (const k of Object.keys(want)) assert.equal(o[k], want[k], `${k} of ${JSON.stringify(s)}`);
  }
});
test('endReason wording', () => { for (const [k, p, s, want] of F.endReason) assert.equal(L.endReason(k, p, s), want); });
test('powerState: 100% on AC is "Plugged in, full" (bug 13)', () => {
  for (const [ac, ch, p, want] of F.powerState) assert.equal(L.powerState(ac, ch, p), want);
});
