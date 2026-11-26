// Battery-Logger/test/v14-fixes.test.mjs
// Build-audit fixes on the page (audit-build.md): I2 (F5 keeps the token) and I3 (failed Desktop saves are shown).
// Logger mode against a tiny mocked exe in the api-contract.md shapes. Run (from Battery-Logger/):
//   node --test --test-concurrency=1 test/v14-fixes.test.mjs
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { openLogger, closeBrowser, ROOT } from './harness.mjs';

after(closeBrowser);
const WEB = path.join(ROOT, 'src', 'web');
const ORIGIN = 'http://localhost:47601';
const TOKEN = '0f1e2d3c4b5a69788796a5b4c3d2e1f0';
const T0 = Date.parse('2026-11-26T09:00:00+08:00');
const realSleep = (ms) => new Promise(r => setTimeout(r, ms));
// Fake time in 1 s steps with a little real time between, so fetch() answers land between timers.
async function advance(L, ms) { for (let t = 0; t < ms; t += 1000) { await L.page.clock.runFor(Math.min(1000, ms - t)); await realSleep(20); } }

function mockExe({ result = null, alarm = false, csvOk = true } = {}) {
  const calls = [];
  const state = {
    rev: 2, version: '14.0.0', unit: { maker: 'HP', model: 'EliteBook 840 G5', serial: '5CB4190FVX', computerName: 'LAPTOP-1' },
    batteries: [{ id: 'BAT', maker: 'SMP', chemistry: 'LIon', designMwh: 51528, fullMwh: 47378, fullEndMwh: null, cycles: null }],
    now: { pct: result ? 15 : 90, mwh: 42000, fullMwh: 47378, rateMw: 9000, ac: false, charging: false, saver: false },
    session: { id: 's1', status: result ? 'done' : 'running', startedMs: T0, waitingSinceMs: null, alarmPct: 15, alarmPctMax: 50, alarm, alarmAck: false,
      segments: [{ startMs: T0, endMs: result ? T0 + 3600000 : null, startPct: 98, endPct: result ? 15 : null, startMwh: 46000, endMwh: null }] },
    samples: [{ rev: 1, ms: T0, elapsedS: 0, pct: 98, mwh: 46000, fullMwh: 47378, rateMw: 9000, rateSrc: 'wmi', voltageMv: 12000, ac: false, saver: false }],
    events: [{ rev: 2, ms: T0, elapsedS: 0, type: 'start', pct: 98, mwh: 46000, rateMw: null, note: '', fromMs: null, toMs: null }],
    result,
  };
  const routes = [[`${ORIGIN}/**`, async (route) => {
    const req = route.request(), u = new URL(req.url());
    const tok = req.headers()['x-laptap-token'] || null;
    calls.push({ m: req.method(), p: u.pathname, tok });
    const send = (status, body, type = 'application/json') => route.fulfill({ status, contentType: type, body: typeof body === 'string' ? body : JSON.stringify(body) });
    if (u.pathname === '/') return send(200, fs.readFileSync(path.join(WEB, 'index.html'), 'utf8'), 'text/html; charset=utf-8');
    if (u.pathname === '/logic.js') return send(200, fs.readFileSync(path.join(WEB, 'logic.js'), 'utf8'), 'text/javascript');
    if (tok !== TOKEN) return send(403, '403', 'text/plain');
    if (u.pathname === '/api/state') { const since = +(u.searchParams.get('since') || 0); return send(200, { ...state, samples: state.samples.filter(x => x.rev > since), events: state.events.filter(x => x.rev > since) }); }
    if (u.pathname === '/api/save-csv') return send(200, csvOk ? { ok: true, file: 'C:\\x.csv' } : { ok: false, error: 'Could not save to the Desktop.' });
    return send(200, { ok: true });
  }]];
  return { calls, routes };
}
async function open(exe) {
  const L = await openLogger({ url: `${ORIGIN}/#t=${TOKEN}`, routes: exe.routes });
  await realSleep(150); await advance(L, 1000);
  return L;
}

test('I2: F5 / reload keeps the token, so the page stays connected (no "Logger stopped")', async () => {
  const exe = mockExe();
  const L = await open(exe);
  try {
    assert.doesNotMatch(L.page.url(), /#t=/, 'token removed from the address bar');
    await L.page.reload();                     // a bare reload: no #t= in the URL any more
    await realSleep(150);
    const from = exe.calls.length;
    await advance(L, 20_000);                   // 4+ polls at 5 s
    const api = exe.calls.slice(from).filter(c => c.p.startsWith('/api/'));
    assert.ok(api.length >= 3, `polls after reload: ${api.length}`);
    assert.ok(api.every(c => c.tok === TOKEN), `token on every call after reload: ${JSON.stringify(api.map(c => c.tok))}`);
    assert.doesNotMatch(await L.page.locator('#helper').innerText(), /Logger stopped/);
    assert.ok(await L.page.isEnabled('#stopBtn'), 'still running, Stop enabled');
  } finally { await L.close(); }
});

test('I3: a failed Desktop save is shown in the result card and the alarm box', async () => {
  const result = { kind: 'alarm', reason: 'Ended at alarm', startEnd: '98% → 15%', alarmAck: false, runtimeS: 3600, startPct: 98, endPct: 15,
    estFullS: null, estFullEnergyS: null, avgW: 9, minW: 8, maxW: 10, healthPct: 91.9, fullChangedMwh: null, gapCount: 0, gapS: 0,
    pauseCount: 0, pauseS: 0, saver: 'off', brightnessStart: 60, brightnessEnd: 60, plan: 'Balanced', windows: 'Windows 11',
    files: { html: null, csv: null } };
  const L = await open(mockExe({ result, alarm: true }));
  try {
    assert.match(await L.page.locator('#reportBox').innerText(), /Report not saved to the Desktop/);
    assert.ok(await L.page.isVisible('#alarm'));
    assert.match(await L.page.locator('#alarmT').innerText(), /could not be saved to the Desktop/);
    assert.doesNotMatch(await L.page.locator('#alarmT').innerText(), /report is saved to the Desktop/);
  } finally { await L.close(); }
});

test('I3: a Save CSV error is shown, not swallowed', async () => {
  const L = await open(mockExe({ csvOk: false }));
  try {
    await L.page.click('#exportBtn'); await realSleep(150); await advance(L, 200);
    assert.ok(await L.page.isVisible('#csvMsg'));
    assert.equal(await L.page.locator('#csvMsg').innerText(), 'Could not save to the Desktop.');
    assert.equal(await L.page.locator('#exportBtn').innerText(), 'Save CSV');
  } finally { await L.close(); }
});
