// Battery-Logger/test/measure-bar.mjs
// Where does the action bar end at 1024×600 once a test is done? (browser-only mode, finished test with rows)
import path from 'node:path';
import { openLogger, closeBrowser, ROOT } from './harness.mjs';
const PAGE = path.join(ROOT, 'src', 'web', 'index.html');
const L = await openLogger({ file: PAGE, level: 1 });
await L.page.setViewportSize({ width: 1024, height: 600 });
await L.click('#startBtn');
for (let i = 0; i < 30; i++) { await L.bat.set({ level: 1 - i / 100 }); await L.run(60_000); }
await L.click('#stopBtn'); await L.click('#stopYes'); await L.run(6000);
const m = await L.page.evaluate(() => {
  const r = (s) => { const b = document.querySelector(s).getBoundingClientRect(); return Math.round(b.bottom); };
  return { done: document.body.classList.contains('done'), cardShown: !document.getElementById('reportBox').hidden, kvRows: document.querySelectorAll('#reportBox .kv div').length,
    barBottom: r('.bar'), chartH: Math.round(document.querySelector('.chart svg').getBoundingClientRect().height), tableH: Math.round(document.querySelector('.tablebox').getBoundingClientRect().height),
    pageH: document.documentElement.scrollHeight, viewport: innerHeight };
});
console.log(JSON.stringify(m));
await L.close(); await closeBrowser();
