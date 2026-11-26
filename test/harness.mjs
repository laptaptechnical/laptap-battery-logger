// Battery-Logger/test/harness.mjs
// Headless test harness for any Battery Logger HTML file.
// - Headless Chromium only (no window, no focus steal), audio muted AND AudioContext stubbed (alarm never sounds).
// - Fake navigator.getBattery() you drive from the test: bat.set({level, charging}) fires levelchange/chargingchange.
// - Fake clock (Playwright page.clock): run(ms) fires every timer; sleep(ms) jumps like a closed lid (due timers fire once).
// - Probes: timer wakeups, DOM mutations, localStorage writes (count + bytes), dialogs, downloads, audio calls.
// Uses the playwright-core already on this PC (laptap-desktop/node_modules) and the cached headless shell; nothing installed.
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
// Default: the playwright-core already installed in the sibling laptap-desktop repo (nothing is installed for these tests).
const PW_CORE = process.env.PW_CORE || path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..', 'laptap-desktop', 'node_modules', 'playwright-core');
const { chromium } = require(PW_CORE);

function findExe() {
  if (process.env.PW_CHROME) return process.env.PW_CHROME;
  const root = path.join(process.env.LOCALAPPDATA || '', 'ms-playwright');
  if (fs.existsSync(root)) {
    const shells = fs.readdirSync(root).filter(d => d.startsWith('chromium_headless_shell-')).sort().reverse();
    for (const d of shells) {
      const exe = path.join(root, d, 'chrome-headless-shell-win64', 'chrome-headless-shell.exe');
      if (fs.existsSync(exe)) return exe;
    }
  }
  return 'C:/Program Files/Google/Chrome/Application/chrome.exe'; // headless mode still never shows a window
}

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const V13 = path.join(ROOT, 'test', 'fixtures', 'v13.html');   // v13 page: positive control for the self-test
export const PAGE = path.join(ROOT, 'src', 'web', 'index.html');     // the v14 page
export const TARGET = process.env.LOGGER_FILE ? path.resolve(process.env.LOGGER_FILE) : V13;

let browserPromise = null;
export function browser() {
  browserPromise ??= chromium.launch({
    executablePath: findExe(),
    headless: true,
    args: ['--mute-audio', '--autoplay-policy=document-user-activation-required'],
  });
  return browserPromise;
}
export async function closeBrowser() { if (browserPromise) { (await browserPromise).close(); browserPromise = null; } }

// Runs in the page before any page script. `cfg` comes from openLogger opts.
function pageInit(cfg) {
  // ---- probes ----
  const P = (window.__probe = { timerFires: 0, timerFiresByMs: {}, storageWrites: 0, storageBytes: 0, storageRemoves: 0,
    mutations: 0, audio: { contexts: 0, closed: 0, resumed: 0, gestureAtCreate: [], oscTypes: [], maxGain: 0, starts: 0 } });
  const wrapTimer = (name) => {
    const orig = window[name];
    window[name] = function (fn, ms, ...a) {
      if (typeof fn !== 'function') return orig.call(window, fn, ms, ...a);
      const key = String(ms | 0);
      return orig.call(window, function () { P.timerFires++; P.timerFiresByMs[key] = (P.timerFiresByMs[key] || 0) + 1; return fn.apply(this, arguments); }, ms, ...a);
    };
  };
  wrapTimer('setInterval'); wrapTimer('setTimeout');
  const rafOrig = window.requestAnimationFrame;
  window.requestAnimationFrame = (cb) => rafOrig.call(window, (t) => { P.timerFires++; P.timerFiresByMs.raf = (P.timerFiresByMs.raf || 0) + 1; cb(t); });
  const setItem = Storage.prototype.setItem, removeItem = Storage.prototype.removeItem;
  Storage.prototype.setItem = function (k, v) { P.storageWrites++; P.storageBytes += String(k).length + String(v).length; return setItem.call(this, k, v); };
  Storage.prototype.removeItem = function (k) { P.storageRemoves++; return removeItem.call(this, k); };
  const startMO = () => new MutationObserver(list => { P.mutations += list.length; })
    .observe(document.documentElement, { subtree: true, childList: true, characterData: true, attributes: true });
  if (document.documentElement) startMO(); else document.addEventListener('DOMContentLoaded', startMO);

  // ---- visibility override (headless tabs are always "visible") ----
  let vis = 'visible';
  Object.defineProperty(Document.prototype, 'visibilityState', { configurable: true, get: () => vis });
  Object.defineProperty(Document.prototype, 'hidden', { configurable: true, get: () => vis === 'hidden' });
  window.__setVisibility = (v) => { vis = v; document.dispatchEvent(new Event('visibilitychange')); };

  // ---- audio stub: records everything, outputs nothing ----
  // "In a gesture" = called while a trusted click/key event is being handled. (navigator.userActivation can't be used:
  // Playwright's evaluate() runs with a user gesture, so every fake battery event would look like a click.)
  const GESTURES = ['click', 'keydown', 'keyup', 'pointerdown', 'pointerup', 'mousedown', 'mouseup', 'touchend'];
  const inGesture = () => !!(window.event && window.event.isTrusted && GESTURES.includes(window.event.type));
  class FakeParam { constructor(isGain) { this.value = 1; this.isGain = isGain; } setValueAtTime(v) { if (this.isGain) P.audio.maxGain = Math.max(P.audio.maxGain, v); this.value = v; return this; }
    linearRampToValueAtTime(v) { return this.setValueAtTime(v); } exponentialRampToValueAtTime(v) { return this.setValueAtTime(v); }
    setTargetAtTime(v) { return this.setValueAtTime(v); } cancelScheduledValues() { return this; } }
  class FakeNode { connect(n) { return n; } disconnect() {} }
  class FakeOsc extends FakeNode { constructor() { super(); this.frequency = new FakeParam(); this.detune = new FakeParam(); this._type = 'sine'; }
    set type(t) { this._type = t; P.audio.oscTypes.push(t); } get type() { return this._type; } start() { P.audio.starts++; } stop() {} }
  class FakeGain extends FakeNode { constructor() { super(); this.gain = new FakeParam(true); } }
  class FakeAudioContext {
    constructor() { P.audio.contexts++; P.audio.gestureAtCreate.push(inGesture());
      this.state = (navigator.userActivation && navigator.userActivation.hasBeenActive) ? 'running' : 'suspended';
      this.currentTime = 0; this.destination = new FakeNode(); this.sampleRate = 44100; }
    createOscillator() { return new FakeOsc(); } createGain() { const g = new FakeGain(); return g; }
    createBiquadFilter() { const f = new FakeNode(); f.frequency = new FakeParam(); f.Q = new FakeParam(); return f; }
    createDynamicsCompressor() { const c = new FakeNode(); ['threshold','knee','ratio','attack','release'].forEach(k => c[k] = new FakeParam()); return c; }
    resume() { if (inGesture()) P.audio.resumed++; if (navigator.userActivation && navigator.userActivation.hasBeenActive) this.state = 'running'; return Promise.resolve(); }
    suspend() { this.state = 'suspended'; return Promise.resolve(); }
    close() { P.audio.closed++; this.state = 'closed'; return Promise.resolve(); }
  }
  window.AudioContext = FakeAudioContext; window.webkitAudioContext = FakeAudioContext;
  HTMLMediaElement.prototype.play = function () { P.audio.starts++; return Promise.resolve(); };

  // ---- fake Battery Status API ----
  if (cfg.battery === 'absent') { try { delete Navigator.prototype.getBattery; } catch {} return; }
  class FakeBattery extends EventTarget {
    constructor(s) { super(); this.level = s.level; this.charging = s.charging; this.chargingTime = s.charging ? 0 : Infinity; this.dischargingTime = Infinity;
      this.onlevelchange = this.onchargingchange = this.onchargingtimechange = this.ondischargingtimechange = null; }
    _fire(type) { const e = new Event(type); this.dispatchEvent(e); const h = this['on' + type]; if (typeof h === 'function') h.call(this, e); }
  }
  const bat = new FakeBattery(cfg.initial);
  let resolveBattery, rejectBattery;
  const promise = new Promise((res, rej) => { resolveBattery = res; rejectBattery = rej; });
  if (cfg.battery === 'ok') resolveBattery(bat);
  else if (cfg.battery === 'reject') rejectBattery(new DOMException('Battery API failure (test)', 'NotAllowedError'));
  // 'pending' = never answers until window.__bat.answer()
  Navigator.prototype.getBattery = function () { P.getBatteryCalls = (P.getBatteryCalls || 0) + 1; return promise; };
  window.__bat = {
    obj: bat,
    answer: () => resolveBattery(bat),
    // silent: change values without firing events (to test code that should re-read)
    set: ({ level, charging, chargingTime, dischargingTime, silent } = {}) => {
      const ev = [];
      if (level !== undefined && level !== bat.level) { bat.level = level; ev.push('levelchange'); }
      if (charging !== undefined && charging !== bat.charging) { bat.charging = charging; ev.push('chargingchange'); }
      if (chargingTime !== undefined) { bat.chargingTime = chargingTime; ev.push('chargingtimechange'); }
      if (dischargingTime !== undefined) { bat.dischargingTime = dischargingTime; ev.push('dischargingtimechange'); }
      if (!silent) ev.forEach(t => bat._fire(t));
    },
  };
}

/**
 * Open a logger page.
 * opts.battery: 'ok' | 'pending' | 'reject' | 'absent'   (default 'ok')
 * opts.level (0..1, default 0.8), opts.charging (default false)
 * opts.locale (default 'en-US'), opts.time (ISO start time), opts.storage ({key: value} pre-seeded localStorage)
 * opts.file (default TARGET), opts.url + opts.routes ([[pattern, handler], …] for context.route)
 */
export async function openLogger(opts = {}) {
  const b = await browser();
  const context = await b.newContext({ locale: opts.locale || 'en-US', timezoneId: 'Asia/Manila', acceptDownloads: true,
    viewport: { width: 1000, height: 900 } });
  const page = await context.newPage();
  const dialogs = [], downloads = [], consoleErrors = [];
  page.on('dialog', async d => { dialogs.push({ type: d.type(), message: d.message() }); opts.acceptDialogs === false ? await d.dismiss() : await d.accept(); });
  page.on('download', async d => { const p = await d.path().catch(() => null); downloads.push({ name: d.suggestedFilename(), text: p ? fs.readFileSync(p, 'utf8') : null }); });
  page.on('pageerror', e => consoleErrors.push(String(e)));
  page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text()); });

  if (!opts.realClock) await page.clock.install({ time: new Date(opts.time || '2026-11-26T09:00:00+08:00') });
  const cfg = { battery: opts.battery || 'ok', initial: { level: opts.level ?? 0.8, charging: !!opts.charging } };
  await page.addInitScript(pageInit, cfg);
  if (opts.storage) await page.addInitScript((s) => { if (!sessionStorage.__seeded) { for (const [k, v] of Object.entries(s)) localStorage.setItem(k, v); sessionStorage.__seeded = 1; } }, opts.storage);
  const file = opts.file || TARGET;
  // opts.url + opts.routes: serve a page over a mocked http origin (v14 logger mode: page + mocked /api). Added for v14.
  if (opts.routes) for (const [pattern, handler] of opts.routes) await context.route(pattern, handler);
  await page.goto(opts.url || pathToFileURL(file).href);
  const tick = (ms) => opts.realClock ? page.waitForTimeout(ms) : page.clock.runFor(ms);
  await tick(50); // let getBattery() promise callbacks run

  const api = {
    page, context, dialogs, downloads, consoleErrors, file,
    bat: {
      set: (s) => page.evaluate((s) => window.__bat.set(s), s),
      answer: () => page.evaluate(() => window.__bat.answer()),
    },
    run: async (ms) => { await page.clock.runFor(ms); },           // normal running time, every timer fires
    sleep: async (ms) => { await page.clock.fastForward(ms); await page.clock.runFor(10); }, // lid closed: due timers fire once
    hide: () => page.evaluate(() => window.__setVisibility('hidden')),
    show: () => page.evaluate(() => window.__setVisibility('visible')),
    click: async (sel) => { await page.click(sel, { timeout: 2000 }); await tick(10); },
    text: (sel) => page.locator(sel).first().innerText({ timeout: 2000 }),
    content: (sel) => page.locator(sel).first().textContent({ timeout: 2000 }),
    storage: () => page.evaluate(() => Object.fromEntries(Object.keys(localStorage).map(k => [k, localStorage.getItem(k)]))),
    probe: () => page.evaluate(() => JSON.parse(JSON.stringify(window.__probe))),
    resetProbe: () => page.evaluate(() => { const P = window.__probe; P.timerFires = 0; P.timerFiresByMs = {}; P.storageWrites = 0; P.storageBytes = 0; P.mutations = 0; }),
    reload: async () => { await page.reload(); await tick(50); },
    close: () => context.close(),
  };
  return api;
}
