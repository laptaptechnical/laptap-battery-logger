// test/v15_1-screens.test.mjs
// v15.1 screens (plan v15.1 §6.1, §6.2, §6.4, G3–G5): every nextStep stage, the countdown popup with the owner's exact
// words, Cancel → Start (G4), re-plug cancels and stays armed (G5), Logging, Paused, alarm and Done.
// Run:  node --test --test-concurrency=1 test/v15_1-screens.test.mjs
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { closeBrowser } from './harness.mjs';
import { FakeExe151, withExe, SEL, MIN } from './v15_1-fakeexe.mjs';

after(closeBrowser);
const stage = (L) => L.text(SEL.stage);
const overlay = async (L) => (await L.page.locator(SEL.overlay).innerText()).trim();
const errs = (L) => assert.deepEqual(L.consoleErrors, [], 'page errors');
const vis = (L, sel) => L.page.isVisible(sel);
async function toLogging(L, exe) { await L.step(1000); exe.unplug(); await L.step(4000); assert.equal(exe.S && exe.S.status, 'running', 'test started'); }

// ---------------- 6.1 Ready stages, one per nextStep ----------------
test('nextStep battery (G3): "No battery found" stage, no error box', () => {
  const exe = new FakeExe151({ battery: false, pct: 0, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(2000);
    const s = await stage(L);
    assert.match(s, /No battery found/);
    assert.match(s, /This laptop can't be tested\. Check the battery is fitted, then open the logger again\./);
    assert.equal(L.dialogs.length, 0);
    errs(L);
  });
});
test('nextStep charge, unplugged: big % + "Plug in and charge to 96%"', () => {
  const exe = new FakeExe151({ pct: 41, ac: false });
  return withExe(exe, {}, async (L) => {
    await L.step(2000);
    const s = await stage(L);
    assert.match(s, /\b41\s*%/); assert.match(s, /Plug in and charge to 96%/);
    assert.match(s, /The charger is unplugged and the battery is too low to start\./);
  });
});
test('nextStep charge, plugged in: "Charging… wait for 96%" + "<n>% to go"', () => {
  const exe = new FakeExe151({ pct: 82, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(2000);
    const s = await stage(L);
    assert.match(s, /\b82\s*%/); assert.match(s, /Charging… wait for 96%/); assert.match(s, /14% to go\. Leave the charger in\./);
  });
});
test('nextStep unplug (row 31: 100% on AC): "Unplug the charger to start"', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(2000);
    const s = await stage(L);
    assert.match(s, /\b100\s*%/); assert.match(s, /Unplug the charger to start/);
    assert.match(s, /The test starts by itself 3 seconds after you unplug\./);
    assert.match(s, /Battery found/); assert.match(s, /Charger unplugged/);
    assert.equal(exe.countdownEndsMs, null);
  });
});
test('nextStep wait-update: "Wait. Updating the logger" + "Downloading v15.1.0…"', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  exe.setUpdate('downloading', { version: '15.1.0', bytes: 120000, total: 190000, percent: 62 });
  return withExe(exe, {}, async (L) => {
    await L.step(2000);
    const s = await stage(L);
    assert.match(s, /Wait\. Updating the logger/);
    assert.match(s, /Downloading v15\.1\.0\. The test can start as soon as it's done\./);
  });
});

// ---------------- countdown popup (F6) ----------------
test('unplug → popup "All conditions met!" / "Starting battery logging in 3..." → "3... 2..." → "3... 2... 1..." → LOGGING', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(1000); exe.unplug(); await L.step(1000);
    const o1 = await overlay(L);
    assert.match(o1, /All conditions met!/);
    assert.match(o1, /Starting battery logging in 3\.\.\.(?! 2)/, o1);
    assert.match(o1, /Plugging the charger back in also cancels\./);
    assert.ok(await vis(L, SEL.cancel), 'Cancel on the popup');
    assert.match(await stage(L), /Unplugged\. Ready\./);
    await L.step(1000); assert.match(await overlay(L), /Starting battery logging in 3\.\.\. 2\.\.\.(?! 1)/);
    await L.step(1000); assert.match(await overlay(L), /Starting battery logging in 3\.\.\. 2\.\.\. 1\.\.\./);
    await L.step(2000);
    assert.equal(exe.S && exe.S.status, 'running');
    assert.doesNotMatch(await overlay(L), /All conditions met!/, 'popup closed');
    assert.match(await stage(L), /LOGGING/);
    errs(L);
  });
});
test('Cancel → "Unplugged. Ready." + big Start (G4); Start → /api/arm → popup again', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(1000); exe.unplug(); await L.step(1000);
    await L.click(SEL.cancel); await L.step(1000);
    assert.equal(exe.count('/api/cancel', 'POST'), 1);
    assert.doesNotMatch(await overlay(L), /All conditions met!/);
    assert.match(await stage(L), /Unplugged\. Ready\./);
    assert.ok(await vis(L, SEL.start), 'Start shown');
    await L.step(5000); assert.equal(exe.S, null, 'no auto-start after Cancel');
    await L.click(SEL.start); await L.step(1000);
    assert.equal(exe.count('/api/arm', 'POST'), 1);
    assert.match(await overlay(L), /All conditions met!/);
  });
});
test('re-plug during the countdown cancels it and stays armed (G5): unplug again re-opens the popup', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(1000); exe.unplug(); await L.step(1000);
    assert.match(await overlay(L), /All conditions met!/);
    exe.plug(); await L.step(1000);
    assert.doesNotMatch(await overlay(L), /All conditions met!/);
    assert.match(await stage(L), /Unplug the charger to start/);
    assert.equal(exe.armed, true);
    exe.unplug(); await L.step(1000);
    assert.match(await overlay(L), /All conditions met!/, 'popup again, no click');
    assert.equal(exe.count('/api/arm', 'POST'), 0);
  });
});
test('the update hold (§2.2, G2): verifying holds the countdown; ready does not', async () => {
  const exe = new FakeExe151({ pct: 100, ac: false });
  exe.latch = { ms: exe.at, pct: 100, mwh: exe.mwh() };
  exe.setUpdate('verifying');
  await withExe(exe, {}, async (L) => {
    await L.step(4000);
    assert.equal(exe.countdownEndsMs, null); assert.equal(exe.S, null);
    assert.doesNotMatch(await overlay(L), /All conditions met!/);
  });
  const exe2 = new FakeExe151({ pct: 100, ac: false });
  exe2.latch = { ms: exe2.at, pct: 100, mwh: exe2.mwh() };
  exe2.setUpdate('ready');
  await withExe(exe2, {}, async (L) => {
    await L.step(1000);
    assert.ok(exe2.countdownEndsMs != null || exe2.S, 'ready never holds the countdown');
  });
});
test('a download holds the countdown for at most 45 s (G2, I2)', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  exe.setUpdate('downloading', { percent: 5, bytes: 1, total: 20 });
  return withExe(exe, {}, async (L) => {
    await L.step(1000);
    assert.match(await stage(L), /Wait\. Updating the logger/);
    await L.click('#hideUpd').catch(() => {}); // hide the modal if shown
    exe.unplug(); await L.step(5000);
    assert.equal(exe.S, null, 'held while downloading');
    await L.step(35_000, 5000); assert.equal(exe.S, null, 'still held at 40 s'); await L.step(15_000, 1000);
    assert.equal(exe.S && exe.S.status, 'running', 'hold lifted after 45 s, test started');
  });
});

// ---------------- 6.2 during a test ----------------
test('Logging: green LOGGING band, elapsed, the 4 tiles, Stop test; no chart, no animation', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await toLogging(L, exe);
    for (let i = 0; i < 6; i++) { exe.at += MIN; exe.drain(99 - i); }
    await L.step(5000, 5000);
    const s = await stage(L);
    assert.match(s, /LOGGING/); assert.match(s, /elapsed/);
    assert.match(s, /Alarm at 15%\. Plug in soon \(recommended\); logging continues until you stop it\./);
    for (const k of ['Charge', 'Time to alarm', 'Power', 'Health']) assert.match(s, new RegExp(k));
    assert.match(s, /estimate, gets better over time/); assert.match(s, /avg 9\.9 W/);
    assert.ok(await vis(L, SEL.stop)); assert.match(await L.text(SEL.stop), /Stop test/);
    assert.equal(await L.page.locator('#chart, .chart svg').count(), 0, 'no chart on the Logging screen (G6)');
    const anims = await L.page.evaluate(() => document.getAnimations().filter(a => !(a.effect && a.effect.target && a.effect.target.closest && a.effect.target.closest('tr'))).length);
    assert.equal(anims, 0, 'nothing animates during Logging');
    errs(L);
  });
});
test('Paused: "PAUSED", frozen clock, "timer stopped · paused 0:02:xx", "Charger plugged in. Unplug to continue."', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await toLogging(L, exe);
    exe.at += MIN; exe.drain(98); exe.plug(); await L.step(2 * MIN + 10_000, 5000);
    const s = await stage(L);
    assert.match(s, /PAUSED/); assert.match(s, /timer stopped · paused 0:02:\d\d/);
    assert.match(s, /Charger plugged in\. Unplug to continue\./);
    exe.unplug(); await L.step(5000);
    assert.match(await stage(L), /LOGGING/);
  });
});
test('Stop test asks in the page; End test → Done screen', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await toLogging(L, exe);
    await L.click(SEL.stop); assert.match(await stage(L), /End the test now\?/);
    await L.click(SEL.stopNo); assert.equal(exe.S.status, 'running');
    await L.click(SEL.stop); await L.click(SEL.stopYes); await L.step(2000);
    const s = await stage(L);
    assert.match(s, /Test finished/); assert.match(s, /Report saved to the Desktop/);
  });
});

// ---------------- alarm while Logging ----------------
test('alarm box while logging: "15%", charger recommendation, logging continues, OK → ack', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await toLogging(L, exe);
    exe.at += MIN; exe.drain(15); exe.end('alarm'); await L.step(5000);
    const o = await overlay(L);
    assert.match(o, /15%/); assert.match(o, /Plug in the charger soon \(recommended\)/); assert.match(o, /Logging continues until you stop it/);
    assert.equal(exe.S.status, 'running');
    await L.click(SEL.alarmOk); await L.run(300);
    assert.ok(exe.count('/api/ack-alarm', 'POST') >= 1);
    assert.doesNotMatch(await overlay(L), /Plug in the charger soon/);
    assert.match(await L.text('#bigWord'), /LOGGING/);
  });
});
test('Done: runtime, range, saved files, facts grid; Open report → /api/open-report; New test → /api/new', () => {
  const exe = new FakeExe151({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await toLogging(L, exe);
    for (let i = 0; i < 6; i++) { exe.at += 10 * MIN; exe.drain(90 - i * 10); }
    exe.end('stop'); await L.step(5000);
    const s = await stage(L);
    assert.match(s, /Test finished/); assert.match(s, /Report saved to the Desktop/);
    assert.match(s, /Battery 5CB4190FVX 2026-11-26 0900\.html/);
    for (const k of ['est. 100', 'Health', 'Average draw', 'Cycles', 'Pauses', 'Brightness']) assert.match(s, new RegExp(k));
    assert.doesNotMatch(s, /undefined|NaN|null/);
    await L.page.getByRole('button', { name: 'Open report' }).click(); await L.run(300);
    assert.equal(exe.count('/api/open-report', 'POST'), 1);
    await L.click(SEL.newBtn); await L.step(1000);
    assert.equal(exe.count('/api/new', 'POST'), 1);
    assert.match(await stage(L), /Unplug the charger to start|Plug in and charge|Charging/);
  });
});
