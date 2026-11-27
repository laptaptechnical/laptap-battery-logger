// test/v15-shots.mjs
// Headless, off-screen screenshots of the v15 page in each band state, at 1024×600, 1366×768 and 1280×720@150%,
// light and dark, against the v15 FakeExe. Also measures: log height (≥ 180 px at 1024×600, plan §2), page overflow,
// clipped text. Output: test/results/v15-screens/<state>-<w>x<h>@<scale>-<scheme>.png + a summary line each.
// Run:  node test/v15-shots.mjs
import fs from 'node:fs';
import path from 'node:path';
import { browser, closeBrowser, ROOT } from './harness.mjs';
import { FakeExe, ORIGIN, MIN, settle } from './v15-fakeexe.mjs';

const OUT = path.join(ROOT, 'test', 'results', 'v15-screens');
fs.mkdirSync(OUT, { recursive: true });

// Put a FakeExe into each state. `poll` lets the page poll at the exe's clock.
const STATES = {
  ready: (e) => { e.pct = 87; },
  countdown: (e) => { e.pct = 99; e.ac = false; e.latch = { ms: e.at, pct: 99, mwh: e.mwh() }; e.lastPollMs = e.at; e.tick(); e.at += 1000; },
  logging: (e) => {
    e.pct = 99; e.unplug(); e.lastPollMs = e.at; e.tick(); e.at += 3000; e.tick();
    for (let i = 0; i < 150; i++) { e.at += MIN; e.drain(Math.max(40, 99 - Math.floor(i / 3))); }
    e.gap(20); for (let i = 0; i < 20; i++) { e.at += MIN; e.drain(e.pct - 1); }
  },
  paused: (e) => { STATES.logging(e); e.at += MIN; e.plug(); },
  done: (e) => { STATES.logging(e); e.at += MIN; e.drain(15); e.end('stop'); e.S.alarmAck = true; e.result.alarmAck = true; },
};

const b = await browser();
const lines = [];
for (const [state, setup] of Object.entries(STATES)) {
  for (const [w, h, scale] of [[1024, 600, 1], [1366, 768, 1], [1280, 720, 1.5]]) {
    for (const scheme of ['light', 'dark']) {
      const exe = new FakeExe({ pct: 99 }); setup(exe);
      if (state === 'countdown') { const t = exe.at; exe.tick = function () { if (this.countdownEndsMs == null && !this.S) this.countdownEndsMs = t + 2000; }; } // hold it at "2.."
      const ctx = await b.newContext({ viewport: { width: w, height: h }, deviceScaleFactor: scale, colorScheme: scheme, timezoneId: 'Asia/Manila', locale: 'en-US' });
      for (const [pat, fn] of exe.routes()) await ctx.route(pat, fn);
      const p = await ctx.newPage();
      await p.clock.install({ time: new Date(exe.at) });
      await p.goto(`${ORIGIN}/#t=${exe.token}`);
      await settle(exe); await p.clock.runFor(1200); await settle(exe); await p.clock.runFor(200); await settle(exe);
      await p.waitForTimeout(300);
      const m = await p.evaluate(() => {
        const q = (s) => document.querySelector(s), r = (s) => q(s) ? q(s).getBoundingClientRect() : null;
        const log = r('.log') || r('#scroll');
        return {
          overX: document.documentElement.scrollWidth > innerWidth, overY: document.documentElement.scrollHeight > innerHeight + 1,
          logH: log ? Math.round(log.height) : null, logBottom: log ? Math.round(log.bottom) : null, vh: innerHeight,
          clipped: [...document.querySelectorAll('.stat .s, .stat .v, button, .unit, .pill, .band *')]
            .filter(e => e.offsetParent && e.children.length === 0 && e.scrollWidth > e.clientWidth + 1).map(e => (e.id || e.className || e.tagName) + ':' + e.textContent.trim().slice(0, 30)),
        };
      });
      const f = `${state}-${w}x${h}@${scale}-${scheme}.png`;
      await p.screenshot({ path: path.join(OUT, f) });
      const flag = (state !== 'ready' && w === 1024 && m.logH != null && m.logH < 180) ? '  << log < 180 px' : '';
      lines.push(`${f} logH=${m.logH} logBottom=${m.logBottom}/${m.vh} overflowX=${m.overX} overflowY=${m.overY} clipped=${JSON.stringify(m.clipped)}${flag}`);
      await ctx.close();
    }
  }
}
await closeBrowser();
fs.writeFileSync(path.join(OUT, 'summary.txt'), lines.join('\n') + '\n');
console.log(lines.join('\n'));
