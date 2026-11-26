// tests/e2e-update.mjs — local end-to-end self-update test (plan §6.3). Loopback + temp folders only; no GitHub,
// no real keys, no window (--qa-no-window), its own data folder and mutex names (LAPTAP_BL_TEST_NS), so a real logger
// on this PC is never touched.
//   node tests/e2e-update.mjs
// 1. throwaway test key  2. TESTHOOKS exe v15.0.0 (release 1) on a fake "stick"  3. release.ps1 makes v15.0.1
// (release 2) signed with the test key  4. a local server plays GitHub Releases  5. run old → it downloads in the
// background and stages  6. run again → swap → the child writes "owner" → the old one exits  7. the child reports
// version 15.0.1, updatedFrom 15.0.0 (the toast flag).
import { spawn, spawnSync, execFileSync } from 'node:child_process';
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'bl-e2e-'));
const ns = 'e2e' + crypto.randomBytes(4).toString('hex');
const dataDir = path.join(process.env.LOCALAPPDATA, 'LaptapBatteryLogger-test-' + ns);
const stick = path.join(tmp, 'stick'), exe = path.join(stick, 'Laptap Battery Logger.exe');
const desk = path.join(tmp, 'desk');
const results = [];
const ok = (cond, what) => { results.push([!!cond, what]); console.log((cond ? 'ok   ' : 'FAIL ') + what); };
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const ps = (args) => {
  const r = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', ...args], { cwd: REPO, encoding: 'utf8', windowsHide: true });
  if (r.status !== 0) throw new Error(args.join(' ') + '\n' + r.stdout + r.stderr);
  return r.stdout;
};
const sha = (f) => crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');
async function waitFor(fn, ms, step = 250) { const end = Date.now() + ms; while (Date.now() < end) { const v = await fn(); if (v) return v; await sleep(step); } return null; }
const alive = (pid) => { try { process.kill(pid, 0); return true; } catch { return false; } };

let server, procs = [];
try {
  fs.mkdirSync(stick); fs.mkdirSync(desk);
  const keyFile = path.join(tmp, 'test.key');
  const pub = ps(['-File', 'tools\\keys.ps1', 'test-new', '-Out', keyFile]).trim().split(/\r?\n/).pop();
  ok(/^[0-9a-f]{64}:[0-9a-f]{64}$/.test(pub), 'test key made');

  ps(['-File', 'build.ps1', '-Out', path.join(tmp, 'old'), '-Version', '15.0.0', '-Release', '1', '-TestHooks', '-SkipTests']);
  fs.copyFileSync(path.join(tmp, 'old', 'Laptap Battery Logger.exe'), exe);
  const oldSha = sha(exe);
  const rel = path.join(tmp, 'rel');
  ps(['-File', 'tools\\release.ps1', '15.0.1', '-Release', '2', '-TestHooks', '-NoGit', '-KeyFile', keyFile, '-OutDir', rel]);
  ok(['update.json', 'update.json.sig', 'Laptap-Battery-Logger.exe'].every(f => fs.existsSync(path.join(rel, f))), 'release.ps1 made the three release files');
  const newSha = sha(path.join(rel, 'Laptap-Battery-Logger.exe'));
  const man = JSON.parse(fs.readFileSync(path.join(rel, 'update.json'), 'utf8'));
  ok(man.release === 2 && man.tag === 'v15.0.1' && man.exe.sha256 === newSha, 'manifest: release 2, tag v15.0.1, exe hash');

  // the fake GitHub Releases
  const hits = [];
  server = http.createServer((req, res) => {
    hits.push(req.url);
    const map = {
      '/releases/latest/download/update.json': 'update.json',
      '/releases/latest/download/update.json.sig': 'update.json.sig',
      '/releases/download/v15.0.1/Laptap-Battery-Logger.exe': 'Laptap-Battery-Logger.exe',
    };
    const f = map[req.url];
    if (!f) { res.statusCode = 404; return res.end(); }
    res.end(fs.readFileSync(path.join(rel, f)));
  });
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const env = { ...process.env, LAPTAP_BL_TEST_BASE: 'http://127.0.0.1:' + server.address().port, LAPTAP_BL_TEST_PUBKEYS: pub, LAPTAP_BL_TEST_NS: ns };
  const args = ['--qa-no-window', '--qa-out', desk];

  // 5. first run: window "opens" at once, then the background check downloads + stages
  const t0 = Date.now();
  const p1 = spawn(exe, args, { env, detached: false, windowsHide: true }); procs.push(p1.pid);
  const helper = path.join(dataDir, 'helper.json');
  ok(await waitFor(() => fs.existsSync(helper), 15000), 'first run: logger up (helper.json) in ' + (Date.now() - t0) + ' ms');
  const staged = await waitFor(() => fs.existsSync(exe + '.update') && fs.existsSync(exe + '.update.json.sig'), 40000);
  ok(staged, 'first run: downloaded and staged in the background');
  ok(staged && sha(exe + '.update') === newSha, 'staged copy is the release exe');
  ok(hits.includes('/releases/download/v15.0.1/Laptap-Battery-Logger.exe'), 'exe fetched from the pinned tag');
  ok(sha(exe) === oldSha, 'the running exe was not touched mid-run');
  execFileSync('taskkill', ['/PID', String(p1.pid), '/F'], { stdio: 'ignore' });
  await waitFor(() => !alive(p1.pid), 5000);
  try { fs.unlinkSync(helper); } catch {}

  // 6. second run: swap + hand-off
  const p2 = spawn(exe, args, { env, windowsHide: true }); procs.push(p2.pid);
  const exitP = new Promise(r => { const t = setTimeout(() => r('timeout'), 70000); p2.on('exit', (c) => { clearTimeout(t); r(c); }); });
  // U1: the new exe says "owner" only after its first /api/state is answered. Play the page: poll it once it is up.
  let polled = false, ownerBeforePoll = false;
  const pollLoop = (async () => {
    for (let i = 0; i < 280 && p2.exitCode == null; i++) {
      try {
        const h = JSON.parse(fs.readFileSync(helper, 'utf8'));
        if (h.pid !== p2.pid && !polled) {
          ownerBeforePoll = fs.readdirSync(dataDir).some(f => f.startsWith('update-ok-') && /^owner/.test(fs.readFileSync(path.join(dataDir, f), 'utf8')));
          await new Promise((res) => http.get({ host: 'localhost', port: h.port, path: '/api/state', headers: { 'X-Laptap-Token': h.token, Host: 'localhost:' + h.port } },
            (r) => { r.resume(); r.on('end', res); }).on('error', res));
          polled = true;
        }
      } catch {}
      await sleep(250);
    }
  })();
  const exit = await exitP; await pollLoop;
  ok(polled && !ownerBeforePoll, 'the new exe said "owner" only after its first poll was answered (U1)');
  ok(exit === 0, 'second run: old process handed off and exited 0 (got ' + exit + ')');
  ok(sha(exe) === newSha, 'the stick now holds the new exe');
  ok(fs.existsSync(exe + '.old') && sha(exe + '.old') === oldSha, 'old exe kept as .old until the next start');
  ok(!fs.readdirSync(dataDir).some(f => f.startsWith('update-ok-')), 'hand-off marker cleaned up');
  const h = await waitFor(() => { try { return JSON.parse(fs.readFileSync(helper, 'utf8')); } catch { return null; } }, 20000);
  ok(h && h.pid !== p2.pid, 'the new exe is running (pid ' + (h && h.pid) + ')');
  if (h) {
    procs.push(h.pid);
    const st = await new Promise((res) => http.get({ host: 'localhost', port: h.port, path: '/api/state', headers: { 'X-Laptap-Token': h.token, Host: 'localhost:' + h.port } },
      (r) => { let b = ''; r.on('data', c => b += c); r.on('end', () => { try { res(JSON.parse(b)); } catch { res(null); } }); }).on('error', () => res(null)));
    ok(st && st.app && st.app.version === '15.0.1', 'child reports version 15.0.1');
    ok(st && st.app && st.app.updatedFrom === '15.0.0', 'child has updatedFrom 15.0.0 (the "Updated to v15.0.1" toast)');
    ok(st && st.checks && 'armed' in st, 'child serves the v15 state');
  }
  const log = fs.existsSync(path.join(dataDir, 'update.log')) ? fs.readFileSync(path.join(dataDir, 'update.log'), 'utf8') : '';
  ok(/staged 15\.0\.1/.test(log) && /swapped to 15\.0\.1/.test(log), 'update.log records staged + swapped');
} catch (e) {
  ok(false, 'crashed: ' + (e.stack || e));
} finally {
  for (const pid of procs) { try { execFileSync('taskkill', ['/PID', String(pid), '/F'], { stdio: 'ignore' }); } catch {} }
  if (server) server.close();
  await sleep(500);
  for (const d of [tmp, dataDir]) { try { fs.rmSync(d, { recursive: true, force: true }); } catch {} }
  const fails = results.filter(r => !r[0]).length;
  console.log(`\ne2e-update: ${results.length - fails} passed, ${fails} failed`);
  process.exitCode = fails ? 1 : 0;
}
