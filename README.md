# Laptap Battery Logger

A battery run-down tester for Laptap's secondhand laptops. It is a single `Laptap Battery Logger.exe` that staff copy
and double-click: no installer, no admin rights.

The exe reads Windows' real battery data: design and full capacity, health, mWh, watts, and the sleep and wake events
in the System log. It keeps the laptop awake while a test runs. It serves its own page to an Edge app window and saves
an HTML report and a CSV to the Desktop when the test ends.

- **Start:** no Start button in the normal flow. A checklist (battery found · charged to 96% or more · charger
  unplugged), then "All conditions are met, starting logging in 3.. 2.. 1..", then a big LOGGING band.
- **Live log:** every reading and event appears the moment it's taken, newest first, and the whole test can be scrolled.
- **Export and media:** export the log as CSV or PDF (choose **Save as PDF** in the print dialog); play looping video
  from the bundled clip or a file on the computer, with mute/unmute controls.
- **Safer closing:** closing the logger asks for confirmation to help prevent accidentally ending the app.
- **Self-update** (from v15): signed releases from this repo's GitHub Releases, downloaded quietly in the background
  and swapped in at the next start, never during a test. If offline at launch, it quietly retries while idle and
  pauses those checks during logging. See [docs/HOW-TO-RELEASE.md](docs/HOW-TO-RELEASE.md).

Version: `VERSION` (`version=` and `release=`) is the only place it is written. `build.ps1` generates
`src/Version.g.cs` and `src/app.manifest` from it, and fails if a version literal appears anywhere else in `src/`.

## Layout

| Path | What |
|---|---|
| `src/*.cs` | The exe (C# 5, built with the .NET Framework `csc` that ships with Windows) |
| `src/Update*.cs` | Self-update: signed manifest, background check + download, swap + hand-off at the next start |
| `src/web/index.html`, `src/web/logic.js` | The page and its pure rules. Both are embedded in the exe. The page also holds the report `<template>` |
| `tests/` | `logic.test.js` (node), `HelperTests.cs` + `V15Tests.cs` (C# console tests), `e2e-update.mjs`, shared `fixtures/` |
| `test/` | Headless page harness: a fake battery, a fake clock and a mocked exe API |
| `tools/` | `keys.ps1` (owner's signing keys), `release.ps1` (signed release), `KeyTools.cs` (their crypto) |
| `docs/` | `api-contract.md` (page ↔ exe), `BUILD-AND-RELEASE.md` (build and ship), `HOW-TO-RELEASE.md` (owner's recipe) |
| `build.ps1` | Version, tests, compile, PE flags, version / test-hook checks, size check |

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

This runs the tests and builds `dist\Laptap Battery Logger.exe`. It checks the version, release-only settings,
packaged video, and executable size. For the complete build, video packaging, and signed GitHub release workflow,
see [docs/BUILD-AND-RELEASE.md](docs/BUILD-AND-RELEASE.md).

- A build refuses to run unless `src\UpdateKeys.cs` holds the owner's 2 signing keys; an exe without them could
  never self-update. `.\build.ps1 -AllowNoKeys` makes a dev-only build that must never be handed out.
- `.\build.ps1 -Out dist-next` builds somewhere other than `dist\`.
- `-TestHooks -Version x.y.z -Release n -SkipTests` are for the update tests only.
- Nothing is installed.

## Test

From the repo root:

```powershell
node --test tests/
```

```powershell
node --test --test-concurrency=1 test/
```

```powershell
node tests/e2e-update.mjs
```

```powershell
node test/measure-bar.mjs
```

```powershell
node test/measure-idle.mjs
```

- `node --test tests/` runs the `logic.js` rules (also run by `build.ps1`).
- `node --test --test-concurrency=1 test/` runs the harness self-test, the page suites and the v15 flow / log suites.
- `node tests/e2e-update.mjs` is the local self-update run. It uses loopback and temp folders only, with a throwaway
  test key, a `TESTHOOKS` exe, its own data folder and its own mutex names.
- `node test/measure-bar.mjs` checks the layout at 1024×600 and 1366×768: every control on each screen is on screen, the
  page never scrolls, and the live log gets ≥ 150 px.
- `node test/measure-idle.mjs` measures timer, DOM and storage cost. Add `--cpu` for real-time CPU.

The harness uses `playwright-core` and Chromium headless shell that are already on the PC; it installs nothing. By
default it uses `..\laptap-desktop\node_modules\playwright-core` and `%LOCALAPPDATA%\ms-playwright\chromium_headless_shell-*`.
Set `PW_CORE` or `PW_CHROME` to point elsewhere. Tests run headless and muted, and never sound the real alarm.

`test/fixtures/v13.html` is the old v13 page. The harness self-test uses it as a known-behaviour control.

## Command line

Staff double-click the exe with no arguments.

- `--verify-release <dir> [--keys-file <file>]`: read-only check of a release folder (signature, version, release,
  exe hash). With `--keys-file` ("X:Y" hex per line) the signature is checked against those keys: the keys the
  laptops in the field have now. Exit 0 = good. Used by `tools\release.ps1`.
- `--self-test`: loads the page, builds the engine, starts and stops the local server, and reads its state back.
  Exit 0 = good. `tools\release.ps1` runs it before signing.
- `--updated <nonce>`, `--no-update`: internal, used by the update hand-off and rollback.
- `--after-exit <pid>`: internal, used by "Restart now" (v15.1): waits for the old logger to exit, then starts normally so the
  staged update installs.

QA only:

- `--qa-no-window`: opens no Edge window, report or message box.
- `--qa-out <dir>`: writes the report and CSV to `<dir>` instead of the Desktop.
- `--qa-alarm-pct <1-95>`: lets a test end at a high % for a quick run-down.
