# Battery Logger — page ↔ exe contract (single copy, v15.1)

v14 contract confirmed by Planning 2026-11-26; v15 changes from the plan's §4 (marked **v15**); v15.1.0 changes from
the v15.1 plan's §5, reduced scope (marked **v15.1**). Times are epoch ms
(`ms`), and the page formats them. Names are camelCase. Null = unknown, never a guess.

## GET /api/state?since=<rev>

```json
{
  "rev": 42, "version": "15.0.0", "nowMs": 1764122398000,
  "checks": { "battery": true, "pct96": true, "unplugged": false, "chargeLimitHint": false },
  "armed": true, "countdownEndsMs": null,
  "nextStep": null | "battery" | "charge" | "wait-update" | "unplug",
  "app": { "version": "15.0.0", "release": 1, "updatedFrom": null, "updateNote": null,
           "update": { "phase": "idle", "version": null, "bytes": null, "total": null, "percent": null, "note": null } },
  "live": { "avgW": null, "etaToAlarmS": null, "healthPct": null },
  "unit": { "maker": "HP", "model": "HP EliteBook 840 G5", "serial": "5CB4190FVX", "computerName": "LAPTOP-1234" },
  "batteries": [ { "id": "...", "maker": "...", "chemistry": "LIon",
                   "designMwh": 51528, "fullMwh": 47378, "fullEndMwh": null, "cycles": null } ],
  "now": { "pct": 98, "mwh": 46000, "fullMwh": 47378, "rateMw": 9500, "ac": false, "charging": false, "saver": false },
  "session": null | {
    "id": "20261126-090000", "status": "running" | "paused" | "done",
    "startedMs": 0,
    "alarmPct": 15, "alarmPctMax": 98, "alarm": false, "alarmAck": false, "pausedSinceMs": null,
    "segments": [ { "startMs": 0, "endMs": null, "startPct": 98, "endPct": null, "startMwh": 46000, "endMwh": null } ]
  },
  "samples": [ { "rev": 41, "ms": 0, "elapsedS": 0, "pct": 98, "mwh": 46000, "fullMwh": 47378,
                 "rateMw": 9500, "rateSrc": "wmi" | "calc", "voltageMv": 12000, "ac": false, "saver": false } ],
  "events":  [ { "rev": 40, "ms": 0, "elapsedS": 0, "type": "start", "pct": 98, "mwh": 46000, "rateMw": null,
                 "note": "", "fromMs": null, "toMs": null } ],
  "result": null | {
    "kind": "stop" | "hibernated" | "shutdown" | "shutdown_user" | "logger_stopped",
    "reason": "Stopped early", "startEnd": "98% → 15%", "alarmAck": true,
    "runtimeS": 39600, "startPct": 98, "endPct": 15,
    "estFullS": 35044, "estFullEnergyS": 35399, "avgW": 4.82, "minW": 3.1, "maxW": 12.4,
    "healthPct": 91.9, "fullChangedMwh": null,
    "gapCount": 1, "gapS": 1800, "pauseCount": 1, "pauseS": 3600,
    "saver": "on from 20% at 2:41", "brightnessStart": 60, "brightnessEnd": 60,
    "plan": "Balanced", "windows": "Windows 11 Pro 24H2",
    "files": { "html": "C:\\Users\\x\\Desktop\\Battery 5CB4190FVX 2026-11-26 0900.html", "csv": "...csv" }
  }
}
```

- **v15 start flow (the exe decides; the page only shows it):**
  - `nowMs` is the exe's clock when the answer was made. The page shows the countdown from `countdownEndsMs − nowMs`.
  - `checks.pct96` is true while plugged in at ≥ 96%. On the unplug edge the exe latches `{ms, pct, mwh}`, and while
    unplugged `pct96` follows the latched % (E6). Re-plugging clears the latch. A logger started already unplugged
    uses its first reading as the latch.
  - `checks.chargeLimitHint` (**v15 addition**) is true when plugged in below 96% with a flat % for 10 minutes. The
    page adds "· check the battery charge-limit setting".
  - `armed` is false after Cancel (the page then shows Start). `countdownEndsMs` is set only while Ready ∧ armed ∧ all
    checks ∧ a page polled `/api/state` within the last 3 s.
  - `session` is null in Ready / Countdown. `waiting` and `waitingSinceMs` are gone.
- **`app`:** `version` + `release` come from the repo's `VERSION`. `updatedFrom` is the previous version for the whole run
  after an update (the page shows "Updated to vX" once per version, remembered in `sessionStorage`). `updateNote` is a
  one-line reason for the version tooltip only: "Folder is read-only" · "Clock is wrong (yyyy-mm-dd), update skipped" ·
  "Update check failed (no internet?)" · "Update refused: bad signature" · "Update vanished (antivirus?)" ·
  "Update failed to start".
- **v15.1 `nextStep`** (the one thing to do before a test; the page picks its screen from it), in this order: no battery →
  `battery`; not 96% → `charge` (the page picks "Plug in and charge" or "Charging… wait" from `checks.unplugged`); an
  update holds the countdown → `wait-update`; plugged in → `unplug`; else null. Null while a session is showing.
- **v15.1 `app.update`** (one snapshot from the updater thread, also read at serve time): `phase` = `idle` · `checking` ·
  `downloading` (`version`, `bytes`, `total`, `percent` 0–100, at most every 250 ms) · `verifying` · `ready` (staged; installs at the
  next start, through the v15 start-time swap) · `failed` (`note` = the `updateNote` wording). `checking` with nothing to do goes
  back to `idle`.
  - **Countdown hold:** `downloading` and `verifying` hold the countdown (`nextStep = wait-update`), for at most 45 s from
    when the hold began; then it lifts and the update carries on quietly. `ready` and `failed` never hold.
- **v15.1 `live`:** from the counted segments, by the same rules as `result`, so the Logging tiles and Done agree. All null
  until 5 counted samples. `healthPct` is raw like `result.healthPct`; `etaToAlarmS` is null while paused.
- **v15.1 `session.pausedSinceMs`:** the plug-in time of the open pause; null unless Paused. Derived, never stored in
  `session.json` (no new stored fields: older copies on sticks share the data folder).
- **Battery alarm:** `alarmPctMax` is the current battery percentage rounded down (or 100 when unavailable). Set
  `alarmPct` from 1 through that ceiling. Crossing the alarm records one `alarm` event and shows an alert; it does not
  finish the session. Plugging in is recommended, not required. `/api/stop` is the normal way to finish logging.
- `samples`/`events`: only rows with `rev > since`; all rows when `since` is 0 or missing. `elapsedS` = counted time.
- **Event types** (same strings as the CSV `event` column): `start`, `unplugged` (the latched unplug reading when the
  countdown ran straight after it), `charger_in`, `charger_out`, `gap` (with `fromMs`/`toMs`; `note` = `slept` |
  `hibernated` | `unknown`), `saver_on`, `saver_off`, `alarm`, `stop`, `hibernated`, `recovered` (`note` = `shutdown` |
  `shutdown_user` | `hibernated` | `logger_stopped`, written when an old test is finished at start-up), `report_saved`,
  `report_failed`. (`waiting_unplug` was v14 only.)
- **Live log (M1):** a reading taken because of an event (same `ms`) is shown once, as the event row with that
  reading's numbers. The CSV keeps both lines.
- **`report_failed`** is written when the Desktop report or CSV can't be saved. `note` = the exception message; `pct`,
  `mwh` and `rateMw` are null. The page shows it as a red row reading "Report not saved to the Desktop".
  `result.files.html` and `result.files.csv` then stay null.
- `batteries[].cycles` is null when 0 or missing. `fullChangedMwh` is null or `{from, to}`.
- **Health:** the page's live Health tile uses `logic.health(fullMwh, designMwh)` (total = Σ). `result.healthPct` is
  raw (one decimal, null if unknown); the page and the report show `min(100, raw)` with one decimal.
- **The exe is the single source** of the formatted texts `result.reason`, `result.startEnd` and `result.saver`.

## POST endpoints

- Every POST needs header `X-Laptap-Token` and a JSON body (or none). It answers `{ "ok": true }` or
  `{ "ok": false, "error": "<text>" }`.
- `/api/heartbeat` (**v15.1:** body `{ "visible": true | false }`; no body = visible) · `/api/stop` · `/api/ack-alarm` · `/api/alarm-pct` body `{ "pct": 20 }` (1 to `alarmPctMax`, the current battery percentage rounded down) ·
  `/api/save-csv`, whose answer adds `"file": "<full path>"`.
- **v15:** `/api/arm` (the Start button: re-arms; the same 3 s countdown runs) · `/api/cancel` (Countdown → Ready,
  disarmed) · `/api/new` (Done → Ready, armed). `/api/start` is **not** in the release exe: it exists only in a
  `TESTHOOKS` build, for tests.
- **v15.1:** `/api/update-restart` (Restart now: only when `phase == ready` and idle, i.e. no session not done and Ready with no
  countdown or Done with the alarm acknowledged; else "No update is ready." / "Finish the test first."). When accepted the
  exe starts "<own exe> --after-exit <own pid>", answers `{ok:true}`, then exits; the new process waits for it (30 s max)
  and starts like a double-click, so the v15 start-time swap installs the update and opens a new window. The page
  shows "Restarting…" and closes itself. · `/api/update-retry` (from `failed`: runs the check again, once per start) ·
  `/api/open-report` (Done only: opens the Desktop report with the shell).
- **v15.1 idle exit:** with no test running (and outside the unacknowledged-alarm window), the exe exits 15 s after the
  last contact (a heartbeat or a token'd `GET /api/state`) if the last heartbeat said visible, 90 s if it said hidden. No
  page yet: the 3 min start-up grace. A countdown with no contact for 15 s is cleared. The page heartbeats every 5 s
  while visible, every 30 s while hidden, and at each visibility change.
- `{ok:false, error}` with **HTTP 200** is used for rule refusals, e.g. "A test is already running.", "No countdown is
  running.", "No finished test is showing.", "Press New test first.", an alarm % out of range. The page shows `error`,
  re-polls and renders from `/api/state`; it never assumes a state.
- **Auth failures are HTTP codes with no JSON:** 403 for non-loopback, a missing or wrong token, or `OPTIONS`; 400 for
  a wrong Host. No `Access-Control-*` header, ever.

## Command line (v15)

- No arguments: what staff use.
- `--verify-release <dir>`: read-only check of a release folder (`update.json`, `.sig`, the exe) against this exe's
  keys, version and release. Exit 0 = good. Used by `tools/release.ps1`.
- `--updated <nonce>`: internal, the swapped-in exe after an update. `--no-update`: internal, after a rollback.
- **v15.1** `--after-exit <pid>`: internal, Restart now. Waits (up to 30 s) for that pid to exit, then starts normally.
- QA only: `--qa-no-window`, `--qa-out <dir>`, `--qa-alarm-pct <1-95>`.

## Report template (`<template id="report">` inside index.html)

The exe reads index.html from its resources and takes the text between `<template id="report">` and `</template>`.
It fills `{{slot}}` exactly like `logic.fillReport`: plain slots are HTML-escaped, slots ending in `Html` are raw
(built by the exe from escaped parts), and a slot with no value becomes "". The build fails if the template's slots
and the exe's `Report.Slots` differ.

| Slot | Example |
|---|---|
| `unitModel`, `unitSerial`, `computerName` | HP EliteBook 840 G5 · 5CB4190FVX · LAPTOP-1234 |
| `date`, `startTime`, `endTime` | 2026-11-26 · 09:00 · 20:00 |
| `reason`, `startEnd`, `runtime` | Stopped early · 98% → 15% · 11:00 |
| `estFull`, `estFullEnergy` | 9:44 · 9:50 (or "n/a") |
| `avgW`, `minW`, `maxW` | 4.8 W · 3.1 W · 12.4 W |
| `healthPct` | 91.9% (or "n/a") |
| `batteryRowsHtml` | one `<tr>` per battery: Id, maker, chemistry, design Wh, full Wh (start → end if changed), cycles, health |
| `gapsPauses` | 1 gap · 0:30 (slept) · 1 pause · 1:00 (or "None") |
| `saver`, `brightness`, `plan`, `windows` | on from 20% at 2:41 · 60% → 60% · Balanced · Windows 11 Pro 24H2 |
| `alarmNote` | "" or "Alarm not acknowledged" |
| `chartSvgHtml` | inline SVG: % area + line, W line, dashed alarm line, gaps shaded, pauses hatched; print-safe colours |
| `eventRowsHtml` | the first 25 events as `<tr>` |
| `eventsMore` | "12 more in the CSV" or "" |
| `version` | from `VERSION` |
