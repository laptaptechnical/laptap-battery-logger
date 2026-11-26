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

// ---------------- v15: band, live log, DOM cap (plan §2, §3, §6.1) ----------------
const BASE = { checks: { battery: true, pct96: true, unplugged: false }, armed: true, countdownEndsMs: null, nowMs: 1000, now: { pct: 100 } };
test('bandView: Ready shows the checklist; Start only after a Cancel', () => {
  const v = L.bandView(BASE);
  assert.equal(v.mode, 'ready');
  assert.deepEqual(v.checks.map(c => [c.id, c.ok]), [['battery', true], ['pct96', true], ['unplugged', false]]);
  assert.equal(v.showStart, false);
  assert.equal(L.bandView({ ...BASE, armed: false }).showStart, true);
});
test('bandView: below 96% says the current %, plus the charge-limit hint after 10 flat minutes', () => {
  const v = L.bandView({ ...BASE, checks: { battery: true, pct96: false, unplugged: false }, now: { pct: 87 } });
  assert.equal(v.checks[1].text, 'Charge to 96% or more (now 87%)');
  const h = L.bandView({ ...BASE, checks: { battery: true, pct96: false, unplugged: false, chargeLimitHint: true }, now: { pct: 80 } });
  assert.equal(h.checks[1].text, 'Charge to 96% or more (now 80%) · check the battery charge-limit setting');
  assert.equal(L.bandView({ ...BASE, checks: { battery: false } }).checks[0].text, 'No battery found');
});
test('bandView: Countdown digits 3 → 2 → 1 from countdownEndsMs − nowMs', () => {
  const at = (now) => L.bandView({ ...BASE, checks: { battery: true, pct96: true, unplugged: true }, countdownEndsMs: 4000, nowMs: now });
  assert.equal(at(1000).mode, 'count');
  assert.deepEqual([at(1000).digit, at(1500).digit, at(2100).digit, at(3200).digit, at(3999).digit, at(5000).digit], [3, 3, 2, 1, 1, 1]);
});
test('bandView: Logging / Paused / Done', () => {
  assert.equal(L.bandView({ ...BASE, session: { status: 'running' } }).mode, 'logging');
  const p = L.bandView({ ...BASE, session: { status: 'paused' } });
  assert.equal(p.mode, 'paused'); assert.equal(p.sub, 'Unplug to continue');
  const d = L.bandView({ ...BASE, session: { status: 'done' }, result: { reason: 'Ended at alarm', files: { html: 'C:/Desktop/x.html' } } });
  assert.equal(d.mode, 'done'); assert.equal(d.sub, 'Ended at alarm · report saved to the Desktop');
  const f = L.bandView({ ...BASE, session: { status: 'done' }, result: { reason: 'Stopped early', files: { html: null } } });
  assert.equal(f.sub, 'Stopped early · report not saved to the Desktop');
});
test('mergeRows: an event and its own reading are one row with the reading\'s numbers (M1)', () => {
  const samples = [{ rev: 1, ms: 100, pct: 98, mwh: 46000, rateMw: null, elapsedS: 0 }, { rev: 3, ms: 200, pct: 97, mwh: 45500, rateMw: 9000, elapsedS: 60 }];
  const events = [{ rev: 2, ms: 100, type: 'unplugged', pct: 98, mwh: 46000, rateMw: null, elapsedS: 0, note: '' }];
  const rows = L.mergeRows(samples, events);
  assert.equal(rows.length, 2);
  assert.deepEqual(rows.map(r => r.type), ['reading', 'unplugged']);   // newest first
  assert.equal(rows[1].mwh, 46000);
});
test('mergeRows: newest first, readings and events interleaved by time', () => {
  const rows = L.mergeRows([{ rev: 1, ms: 10, pct: 99 }, { rev: 4, ms: 40, pct: 97 }], [{ rev: 2, ms: 20, type: 'saver_on' }, { rev: 3, ms: 30, type: 'gap', fromMs: 25, toMs: 30, note: 'slept' }]);
  assert.deepEqual(rows.map(r => r.ms), [40, 30, 20, 10]);
  assert.equal(rows[1].note, 'slept');
});
test('capRows: at most 20,000 DOM rows; the in-memory samples are never touched', () => {
  const samples = Array.from({ length: 5 }, (_, i) => ({ ms: i }));
  assert.equal(L.capRows(19990, 5), 0);
  assert.equal(L.capRows(19998, 5), 3);
  assert.equal(L.capRows(20000, 500), 500);
  assert.equal(L.capRows(10, 30000), 10);   // never more than what's in the DOM
  assert.equal(L.capRows(100, 5, 50), 55);
  assert.equal(samples.length, 5);
});
