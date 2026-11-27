// test/v15_1-update.test.mjs
// v15.1.0 update UI (plan v15.1 §2.3, §4.3, G1, G7) as changed by v15.2.1 (owner request): one modal per
// app.update.phase; ready + idle = "Update complete..." + a 3 s "restarting Battery Logger in 3.. 2.. 1.." + Restart now,
// then an automatic /api/update-restart; a refused restart falls back to "Installs next time you open the logger." + OK; failed → Later / Try again; the "Updated to" toast; the header chip during a test.
// Run:  node --test --test-concurrency=1 test/v15_1-update.test.mjs
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { closeBrowser } from './harness.mjs';
import { FakeExe151, withExe, SEL, MIN, settle } from './v15_1-fakeexe.mjs';

after(closeBrowser);
const overlay = async (L) => (await L.page.locator(SEL.overlay).innerText()).trim();
const chip = async (L) => (await L.page.isVisible(SEL.chip)) ? (await L.page.locator(SEL.chip).innerText()).trim() : '';
const errs = (L) => assert.deepEqual(L.consoleErrors, [], 'page errors');
async function toLogging(L, exe) { await L.step(1000); exe.unplug(); await L.step(4000); assert.equal(exe.S && exe.S.status, 'running', 'test started'); }

test('checking for < 1 s shows nothing; > 1 s shows "Checking for updates…" (G1)', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  exe.setUpdate('checking', { version: null });
  return withExe(exe, {}, async (L) => {
    await L.run(400);
    assert.doesNotMatch(await overlay(L), /Checking for updates/, 'no flash under 1 s');
    await L.step(2000);
    assert.match(await overlay(L), /Checking for updates…/);
    exe.setUpdate('idle', { version: null }); await L.step(1000);
    assert.doesNotMatch(await overlay(L), /Checking for updates/, 'closes when idle');
  });
});

test('downloading: "Updating the logger", versions, steps, %, MB; Hide → header chip "Downloading v15.1.0 62%"', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  exe.setUpdate('downloading', { version: '15.1.0', bytes: 3_000_000, total: 4_600_000, percent: 62 });
  return withExe(exe, {}, async (L) => {
    await L.step(2000);
    const o = await overlay(L);
    assert.match(o, /Updating the logger/); assert.match(o, /v15\.0\.1 → v15\.1\.0/);
    for (const w of ['Download', 'Check', 'Install']) assert.match(o, new RegExp(w));
    assert.doesNotMatch(o, /Restart/, 'G7: the step says Install');
    assert.match(o, /Downloading update 15\.1\.0…/); assert.match(o, /62%/); assert.match(o, /3\.0 of 4\.6 MB/);
    await L.click(SEL.hide); await L.step(1000);
    assert.doesNotMatch(await overlay(L), /Updating the logger/, 'hidden');
    assert.match(await chip(L), /Downloading v15\.1\.0.*62%/s);
    exe.app.update.percent = 80; await L.step(1000);
    assert.match(await chip(L), /80%/, 'chip follows the percent');
    errs(L);
  });
});

test('verifying: "Checking the download…" / "Checking it\'s signed by Laptap"', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  exe.setUpdate('verifying');
  return withExe(exe, {}, async (L) => {
    await L.step(2000);
    const o = await overlay(L);
    assert.match(o, /Checking the download…/); assert.match(o, /Checking it's signed by Laptap/);
  });
});

// v15.2.1 (owner request): ready + idle restarts by itself after 3 s; "Installs next time" + OK is only the fallback.
test('ready (idle, v15.2.1): "Update complete..." + "restarting Battery Logger in 3.. 2.. 1.." + Restart now, no OK; posts restart after 3 s', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  exe.setUpdate('ready');
  return withExe(exe, {}, async (L) => {
    await L.step(1000);
    let o = await overlay(L);
    assert.match(o, /Update complete\.\.\./);
    assert.match(o, /restarting Battery Logger in 3\.\./);
    assert.ok(await L.page.isVisible(SEL.restart), 'Restart now');
    assert.equal(await L.page.getByRole('button', { name: 'OK' }).count(), 0, 'no OK while it counts down');
    await L.step(1000); o = await overlay(L);
    assert.match(o, /restarting Battery Logger in 3\.\. 2\.\./, `words grow: ${o}`);
    await L.step(1000); o = await overlay(L);
    assert.match(o, /restarting Battery Logger in 3\.\. 2\.\. 1\.\.|Restarting…/, `words grow to 1.. or it has posted: ${o}`);
    await L.step(2000);
    assert.equal(exe.count('/api/update-restart', 'POST'), 1, 'posted by itself after 3 s');
    assert.match(await overlay(L), /Restarting…/);
  });
});

test('ready (idle), restart refused → falls back to "Installs next time you open the logger." + OK', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  exe.setUpdate('ready'); exe.refuseRestart = true;
  return withExe(exe, {}, async (L) => {
    await L.step(5000);
    assert.ok(exe.count('/api/update-restart', 'POST') >= 1, 'tried');
    const o = await overlay(L);
    assert.match(o, /Installs next time you open the logger\./);
    assert.ok(await L.page.getByRole('button', { name: 'OK' }).isVisible(), 'OK');
    await L.page.getByRole('button', { name: 'OK' }).click(); await L.step(1000);
    assert.doesNotMatch(await overlay(L), /Installs next time you open the logger/, 'OK closes it');
    assert.match(await chip(L), /v15\.1\.0 installs next time/);
    const n = exe.count('/api/update-restart', 'POST'); await L.step(5000);
    assert.equal(exe.count('/api/update-restart', 'POST'), n, 'no retry loop after the fallback');
  });
});

test('Restart now (idle) → POST /api/update-restart → "Restarting…" with the Designer words', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  exe.setUpdate('ready');
  return withExe(exe, {}, async (L) => {
    await L.step(1000);
    await L.click(SEL.restart); await L.run(300);
    assert.equal(exe.count('/api/update-restart', 'POST'), 1);
    const o = await overlay(L);
    assert.match(o, /Restarting…/); assert.match(o, /This window closes and v15\.1\.0 opens by itself\./);
  });
});

test('not idle: Done with an unacknowledged alarm → no modal countdown, no restart; after OK the 3 s auto restart runs', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await toLogging(L, exe);
    exe.at += MIN; exe.drain(15); exe.end('alarm'); exe.setUpdate('ready'); await L.step(5000);
    assert.ok(!(await L.page.isVisible(SEL.restart)), 'hidden while the alarm is unacknowledged');
    assert.equal(exe.count('/api/update-restart', 'POST'), 0, 'no restart while not idle');
    await L.click(SEL.alarmOk); await L.step(1000);
    assert.ok(await L.page.isVisible(SEL.restart), 'shown once idle (Done, alarm acknowledged)');
    await L.step(4000);
    assert.equal(exe.count('/api/update-restart', 'POST'), 1, 'auto restart after the alarm OK');
  });
});

test('not idle: countdown or a test → chip only, never a restart', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await toLogging(L, exe);
    exe.setUpdate('ready'); await L.step(15_000, 5000);
    assert.match(await chip(L), /Update v15\.1\.0 installs after this test/);
    assert.equal(exe.count('/api/update-restart', 'POST'), 0);
  });
});

test('failed: "Update didn\'t download" / "Still on v15.0.1…"; Later hides; Try again → /api/update-retry', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  exe.setUpdate('failed', { note: 'Update check failed (no internet?)' });
  return withExe(exe, {}, async (L) => {
    await L.step(2000);
    let o = await overlay(L);
    assert.match(o, /Update didn't download/); assert.match(o, /Still on v15\.0\.1\. Testing works as normal\./);
    await L.click(SEL.retry); await L.step(1000);
    assert.equal(exe.count('/api/update-retry', 'POST'), 1);
    exe.setUpdate('failed', { note: 'x' }); await L.step(2000);
    assert.match(await overlay(L), /Update didn't download/);
    await L.page.getByRole('button', { name: 'Later' }).click(); await L.step(2000);
    assert.doesNotMatch(await overlay(L), /Update didn't download/, 'Later hides it for this start');
  });
});

test('after an update: toast "Updated to v15.1.0" once per version (sessionStorage)', () => {
  const exe = new FakeExe151({ pct: 100, ac: true, app: { version: '15.1.0', release: 4, updatedFrom: '15.0.1' } });
  return withExe(exe, {}, async (L) => {
    await L.step(1000);
    assert.match(await overlay(L) + await L.page.locator('body').innerText(), /Updated to v15\.1\.0/);
    await L.page.reload(); await settle(exe); await L.step(1000);
    assert.doesNotMatch(await L.page.locator('body').innerText(), /Updated to v15\.1\.0/, 'not again after a reload');
    assert.match(await L.text(SEL.ver), /15\.1\.0/);
  });
});

test('update during a test: no modal, header chip "Update v15.1.0 installs after this test"', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await toLogging(L, exe);
    for (const ph of ['downloading', 'verifying', 'ready']) {
      exe.setUpdate(ph, { percent: 50, bytes: 1, total: 2 }); await L.step(5000, 5000);
      assert.doesNotMatch(await overlay(L), /Updating the logger|Checking the download|Update ready/, `no modal in ${ph}`);
      assert.match(await chip(L), /Update v15\.1\.0 installs after this test/, `chip in ${ph}`);
    }
    assert.equal(exe.count('/api/update-restart', 'POST'), 0, 'no restart during a test');
  });
});

void MIN;
