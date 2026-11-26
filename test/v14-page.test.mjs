// test/v14-page.test.mjs
// v13's 17 bug tests, kept as regressions on the current page (src/web/index.html). Same harness: headless, muted,
// AudioContext stubbed, fake clock. Two modes:
//   - logger mode: the page is served at http://localhost:47600/ by a mocked exe (FakeExe answers /api/*);
//   - browser-only mode: the page opened as file:// with the fake navigator.getBattery().
// v15.1: the screens follow prototype v15.1 (one stage per state; the % before a test is #bigpct, the stats tiles show
// during a test). v15: there is no Start button in the normal flow. The exe runs the checklist + 3 s countdown, so logger-mode tests
// start the mocked test with exe.post('/api/start') (the TESTHOOKS hook) and browser-only tests let the page's own
// countdown run (unplugged at 100%). The v15 start flow itself is covered by Impl #2's test/v15-*.test.mjs.
// Run (from the repo root):  node --test --test-concurrency=1 test/v14-page.test.mjs
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { openLogger, closeBrowser, ROOT } from './harness.mjs';

after(closeBrowser);
const WEB = path.join(ROOT, 'src', 'web');
const PAGE = path.join(WEB, 'index.html');
const MIN = 60_000;
const T0 = Date.parse('2026-11-26T09:00:00+08:00');
const ORIGIN = 'http://localhost:47600';
// Which /api/state shapes the mocked exe sends: 'contract' (api-contract.md = Session.cs since 11:25, default) or 'impl1' (the 11:20 page's older names).
const SHAPES = process.env.V14_SHAPES || 'contract';

// ---------- mocked exe: one internal model; state() = older Session.cs shapes, stateContract() = api-contract.md ----------
const LABELS = { start: 'Test started', waiting: 'Waiting for unplug', unplugged: 'Unplugged', charger_in: 'Charger plugged in · paused',
  charger_out: 'Unplugged · resumed', stop: 'Stopped by tech', report: 'Report saved', saver_on: 'Battery saver on' };
const KINDS = { gap: 'gap', alarm: 'alarm' };
class FakeExe {
  constructor(o = {}) {
    this.token = 'a1b2c3d4e5f60718293a4b5c6d7e8f90';
    this.at = T0; this.rev = 0; this.down = false; this.calls = []; this.inflight = 0; this.alarmPct = 15;
    this.unit = { manufacturer: 'HP', model: o.model || 'EliteBook 840 G5', serial: '5CB4190FVX', serialSource: 'BIOS' };
    this.batteries = o.batteries || [{ id: '1', maker: 'SMP', chemistry: 'LIon', designMwh: 51528, fullMwh: 47378, cycles: null }];
    this.now = { pct: 'pct' in o ? o.pct : 100, mwh: o.pct === null ? null : 47378, fullMwh: 47378, rateMw: null, ac: o.ac ?? true, charging: false, saver: false };
    this.S = null; this.samples = []; this.events = []; this.result = null;
  }
  health() {
    const f = this.batteries.reduce((a, b) => a + (b.fullMwh || 0), 0), d = this.batteries.reduce((a, b) => a + (b.designMwh || 0), 0);
    const raw = Math.floor(f / d * 1000 + 0.5) / 10;
    return { raw, shown: Math.min(100, raw), fullMwh: f, designMwh: d, cycles: this.batteries.length === 1 ? this.batteries[0].cycles : null };
  }
  elapsed() { const S = this.S; if (!S) return 0; let t = 0; for (const g of S.segs) t += ((g.end ?? this.at) - g.start) / 1000; return Math.round(t); }
  ev(type, label, x = {}) { this.events.push({ rev: ++this.rev, ms: this.at, type, label: label ?? LABELS[type] ?? type, kind: KINDS[type] || 'ev', pct: this.now.pct, mwh: this.now.mwh, rateMw: this.now.rateMw, e: this.elapsed(), ...x }); }
  sample(ev = 'reading') { this.samples.push({ rev: ++this.rev, ms: this.at, e: this.elapsed(), pct: this.now.pct, mwh: this.now.mwh, rateMw: this.now.rateMw, ac: this.now.ac, ev }); }
  open() { this.S.segs.push({ start: this.at, end: null }); }
  close() { const g = this.S.segs[this.S.segs.length - 1]; if (g && g.end == null) g.end = this.at; }
  unplug() {
    this.now.ac = false; this.now.rateMw = 9500; const S = this.S; if (!S) return;
    if (S.status === 'waiting') { S.status = 'running'; S.unplugMs = this.at; S.startPct = this.now.pct; this.open(); this.ev('unplugged'); this.sample(); }
    else if (S.status === 'paused') { S.status = 'running'; S.pauses[S.pauses.length - 1].toMs = this.at; this.open(); this.ev('charger_out'); }
  }
  plug() { this.now.ac = true; this.now.rateMw = null; const S = this.S; if (S && S.status === 'running') { this.close(); S.status = 'paused'; S.pauses.push({ fromMs: this.at, toMs: null }); this.ev('charger_in'); } }
  drain(pct) { this.now.pct = pct; this.now.mwh = Math.round(47378 * pct / 100); this.sample(); }
  gap(mins, reason = 'slept') { const from = this.at; this.close(); this.at += mins * MIN; this.S.gaps.push({ fromMs: from, toMs: this.at, reason }); this.ev('gap', `Gap ${Math.floor(mins / 60)}:${String(mins % 60).padStart(2, '0')} · laptop ${reason}`); this.open(); }
  end(kind) {
    const S = this.S; this.close(); S.status = 'done'; S.alarm = kind === 'alarm';
    this.ev(kind, kind === 'alarm' ? `Alarm at ${this.now.pct}%` : LABELS.stop);
    const run = this.elapsed();
    this.result = { kind, text: kind === 'alarm' ? 'Ended at alarm' : 'Stopped early', startPct: S.startPct, endPct: this.now.pct, runtimeS: run,
      runtime: `${Math.floor(run / 3600)}:${String(Math.floor(run % 3600 / 60)).padStart(2, '0')} (${S.startPct}% → ${this.now.pct}%)`,
      estFull: 'n/a', estEnergy: 'n/a', avgW: '9.5 W', minMaxW: '8.0 / 11.0 W', health: '91.9%', healthRaw: 91.9, capacity: '47.4 / 51.5 Wh', cycles: 'n/a',
      gaps: 'None', pauses: 'None', saver: 'off', brightness: '60% → 60%', plan: 'Balanced', windows: 'Windows 11', alarm: S.alarm, alarmAck: false,
      reportPath: 'C:\\Users\\tech\\Desktop\\Battery 5CB4190FVX 2026-11-26 0900.html', csvPath: 'C:\\Users\\tech\\Desktop\\Battery 5CB4190FVX 2026-11-26 0900.csv' };
    this.ev('report');
  }
  state(since) {
    const S = this.S;
    return { rev: this.rev, version: '14.0', alarmPct: this.alarmPct, qa: false, unit: this.unit,
      batteries: this.batteries, health: this.health(), now: { ...this.now, ms: this.at },
      session: S && { id: S.id, status: S.status, startedMs: S.startedMs, unplugMs: S.unplugMs, startPct: S.startPct, elapsedS: this.elapsed(), elapsedAtMs: this.at,
        gaps: S.gaps, pauses: S.pauses, alarm: S.alarm, alarmAck: S.alarmAck },
      samples: S ? this.samples.filter(x => x.rev > since) : [], events: S ? this.events.filter(x => x.rev > since) : [],
      result: S && S.status === 'done' ? this.result : null };
  }
  // api-contract.md shapes (default).
  stateContract(since) {
    const S = this.S, r = this.result, TYPE = { waiting: 'waiting_unplug', report: 'report_saved' };
    const segs = S ? S.segs.map(g => ({ startMs: g.start, endMs: g.end, startPct: null, endPct: null, startMwh: null, endMwh: null })) : [];
    const ready = !S || S.status === 'waiting';
    return { rev: this.rev, version: '15.0.0', nowMs: this.at,
      checks: { battery: true, pct96: this.now.pct != null && this.now.pct >= 96, unplugged: !this.now.ac, chargeLimitHint: false },
      armed: true, countdownEndsMs: null, app: { version: '15.0.0', release: 1, updatedFrom: null, updateNote: null },
      unit: { maker: this.unit.manufacturer, model: this.unit.model, serial: this.unit.serial, computerName: 'LAPTOP-1234' },
      batteries: this.batteries.map(b => ({ fullEndMwh: null, ...b })), now: { ...this.now },
      session: ready ? null : { id: S.id, status: S.status, startedMs: S.startedMs,
        alarmPct: this.alarmPct, alarmPctMax: 50, alarm: S.alarm, alarmAck: S.alarmAck, segments: segs },
      samples: !ready ? this.samples.filter(x => x.rev > since).map(x => ({ rev: x.rev, ms: x.ms, elapsedS: x.e, pct: x.pct, mwh: x.mwh, fullMwh: 47378,
        rateMw: x.rateMw, rateSrc: 'wmi', voltageMv: 12000, ac: x.ac, saver: false })) : [],
      events: !ready ? this.events.filter(x => x.rev > since && x.type !== 'waiting').map(e => {
        const g = e.type === 'gap' ? S.gaps.find(q => q.toMs === e.ms) : null;
        return { rev: e.rev, ms: e.ms, elapsedS: e.e, type: TYPE[e.type] || e.type, pct: e.pct, mwh: e.mwh, rateMw: e.rateMw,
          note: g ? g.reason : '', fromMs: g ? g.fromMs : null, toMs: g ? g.toMs : null }; }) : [],
      result: S && S.status === 'done' ? { kind: r.kind, reason: r.text, startEnd: `${r.startPct}% → ${r.endPct}%`, alarmAck: !!S.alarmAck,
        runtimeS: r.runtimeS, startPct: r.startPct, endPct: r.endPct, estFullS: null, estFullEnergyS: null, avgW: 9.5, minW: 8, maxW: 11,
        healthPct: 91.9, fullChangedMwh: null, gapCount: S.gaps.length, gapS: S.gaps.reduce((a, q) => a + (q.toMs - q.fromMs) / 1000, 0),
        pauseCount: S.pauses.length, pauseS: 0, saver: 'off', brightnessStart: 60, brightnessEnd: 60, plan: 'Balanced', windows: 'Windows 11',
        files: { html: r.reportPath, csv: r.csvPath } } : null };
  }
  post(p, body) {
    if (p === '/api/start') {
      if (this.S && this.S.status !== 'done') return { ok: false, error: 'A test is already running.' };
      if (!(this.now.pct >= 96)) return { ok: false, error: 'Charge to at least 96% first.' };
      this.samples = []; this.events = []; this.result = null;
      this.S = { id: 's' + this.at, status: 'waiting', startedMs: this.at, unplugMs: null, startPct: null, segs: [], gaps: [], pauses: [], alarm: false, alarmAck: false };
      this.sample('start'); this.ev('start');
      if (this.now.ac) this.ev('waiting'); else { this.S.status = 'running'; this.S.unplugMs = this.at; this.S.startPct = this.now.pct; this.open(); }
      return { ok: true };
    }
    if (p === '/api/stop') { if (!this.S || this.S.status === 'done') return { ok: false, error: 'No test is running.' }; this.end('stop'); return { ok: true }; }
    if (p === '/api/ack-alarm') { if (this.S) this.S.alarmAck = true; if (this.result) this.result.alarmAck = true; return { ok: true }; }
    if (p === '/api/alarm-pct') { if (!(body.pct >= 10 && body.pct <= 50)) return { ok: false, error: 'Alarm must be 10 to 50%.' }; this.alarmPct = body.pct; return { ok: true }; }
    if (p === '/api/save-csv') return this.S ? { ok: true, file: 'C:\\Users\\tech\\Desktop\\x.csv',path: 'C:\\Users\\tech\\Desktop\\x.csv' } : { ok: false, error: 'No test yet.' };
    if (p === '/api/heartbeat') return { ok: true };
    if (p === '/api/new') { if (!this.S || this.S.status !== 'done') return { ok: false, error: 'No finished test is showing.' }; this.S = null; this.samples = []; this.events = []; this.result = null; return { ok: true }; }
    if (p === '/api/arm' || p === '/api/cancel') return { ok: true };
    return null;
  }
  routes() {
    const send = (route, status, body, type = 'application/json') => route.fulfill({ status, contentType: type, body: typeof body === 'string' ? body : JSON.stringify(body) });
    return [[`${ORIGIN}/**`, async (route) => {
      this.inflight++;
      try { return await this.handle(route, send); } finally { this.inflight--; }
    }]];
  }
  async handle(route, send) {
    const req = route.request(), u = new URL(req.url()), m = req.method();
    this.calls.push({ m, p: u.pathname, token: req.headers()['x-laptap-token'] || null, q: u.search });
    if (this.down) return route.abort('connectionrefused');
    if (u.pathname === '/') return send(route, 200, fs.readFileSync(PAGE, 'utf8'), 'text/html; charset=utf-8');
    if (u.pathname === '/logic.js') return send(route, 200, fs.readFileSync(path.join(WEB, 'logic.js'), 'utf8'), 'text/javascript');
    if (!u.pathname.startsWith('/api/')) return send(route, 404, { ok: false });
    if (req.headers()['x-laptap-token'] !== this.token) return send(route, 403, { ok: false });
    if (m === 'GET' && u.pathname === '/api/state') { const since = +(u.searchParams.get('since') || 0); return send(route, 200, SHAPES === 'impl1' ? this.state(since) : this.stateContract(since)); }
    if (m === 'POST') { const r = this.post(u.pathname, JSON.parse(req.postData() || '{}')); if (r) return send(route, 200, r); }
    return send(route, 404, { ok: false });
  }
}
// Network answers arrive in real time while the page clock is fake: wait until the page's requests are answered.
const realSleep = (ms) => new Promise(r => setTimeout(r, ms));
async function settle(exe) { for (let quiet = 0; quiet < 3;) { await realSleep(25); quiet = exe.inflight ? 0 : quiet + 1; } }
async function waitFor(fn, ms = 3000) { const end = Date.now() + ms; while (Date.now() < end) { if (await fn()) return true; await realSleep(25); } return false; }

async function withExe(exe, opts, fn) {
  const L = await openLogger({ ...opts, url: `${ORIGIN}/#t=${exe.token}`, routes: exe.routes() });
  const click = L.click, run = L.run;
  L.run = async (ms) => { await run(ms); await settle(exe); };
  L.click = async (sel) => { await click(sel); await settle(exe); };
  L.step = async (ms) => { exe.at += ms; await L.run(ms); };
  try { await settle(exe); await L.run(100); await fn(L); } finally { await L.close(); }
}
async function withPage(opts, fn) { const L = await openLogger({ ...opts, file: PAGE }); try { await L.run(100); await fn(L); } finally { await L.close(); } }
const rows = (L) => L.page.locator('#rows').innerText();
const txt = (L, id) => L.page.locator(id).innerText();
const errs = (L) => assert.deepEqual(L.consoleErrors, [], 'page errors');

// ---------------- bug 1: fake 95% ----------------
test('bug 1a: no % shown or loggable while the Battery API has not answered; Start refused', () => withPage({ battery: 'pending', level: 0.42 }, async (L) => {
  assert.doesNotMatch(await txt(L, '#bigpct'), /\d/, 'a % with no reading');
  await L.run(5000);
  assert.ok(!(await L.page.getAttribute('#cP', 'class') || '').includes('ok'), '96% check not met without a reading');
  assert.ok(!(await L.page.isVisible('#sRun')), 'no test started');
  assert.doesNotMatch(await rows(L), /\d+/);
  errs(L);
}));
test('bug 1b: no % with no Battery API at all (browser-only)', () => withPage({ battery: 'absent' }, async (L) => {
  await L.run(5000);
  assert.doesNotMatch(await txt(L, '#bigpct'), /\d/, 'a % with no reading');
  assert.doesNotMatch(await rows(L), /%|\d/);
}));
test('bug 1c: logger mode shows "—" when Windows gives no % (never a default)', () => withExe(new FakeExe({ pct: null }), {}, async (L) => {
  assert.doesNotMatch(await txt(L, '#bigpct'), /\d/, 'a % with no reading');
}));

// ---------------- bug 2 / 7: gaps ----------------
test('bug 2: a 30-min sleep is a Gap row and is not counted (browser-only)', () => withPage({ level: 1, charging: false }, async (L) => {
  await L.run(4500);
  await L.run(2 * MIN); await L.sleep(30 * MIN); await L.run(1 * MIN);
  assert.match(await rows(L), /Gap 0:3\d · unknown/);
  const [h, m] = (await txt(L, '#elapsed')).split(':').map(Number);
  assert.ok(h * 60 + m < 30, `elapsed ${await txt(L, '#elapsed')}`);
  errs(L);
}));
test('bug 2 (logger): the exe gap event shows "Gap 0:30 · laptop slept" and Elapsed skips it', () => {
  const exe = new FakeExe();
  return withExe(exe, {}, async (L) => {
    exe.post('/api/start'); exe.unplug(); await L.step(2 * MIN); exe.drain(98);
    exe.gap(30); exe.drain(95); await L.step(6000);
    assert.match(await rows(L), /Gap 0:30 · laptop slept/);
    const [h, m] = (await txt(L, '#elapsed')).split(':').map(Number);
    assert.ok(h * 60 + m < 30, `elapsed ${await txt(L, '#elapsed')}`);
  });
});
test('bug 7: a 5-min freeze of the page leaves a Gap row (browser-only)', () => withPage({ level: 1 }, async (L) => {
  await L.run(4500); await L.run(1 * MIN);
  await L.page.evaluate(() => document.dispatchEvent(new Event('freeze')));
  await L.sleep(5 * MIN);
  await L.page.evaluate(() => document.dispatchEvent(new Event('resume')));
  await L.run(1000);
  assert.match(await rows(L), /Gap/);
}));

// ---------------- bug 3: confirm, no wipe ----------------
test('bug 3: Stop asks in-page first; "Keep running" keeps the test; no Clear/Restart/Pause', () => { const exe = new FakeExe(); return withExe(exe, {}, async (L) => {
  exe.post('/api/start'); exe.unplug();
  await L.step(5000);
  await L.click('#stopBtn');
  assert.ok(await L.page.isVisible('#stopConfirm'));
  assert.match(await txt(L, '#stopConfirm'), /End the test now\?/);
  await L.click('#stopNo');
  await L.step(5000);
  assert.equal(L.dialogs.length, 0, 'no browser dialog');
  assert.ok(await L.page.isEnabled('#stopBtn'), 'test still running');
  const names = (await L.page.locator('button').allInnerTexts()).join('|');
  assert.doesNotMatch(names, /Clear|Restart|Pause/);
}); });

// ---------------- bug 4: files ----------------
test('bug 4 (logger): Done names the Desktop files; Open report asks the exe; Save CSV only after a failed save', () => {
  const exe = new FakeExe();
  return withExe(exe, {}, async (L) => {
    exe.post('/api/start'); exe.unplug(); await L.step(5000);
    await L.click('#stopBtn'); await L.click('#stopYes'); await L.run(200);
    assert.equal(await txt(L, '#savedT'), 'Report saved to the Desktop');
    assert.match(await txt(L, '#savedF'), /\.html · \.csv$/);
    assert.ok(!(await L.page.isVisible('#csvBtn')), 'no Save CSV when the report and CSV were saved');
    await L.click('#openBtn'); await L.run(50);
    assert.ok(exe.calls.some(c => c.m === 'POST' && c.p === '/api/open-report'));
  });
});
test('bug 4 (browser-only): Stop downloads the report + CSV built from the embedded template', () => withPage({ level: 1 }, async (L) => {
  await L.run(4500); await L.run(15 * MIN);
  await L.click('#stopBtn'); await L.click('#stopYes'); await L.run(500);
  await waitFor(() => L.downloads.length >= 2 && L.downloads.every(d => d.text != null));
  const names = L.downloads.map(d => d.name).join('|');
  assert.match(names, /Battery UNKNOWN 2026-11-26 09\d\d\.html/);
  assert.match(names, /Battery UNKNOWN 2026-11-26 09\d\d\.csv/);
  const html = L.downloads.find(d => d.name.endsWith('.html')).text;
  assert.doesNotMatch(html, /\{\{|<script/i, 'slot left or script in report');
  assert.match(html, /Stopped early/);
}));

// ---------------- bug 5: reload ----------------
test('bug 5 (logger): a reload reconnects to the running test with its rows', () => {
  const exe = new FakeExe();
  return withExe(exe, {}, async (L) => {
    exe.post('/api/start'); exe.unplug(); await L.step(6000);
    const before = await rows(L);
    await L.page.goto(`${ORIGIN}/#t=${exe.token}`); await L.run(200);
    assert.equal(await rows(L), before);
    assert.ok(await L.page.isEnabled('#stopBtn'));
  });
});
test('bug 5 (browser-only): a reload restores the running test from IndexedDB', () => withPage({ level: 1 }, async (L) => {
  await L.run(4500); await L.run(2 * MIN);
  const before = (await rows(L)).split('\n').length;
  await L.reload(); await L.run(500);
  assert.ok(await L.page.isEnabled('#stopBtn'), 'still running after reload');
  assert.ok((await rows(L)).split('\n').length >= before, 'rows restored');
}));

// ---------------- bug 6: alarm audio ----------------
test('bug 6a: one AudioContext, made when the alarm shows (the exe starts Edge with autoplay allowed)', () => { const exe = new FakeExe(); return withExe(exe, {}, async (L) => {
  exe.post('/api/start'); exe.unplug(); await L.step(5000);
  assert.equal((await L.probe()).audio.contexts, 0, 'nothing made before the alarm');
  exe.drain(15); exe.end('alarm'); await L.step(6000);
  assert.equal((await L.probe()).audio.contexts, 1);
}); });
test('bug 6b: alarm rings until OK, with no extra AudioContexts, then acks to the exe', () => {
  const exe = new FakeExe();
  return withExe(exe, {}, async (L) => {
    exe.post('/api/start'); exe.unplug(); exe.drain(15); exe.end('alarm'); await L.step(6000);
    assert.ok(await L.page.isVisible('#alarm'));
    assert.equal(await txt(L, '#alarmT'), 'Plug in the charger');
    assert.match(await txt(L, '#alarmS'), /^The test is done and the report is saved to the Desktop\. Beeping until you click OK\.$/);
    await L.run(12_000);
    let { audio } = await L.probe();
    assert.equal(audio.contexts, 1); assert.ok(audio.starts >= 6, `beeps ${audio.starts}`);
    await L.click('#aOk'); await L.run(100);
    assert.ok(!(await L.page.isVisible('#alarm')));
    assert.ok(exe.calls.some(c => c.p === '/api/ack-alarm'));
    const n = (await L.probe()).audio.starts; await L.run(10_000);
    assert.equal((await L.probe()).audio.starts, n, 'beeping stopped after OK');
  });
});

// ---------------- bug 8: plug + changes ----------------
test('bug 8a: charger in mid-test pauses at once, unplug resumes (browser-only)', () => withPage({ level: 1 }, async (L) => {
  await L.run(4500); await L.run(30_000);
  await L.bat.set({ level: 0.97 }); await L.bat.set({ charging: true }); await L.run(5000);
  assert.match(await rows(L), /Charger plugged in · paused/);
  assert.equal(await txt(L, '#bigWord'), 'PAUSED');
  assert.equal(await txt(L, '#bandsub'), 'Charger plugged in. Unplug to continue.');
  assert.equal(await txt(L, '#eta'), 'Charging, timer stopped');
  const el1 = await txt(L, '#elapsed'); await L.run(5 * MIN);
  await L.bat.set({ charging: false }); await L.run(5000);
  assert.match(await rows(L), /Unplugged · resumed/);
  assert.equal((await txt(L, '#elapsed')).slice(0, 4), el1.slice(0, 4), 'charging time not counted');
}));
test('bug 8b (logger): a % change shows within one 5 s poll', () => {
  const exe = new FakeExe();
  return withExe(exe, {}, async (L) => {
    exe.now.pct = 79; await L.step(5000);
    assert.equal(await txt(L, '#bigpct'), '79%');
  });
});

// ---------------- bug 9: end + summary ----------------
test('bug 9: Stop ends the test and shows the Result card with the Desktop paths', () => {
  const exe = new FakeExe();
  return withExe(exe, {}, async (L) => {
    exe.post('/api/start'); exe.unplug(); await L.step(10 * MIN); exe.drain(90);
    await L.click('#stopBtn'); await L.click('#stopYes'); await L.run(200);
    assert.match(await txt(L, '#range'), /^100% → 90% · Stopped early$/);
    assert.equal(await txt(L, '#savedT'), 'Report saved to the Desktop');
    assert.match(await txt(L, '#runtime'), /^(\d+ h )?\d+ min$/);
    assert.ok(await L.page.isVisible('#newBtn'), 'New test shown');
  });
});

// ---------------- bug 10: no joined text ----------------
test('bug 10: rows are separate table rows; New test starts a clean table', () => {
  const exe = new FakeExe();
  return withExe(exe, {}, async (L) => {
    exe.post('/api/start'); exe.unplug(); await L.step(6000);
    assert.ok(await L.page.locator('#rows tr').count() >= 2);   // v15 (M1): an event and its own reading are one row
    await L.click('#stopBtn'); await L.click('#stopYes'); await L.run(200);
    await L.click('#newBtn');
    assert.equal(await L.page.locator('#rows tr').count(), 0);
    assert.ok(!(await L.page.isVisible('#sDone')), 'the Done screen is gone');
  });
});

// ---------------- bug 11: pause removed, fresh readings ----------------
test('bug 11: no Pause; each event row carries the reading at that moment', () => {
  const exe = new FakeExe();
  return withExe(exe, {}, async (L) => {
    exe.post('/api/start'); exe.unplug(); await L.step(5000);
    exe.drain(70); exe.plug(); await L.step(5000);
    const row = await L.page.locator('#rows tr', { hasText: 'Charger plugged in' }).innerText();
    assert.match(row, /\t70%\t/);
    assert.equal(await L.page.locator('button', { hasText: /^Pause/ }).count(), 0);
  });
});

// ---------------- bug 12: failures shown ----------------
test('bug 12 (browser-only): a getBattery() failure is shown, not "Checking…"', () => withPage({ battery: 'reject' }, async (L) => {
  await L.run(2000);
  assert.match(await txt(L, '#helper'), /Browser only · no health/);
  assert.equal(await txt(L, '#step'), 'No battery found', 'the screen says why it cannot start');
  assert.doesNotMatch(await L.page.locator('body').innerText(), /Checking/);
}));
test('bug 12 (logger): 3 failed polls → "Logger stopped", buttons off, never browser-only', () => {
  const exe = new FakeExe();
  return withExe(exe, {}, async (L) => {
    exe.down = true; for (let i = 0; i < 4; i++) await L.step(5000);
    assert.match(await txt(L, '#helper'), /Logger stopped\. Open Laptap Battery Logger again\./);
    for (const b of ['#startBtn', '#cancelBtn', '#stopBtn', '#csvBtn', '#newBtn', '#openBtn']) assert.ok(await L.page.isDisabled(b), b);
    const calls = exe.calls.length; await L.step(MIN);
    assert.equal(exe.calls.length, calls, 'stops polling once lost');
  });
});

// ---------------- bug 13 ----------------
test('bug 13: 100% on AC is "Plugged in, full" (logger and browser-only)', async () => {
  await withExe(new FakeExe({ pct: 100, ac: true }), {}, async (L) => assert.equal(await txt(L, '#wattsSub'), 'Plugged in, full'));
  await withPage({ level: 1, charging: true }, async (L) => assert.equal(await txt(L, '#wattsSub'), 'Plugged in, full'));
});

// ---------------- bug 14 ----------------
test('bug 14: two batteries → health = ΣFull ÷ ΣDesign, both counted', () => withExe(new FakeExe({ batteries: [
  { id: '1', maker: 'A', chemistry: 'LIon', designMwh: 23000, fullMwh: 20000, fullEndMwh: null, cycles: 120 },
  { id: '2', maker: 'B', chemistry: 'LIon', designMwh: 24000, fullMwh: 22000, fullEndMwh: null, cycles: 80 }] }), {}, async (L) => {
  assert.equal(await txt(L, '#health'), '89.4%'); // 42000 / 47000
  assert.equal(await txt(L, '#healthSub'), '42.0 of 47.0 Wh');
}));

// ---------------- bug 15 ----------------
test('bug 15: CSV times are ISO 8601 +08:00 whatever the locale; table is 24 h', async () => {
  const firstTime = {};
  for (const locale of ['en-US', 'de-DE']) await withPage({ locale, level: 1 }, async (L) => {
    await L.run(4500); await L.run(2 * MIN);
    await L.click('#stopBtn'); await L.click('#stopYes'); await L.run(300);   // v15: Save CSV lives in the Done band
    await waitFor(() => L.downloads.some(d => d.name.endsWith('.csv') && d.text != null));
    const csv = L.downloads.find(d => d.name.endsWith('.csv')).text;
    firstTime[locale] = csv.split('\r\n')[1].split(',')[0];
    assert.match(await rows(L), /^09:0\d:\d\d/m);
  });
  assert.equal(firstTime['en-US'], firstTime['de-DE']);
  assert.match(firstTime['en-US'], /^2026-11-26T09:00:\d\d\+08:00$/);
});

// ---------------- bug 16: low power ----------------
test('bug 16a (logger): hidden 5 min → ≤ 10 timer wakeups (30 s poll, heartbeat rides along)', () => withExe(new FakeExe(), {}, async (L) => {
  await L.run(10_000); await L.hide(); await L.resetProbe();
  await L.step(5 * MIN);
  const p = await L.probe();
  assert.ok(p.timerFires <= 10, `${p.timerFires} wakeups ${JSON.stringify(p.timerFiresByMs)}`);
  assert.equal(p.mutations, 0, 'nothing changed, nothing written to the DOM');
}));
test('bug 16a (logger): Logging, visible → 5 s polls + the 1 s elapsed tick only (E7)', () => { const exe = new FakeExe(); return withExe(exe, {}, async (L) => {
  exe.post('/api/start'); exe.unplug(); await L.step(5000);
  await L.resetProbe(); await L.step(MIN);
  const p = await L.probe();
  assert.ok(p.timerFires <= 75, `${p.timerFires} wakeups ${JSON.stringify(p.timerFiresByMs)}`);
  assert.ok((p.timerFiresByMs['5000'] || 0) <= 13, 'polls every 5 s');
}); });
test('bug 16a (logger): Logging, hidden 5 min → no 1 s tick, 30 s polls only', () => { const exe = new FakeExe(); return withExe(exe, {}, async (L) => {
  exe.post('/api/start'); exe.unplug(); await L.step(5000);
  await L.hide(); await L.resetProbe(); await L.step(5 * MIN);
  const p = await L.probe();
  assert.ok(p.timerFires <= 12, `${p.timerFires} wakeups ${JSON.stringify(p.timerFiresByMs)}`);
  assert.ok(!p.timerFiresByMs['1000'], 'no 1 s tick while hidden');
}); });
test('bug 16b: storage does not grow with the log (no localStorage; v13 key untouched)', () => withPage({ level: 1, storage: { BatteryTestResultW7: 'old v13 log' } }, async (L) => {
  await L.run(4500); await L.run(120 * MIN);
  await L.resetProbe(); await L.run(MIN);
  const p = await L.probe();
  assert.equal(p.storageWrites, 0);
  assert.equal((await L.storage()).BatteryTestResultW7, 'old v13 log');
  assert.equal(p.storageRemoves, 0);
}));

// ---------------- bug 17 ----------------
test('bug 17a: alarm % is a setting, 10–50, sent to the exe', () => {
  const exe = new FakeExe();
  return withExe(exe, {}, async (L) => {
    exe.post('/api/start'); exe.unplug(); await L.step(5000);
    const el = L.page.locator('#alarmPct');
    assert.equal(await el.getAttribute('min'), '10'); assert.equal(await el.getAttribute('max'), '50');
    await el.fill('20'); await el.press('Tab'); await L.run(100);
    assert.ok(exe.calls.some(c => c.p === '/api/alarm-pct'));
    await el.fill('5'); await el.press('Tab'); await L.run(100);
    const v = +(await el.inputValue());
    assert.ok(v >= 10 && v <= 50, `alarm box shows ${v}`);
  });
});
test('bug 17b: the alarm is a soft sine (gain 0.2), not a full-volume sawtooth', () => {
  const exe = new FakeExe();
  return withExe(exe, {}, async (L) => {
    exe.post('/api/start'); exe.unplug(); exe.drain(15); exe.end('alarm'); await L.step(6000);
    const { audio } = await L.probe();
    assert.ok(audio.oscTypes.length && audio.oscTypes.every(t => t === 'sine'), `${audio.oscTypes}`);
    assert.ok(audio.maxGain <= 0.2 + 1e-9 && audio.maxGain > 0, `gain ${audio.maxGain}`);
  });
});

// ---------------- contract checks ----------------
test('token: read from #t=, removed from the URL, sent as X-Laptap-Token on every /api call', () => {
  const exe = new FakeExe();
  return withExe(exe, {}, async (L) => {
    exe.post('/api/start'); exe.unplug(); await L.step(6000);
    assert.doesNotMatch(L.page.url(), /#t=|a1b2c3/);
    const apiCalls = exe.calls.filter(c => c.p.startsWith('/api/'));
    assert.ok(apiCalls.length >= 3);
    assert.ok(apiCalls.every(c => c.token === exe.token));
    assert.ok(exe.calls.some(c => c.m === 'POST' && c.p === '/api/heartbeat'));
    assert.ok(exe.calls.some(c => c.p === '/api/state' && /since=\d+/.test(c.q)));
  });
});
test('below 96%: "Plug in and charge to 96%" with the big 95% and nothing starts', () => {
  const exe = new FakeExe({ pct: 95, ac: false });
  return withExe(exe, {}, async (L) => {
    await L.step(5000);
    assert.equal(await txt(L, '#step'), 'Plug in and charge to 96%'); assert.equal(await txt(L, '#bigpct'), '95%');
    assert.ok(!(await L.page.isVisible('#cdOv')) && !(await L.page.isVisible('#sRun')));
    assert.ok(!exe.calls.some(c => c.p === '/api/start'), 'the page never asks to start');
    assert.equal(exe.S, null);
  });
});
test('model name with <script> is escaped on the page', () => withExe(new FakeExe({ model: '<img src=x onerror=alert(1)>' }), {}, async (L) => {
  assert.equal(await L.page.locator('#unit img').count(), 0);
  assert.match(await txt(L, '#unit'), /<img src=x/);
}));
test('plugged in → "Unplug the charger to start"; unplugged → LOGGING "Measuring…", then "h min" to alarm after 5+ samples', () => {
  const exe = new FakeExe();
  return withExe(exe, {}, async (L) => {
    exe.post('/api/start'); await L.step(5000);
    assert.ok(await L.page.isVisible('#sPre'), 'still Ready while plugged in');
    assert.equal(await txt(L, '#step'), 'Unplug the charger to start');
    exe.unplug(); await L.step(5000);
    assert.equal(await txt(L, '#eta'), 'Measuring…');
    assert.equal(await txt(L, '#bigWord'), 'LOGGING');
    for (let i = 1; i <= 6; i++) { exe.at += MIN; exe.drain(100 - i); }
    await L.step(5000);
    assert.match(await txt(L, '#eta'), /^(\d+ h )?\d+ min$/);
    assert.equal(await txt(L, '#etaSub'), 'estimate, gets better over time');
    errs(L);
  });
});

// ---------------- contract render check: a finished test leaves no blank, "undefined", "NaN" or "null" ----------------
test('contract: a finished test renders every stat and Result field (no blanks)', () => {
  const exe = new FakeExe({ batteries: [
    { id: '1', maker: 'SMP', chemistry: 'LIon', designMwh: 23000, fullMwh: 20000, cycles: 120 },
    { id: '2', maker: 'LGC', chemistry: 'LIon', designMwh: 24000, fullMwh: 26000, cycles: null }] });
  return withExe(exe, {}, async (L) => {
    exe.post('/api/start'); exe.unplug(); await L.step(5000);
    for (let i = 1; i <= 6; i++) { exe.at += 10 * MIN; exe.drain(98 - i * 3); }
    exe.gap(20); exe.drain(70); exe.plug(); exe.at += 5 * MIN; exe.unplug(); exe.drain(15); exe.end('alarm');
    await L.step(6000);
    await L.click('#aOk'); await L.run(200);
    const bad = /undefined|NaN|null|\[object/;
    const body = await L.page.locator('body').innerText();
    assert.doesNotMatch(body, bad, 'a raw value leaked onto the page');
    for (const id of ['#pct', '#health', '#watts', '#pctSub', '#healthSub', '#wattsSub', '#unit', '#runtime', '#range', '#savedT', '#savedF', '#facts'])
      assert.ok((await txt(L, id)).trim().length > 0, `${id} is blank`);
    assert.equal(await txt(L, '#savedT'), 'Report saved to the Desktop');
    assert.match(await txt(L, '#range'), /% → 15% · alarm reached at \d\d:\d\d$/);
    assert.match(await txt(L, '#health'), /^(\d+\.\d%|100\.0%)$/);
    const ev = await L.page.locator('#rows tr td:nth-child(2)').allInnerTexts();
    ev.forEach((t, i) => assert.ok(t.trim().length > 0, `table row ${i} has no event label`));
    assert.ok(ev.some(t => /^Gap 0:20 · laptop slept$/.test(t)), ev.join(' | '));
    errs(L);
  });
});
test('contract: health shows min(100, raw) with one decimal', () => withExe(new FakeExe({ batteries: [
  { id: '1', maker: 'A', chemistry: 'LIon', designMwh: 50000, fullMwh: 52000, cycles: 3 }] }), {}, async (L) => {
  assert.equal(await txt(L, '#health'), '100.0%');
}));
