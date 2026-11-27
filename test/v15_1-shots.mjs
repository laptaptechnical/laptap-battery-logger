// test/v15_1-shots.mjs
// Headless, off-screen screenshots of every v15.1 screen against FakeExe151, at 1024×600, 1366×768 and
// 1280×720@150%, light and dark. Measures page overflow and clipped text, and checks the main controls sit inside the
// window. Output: test/results/v15_1-screens/<screen>-<w>x<h>@<scale>-<scheme>.png + summary.txt.
// Run:  node test/v15_1-shots.mjs
import fs from 'node:fs';
import path from 'node:path';
import { browser, closeBrowser, ROOT } from './harness.mjs';
import { FakeExe151, ORIGIN, MIN, settle } from './v15_1-fakeexe.mjs';

const OUT = path.join(ROOT, 'test', 'results', 'v15_1-screens');
fs.mkdirSync(OUT, { recursive: true });

function logging(e) {
  e.pct = 100; e.unplug(); e.lastPollMs = e.at; e.tick(); e.at += 3000; e.tick();
  for (let i = 0; i < 90; i++) { e.at += MIN; e.drain(Math.max(40, 100 - Math.floor(i / 2))); }
}
const freezeCountdown = (e) => { const t = e.at; e.tick = function () { if (!this.S) { this.countdownEndsMs = t + 2000; this.lastPollMs = this.at; } }; };
const SCREENS = {
  nobatt: (e) => { e.battery = false; e.pct = 0; },
  low: (e) => { e.pct = 41; e.ac = false; },
  charge: (e) => { e.pct = 82; e.ac = true; },
  unplug: (e) => { e.pct = 100; e.ac = true; },
  waitupd: (e) => { e.pct = 100; e.ac = true; e.setUpdate('downloading', { percent: 62, bytes: 2_852_000, total: 4_600_000 }); e.hideModal = true; },
  countdown: (e) => { e.pct = 100; e.ac = false; e.latch = { ms: e.at, pct: 100, mwh: e.mwh() }; freezeCountdown(e); },
  afterCancel: (e) => { e.pct = 100; e.ac = false; e.armed = false; },
  logging: (e) => { logging(e); e.setUpdate('downloading', { percent: 40, bytes: 1, total: 2 }); },
  paused: (e) => { logging(e); e.at += MIN; e.plug(); e.at += 130_000; },
  alarm: (e) => { logging(e); e.at += MIN; e.drain(15); e.end('alarm'); },
  done: (e) => { logging(e); e.at += MIN; e.drain(15); e.end('stop'); e.S.alarmAck = true; e.result.alarmAck = true; },
  'upd-downloading': (e) => { e.pct = 100; e.ac = true; e.setUpdate('downloading', { percent: 62, bytes: 2_852_000, total: 4_600_000 }); },
  'upd-ready': (e) => { e.pct = 100; e.ac = true; e.setUpdate('ready'); },
  'upd-failed': (e) => { e.pct = 100; e.ac = true; e.setUpdate('failed', { note: 'Update check failed (no internet?)' }); },
};

const b = await browser();
const lines = [];
for (const [name, setup] of Object.entries(SCREENS)) {
  for (const [w, h, scale] of [[1024, 600, 1], [1366, 768, 1], [1280, 720, 1.5]]) {
    for (const scheme of ['light', 'dark']) {
      const exe = new FakeExe151({ pct: 100 }); setup(exe);
      const ctx = await b.newContext({ viewport: { width: w, height: h }, deviceScaleFactor: scale, colorScheme: scheme, timezoneId: 'Asia/Manila', locale: 'en-US', reducedMotion: 'reduce' });
      for (const [pat, fn] of exe.routes()) await ctx.route(pat, fn);
      const p = await ctx.newPage();
      await p.clock.install({ time: new Date(exe.at) });
      await p.goto(`${ORIGIN}/#t=${exe.token}`);
      for (let i = 0; i < 3; i++) { await settle(exe); exe.at += 1000; await p.clock.runFor(1000); }
      await settle(exe);
      if (exe.hideModal) { const hb = p.locator('#hideUpd'); if (await hb.count()) { await hb.click(); await settle(exe); await p.clock.runFor(1000); await settle(exe); } }
      await p.waitForTimeout(250);
      const m = await p.evaluate(() => {
        const outside = [...document.querySelectorAll('button')].filter(x => x.offsetParent && getComputedStyle(x).visibility !== 'hidden')
          .filter(x => { const r = x.getBoundingClientRect(); return r.bottom > innerHeight + 1 || r.right > innerWidth + 1; }).map(x => x.textContent.trim());
        return {
          overX: document.documentElement.scrollWidth > innerWidth, overY: document.documentElement.scrollHeight > innerHeight + 1, outside,
          clipped: [...document.querySelectorAll('#stage *, #overlay *, header *')]
            .filter(e => e.offsetParent && e.children.length === 0 && e.textContent.trim() && e.scrollWidth > e.clientWidth + 1 && getComputedStyle(e).textOverflow !== 'ellipsis')
            .map(e => (e.id || e.className || e.tagName) + ':' + e.textContent.trim().slice(0, 30)).slice(0, 6),
        };
      });
      const f = `${name}-${w}x${h}@${scale}-${scheme}.png`;
      await p.screenshot({ path: path.join(OUT, f) });
      const bad = m.overX || m.overY || m.outside.length || m.clipped.length;
      lines.push(`${bad ? '!! ' : 'ok '}${f} overflowX=${m.overX} overflowY=${m.overY} offscreenButtons=${JSON.stringify(m.outside)} clipped=${JSON.stringify(m.clipped)}`);
      await ctx.close();
    }
  }
}
await closeBrowser();
fs.writeFileSync(path.join(OUT, 'summary.txt'), lines.join('\n') + '\n');
console.log(lines.join('\n'));
