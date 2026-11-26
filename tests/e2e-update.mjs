// tests/e2e-update.mjs — local end-to-end tests (v15 plan §6.3, v15.1 plan §6.3). Loopback + temp folders only; no
// GitHub, no real keys, no window (--qa-no-window), its own data folders and mutex names (LAPTAP_BL_TEST_NS), so a real
// logger on this PC is never touched.
//   node tests/e2e-update.mjs
// Setup: a throwaway test key; the 15.1.0 candidate and a dummy 15.1.1 made by release.ps1 (TESTHOOKS, test key);
// a TESTHOOKS build of tag v15.0.1 (from `git archive`, the working tree is not touched); a local server plays GitHub.
// A. Release gate, audit C3 (a): v15.0.1 updates to the 15.1.0 candidate through its own start-time TrySwap: the
//    child says "alive", waits for the main mutex, writes "owner" only after its first poll, reports updatedFrom.
// B. Restart now (v15.1 §2.3): the candidate at Ready with 15.1.1 staged → POST /api/update-restart → it exits →
//    "--after-exit <pid>" → the v15 TrySwap → the 15.1.1 child says "owner" → updatedFrom 15.1.0.
// C. Window closed (v15.1 §3): visible heartbeats then silence → the idle logger exits within ~20 s; a hidden one
//    stays past 20 s.
import { spawn, spawnSync, execFileSync } from 'node:child_process';
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'bl-e2e-'));
const EXE = 'Laptap Battery Logger.exe';
const desk = path.join(tmp, 'desk');
const results = [];
const ok = (cond, what) => { results.push([!!cond, what]); console.log((cond ? 'ok   ' : 'FAIL ') + what); };
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const ps = (args, cwd = REPO) => {
  const r = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', ...args], { cwd, encoding: 'utf8', windowsHide: true });
  if (r.status !== 0) throw new Error(args.join(' ') + '\n' + r.stdout + r.stderr);
  return r.stdout;
};
const sha = (f) => crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');
async function waitFor(fn, ms, step = 250) { const end = Date.now() + ms; while (Date.now() < end) { const v = await fn(); if (v) return v; await sleep(step); } return null; }
const alive = (pid) => { try { process.kill(pid, 0); return true; } catch { return false; } };
const newNs = () => 'e2e' + crypto.randomBytes(4).toString('hex');
const dataOf = (ns) => path.join(process.env.LOCALAPPDATA, 'LaptapBatteryLogger-test-' + ns);
const readHelper = (ns) => { try { return JSON.parse(fs.readFileSync(path.join(dataOf(ns), 'helper.json'), 'utf8')); } catch { return null; } };
const ownerMarker = (ns) => { try { const d = dataOf(ns); return fs.readdirSync(d).some(f => f.startsWith('update-ok-') && /^owner/.test(fs.readFileSync(path.join(d, f), 'utf8'))); } catch { return false; } };
function api(port, token, p, method = 'GET', body = null) {
  return new Promise((res) => {
    const data = body == null ? null : JSON.stringify(body);
    const rq = http.request({ host: 'localhost', port, path: p, method,
      headers: Object.assign({ 'X-Laptap-Token': token, Host: 'localhost:' + port }, data ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(data) } : {}) },
      (r) => { let b = ''; r.on('data', c => b += c); r.on('end', () => { try { res(JSON.parse(b)); } catch { res(null); } }); });
    rq.on('error', () => res(null)); rq.setTimeout(3000, () => { rq.destroy(); res(null); });
    if (data) rq.write(data);
    rq.end();
  });
}
function stick(name, from) { const d = path.join(tmp, name); fs.mkdirSync(d, { recursive: true }); const e = path.join(d, EXE); fs.copyFileSync(from, e); return e; }
// Every Battery Logger process started from a test stick (by exe path), so after-exit children are found and cleaned up.
function loggersIn(dir) {
  const r = spawnSync('powershell.exe', ['-NoProfile', '-Command',
    `Get-CimInstance Win32_Process -Filter "Name='Laptap Battery Logger.exe'" | Where-Object { $_.ExecutablePath -like '${dir.replace(/'/g, "''")}*' } | ForEach-Object { $_.ProcessId }`],
    { encoding: 'utf8', windowsHide: true });
  return (r.stdout || '').split(/\r?\n/).map(s => +s.trim()).filter(Boolean);
}
const exitOf = (p, ms) => new Promise(r => { if (p.exitCode != null) return r(p.exitCode); const t = setTimeout(() => r('timeout'), ms); p.on('exit', (c) => { clearTimeout(t); r(c); }); });

let server, procs = [], namespaces = [], serving = null;
try {
  fs.mkdirSync(desk);
  const keyFile = path.join(tmp, 'test.key');
  const pub = ps(['-File', 'tools\\keys.ps1', 'test-new', '-Out', keyFile]).trim().split(/\r?\n/).pop();
  ok(/^[0-9a-f]{64}:[0-9a-f]{64}$/.test(pub), 'test key made');

  // the 15.1.0 candidate (this working tree) and a dummy 15.1.1, both signed with the test key
  const rel0 = path.join(tmp, 'rel-15.1.0'), rel1 = path.join(tmp, 'rel-15.1.1');
  ps(['-File', 'tools\\release.ps1', '15.1.0', '-Release', '4', '-TestHooks', '-NoGit', '-KeyFile', keyFile, '-OutDir', rel0]);
  ps(['-File', 'tools\\release.ps1', '15.1.1', '-Release', '5', '-TestHooks', '-NoGit', '-KeyFile', keyFile, '-OutDir', rel1]);
  ok(['update.json', 'update.json.sig', 'Laptap-Battery-Logger.exe'].every(f => fs.existsSync(path.join(rel0, f)) && fs.existsSync(path.join(rel1, f))), 'release.ps1 made both releases');
  const cand = path.join(rel0, 'Laptap-Battery-Logger.exe'), dummy = path.join(rel1, 'Laptap-Battery-Logger.exe');

  // tag v15.0.1, TESTHOOKS (the code every laptop runs today), built from git archive into a temp folder
  const old = path.join(tmp, 'v15.0.1-src'); fs.mkdirSync(old);
  const tar = path.join(tmp, 'v15.0.1.tar');
  execFileSync('git', ['archive', '--format=tar', '-o', tar, 'v15.0.1'], { cwd: REPO });
  execFileSync('tar', ['-xf', '../v15.0.1.tar'], { cwd: old });   // relative: Git's tar reads C: as a host
  ps(['-File', 'build.ps1', '-Out', path.join(tmp, 'v15.0.1-bin'), '-Version', '15.0.1', '-Release', '3', '-TestHooks', '-SkipTests'], old);
  const v1501 = path.join(tmp, 'v15.0.1-bin', EXE);
  ok(fs.existsSync(v1501), 'tag v15.0.1 built (TESTHOOKS)');

  const hits = [];
  server = http.createServer((req, res) => {
    hits.push(req.url);
    const tag = serving === rel0 ? 'v15.1.0' : 'v15.1.1';
    const map = {
      '/releases/latest/download/update.json': 'update.json',
      '/releases/latest/download/update.json.sig': 'update.json.sig',
      [`/releases/download/${tag}/Laptap-Battery-Logger.exe`]: 'Laptap-Battery-Logger.exe',
    };
    const f = serving && map[req.url];
    if (!f) { res.statusCode = 404; return res.end(); }
    res.end(fs.readFileSync(path.join(serving, f)));
  });
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const base = 'http://127.0.0.1:' + server.address().port;
  const args = ['--qa-no-window', '--qa-out', desk];

  // ================= A. release gate C3 (a): v15.0.1 → 15.1.0 through its own TrySwap =================
  {
    serving = rel0;
    const ns = newNs(); namespaces.push(ns);
    const env = { ...process.env, LAPTAP_BL_TEST_BASE: base, LAPTAP_BL_TEST_PUBKEYS: pub, LAPTAP_BL_TEST_NS: ns };
    const exe = stick('A', v1501);
    const p1 = spawn(exe, args, { env, windowsHide: true }); procs.push(p1.pid);
    ok(await waitFor(() => { const h = readHelper(ns); return h && h.pid === p1.pid; }, 15000), 'A: v15.0.1 up');
    const staged = await waitFor(() => fs.existsSync(exe + '.update') && fs.existsSync(exe + '.update.json.sig'), 40000);
    ok(staged && sha(exe + '.update') === sha(cand), 'A: v15.0.1 downloaded and staged the 15.1.0 candidate');
    execFileSync('taskkill', ['/PID', String(p1.pid), '/F'], { stdio: 'ignore' });
    await waitFor(() => !alive(p1.pid), 5000);
    try { fs.unlinkSync(path.join(dataOf(ns), 'helper.json')); } catch {}
    // the next start: v15.0.1's TrySwap → HandOff → the 15.1.0 child (no new flags: --updated <nonce> + QA switches)
    const p2 = spawn(exe, args, { env, windowsHide: true }); procs.push(p2.pid);
    const exitP = exitOf(p2, 70000);
    let polled = false, ownerBefore = null, st = null, child = null;
    for (let i = 0; i < 280 && p2.exitCode == null && !polled; i++) {
      const h = readHelper(ns);
      if (h && h.pid !== p2.pid) { ownerBefore = ownerMarker(ns); child = h; st = await api(h.port, h.token, '/api/state'); polled = true; }
      else await sleep(250);
    }
    const code = await exitP;
    ok(polled && ownerBefore === false, 'A: the 15.1.0 child said "owner" only after its first poll (I8)');
    ok(code === 0, 'A: v15.0.1 handed off and exited 0 (got ' + code + ')');
    ok(sha(exe) === sha(cand), 'A: the stick now holds 15.1.0');
    ok(st && st.app && st.app.version === '15.1.0' && st.app.updatedFrom === '15.0.1', 'A: the child is 15.1.0, updatedFrom 15.0.1 (the toast)');
    ok(st && 'nextStep' in st && st.app.update && 'phase' in st.app.update, 'A: the child serves the v15.1 state');
    if (child) procs.push(child.pid);
    // let it check once more: 15.1.0 is the latest → no second update
    await waitFor(async () => { const s2 = child && await api(child.port, child.token, '/api/heartbeat', 'POST', { visible: true }); return false; }, 3000, 1000);
    const st2 = child && await api(child.port, child.token, '/api/state');
    ok(st2 && st2.app.update.phase === 'idle' && !fs.existsSync(exe + '.update'), 'A: no second update (phase ' + (st2 && st2.app.update.phase) + ')');
    for (const pid of loggersIn(path.dirname(exe))) { try { execFileSync('taskkill', ['/PID', String(pid), '/F'], { stdio: 'ignore' }); } catch {} }
  }

  // ================= B. Restart now: 15.1.0 at Ready with 15.1.1 staged =================
  {
    serving = rel1;
    const ns = newNs(); namespaces.push(ns);
    const env = { ...process.env, LAPTAP_BL_TEST_BASE: base, LAPTAP_BL_TEST_PUBKEYS: pub, LAPTAP_BL_TEST_NS: ns };
    const exe = stick('B', cand);
    const p1 = spawn(exe, args, { env, windowsHide: true }); procs.push(p1.pid);
    const h1 = await waitFor(() => { const h = readHelper(ns); return h && h.pid === p1.pid ? h : null; }, 15000);
    ok(h1, 'B: 15.1.0 up');
    // play the page: heartbeat + poll every second until the update is ready
    const phases = new Set(); let ready = null;
    for (let i = 0; i < 80 && h1 && !ready; i++) {
      await api(h1.port, h1.token, '/api/heartbeat', 'POST', { visible: true });
      const st = await api(h1.port, h1.token, '/api/state?since=999999');
      if (st && st.app && st.app.update) { phases.add(st.app.update.phase); if (st.app.update.phase === 'ready') ready = st; }
      if (!ready) await sleep(500);
    }
    ok(ready && ready.app.update.version === '15.1.1', 'B: phases seen by the page: ' + [...phases].join(' → '));
    ok(ready && ready.nextStep !== 'wait-update', 'B: ready never holds the countdown');
    const r = h1 && await api(h1.port, h1.token, '/api/update-restart', 'POST');
    ok(r && r.ok === true, 'B: Restart now accepted at Ready (' + JSON.stringify(r) + ')');
    const c1 = await exitOf(p1, 15000);
    ok(c1 === 0, 'B: the old logger exited 0 (got ' + c1 + ')');
    // --after-exit → TrySwap → HandOff → the 15.1.1 child; play its page
    let child = null, ownerBefore = null, st = null;
    for (let i = 0; i < 280 && !child; i++) {
      const h = readHelper(ns);
      if (h && h.pid !== p1.pid) { ownerBefore = ownerMarker(ns); child = h; st = await api(h.port, h.token, '/api/state'); }
      else await sleep(250);
    }
    ok(child, 'B: a new logger came up after Restart now');
    if (child) procs.push(child.pid);
    ok(ownerBefore === false, 'B: the child said "owner" only after its first poll');
    ok(st && st.app && st.app.version === '15.1.1' && st.app.updatedFrom === '15.1.0', 'B: the new logger is 15.1.1, updatedFrom 15.1.0');
    ok(sha(exe) === sha(dummy), 'B: the stick now holds 15.1.1');
    const settled = await waitFor(() => { const l = loggersIn(path.dirname(exe)); return l.length === 1 && l[0] === child.pid ? l : null; }, 20000);
    ok(settled, 'B: the --after-exit process handed off and exited; only the new logger runs');
    const log = fs.existsSync(path.join(dataOf(ns), 'update.log')) ? fs.readFileSync(path.join(dataOf(ns), 'update.log'), 'utf8') : '';
    ok(/restart now: quitting/.test(log) && /swapped to 15\.1\.1/.test(log), 'B: update.log records Restart now + the v15 swap');
    ok(/phase: downloading/.test(log) && /phase: verifying/.test(log) && /phase: ready/.test(log), 'B: update.log has one line per phase');
    for (const pid of loggersIn(path.dirname(exe))) { try { execFileSync('taskkill', ['/PID', String(pid), '/F'], { stdio: 'ignore' }); } catch {} }
  }

  // ================= C. window closed → the idle logger exits quickly; hidden → it stays =================
  {
    serving = null;
    for (const [visible, name] of [[true, 'C-visible'], [false, 'C-hidden']]) {
      const ns = newNs(); namespaces.push(ns);
      const env = { ...process.env, LAPTAP_BL_TEST_NS: ns };
      const exe = stick(name, cand);
      const p = spawn(exe, [...args, '--no-update'], { env, windowsHide: true }); procs.push(p.pid);
      const h = await waitFor(() => { const x = readHelper(ns); return x && x.pid === p.pid ? x : null; }, 15000);
      for (let i = 0; i < 3 && h; i++) { await api(h.port, h.token, '/api/heartbeat', 'POST', { visible }); await api(h.port, h.token, '/api/state'); await sleep(1000); }
      const t0 = Date.now();
      if (visible) {
        const code = await exitOf(p, 30000);
        const took = Date.now() - t0;
        ok(code === 0 && took < 22000, `C: window closed (visible page went quiet) → exited in ${Math.round(took / 1000)} s`);
        ok(!fs.existsSync(path.join(dataOf(ns), 'helper.json')), 'C: its helper.json was removed');
      } else {
        await sleep(25000);
        ok(p.exitCode == null && alive(p.pid), 'C: a hidden page gone quiet for 25 s → still running (90 s rule)');
      }
    }
  }
} catch (e) {
  ok(false, 'crashed: ' + (e.stack || e));
} finally {
  for (const pid of procs) { try { execFileSync('taskkill', ['/PID', String(pid), '/F'], { stdio: 'ignore' }); } catch {} }
  for (const pid of loggersIn(tmp)) { try { execFileSync('taskkill', ['/PID', String(pid), '/F'], { stdio: 'ignore' }); } catch {} }
  if (server) server.close();
  await sleep(800);
  for (const d of [tmp, ...namespaces.map(dataOf)]) { try { fs.rmSync(d, { recursive: true, force: true }); } catch {} }
  const fails = results.filter(r => !r[0]).length;
  console.log(`\ne2e-update: ${results.length - fails} passed, ${fails} failed`);
  process.exitCode = fails ? 1 : 0;
}
