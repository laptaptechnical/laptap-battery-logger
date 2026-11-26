// test/v15-log.test.mjs
// v15 live log (plan §2, E1, M1, I12), the visible-only 1 s tick (E7) and the update-status UI (§4 app.*).
// Run:  node --test --test-concurrency=1 test/v15-log.test.mjs
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { closeBrowser } from './harness.mjs';
import { FakeExe, withExe, SEL, MIN, settle } from './v15-fakeexe.mjs';

after(closeBrowser);
const rowTexts = (L) => L.page.locator(`${SEL.rows} tr`).allInnerTexts();
const errs = (L) => assert.deepEqual(L.consoleErrors, [], 'page errors');

// Start a test straight away: unplug at 99%, let the countdown run.
async function logging(L, exe) { await L.step(1000); exe.unplug(); await L.step(4000); assert.equal(exe.S && exe.S.status, 'running'); }
// n readings, one per minute, dropping 1% each.
async function readings(L, exe, n, pollEvery = 5000) {
  for (let i = 0; i < n; i++) { exe.at += MIN; exe.drain(Math.max(16, exe.pct - 1)); }
  await L.step(pollEvery, pollEvery);
}
const toSec = (t) => { const [h, m, s] = t.split(':').map(Number); return h * 3600 + m * 60 + s; };

test('every reading is a row, newest on top, the moment a poll brings it', () => {
  const exe = new FakeExe({ pct: 99 });
  return withExe(exe, {}, async (L) => {
    await logging(L, exe);
    await readings(L, exe, 12);
    const rows = await rowTexts(L);
    const readingRows = rows.filter(r => /\tReading\t/.test(r));
    assert.equal(readingRows.length, exe.samples.length - 1, `readings ${readingRows.length} vs samples ${exe.samples.length} (1 sample is shown as the start event row)`);
    const times = rows.map(r => toSec(r.split('\t')[0]));
    for (let i = 1; i < times.length; i++) assert.ok(times[i - 1] >= times[i], `row ${i} out of order: ${rows[i - 1]} / ${rows[i]}`);
    assert.match(await L.text(SEL.rowCount), new RegExp(`\\b${rows.length} rows\\b`));
    errs(L);
  });
});

test('an event and its own sample (same ms) show as ONE row with the sample\'s numbers (M1)', () => {
  const exe = new FakeExe({ pct: 99 });
  return withExe(exe, {}, async (L) => {
    await logging(L, exe);
    exe.at += MIN; exe.drain(97); exe.at += MIN; exe.plug(); const plugMs = exe.at;
    await L.step(5000, 5000);
    const rows = await rowTexts(L);
    const hhmmss = new Intl.DateTimeFormat('en-PH', { hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23', timeZone: 'Asia/Manila' }).format(plugMs);
    const atPlug = rows.filter(r => r.startsWith(hhmmss));
    assert.equal(atPlug.length, 1, `rows at ${hhmmss}: ${JSON.stringify(atPlug)}`);
    assert.match(atPlug[0], /Charger plugged in/);
  });
});

test('no duplicate rows: polls, a reload rebuild and since=0 give the same rows', () => {
  const exe = new FakeExe({ pct: 99 });
  return withExe(exe, {}, async (L) => {
    await logging(L, exe);
    await readings(L, exe, 8); await L.step(15_000, 5000);
    const before = await rowTexts(L);
    assert.equal(new Set(before).size, before.length, 'duplicate row after polls');
    await L.page.reload(); await settle(exe); await L.step(2000);
    const after_ = await rowTexts(L);
    assert.deepEqual(after_, before, 'rebuild after reload differs');
  });
});

test('scrolled down: new rows keep the reader\'s place, "Newest ↑" appears and jumps back', () => {
  const exe = new FakeExe({ pct: 99 });
  return withExe(exe, {}, async (L) => {
    await logging(L, exe);
    await readings(L, exe, 60);
    const sc = L.page.locator(SEL.scroll);
    await sc.evaluate(el => { el.scrollTop = 300; el.dispatchEvent(new Event('scroll')); });
    const firstVisible = () => sc.evaluate(el => { const top = el.getBoundingClientRect().top + 30; const r = [...el.querySelectorAll('tbody tr')].find(tr => tr.getBoundingClientRect().bottom > top); return r ? r.innerText : null; });
    const anchor = await firstVisible();
    await readings(L, exe, 5);
    assert.equal(await firstVisible(), anchor, 'the row under the reader moved');
    assert.ok(await L.page.isVisible(SEL.jump), '"Newest ↑" shown');
    await L.click(SEL.jump);
    assert.ok((await sc.evaluate(el => el.scrollTop)) < 4, 'jumped to the top');
    // The oldest row is still reachable by scrolling.
    await sc.evaluate(el => { el.scrollTop = el.scrollHeight; });
    const last = await L.page.locator(`${SEL.rows} tr`).last().innerText();
    assert.match(last, /Test started|Unplugged/);
  });
});

test('existing rows are never touched by a poll; idle polls write nothing to the table', () => {
  const exe = new FakeExe({ pct: 99 });
  return withExe(exe, {}, async (L) => {
    await logging(L, exe);
    await readings(L, exe, 5);
    await L.page.evaluate(() => {
      const tb = document.querySelector('#rows'); window.__rowMut = { added: 0, removed: 0, changed: 0 };
      new MutationObserver(list => list.forEach(m => { if (m.type === 'childList' && m.target === tb) { window.__rowMut.added += m.addedNodes.length; window.__rowMut.removed += m.removedNodes.length; } else window.__rowMut.changed++; }))
        .observe(tb, { subtree: true, childList: true, characterData: true, attributes: true });
    });
    await L.step(30_000, 5000);  // nothing new
    let m = await L.page.evaluate(() => window.__rowMut);
    assert.deepEqual(m, { added: 0, removed: 0, changed: 0 }, 'idle polls touched the table');
    await readings(L, exe, 3);
    m = await L.page.evaluate(() => window.__rowMut);
    assert.equal(m.added, 3); assert.equal(m.removed, 0);
    // Highlight class (if any) may be removed later; nothing else may change on old rows.
  });
});

test('1 s elapsed tick only while visible; hidden shows h:mm and has no 1 s timer', () => {
  const exe = new FakeExe({ pct: 99 });
  return withExe(exe, {}, async (L) => {
    await logging(L, exe);
    await L.resetProbe(); await L.step(10_000, 1000);
    let p = await L.probe();
    assert.ok((p.timerFiresByMs['1000'] || 0) >= 8, `visible: 1 s ticks ${JSON.stringify(p.timerFiresByMs)}`);
    const e1 = await L.text(SEL.elapsed); await L.step(3000, 1000);
    assert.notEqual(await L.text(SEL.elapsed), e1, 'elapsed advances each second while visible');
    assert.match(await L.text(SEL.elapsed), /^\d+:\d\d:\d\d$/);
    await L.hide(); await L.run(100);
    assert.match(await L.text(SEL.elapsed), /^\d+:\d\d$/, 'hidden: h:mm');
    await L.resetProbe(); await L.step(5 * MIN, 30_000);
    p = await L.probe();
    assert.ok(!p.timerFiresByMs['1000'], `hidden: no 1 s timer ${JSON.stringify(p.timerFiresByMs)}`);
    assert.ok(p.timerFires <= 12, `hidden 5 min: ${p.timerFires} wakeups`);
    assert.equal(p.mutations, 0, `hidden, nothing new: ${p.mutations} DOM writes`);
    await L.show(); await L.step(2000, 1000);
    assert.match(await L.text(SEL.elapsed), /^\d+:\d\d:\d\d$/, 'visible again: h:mm:ss');
  });
});

test('poll rates: Ready 1 s visible, Logging 5 s visible / 30 s hidden, Done 30 s', () => {
  const exe = new FakeExe({ pct: 99 });
  return withExe(exe, {}, async (L) => {
    const rate = async (ms, chunk) => { const a = exe.count('/api/state'); await L.step(ms, chunk); return exe.count('/api/state') - a; };
    const ready = await rate(10_000, 1000);
    assert.ok(ready >= 8 && ready <= 12, `Ready polls in 10 s: ${ready}`);
    exe.unplug(); await L.step(5000);
    assert.equal(exe.S.status, 'running');
    const logV = await rate(60_000, 5000);
    assert.ok(logV >= 10 && logV <= 14, `Logging visible polls/min: ${logV}`);
    await L.hide(); await L.run(100);
    const logH = await rate(5 * MIN, 30_000);
    assert.ok(logH >= 8 && logH <= 12, `Logging hidden polls in 5 min: ${logH}`);
    await L.show(); exe.end('stop'); await L.step(5000);
    const done = await rate(3 * MIN, 30_000);
    assert.ok(done >= 5 && done <= 8, `Done polls in 3 min: ${done}`);
  });
});

test('no pulse animation in LOGGING (E7)', () => {
  const exe = new FakeExe({ pct: 99 });
  return withExe(exe, {}, async (L) => {
    await logging(L, exe);
    const anims = await L.page.evaluate(() => document.getAnimations().filter(a => !(a.effect && a.effect.target && a.effect.target.closest && a.effect.target.closest('tr'))).length);
    assert.equal(anims, 0, `running animations outside log rows: ${anims}`);
  });
});

test('update UI: version shown; "Updated to v15.0.1" toast once per version; updateNote only in the tooltip', () => {
  const exe = new FakeExe({ pct: 99, app: { version: '15.0.1', release: 2, updatedFrom: '15.0.0', updateNote: 'Folder is read-only' } });
  return withExe(exe, {}, async (L) => {
    await L.step(2000);
    assert.match(await L.text(SEL.ver), /15\.0/);
    assert.ok(await L.page.isVisible(SEL.toast), 'toast shown');
    assert.match(await L.text(SEL.toast), /Updated to v15\.0\.1/);
    const title = await L.page.locator(SEL.ver).getAttribute('title');
    assert.match(title || '', /Folder is read-only/, 'updateNote in the version tooltip');
    assert.doesNotMatch(await L.page.locator('body').innerText(), /Folder is read-only/, 'updateNote not shown as page text');
    await L.page.reload(); await settle(exe); await L.step(2000);
    assert.ok(!(await L.page.isVisible(SEL.toast)), 'toast shown only once per version (sessionStorage)');
  });
});

test('no update: no toast, no note', () => {
  const exe = new FakeExe({ pct: 99 });
  return withExe(exe, {}, async (L) => {
    await L.step(2000);
    assert.ok(!(await L.page.isVisible(SEL.toast)));
    assert.match(await L.text(SEL.ver), /15\.0/);
    errs(L);
  });
});
