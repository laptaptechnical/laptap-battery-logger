// test/measure-bar.mjs
// v15 layout check at 1024×600 (plan §2): the band's buttons stay on screen and the live log gets ≥ 180 px,
// while logging and once the test is done (browser-only mode, 30 readings in the log). Exit code 1 if not.
import path from 'node:path';
import { openLogger, closeBrowser, ROOT } from './harness.mjs';
const PAGE = path.join(ROOT, 'src', 'web', 'index.html');
const L = await openLogger({ file: PAGE, level: 1 });
await L.page.setViewportSize({ width: 1024, height: 600 });
await L.run(4500);   // the checklist is met (unplugged at 100%): 3 s countdown → LOGGING
const measure = () => L.page.evaluate(() => {
  const box = (s) => { const e = document.querySelector(s); if (!e) return null; const b = e.getBoundingClientRect(); return { top: Math.round(b.top), bottom: Math.round(b.bottom), h: Math.round(b.height) }; };
  const vis = [...document.querySelectorAll('#band button')].filter(b => b.offsetParent !== null).map(b => ({ id: b.id, ...box('#' + b.id) }));
  return { band: document.getElementById('band').className, bandButtons: vis, log: box('.log'), scroll: box('#scroll'), viewport: innerHeight, pageH: document.documentElement.scrollHeight };
});
for (let i = 1; i <= 30; i++) { await L.bat.set({ level: 1 - i / 100 }); await L.run(60_000); }
const logging = await measure();
await L.click('#stopBtn'); await L.click('#stopYes'); await L.run(6000);
const done = await measure();
let ok = true;
for (const [name, m] of [['logging', logging], ['done', done]]) {
  const buttonsOn = m.bandButtons.length > 0 && m.bandButtons.every(b => b.bottom <= m.viewport);
  const logOk = m.log && m.log.h >= 180 && m.log.bottom <= m.viewport + 1;
  console.log(name, JSON.stringify({ band: m.band, buttons: m.bandButtons.map(b => b.id + '@' + b.bottom), logH: m.log && m.log.h, logBottom: m.log && m.log.bottom, viewport: m.viewport, pageH: m.pageH }));
  console.log(`  ${buttonsOn ? 'ok  ' : 'FAIL'} band buttons on screen   ${logOk ? 'ok  ' : 'FAIL'} log ≥ 180 px and on screen`);
  ok = ok && buttonsOn && logOk;
}
await L.close(); await closeBrowser();
process.exitCode = ok ? 0 : 1;
