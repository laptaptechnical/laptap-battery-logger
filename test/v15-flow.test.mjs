// test/v15-flow.test.mjs
// v15 start flow (plan §3, E2/E6), in v15.1's words: checklist → popup "3... 2... 1..." → LOGGING; Cancel / Start; a condition dropping during the
// countdown; the unplug latch; PAUSED on the charger; Done → New test; {ok:false} answers re-poll and never assume.
// Run:  node --test --test-concurrency=1 test/v15-flow.test.mjs
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { closeBrowser } from './harness.mjs';
import { FakeExe, withExe, SEL, MIN } from './v15-fakeexe.mjs';

after(closeBrowser);
const band = (L) => L.text(SEL.band);
const errs = (L) => assert.deepEqual(L.consoleErrors, [], 'page errors');
const visible = (L, sel) => L.page.isVisible(sel);

test('Ready: checklist shows each condition; nothing starts while plugged in', () => {
  const exe = new FakeExe({ pct: 100, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(3000);
    const b = await band(L);
    assert.match(b, /Unplug the charger to start/);
    assert.match(b, /Battery found/); assert.match(b, /Charged to 96%\+/); assert.match(b, /Charger unplugged/);
    assert.equal(await L.page.locator(`${SEL.cBat}.ok`).count(), 1, 'battery ✓');
    assert.equal(await L.page.locator(`${SEL.cPct}.ok`).count(), 1, '96% ✓');
    assert.equal(await L.page.locator(`${SEL.cAc}.ok`).count(), 0, 'charger not yet ✓');
    assert.equal(exe.S, null); assert.equal(exe.countdownEndsMs, null);
    assert.ok(!(await visible(L, SEL.start)), 'no Start button in the normal flow');
    errs(L);
  });
});

test('Ready below 96%: "Charging… wait for 96%" · "9% to go"', () => {
  const exe = new FakeExe({ pct: 87, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(2000);
    assert.match(await band(L), /Charging… wait for 96%/); assert.match(await band(L), /9% to go/);
    assert.equal(await L.page.locator(`${SEL.cPct}.ok`).count(), 0);
  });
});

test('unplug → popup "Starting battery logging in 3..." → "3... 2..." → "3... 2... 1..." → LOGGING', () => {
  const exe = new FakeExe({ pct: 99, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(1000);
    exe.unplug(); await L.step(1000);
    const seen = [await band(L)];
    await L.step(1000); seen.push(await band(L));
    await L.step(1000); seen.push(await band(L));
    assert.match(seen[0], /All conditions met!/, seen[0]); assert.match(seen[0], /Starting battery logging in 3\.\.\.(?! 2)/, seen[0]);
    assert.match(seen[1], /in 3\.\.\. 2\.\.\.(?! 1)/, seen[1]); assert.match(seen[2], /in 3\.\.\. 2\.\.\. 1\.\.\./, seen[2]);
    assert.ok(await visible(L, SEL.cancel), 'Cancel shown during the countdown');
    await L.step(2000);
    assert.equal(exe.S && exe.S.status, 'running');
    const b = await band(L);
    assert.match(b, /LOGGING/); assert.match(b, /\d+:\d\d:\d\d/);
    assert.equal(exe.count('/api/start'), 0, 'the page never calls /api/start');
    errs(L);
  });
});

test('Cancel → Ready with Start shown; Start → /api/arm → countdown again → LOGGING', () => {
  const exe = new FakeExe({ pct: 99, ac: false });
  exe.latch = { ms: exe.at, pct: 99, mwh: exe.mwh() };
  return withExe(exe, {}, async (L) => {
    await L.step(1000);
    assert.match(await band(L), /Starting battery logging in/);
    await L.click(SEL.cancel); await L.step(1000);
    assert.equal(exe.count('/api/cancel', 'POST'), 1);
    assert.equal(exe.armed, false);
    assert.doesNotMatch(await band(L), /Starting battery logging in/);
    assert.ok(await visible(L, SEL.start), 'Start appears after Cancel');
    await L.step(5000);
    assert.equal(exe.S, null, 'no auto-start after Cancel');
    await L.click(SEL.start); await L.step(1000);
    assert.equal(exe.count('/api/arm', 'POST'), 1);
    assert.match(await band(L), /Starting battery logging in/);
    await L.step(4000);
    assert.equal(exe.S && exe.S.status, 'running');
    assert.notEqual(exe.S.segments[0].startMs, exe.latch.ms, 'after Cancel + Start the test starts at the countdown end, not the latch');
  });
});

test('re-plugging during the countdown goes back to the checklist', () => {
  const exe = new FakeExe({ pct: 99, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(1000); exe.unplug(); await L.step(1000);
    assert.match(await band(L), /Starting battery logging in/);
    exe.plug(); await L.step(1000);
    assert.doesNotMatch(await band(L), /Starting battery logging in/);
    assert.equal(await L.page.locator(`${SEL.cAc}.ok`).count(), 0);
    await L.step(4000);
    assert.equal(exe.S, null);
  });
});

test('below 96% (plugged in) during the countdown goes back to the checklist', () => {
  const exe = new FakeExe({ pct: 96, ac: false });
  return withExe(exe, {}, async (L) => {
    // Unplugged without a latch (e.g. opened already unplugged) → the 96% check is not met: never counts down.
    await L.step(4000);
    assert.equal(exe.S, null); assert.doesNotMatch(await band(L), /Starting battery logging in/);
    exe.plug(); exe.setPct(96); await L.step(1000); exe.unplug(); await L.step(1000);
    assert.match(await band(L), /Starting battery logging in/);
    exe.plug(); exe.setPct(95); await L.step(1000);
    assert.match(await band(L), /Charging… wait for 96%/); assert.match(await band(L), /1% to go/);
    assert.equal(exe.S, null);
  });
});

test('latch (E6): unplugged at 96%, reads 95% by the end → still starts, at the latched 96%', () => {
  const exe = new FakeExe({ pct: 96, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(1000);
    exe.unplug(); const latchMs = exe.at;
    await L.step(1000); exe.setPct(95); await L.step(3000);
    assert.equal(exe.S && exe.S.status, 'running', 'started');
    assert.equal(exe.S.segments[0].startPct, 96); assert.equal(exe.S.segments[0].startMs, latchMs);
    assert.match(await band(L), /LOGGING/);
  });
});

test('no poll in 3 s → no countdown (hidden page in Ready makes no polls)', () => {
  const exe = new FakeExe({ pct: 99, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(2000);
    await L.hide(); const before = exe.count('/api/state');
    exe.unplug(); await L.step(10_000);
    assert.equal(exe.count('/api/state'), before, 'no polls while hidden in Ready');
    assert.equal(exe.S, null, 'no auto-start while nobody is looking');
    await L.show(); await L.step(5000);
    assert.equal(exe.S && exe.S.status, 'running', 'starts once visible again');
  });
});

test('PAUSED on the charger: amber band, elapsed frozen, "Unplug to continue"; unplug resumes', () => {
  const exe = new FakeExe({ pct: 99, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(1000); exe.unplug(); await L.step(5000);
    assert.equal(exe.S.status, 'running');
    await L.step(60_000, 5000); exe.drain(97);
    exe.plug(); await L.step(5000);
    const b = await band(L);
    assert.match(b, /PAUSED/); assert.match(b, /Charger plugged in\. Unplug to continue\./); assert.match(b, /timer stopped/);
    const e1 = await L.text(SEL.elapsed); await L.step(60_000, 5000);
    assert.equal(await L.text(SEL.elapsed), e1, 'elapsed frozen while paused');
    exe.unplug(); await L.step(5000);
    assert.match(await band(L), /LOGGING/); assert.doesNotMatch(await band(L), /PAUSED/);
  });
});

test('Stop asks in the page, then Done: "Test finished" + reason + "report saved to the Desktop"; New test → Ready (armed)', () => {
  const exe = new FakeExe({ pct: 99, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(1000); exe.unplug(); await L.step(5000);
    await L.click(SEL.stop);
    assert.match(await band(L), /End the test now\?/);
    await L.click(SEL.stopNo); assert.equal(exe.S.status, 'running');
    await L.click(SEL.stop); await L.click(SEL.stopYes); await L.step(1000);
    const b = await band(L);
    assert.match(b, /Test finished/); assert.match(b, /Stopped early/); assert.match(b, /Report saved to the Desktop/);
    assert.ok(await visible(L, SEL.open) && await visible(L, SEL.newBtn), 'Open report + New test');
    await L.step(5000);
    assert.equal(exe.S.status, 'done', 'never auto-arms while Done shows');
    await L.click(SEL.newBtn); await L.step(1000);
    assert.equal(exe.count('/api/new', 'POST'), 1);
    assert.equal(exe.armed, true); assert.equal(exe.S, null);
    assert.match(await band(L), /Battery found/); assert.doesNotMatch(await band(L), /Test finished/);
    errs(L);
  });
});

test('{ok:false} from the exe: the page re-polls and shows the exe\'s state, never an assumed one', () => {
  const exe = new FakeExe({ pct: 99, ac: false });
  exe.latch = { ms: exe.at, pct: 99, mwh: exe.mwh() };
  return withExe(exe, {}, async (L) => {
    await L.step(1000);
    exe.refuse = 'A test is already running.';
    const polls = exe.count('/api/state');
    await L.click(SEL.cancel); await L.run(300);
    assert.ok(exe.count('/api/state') > polls, 're-polled after the refusal');
    assert.ok(exe.countdownEndsMs != null || exe.S, 'exe still counting down (refused)');
    await L.step(4000);
    assert.equal(exe.S && exe.S.status, 'running');
    assert.match(await band(L), /LOGGING/, 'page follows the exe, not its own guess');
  });
});

test('alarm in LOGGING: warning appears while logging continues until manual stop', () => {
  const exe = new FakeExe({ pct: 99, ac: true });
  return withExe(exe, {}, async (L) => {
    await L.step(1000); exe.unplug(); await L.step(5000);
    exe.drain(15); exe.end('alarm'); await L.step(5000);
    assert.ok(await visible(L, SEL.alarm), 'alarm box');
    assert.equal(exe.S.status, 'running');
    await L.click(SEL.alarmOk); await L.run(200);
    assert.match(await band(L), /LOGGING/);
    await L.click(SEL.stop); await L.click(SEL.stopYes); await L.step(2000);
    assert.match(await band(L), /Stopped early/);
    assert.ok(exe.count('/api/ack-alarm', 'POST') >= 1);
  });
});
void MIN;
