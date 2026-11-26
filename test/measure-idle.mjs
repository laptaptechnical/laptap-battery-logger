// test/measure-idle.mjs
// Idle-cost check for a logger page (default the v14 page in src/web, browser-only mode; LOGGER_FILE / ADAPTER to point elsewhere).
//   node test/measure-idle.mjs            → fake-clock counts (exact, ~10 s)
//   node test/measure-idle.mjs --cpu      → also real-time CPU, 120 s per case (~5 min)
// Writes test/results/idle-<file>.json and prints a table.
import fs from 'node:fs';
import path from 'node:path';
import { openLogger, closeBrowser, browser, PAGE, ROOT } from './harness.mjs';
const TARGET = process.env.LOGGER_FILE ? path.resolve(process.env.LOGGER_FILE) : PAGE;

const A = (await import(`./adapters/${process.env.ADAPTER || 'v14'}.mjs`)).default;
const MIN = 60_000;
const out = { file: path.basename(TARGET), measuredAt: new Date().toISOString(), fakeClock: {}, cpu: null };

async function window_(L, minutes) {
  await L.resetProbe();
  await L.run(minutes * MIN);
  const p = await L.probe();
  return { wakeupsPerMin: +(p.timerFires / minutes).toFixed(1), byInterval: p.timerFiresByMs, domMutationsPerMin: +(p.mutations / minutes).toFixed(1),
    storageWritesPerMin: +(p.storageWrites / minutes).toFixed(2), storageCharsPerMin: Math.round(p.storageBytes / minutes) };
}

// 1) open, not logging
{ const L = await openLogger({ file: TARGET }); out.fakeClock.openNotLogging = await window_(L, 10); await L.close(); }
// 2) logging, visible / hidden, plus growth of storage writes over an 8-hour run
{
  const L = await openLogger({ file: TARGET, level: 1 });
  await L.run(4500);   // v15: the page counts down and starts by itself (unplugged at 100%)
  out.fakeClock.loggingVisible = await window_(L, 10);
  await L.hide();
  out.fakeClock.loggingHidden = await window_(L, 10);
  const growth = {};
  let elapsed = 20;
  for (const at of [60, 240, 480]) {
    await L.run((at - elapsed - 1) * MIN); elapsed = at - 1;
    // drain 1% every 5 min so the log looks real
    await L.page.evaluate((lvl) => window.__bat.set({ level: lvl, silent: true }), Math.max(0.05, 1 - at / 500));
    const w = await window_(L, 1); elapsed = at;
    const logChars = (await L.content(A.log)).length;
    growth[`minute${at}`] = { storageCharsWrittenThatMinute: w.storageCharsPerMin, logChars };
  }
  out.fakeClock.storageGrowth = growth;
  await L.close();
}

// 3) real-time CPU (renderer main-thread task time) while logging with the page reported hidden
if (process.argv.includes('--cpu')) {
  const SECS = 120;
  const metrics = async (page) => { const s = await page.context().newCDPSession(page); await s.send('Performance.enable');
    return async () => Object.fromEntries((await s.send('Performance.getMetrics')).metrics.map(m => [m.name, m.value])); };
  async function cpuCase(name, setup) {
    const { page, close } = await setup();
    const get = await metrics(page); const a = await get();
    await page.waitForTimeout(SECS * 1000);
    const b = await get(); await close();
    const d = k => +(((b[k] || 0) - (a[k] || 0)) * 1000).toFixed(1);
    return { name, seconds: SECS, taskMs: d('TaskDuration'), scriptMs: d('ScriptDuration'), layoutMs: d('LayoutDuration'), styleMs: d('RecalcStyleDuration'),
      taskMsPerMin: +(d('TaskDuration') / (SECS / 60)).toFixed(1), cpuPercentOfOneCore: +((d('TaskDuration') / (SECS * 1000)) * 100).toFixed(3) };
  }
  out.cpu = [];
  out.cpu.push(await cpuCase('blank page (control)', async () => { const c = await (await browser()).newContext(); const p = await c.newPage(); await p.setContent('<p>x</p>'); return { page: p, close: () => c.close() }; }));
  out.cpu.push(await cpuCase('logger open, not logging', async () => { const L = await openLogger({ file: TARGET, level: 1, realClock: true }); return { page: L.page, close: L.close }; }));
  out.cpu.push(await cpuCase('logger logging, reported hidden', async () => { const L = await openLogger({ file: TARGET, level: 1, realClock: true }); await L.page.waitForTimeout(4500); await L.hide(); return { page: L.page, close: L.close }; }));
}

await closeBrowser();
fs.mkdirSync(path.join(ROOT, 'test', 'results'), { recursive: true });
const f = path.join(ROOT, 'test', 'results', `idle-${out.file.replace(/[^\w.-]+/g, '_')}.json`);
fs.writeFileSync(f, JSON.stringify(out, null, 2));
console.log(JSON.stringify(out, null, 2));
console.log('saved', f);
