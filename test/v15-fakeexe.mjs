// test/v15-fakeexe.mjs
// A mocked Laptap Battery Logger.exe that speaks the v15 contract (plan §3 + §4, on top of docs/api-contract.md).
// It owns the start flow like Session.cs: checks, armed, the 3 s countdown (only while a page polled in the last 3 s),
// the unplug latch (E6), Logging / Paused / Done. Tests drive its clock (`at`) in step with the page's fake clock.
// Shared by test/v15-*.test.mjs. Nothing here is copied from the exe; it follows the plan's words.
import fs from 'node:fs';
import path from 'node:path';
import { openLogger, ROOT } from './harness.mjs';

export const WEB = path.join(ROOT, 'src', 'web');
export const PAGE = path.join(WEB, 'index.html');
export const ORIGIN = 'http://localhost:47600';
export const MIN = 60_000;
export const T0 = Date.parse('2026-11-26T09:00:00+08:00');
const realSleep = (ms) => new Promise(r => setTimeout(r, ms));

export class FakeExe {
  constructor(o = {}) {
    this.token = 'a1b2c3d4e5f60718293a4b5c6d7e8f90';
    this.at = T0; this.rev = 0; this.calls = []; this.inflight = 0; this.down = false;
    this.unit = { maker: 'HP', model: 'HP EliteBook 840 G5', serial: '5CB4190FVX', computerName: 'LAPTOP-1234' };
    this.batteries = [{ id: '1', maker: 'SMP', chemistry: 'LIon', designMwh: 51528, fullMwh: 47378, fullEndMwh: null, cycles: null }];
    this.pct = o.pct ?? 100; this.ac = o.ac ?? true; this.rateMw = null;
    this.app = { version: '15.0.0', release: 1, updatedFrom: null, updateNote: null, ...(o.app || {}) };
    this.armed = true; this.countdownEndsMs = null; this.lastPollMs = null; this.latch = null; this.cancelledSinceUnplug = false;
    this.alarmPct = 15; this.S = null; this.samples = []; this.events = []; this.result = null;
    this.refuse = null; // set to a text to make the next POST answer {ok:false, error}
  }
  mwh() { return Math.round(473.78 * this.pct); }
  checks() {
    const pct96 = this.ac ? this.pct >= 96 : !!(this.latch && this.latch.pct >= 96);
    return { battery: true, pct96, unplugged: !this.ac };
  }
  allOk() { const c = this.checks(); return c.battery && c.pct96 && c.unplugged; }
  polledRecently() { return this.lastPollMs != null && this.at - this.lastPollMs <= 3000; }
  elapsed(ms = this.at) { if (!this.S) return 0; let t = 0; for (const g of this.S.segments) t += ((g.endMs ?? ms) - g.startMs) / 1000; return Math.round(t); }
  seg(open, at = this.at, pct = this.pct, mwh = this.mwh()) {
    const s = this.S.segments;
    if (open) s.push({ startMs: at, endMs: null, startPct: pct, endPct: null, startMwh: mwh, endMwh: null });
    else { const g = s[s.length - 1]; if (g && g.endMs == null) Object.assign(g, { endMs: at, endPct: pct, endMwh: mwh }); }
  }
  sample(ms = this.at) {
    this.samples.push({ rev: ++this.rev, ms, elapsedS: this.elapsed(ms), pct: this.pct, mwh: this.mwh(), fullMwh: 47378,
      rateMw: this.ac ? null : this.rateMw, rateSrc: 'wmi', voltageMv: 12000, ac: this.ac, saver: false });
  }
  ev(type, x = {}) {
    this.events.push({ rev: ++this.rev, ms: this.at, elapsedS: this.elapsed(), type, pct: this.pct, mwh: this.mwh(),
      rateMw: this.ac ? null : this.rateMw, note: '', fromMs: null, toMs: null, ...x });
  }
  // The exe's 1 s tick (Session.cs). Called on every state poll and by the tests.
  tick() {
    if (this.S) return;            // Logging / Paused / Done: no countdown
    if (this.countdownEndsMs != null) {
      if (!(this.armed && this.allOk() && this.polledRecently())) { this.countdownEndsMs = null; return; }
      if (this.at >= this.countdownEndsMs) this.startTest();
      return;
    }
    if (this.armed && this.allOk() && this.polledRecently()) this.countdownEndsMs = this.at + 3000;
  }
  startTest() {
    this.countdownEndsMs = null;
    this.S = { id: 's' + this.at, status: 'running', startedMs: this.at, alarmPct: this.alarmPct, alarmPctMax: 50, alarm: false, alarmAck: false, segments: [] };
    this.samples = []; this.events = []; this.result = null;
    const L = this.latch && !this.cancelledSinceUnplug ? this.latch : null; // E6: straight after the unplug → start at the latch
    if (L) this.seg(true, L.ms, L.pct, L.mwh); else this.seg(true);
    this.ev('start'); this.sample();
  }
  unplug() {
    this.ac = false; this.rateMw = 9500;
    if (!this.S) { this.latch = { ms: this.at, pct: this.pct, mwh: this.mwh() }; this.cancelledSinceUnplug = false; return; }
    if (this.S.status === 'paused') { this.S.status = 'running'; this.seg(true); this.ev('charger_out'); this.sample(); }
  }
  plug() {
    this.ac = true; this.rateMw = null; this.latch = null;
    if (this.S && this.S.status === 'running') { this.seg(false); this.S.status = 'paused'; this.ev('charger_in'); this.sample(); } // M1: same ms
  }
  setPct(p) { this.pct = p; }
  drain(p) { this.pct = p; this.sample(); }
  gap(mins, note = 'slept') { const from = this.at; this.seg(false); this.at += mins * MIN; this.ev('gap', { fromMs: from, toMs: this.at, note }); this.seg(true); }
  end(kind) {
    this.seg(false); this.ev(kind === 'alarm' ? 'alarm' : 'stop');
    this.S.status = 'done'; this.S.alarm = kind === 'alarm';
    const g0 = this.S.segments[0], run = this.elapsed();
    this.result = { kind, reason: kind === 'alarm' ? 'Ended at alarm' : 'Stopped early', startEnd: `${g0.startPct}% → ${this.pct}%`,
      alarmAck: kind !== 'alarm', runtimeS: run, startPct: g0.startPct, endPct: this.pct, estFullS: null, estFullEnergyS: null,
      avgW: 9.5, minW: 8.1, maxW: 11.2, healthPct: 91.9, fullChangedMwh: null, gapCount: 0, gapS: 0, pauseCount: 0, pauseS: 0,
      saver: 'off', brightnessStart: 60, brightnessEnd: 60, plan: 'Balanced', windows: 'Windows 11 Pro',
      files: { html: 'C:\\Users\\tech\\Desktop\\Battery 5CB4190FVX 2026-11-26 0900.html', csv: 'C:\\Users\\tech\\Desktop\\Battery 5CB4190FVX 2026-11-26 0900.csv' } };
    this.ev('report_saved');
  }
  state(since) {
    const S = this.S;
    return { rev: this.rev, version: this.app.version, nowMs: this.at, unit: this.unit, batteries: this.batteries,
      now: { pct: this.pct, mwh: this.mwh(), fullMwh: 47378, rateMw: this.ac ? null : this.rateMw, ac: this.ac, charging: this.ac && this.pct < 100, saver: false },
      checks: this.checks(), armed: this.armed, countdownEndsMs: this.countdownEndsMs, app: { ...this.app },
      session: S && { id: S.id, status: S.status, startedMs: S.startedMs, alarmPct: S.alarmPct, alarmPctMax: 50, alarm: S.alarm, alarmAck: S.alarmAck,
        segments: S.segments.map(g => ({ ...g })) },
      samples: this.samples.filter(x => x.rev > since), events: this.events.filter(x => x.rev > since), result: S && S.status === 'done' ? this.result : null };
  }
  post(p, body) {
    if (this.refuse) { const e = this.refuse; this.refuse = null; return { ok: false, error: e }; }
    const st = !this.S ? (this.countdownEndsMs != null ? 'countdown' : 'ready') : this.S.status;
    switch (p) {
      case '/api/heartbeat': return { ok: true };
      case '/api/cancel': if (st !== 'countdown') return { ok: false, error: 'Nothing to cancel.' };
        this.countdownEndsMs = null; this.armed = false; this.cancelledSinceUnplug = true; return { ok: true };
      case '/api/arm': if (st !== 'ready') return { ok: false, error: 'A test is already running.' };
        this.armed = true; this.tick(); return { ok: true };
      case '/api/new': if (st !== 'done') return { ok: false, error: 'A test is already running.' };
        this.S = null; this.result = null; this.armed = true; this.latch = null; return { ok: true };
      case '/api/stop': if (st !== 'running' && st !== 'paused') return { ok: false, error: 'No test is running.' }; this.end('stop'); return { ok: true };
      case '/api/ack-alarm': if (this.S) this.S.alarmAck = true; if (this.result) this.result.alarmAck = true; return { ok: true };
      case '/api/alarm-pct': if (!(body.pct >= 10 && body.pct <= 50)) return { ok: false, error: 'Alarm must be 10 to 50%.' };
        this.alarmPct = body.pct; if (this.S) this.S.alarmPct = body.pct; return { ok: true };
      case '/api/save-csv': return this.S ? { ok: true, file: 'C:\\Users\\tech\\Desktop\\x.csv' } : { ok: false, error: 'No test yet.' };
      case '/api/start': return { ok: false, error: 'Not available.' }; // test-only in the real exe (TESTHOOKS)
    }
    return null;
  }
  routes() {
    const send = (route, status, body, type = 'application/json') => route.fulfill({ status, contentType: type, body: typeof body === 'string' ? body : JSON.stringify(body) });
    return [[`${ORIGIN}/**`, async (route) => {
      this.inflight++;
      try {
        const req = route.request(), u = new URL(req.url()), m = req.method();
        this.calls.push({ m, p: u.pathname, at: this.at, token: req.headers()['x-laptap-token'] || null });
        if (this.down) return await route.abort('connectionrefused');
        if (u.pathname === '/') return await send(route, 200, fs.readFileSync(PAGE, 'utf8'), 'text/html; charset=utf-8');
        if (u.pathname === '/logic.js') return await send(route, 200, fs.readFileSync(path.join(WEB, 'logic.js'), 'utf8'), 'text/javascript');
        if (!u.pathname.startsWith('/api/')) return await send(route, 404, '', 'text/plain');
        if (req.headers()['x-laptap-token'] !== this.token) return await send(route, 403, '', 'text/plain');
        if (m === 'GET' && u.pathname === '/api/state') { this.lastPollMs = this.at; this.tick(); return await send(route, 200, this.state(+(u.searchParams.get('since') || 0))); }
        if (m === 'POST') { const r = this.post(u.pathname, JSON.parse(req.postData() || '{}')); if (r) return await send(route, 200, r); }
        return await send(route, 404, '', 'text/plain');
      } finally { this.inflight--; }
    }]];
  }
  count(p, m) { return this.calls.filter(c => c.p === p && (!m || c.m === m)).length; }
}

// Network answers arrive in real time while the page clock is fake: wait until the page's requests are answered.
export async function settle(exe) { for (let quiet = 0; quiet < 3;) { await realSleep(25); quiet = exe.inflight ? 0 : quiet + 1; } }

// Open the page against a FakeExe. L.step(ms) moves both clocks in 1 s steps (the exe ticks each second).
export async function withExe(exe, opts, fn) {
  const L = await openLogger({ viewport: opts.viewport, ...opts, url: `${ORIGIN}/#t=${exe.token}`, routes: exe.routes(), time: new Date(exe.at).toISOString() });
  const click = L.click, run = L.run;
  L.run = async (ms) => { await run(ms); await settle(exe); };
  L.click = async (sel) => { await click(sel); await settle(exe); };
  L.step = async (ms, chunk = 1000) => { for (let t = 0; t < ms; t += chunk) { const d = Math.min(chunk, ms - t); exe.at += d; exe.tick(); await run(d); await settle(exe); } };
  L.text = async (sel) => (await L.page.locator(sel).first().innerText({ timeout: 2000 })).trim();
  try { await settle(exe); await L.run(100); await fn(L); } finally { await L.close(); }
}

// Element ids the v15 tests rely on (the prototype v6 ids). If the page uses others, change them here only.
// v15.1 ids: "band" now reads the whole window (stage + overlay: the countdown is a popup since v15.1).
export const SEL = {
  band: '#app', checks: '.conds', cBat: '#cB', cPct: '#cP', cAc: '#cA', cd: '#cdw', cancel: '#cancelBtn', start: '#startBtn',
  bigWord: '#bigWord', elapsed: '#elapsed', eta: '#eta', stop: '#stopBtn', stopYes: '#stopYes', stopNo: '#stopNo',
  doneSub: '#range', newBtn: '#newBtn', open: '#openBtn', rows: '#rows', rowCount: '#rowCount', jump: '#jumpBtn', scroll: '#scroll',
  ver: '#ver', toast: '#toast', alarm: '#alarm', alarmOk: '#aOk', alarmPct: '#alarmPct',
};
