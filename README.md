# Laptap Battery Logger

A battery run-down tester for Laptap's secondhand laptops. It is a single `Laptap Battery Logger.exe` that staff copy
and double-click: no installer, no admin rights, no internet.

The exe reads Windows' real battery data: design and full capacity, health, mWh, watts, and the sleep and wake events
in the System log. It keeps the laptop awake while a test runs. It serves its own page to an Edge app window and saves
an HTML report and a CSV to the Desktop when the test ends.

Version: see `VERSION`.

## Layout

| Path | What |
|---|---|
| `src/*.cs` | The exe (C# 5, built with the .NET Framework `csc` that ships with Windows) |
| `src/web/index.html`, `src/web/logic.js` | The page and its pure rules. Both are embedded in the exe. The page also holds the report `<template>` |
| `tests/` | `logic.test.js` (node), `HelperTests.cs` (C# console tests), shared `fixtures/` |
| `test/` | Headless page harness: a fake battery, a fake clock and a mocked exe API |
| `build.ps1` | Tests, compile, PE flags, size check |

## Build

```powershell
.\build.ps1
```

This runs the node tests, compiles `dist\Laptap Battery Logger.exe` with
`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, and sets the run-from-removable-media PE flags. It then
compiles and runs `tests\bin\HelperTests.exe` and checks that `dist\` holds exactly one file under 400 KB.

Use `.\build.ps1 -Out dist-next` to build somewhere other than `dist\`. Nothing is installed.

## Test

From the repo root:

```powershell
node --test tests/
```

```powershell
node --test --test-concurrency=1 test/
```

```powershell
node test/measure-bar.mjs
```

```powershell
node test/measure-idle.mjs
```

- `node --test tests/` runs the `logic.js` rules (also run by `build.ps1`).
- `node --test --test-concurrency=1 test/` runs the harness self-test, the v14 page suite and the fix tests.
- `node test/measure-bar.mjs` checks where the action bar sits at 1024×600.
- `node test/measure-idle.mjs` measures timer, DOM and storage cost. Add `--cpu` for real-time CPU.

The harness uses `playwright-core` and Chromium headless shell that are already on the PC; it installs nothing. By
default it uses `..\laptap-desktop\node_modules\playwright-core` and `%LOCALAPPDATA%\ms-playwright\chromium_headless_shell-*`.
Set `PW_CORE` or `PW_CHROME` to point elsewhere. Tests run headless and muted, and never sound the real alarm.

`test/fixtures/v13.html` is the old v13 page. The harness self-test uses it as a known-behaviour control.

## QA-only switches

These are never used by staff, who double-click the exe with no arguments.

- `--qa-no-window`: opens no Edge window, report or message box.
- `--qa-out <dir>`: writes the report and CSV to `<dir>` instead of the Desktop.
- `--qa-alarm-pct <1-95>`: lets a test end at a high % for a quick run-down.
