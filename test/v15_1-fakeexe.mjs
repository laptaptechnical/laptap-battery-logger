// test/v15_1-fakeexe.mjs
// The v15 FakeExe extended to the v15.1.0 contract, reduced scope (plan v15.1 §2, §5 after the Manager's scope cut):
// nextStep, app.update phases (no auto restart: ready waits for the next start or an idle-only Restart now), the
// countdown hold while downloading/verifying capped at 45 s (G2, build audit I2), session.pausedSinceMs, live.*, and the new POSTs.
// Takeover, in-place restart, the close beacon and the tray are 15.2 (not modelled). Shared by test/v15_1-*.test.mjs. Follows the plan's words; nothing copied from the exe.
import { FakeExe, ORIGIN, MIN, T0, settle, withExe } from './v15-fakeexe.mjs';
export { ORIGIN, MIN, T0, settle, withExe };

export class FakeExe151 extends FakeExe {
  constructor(o = {}) {
    super(o);
    this.battery = o.battery ?? true;
    this.app = { version: '15.0.1', release: 3, updatedFrom: null, updateNote: null, nextUrl: null, closing: null, newerWaiting: null,
      update: { phase: 'idle', version: null, bytes: null, total: null, percent: null, note: null, restartAtMs: null }, ...(o.app || {}) };
    if (o.update) Object.assign(this.app.update, o.update);
    this.phaseSinceMs = this.at;
    this.pausedSinceMs = null;
    this.restartedTo = null;        // set when the fake "restart" hands over (nextUrl)
    this.nextToken = 'b1c2d3e4f5a60718293a4b5c6d7e8f91';
  }
  // ---- update phase control (tests drive it) ----
  setUpdate(phase, extra = {}) {
    Object.assign(this.app.update, { phase, restartAtMs: null, note: null }, extra);
    if (phase !== 'idle' && !this.app.update.version) this.app.update.version = '15.1.0';
    this.phaseSinceMs = this.at;
  }
  checks() { const c = super.checks(); c.battery = this.battery; if (!this.battery) { c.pct96 = false; } return c; }
  // G2 / audit I7 + build audit I2: downloading and verifying hold the countdown, one cap of 45 s from when the hold began.
  // ready and failed never hold.
  holdsCountdown() {
    const p = this.app.update.phase;
    if (p !== 'downloading' && p !== 'verifying') { this.holdSinceMs = null; return false; }
    if (this.holdSinceMs == null) this.holdSinceMs = this.at;
    return this.at - this.holdSinceMs < 45_000;
  }
  nextStep() {
    if (this.S) return null;
    const c = this.checks();
    if (!c.battery) return 'battery';
    if (!c.pct96) return 'charge';
    if (this.holdsCountdown()) return 'wait-update';
    if (this.ac) return 'unplug';
    return null;
  }
  idleForUpdate() {
    if (!this.S) return this.countdownEndsMs == null;
    return this.S.status === 'done' && (!this.S.alarm || this.S.alarmAck);
  }
  tick() {
    if (this.S) return;
    if (this.countdownEndsMs != null) {
      if (!(this.armed && this.allOk() && this.polledRecently())) { this.countdownEndsMs = null; return; }
      if (this.at >= this.countdownEndsMs) this.startTest();
      return;
    }
    if (this.armed && this.allOk() && this.polledRecently() && !this.holdsCountdown()) this.countdownEndsMs = this.at + 3000;
  }
  restart() { this.setUpdate('restarting', { version: this.app.update.version }); }
  plug() {
    const wasRunning = this.S && this.S.status === 'running';
    super.plug();
    if (wasRunning) this.pausedSinceMs = this.at;
    // G5: re-plug during the countdown cancels it and stays armed (the base tick clears it on the next poll).
  }
  unplug() { const wasPaused = this.S && this.S.status === 'paused'; super.unplug(); if (wasPaused) this.pausedSinceMs = null; }
  live() {
    if (this.samples.length < 5) return { avgW: null, etaToAlarmS: null, healthPct: null };
    return { avgW: 9.9, etaToAlarmS: 11400, healthPct: 91.9 };
  }
  state(since) {
    const s = super.state(since);
    s.nextStep = this.nextStep();
    s.live = this.live();
    s.app = JSON.parse(JSON.stringify(this.app));
    s.version = this.app.version;
    if (s.session) s.session.pausedSinceMs = this.S.status === 'paused' ? this.pausedSinceMs : null;
    return s;
  }
  post(p, body) {
    const u = this.app.update;
    switch (p) {
      case '/api/update-restart':
        if (u.phase !== 'ready' || !this.idleForUpdate()) return { ok: false, error: 'Finish the test first.' };
        this.restart(); return { ok: true };
      case '/api/update-retry':
        if (u.phase !== 'failed') return { ok: false, error: 'Nothing to retry.' };
        this.setUpdate('checking'); return { ok: true };
      case '/api/open-report':
        if (!this.S || this.S.status !== 'done') return { ok: false, error: 'No report yet.' };
        return { ok: true };
      case '/api/quit': if (!this.idleForUpdate()) return { ok: false, error: 'A test is running.' }; this.app.closing = 'quit'; return { ok: true };
      case '/api/newer-waiting': this.app.newerWaiting = body.version; return { ok: true };
      case '/api/closing': return { ok: true };
    }
    return super.post(p, body);
  }
  routes() {
    const [[pat, base]] = super.routes();
    return [[pat, async (route) => {
      const req = route.request(), u = new URL(req.url());
      // the beacon carries the token as a text/plain body (no header)
      if (req.method() === 'POST' && u.pathname === '/api/closing') {
        this.calls.push({ m: 'POST', p: '/api/closing', at: this.at, token: null, body: req.postData() });
        return route.fulfill({ status: 200, contentType: 'application/json', body: '{"ok":true}' });
      }
      // after a restart the page follows nextUrl with the new token: accept it as the "new exe"
      if (this.app.nextUrl && req.headers()['x-laptap-token'] === this.nextToken) {
        this.restartedTo = this.restartedTo || this.at;
        this.token = this.nextToken;
        Object.assign(this.app, { version: this.app.update.version || this.app.version, updatedFrom: this.app.version, nextUrl: null });
        this.app.update = { phase: 'idle', version: null, bytes: null, total: null, percent: null, note: null, restartAtMs: null };
      }
      return base(route);
    }]];
  }
}

// Ids the v15.1 tests rely on (the v15.1 prototype's ids). If the page uses others, change them here only.
export const SEL = {
  stage: '#stage', overlay: '#overlay', chip: '#updChip', ver: '#ver',
  cancel: '#cancelBtn', start: '#startBtn', cdWords: '#cdw', cdNum: '#cdn', elapsed: '#elapsed',
  stop: '#stopBtn', stopYes: '#stopYes', stopNo: '#stopNo', alarmPct: '#alarmPct', newBtn: '#newBtn',
  restart: '#restartBtn', hide: '#hideUpd', retry: '#retryUpd', alarmOk: '#aOk',
};
