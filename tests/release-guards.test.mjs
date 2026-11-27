// tests/release-guards.test.mjs — build audit B1 / U3: the owner's tools refuse unsafe releases before doing anything.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const ps = (args) => {
  const r = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', ...args], { cwd: REPO, encoding: 'utf8', windowsHide: true });
  return { code: r.status, out: (r.stdout || '') + (r.stderr || '') };
};

test('U3: release.ps1 refuses -TestHooks / -KeyFile / -Release / -OutDir without -NoGit', () => {
  for (const extra of [['-TestHooks'], ['-KeyFile', 'x.key'], ['-Release', '5'], ['-OutDir', 'x']]) {
    const r = ps(['-File', path.join(REPO, 'tools', 'release.ps1'), '15.0.1', ...extra]);
    assert.notEqual(r.code, 0, extra.join(' '));
    assert.match(r.out, /for the local test only/, extra.join(' ') + ': ' + r.out);
  }
});

test('B1: a release build with fewer than 2 signing keys is refused (dev builds need -AllowNoKeys)', () => {
  const r = ps(['-File', 'build.ps1', '-Out', path.join(REPO, 'dist-guard-test'), '-SkipTests']);
  if (/[2-9] signing keys in UpdateKeys\.cs/.test(r.out)) return;   // the owner's keys are in: nothing to refuse
  assert.notEqual(r.code, 0);
  assert.match(r.out, /a release build needs the owner's 2/);
});

test('release.ps1 pushes the tag by its full ref (a branch may share the tag name)', () => {
  const src = fs.readFileSync(path.join(REPO, 'tools', 'release.ps1'), 'utf8');
  assert.ok(src.includes('git push origin "refs/tags/v$Version"'), 'push uses refs/tags/');
  assert.ok(!src.includes('git push origin "v$Version"'), 'no bare tag push');
  assert.ok(src.includes('Later: git push origin HEAD; git push origin refs/tags/v$Version'), 'the hint matches');
});
