// Battery-Logger/test/harness-selftest.test.mjs
// Positive controls: prove the fake battery, fake clock and probes work, so a bug-test FAIL is the logger, not the harness.
// These must PASS on v13 (they test things v13 does right).
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { openLogger, closeBrowser } from './harness.mjs';

const A = (await import(`./adapters/${process.env.ADAPTER || 'v13'}.mjs`)).default;
after(closeBrowser);
const MIN = 60_000;

test('self: fake battery level reaches the card', async () => {
  const L = await openLogger({ level: 0.63 });
  assert.equal((await L.text(A.percent)).trim(), '63%');
  await L.bat.set({ level: 0.58 });
  assert.equal((await L.text(A.percent)).trim(), '58%');
  await L.close();
});

test('self: fake clock drives checkpoints (1 at start + 1 per minute)', async () => {
  const L = await openLogger({ level: 0.9 });
  await L.click(A.start);
  await L.run(5 * MIN);
  const t = await L.content(A.log);
  assert.equal((t.match(/Checkpoint:/g) || []).length, 6, t);
  assert.match(t, /C: 90%/);
  await L.close();
});

test('self: lid-closed sleep fires the missed interval only once', async () => {
  const L = await openLogger({});
  await L.click(A.start);
  await L.sleep(30 * MIN);
  assert.equal(((await L.content(A.log)).match(/Checkpoint:/g) || []).length, 2);
  await L.close();
});

test('self: probes count timers, storage and audio; no real audio', async () => {
  const L = await openLogger({ level: 0.5 });
  await L.click(A.start);
  await L.resetProbe();
  await L.run(1 * MIN);
  const p = await L.probe();
  assert.ok(p.timerFires >= 60, JSON.stringify(p));
  assert.ok(p.storageWrites >= 1, JSON.stringify(p));
  assert.equal(await L.page.evaluate(() => typeof new AudioContext().createOscillator().start), 'function');
  assert.match(await L.page.evaluate(() => AudioContext.name), /Fake/);
  await L.close();
});

test('self: visibility override works', async () => {
  const L = await openLogger({});
  await L.hide();
  assert.equal(await L.page.evaluate(() => document.visibilityState), 'hidden');
  await L.close();
});
