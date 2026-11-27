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

// ---------------- v15.1 page helpers (plan v15.1 §6.2) ----------------
const st151 = (o) => Object.assign({ checks: { battery: true, pct96: true, unplugged: false }, armed: true, countdownEndsMs: null, nowMs: 1000,
  session: null, app: { version: '15.1.0', update: { phase: 'idle' } }, nextStep: 'unplug' }, o);

test('v15.1 stageView: every nextStep × unplugged × armed/disarmed × countdown × band', () => {
  const rows = [];
  for (const ns of ['battery', 'charge', 'wait-update', 'unplug', null])
    for (const unplugged of [false, true])
      for (const armed of [true, false])
        for (const cd of [null, 4000]) {
          const v = L.stageView(st151({ nextStep: ns, checks: { battery: ns !== 'battery', pct96: ns !== 'charge' && ns !== 'battery', unplugged }, armed, countdownEndsMs: cd }));
          const want = ns === 'battery' ? 'nobatt' : ns === 'charge' ? (unplugged ? 'low' : 'charge') : ns === 'wait-update' ? 'waitupd'
            : ns === 'unplug' ? 'unplug' : cd != null ? 'countdown' : 'ready';
          assert.equal(v.stage, want, JSON.stringify({ ns, unplugged, armed, cd }));
          assert.equal(v.showStart, want === 'ready' && !armed, 'Start only after Cancel (G4), never armed-unwatched (M1): ' + JSON.stringify({ ns, armed, cd }));
          rows.push(v.stage);
        }
  assert.equal(rows.length, 40);
  assert.equal(L.stageView(st151({ nextStep: 'battery' })).now, 'b');
  assert.equal(L.stageView(st151({ nextStep: 'charge' })).now, 'p');
  assert.equal(L.stageView(st151({ nextStep: 'unplug' })).now, 'a');
  for (const [status, stage] of [['running', 'logging'], ['paused', 'paused'], ['done', 'done']])
    assert.equal(L.stageView(st151({ session: { status }, nextStep: 'unplug' })).stage, stage, 'bands win over nextStep');
  // an exe without nextStep (older contract): the same order from the checks
  assert.equal(L.stageView({ checks: { battery: false } }).stage, 'nobatt');
  assert.equal(L.stageView({ checks: { battery: true, pct96: false, unplugged: true } }).stage, 'low');
  assert.equal(L.stageView({ checks: { battery: true, pct96: true, unplugged: false } }).stage, 'unplug');
  assert.equal(L.stageView({ checks: { battery: true, pct96: true, unplugged: true }, armed: true }).stage, 'ready');
});

test('v15.1 modalView: one modal per phase, checking only after 1 s (G1), none while a test is on screen', () => {
  for (const ph of ['downloading', 'verifying', 'ready', 'failed'])
    assert.equal(L.modalView(st151({ app: { update: { phase: ph } } })), ph, ph);
  assert.equal(L.modalView(st151({ app: { update: { phase: 'idle' } } })), null);
  assert.equal(L.modalView(st151({ app: { update: { phase: 'checking' } } }), { checkingForMs: 900 }), null, 'checking < 1 s: nothing');
  assert.equal(L.modalView(st151({ app: { update: { phase: 'checking' } } }), { checkingForMs: 1200 }), 'checking');
  for (const o of [{ session: { status: 'running' } }, { session: { status: 'paused' } }, { nextStep: null, countdownEndsMs: 5000 }])
    for (const ph of ['checking', 'downloading', 'verifying', 'ready', 'failed'])
      assert.equal(L.modalView(st151(Object.assign({ app: { update: { phase: ph } } }, o)), { checkingForMs: 5000 }), null, 'no modal during a test: ' + ph);
  assert.equal(L.modalView(st151({ session: { status: 'done' }, app: { update: { phase: 'ready' } } })), 'ready', 'Done shows the modal');
  assert.equal(L.modalView(st151({ app: { update: { phase: 'downloading' } } }), { hidden: { downloading: true } }), null, 'Hide');
  assert.equal(L.modalView(st151({ app: { update: { phase: 'failed' } } }), { hidden: { failed: true } }), null, 'Later');
  assert.equal(L.modalView(st151({ app: { update: { phase: 'ready' } } }), { hidden: { ready: true } }), null, 'OK');
  assert.equal(L.modalView(st151({ app: { update: { phase: 'ready' } } }), { restarting: true }), 'restarting', 'after Restart now');
});

test('v15.1 Restart now shows only when idle', () => {
  assert.equal(L.canRestart(st151({})), true, 'Ready');
  assert.equal(L.canRestart(st151({ nextStep: null, countdownEndsMs: 5000 })), false, 'Countdown');
  assert.equal(L.canRestart(st151({ session: { status: 'running' } })), false, 'Logging');
  assert.equal(L.canRestart(st151({ session: { status: 'paused' } })), false, 'Paused');
  assert.equal(L.canRestart(st151({ session: { status: 'done', alarm: true, alarmAck: false } })), false, 'Done, alarm not acknowledged');
  assert.equal(L.canRestart(st151({ session: { status: 'done', alarm: true, alarmAck: true } })), true, 'Done, acknowledged');
  assert.equal(L.canRestart(st151({ session: { status: 'done', alarm: false } })), true, 'Done, no alarm');
});

test('v15.1 chipView: "installs after this test", hidden download, "installs next time"', () => {
  const run = { session: { status: 'running' } };
  for (const ph of ['downloading', 'verifying', 'ready'])
    assert.equal(L.chipView(st151(Object.assign({ app: { update: { phase: ph, version: '15.2.0' } } }, run))).text, 'Update v15.2.0 installs after this test');
  assert.equal(L.chipView(st151(Object.assign({ app: { update: { phase: 'idle' } } }, run))), null);
  assert.equal(L.chipView(st151({ app: { update: { phase: 'downloading', version: '15.2.0', percent: 62 } } }), { hidden: { downloading: true } }).text, 'Downloading v15.2.0 62%');
  assert.equal(L.chipView(st151({ app: { update: { phase: 'downloading', version: '15.2.0', percent: 62 } } })), null, 'not hidden: the modal shows instead');
  assert.equal(L.chipView(st151({ app: { update: { phase: 'ready', version: '15.2.0' } } }), { hidden: { ready: true } }).text, 'v15.2.0 installs next time');
});

test('v15.1 countdown words grow one count at a time (F4)', () => {
  assert.equal(L.countdownWords(3000), '3...');
  assert.equal(L.countdownWords(2500), '3...');
  assert.equal(L.countdownWords(2000), '3... 2...');
  assert.equal(L.countdownWords(1001), '3... 2...');
  assert.equal(L.countdownWords(1000), '3... 2... 1...');
  assert.equal(L.countdownWords(10), '3... 2... 1...');
  assert.equal(L.countdownWords(-500), '3... 2... 1...');
  assert.equal(L.countdownWords(null), '3...');
  assert.deepEqual([3000, 2000, 1000].map(L.countdownDigit), [3, 2, 1]);
});

test('v15.1 heartbeat cadence: 5 s visible, 30 s hidden (inside the exe 15 s / 90 s windows)', () => {
  assert.equal(L.heartbeatEvery(false), 5000);
  assert.equal(L.heartbeatEvery(true), 30000);
  assert.ok(L.heartbeatEvery(false) * 3 <= 15000, 'three visible heartbeats fit in the 15 s window');
  assert.ok(L.heartbeatEvery(true) * 2 < 90000, 'a throttled hidden heartbeat (up to 60 s late) still lands inside 90 s');
  assert.equal(L.fmtHMin(11400), '3 h 10 min'); assert.equal(L.fmtHMin(2520), '42 min'); assert.equal(L.fmtHMin(null), null);
});

test('v15.2.1 auto restart: only ready + idle; words 3.. → 3.. 2.. → 3.. 2.. 1..', () => {
  const r = (o) => L.autoRestart(Object.assign({ checks: {}, nextStep: null, countdownEndsMs: null, session: null, app: { update: { phase: 'ready' } } }, o));
  assert.equal(r({}), true, 'Ready, idle');
  assert.equal(r({ app: { update: { phase: 'verifying' } } }), false, 'not before ready');
  assert.equal(r({ countdownEndsMs: 5000 }), false, 'not in a countdown');
  assert.equal(r({ session: { status: 'running' } }), false, 'not while Logging');
  assert.equal(r({ session: { status: 'paused' } }), false, 'not while Paused');
  assert.equal(r({ session: { status: 'done', alarm: true, alarmAck: false } }), false, 'not before the alarm OK');
  assert.equal(r({ session: { status: 'done', alarm: true, alarmAck: true } }), true, 'Done, alarm acknowledged');
  assert.deepEqual([3000, 2500, 2000, 1000, 0].map(L.autoRestartWords), ['3..', '3..', '3.. 2..', '3.. 2.. 1..', '3.. 2.. 1..']);
});
