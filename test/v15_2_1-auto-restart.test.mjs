// test/v15_2_1-auto-restart.test.mjs
// v15.2.1 (owner): when the update is ready and the logger is idle, the modal says "Update complete... restarting
// Battery Logger in 3.. 2.. 1.." and then calls /api/update-restart by itself (the proven Restart now path).
// Never during a test or countdown; after the test it runs once Done and the alarm is acknowledged; a refused restart
// falls back to "Installs next time you open the logger".
// Run:  node --test --test-concurrency=1 test/v15_2_1-auto-restart.test.mjs
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { closeBrowser } from './harness.mjs';
import { FakeExe151, withExe, SEL, MIN } from './v15_1-fakeexe.mjs';

after(closeBrowser);
const overlay = async (L) => (await L.page.locator(SEL.overlay).innerText()).trim();
const restarts = (exe) => exe.count('/api/update-restart', 'POST');
async function toLogging(L, exe) { await L.step(1000); exe.unplug(); await L.step(4000); assert.equal(exe.S && exe.S.status, 'running', 'test started'); }

test('ready + idle: "Update complete..." counts 3.. → 3.. 2.. → 3.. 2.. 1.., then calls update-restart by itself', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  exe.setUpdate('ready');
  return withExe(exe, {}, async (L) => {
    await L.step(500);
    let o = await overlay(L);
    assert.match(o, /Update complete\.\.\./); assert.match(o, /restarting Battery Logger in 3\.\.(?! 2)/, o);
    assert.doesNotMatch(o, /Installs next time/, 'no "installs next time" while idle');
    assert.equal(await L.page.getByRole('button', { name: 'OK' }).count(), 0, 'no OK while idle');
    assert.ok(await L.page.isVisible(SEL.restart), 'Restart now skips the wait');
    await L.run(1000); assert.match(await overlay(L), /in 3\.\. 2\.\.(?! 1)/);
    await L.run(1000); assert.match(await overlay(L), /in 3\.\. 2\.\. 1\.\./);
    assert.equal(restarts(exe), 0, 'not before the countdown ends');
    await L.run(1200);
    assert.equal(restarts(exe), 1, 'update-restart called once by itself');
    assert.match(await overlay(L), /Restarting…/);
    await L.run(5000); assert.equal(restarts(exe), 1, 'only once');
    assert.deepEqual(L.consoleErrors, []);
  });
});

test('Restart now skips the countdown', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  exe.setUpdate('ready');
  return withExe(exe, {}, async (L) => {
    await L.step(500);
    await L.click(SEL.restart); await L.run(300);
    assert.equal(restarts(exe), 1);
    await L.run(4000); assert.equal(restarts(exe), 1, 'no second call from the countdown');
  });
});

test('no auto restart during a test or a countdown; the chip shows; it runs after Done + alarm OK', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(1000); exe.unplug(); await L.step(1000);
    exe.setUpdate('ready'); await L.step(1000);
    assert.match(await overlay(L), /All conditions met!/, 'countdown popup, no update modal');
    assert.doesNotMatch(await overlay(L), /Update complete/);
    await L.step(3000);
    assert.equal(exe.S && exe.S.status, 'running', 'test running');
    await L.step(10_000, 5000);
    assert.equal(restarts(exe), 0, 'never during the countdown or the test');
    assert.match(await L.page.locator(SEL.chip).innerText(), /installs after this test/);
    exe.at += MIN; exe.drain(15); exe.end('alarm'); await L.step(5000);
    assert.doesNotMatch(await overlay(L), /Update complete/, 'not while the alarm is unacknowledged');
    await L.step(5000); assert.equal(restarts(exe), 0);
    await L.click(SEL.alarmOk); await L.step(1000, 250);
    assert.match(await overlay(L), /Update complete\.\.\./, 'Done + alarm OK → the countdown');
    await L.run(3500);
    assert.equal(restarts(exe), 1, 'then the restart');
  });
});

test('a refused restart falls back to "Installs next time you open the logger"', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  exe.setUpdate('ready');
  const post = exe.post.bind(exe);
  exe.post = (p, b) => p === '/api/update-restart' ? { ok: false, error: 'Could not restart the logger.' } : post(p, b);
  return withExe(exe, {}, async (L) => {
    await L.step(500); await L.run(3500);
    assert.equal(restarts(exe), 1, 'tried once');
    const o = await overlay(L);
    assert.match(o, /Installs next time you open the logger\./); assert.match(o, /Could not restart the logger\./);
    assert.doesNotMatch(o, /Update complete/);
    await L.run(6000); assert.equal(restarts(exe), 1, 'no automatic retry');
  });
});
void toLogging;
