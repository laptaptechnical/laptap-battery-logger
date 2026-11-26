// tests/keys.test.mjs — tools/keys.ps1 on a fresh PC (no %APPDATA%\Laptap\BatteryLogger yet), non-interactive.
// Runs with APPDATA pointed at an empty temp folder and LAPTAP_KEYS_TEST=1 (-TestPassphrase / -TestBackupFile replace
// the prompts and the file dialog). Never touches the real %APPDATA%.
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bl-keys-'));
after(() => fs.rmSync(root, { recursive: true, force: true }));
const PASS = 'correct horse battery';

function keys(appdata, args, testMode = true) {
  const env = { ...process.env, APPDATA: appdata };
  if (testMode) env.LAPTAP_KEYS_TEST = '1'; else delete env.LAPTAP_KEYS_TEST;
  const r = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(REPO, 'tools', 'keys.ps1'), ...args],
    { env, encoding: 'utf8', windowsHide: true });
  return { code: r.status, out: (r.stdout || '') + (r.stderr || '') };
}
const fresh = (name) => { const a = path.join(root, name, 'Roaming'); fs.mkdirSync(a, { recursive: true }); return a; };
const keyDir = (a) => path.join(a, 'Laptap', 'BatteryLogger');
const keyLines = (out) => (out.match(/new string\[\] \{ "[0-9a-f]{64}", "[0-9a-f]{64}" \}/g) || []);

test('new on a fresh PC: creates the folder, the primary, backup.pub and the backup file', () => {
  const a = fresh('fresh');
  assert.ok(!fs.existsSync(path.join(a, 'Laptap')), 'precondition: no Laptap folder');
  const usb = path.join(root, 'usb'); fs.mkdirSync(usb);
  const bk = path.join(usb, 'laptap-battery-logger-backup.lbk');
  const r = keys(a, ['new', '-TestPassphrase', PASS, '-TestBackupFile', bk]);
  assert.equal(r.code, 0, r.out);
  assert.ok(fs.existsSync(path.join(keyDir(a), 'signing.key')), 'primary saved');
  assert.ok(fs.existsSync(path.join(keyDir(a), 'backup.pub')), 'backup.pub saved');
  assert.ok(fs.existsSync(bk), 'backup file saved');
  assert.equal(fs.readFileSync(bk).subarray(0, 4).toString('ascii'), 'LBK1');
  assert.equal(keyLines(r.out).length, 2, 'prints the two public keys for UpdateKeys.cs');

  const pub = keys(a, ['public']);
  assert.equal(pub.code, 0, pub.out);
  assert.deepEqual(keyLines(pub.out), keyLines(r.out), "'public' prints the same keys");

  const again = keys(a, ['new', '-TestPassphrase', PASS, '-TestBackupFile', bk]);
  assert.notEqual(again.code, 0);
  assert.match(again.out, /Keys already exist/);
});

test('new is all-or-nothing: a failed write leaves no key files behind', () => {
  const a = fresh('partial');
  fs.mkdirSync(path.join(keyDir(a), 'backup.pub'), { recursive: true });   // a folder where backup.pub must go → that write fails
  const bk = path.join(root, 'usb2', 'backup.lbk');
  const r = keys(a, ['new', '-TestPassphrase', PASS, '-TestBackupFile', bk]);
  assert.notEqual(r.code, 0, r.out);
  assert.match(r.out, /Nothing was saved/);
  assert.ok(!fs.existsSync(bk), 'the backup file written first was removed again');
  assert.ok(!fs.existsSync(path.join(keyDir(a), 'signing.key')), 'no primary key');
});

test('a short passphrase is refused before anything is written', () => {
  const a = fresh('short');
  const bk = path.join(root, 'usb3', 'backup.lbk');
  const r = keys(a, ['new', '-TestPassphrase', 'short', '-TestBackupFile', bk]);
  assert.notEqual(r.code, 0);
  assert.match(r.out, /at least 12 characters/);
  assert.ok(!fs.existsSync(bk) && !fs.existsSync(path.join(keyDir(a), 'signing.key')));
});

test('the test-only switches are refused outside test mode', () => {
  const a = fresh('notest');
  const r = keys(a, ['new', '-TestPassphrase', PASS, '-TestBackupFile', path.join(root, 'x.lbk')], false);
  assert.notEqual(r.code, 0);
  assert.match(r.out, /for tests only/);
  assert.ok(!fs.existsSync(path.join(keyDir(a), 'signing.key')));
});
