// test/measure-bar.mjs
// v15.1 layout check at 1024×600 and 1366×768 (plan v15.1 §6, owner rule: no clipping): every control on each screen
// is on screen, nothing scrolls the page, and while logging the live log gets ≥ 150 px (browser-only mode, 30 readings).
// Exit code 1 if not.
import path from 'node:path';
import { openLogger, closeBrowser, ROOT } from './harness.mjs';
const PAGE = path.join(ROOT, 'src', 'web', 'index.html');
let ok = true;
for (const [w, h] of [[1024, 600], [1366, 768]]) {
  const L = await openLogger({ file: PAGE, level: 1, charging: true });
  await L.page.setViewportSize({ width: w, height: h });
  const measure = () => L.page.evaluate(() => {
    const box = (e) => { const b = e.getBoundingClientRect(); return { top: Math.round(b.top), bottom: Math.round(b.bottom), right: Math.round(b.right), h: Math.round(b.height) }; };
    const vis = [...document.querySelectorAll('button,input')].filter(b => b.offsetParent !== null && !b.closest('[hidden]')).map(b => ({ id: b.id || b.textContent.trim(), ...box(b) }));
    const lb = document.querySelector('#sRun:not([hidden]) .logbox');
    const say = document.querySelector('#sPre:not([hidden]) .say');
    if (say) vis.push({ id: 'instruction', ...box(say) });
    return { controls: vis, log: lb ? box(lb) : null, viewport: innerHeight, width: innerWidth, pageH: document.documentElement.scrollHeight };
  });
  await L.run(1500);
  const shots = [['unplug', await measure()]];
  await L.bat.set({ charging: false }); await L.run(4500);   // countdown → LOGGING
  for (let i = 1; i <= 30; i++) { await L.bat.set({ level: 1 - i / 100 }); await L.run(60_000); }
  shots.push(['logging', await measure()]);
  await L.click('#stopBtn'); shots.push(['stop confirm', await measure()]);
  await L.click('#stopYes'); await L.run(6000);
  shots.push(['done', await measure()]);
  for (const [name, m] of shots) {
    const on = m.controls.length > 0 && m.controls.every(c => c.bottom <= m.viewport && c.right <= m.width);
    const noScroll = m.pageH <= m.viewport;
    const logOk = name !== 'logging' || (m.log && m.log.h >= 150 && m.log.bottom <= m.viewport + 1);
    console.log(`${w}×${h} ${name}`, JSON.stringify({ controls: m.controls.map(c => c.id + '@' + c.bottom), logH: m.log && m.log.h, pageH: m.pageH }));
    console.log(`  ${on ? 'ok  ' : 'FAIL'} controls on screen   ${noScroll ? 'ok  ' : 'FAIL'} no page scroll   ${logOk ? 'ok  ' : 'FAIL'} log`);
    ok = ok && on && noScroll && logOk;
  }
  await L.close();
}
await closeBrowser();
process.exitCode = ok ? 0 : 1;
