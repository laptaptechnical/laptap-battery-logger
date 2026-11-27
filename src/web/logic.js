// Battery-Logger/v14/src/web/logic.js
// Pure rules for the Battery Logger page. No DOM, no timers. Loaded by the page (<script src="logic.js">)
// and by the node tests (require). Rules.cs holds the same numeric rules in C#; tests/fixtures/rules.json proves both agree.
(function (root) {
  'use strict';

  // Full-sample interval in seconds (plan §3.3).
  function intervalFor(pct, alarmPct, running) {
    if (!running || pct == null) return 60;
    if (pct <= alarmPct + 3) return 10;
    if (pct <= 20) return 30;
    return 60;
  }

  // A gap = the next sample is later than interval + 90 s (bug 2/7).
  function detectGap(lastMs, nowMs, intervalS) {
    if (lastMs == null) return false;
    return nowMs - lastMs > (intervalS + 90) * 1000;
  }

  // D15: Start only at 96% or more (some laptops cap the charge at 99%).
  function canStart(pct) { return pct != null && pct >= 96; }

  // Segments from an event list. Opening events start the clock, closing events stop it (C2).
  var OPEN = { unplugged: 1, charger_out: 1, gap_end: 1 };
  var CLOSE = { charger_in: 1, gap: 1, alarm: 1, stop: 1, end: 1, hibernated: 1 };
  function segments(events) {
    var out = [], cur = null;
    for (var i = 0; i < events.length; i++) {
      var e = events[i];
      if (OPEN[e.type] && !cur) cur = { startMs: e.ms, startPct: num(e.pct), startMwh: num(e.mwh) };
      else if (CLOSE[e.type] && cur) {
        cur.endMs = e.ms; cur.endPct = num(e.pct); cur.endMwh = num(e.mwh);
        out.push(cur); cur = null;
      }
    }
    return out;
  }
  function num(v) { return v == null || v === '' || isNaN(v) ? null : +v; }

  function countedTime(segs) { var s = 0; segs.forEach(function (g) { s += (g.endMs - g.startMs) / 1000; }); return s; }
  function countedDrop(segs) {
    var s = 0;
    segs.forEach(function (g) { if (g.startPct != null && g.endPct != null) s += g.startPct - g.endPct; });
    return s;
  }
  function countedEnergy(segs) {
    if (!segs.length) return null;
    var s = 0;
    for (var i = 0; i < segs.length; i++) {
      if (segs[i].startMwh == null || segs[i].endMwh == null) return null;
      s += segs[i].startMwh - segs[i].endMwh;
    }
    return s;
  }
  // est. 100→0% time in seconds; null until at least 10 counted points have dropped (C2, M7).
  function estFull(segs) {
    var d = countedDrop(segs);
    if (d < 10) return null;
    return Math.round(countedTime(segs) * 100 / d);
  }
  // Average watts = Σ counted ΔmWh ÷ counted hours.
  function avgW(segs) {
    var e = countedEnergy(segs), h = countedTime(segs) / 3600;
    if (e == null || h <= 0) return null;
    return e / 1000 / h;
  }
  // Energy method: full mWh ÷ avg W, in seconds.
  function estFullEnergy(fullMwh, w) {
    if (fullMwh == null || w == null || w <= 0) return null;
    return Math.round(fullMwh / 1000 / w * 3600);
  }

  // "About h:mm to alarm": least-squares fit of % over the last 15 counted minutes (M9).
  // points = [{e: counted seconds, pct}], oldest first. Needs 5+ points in the window.
  function etaToAlarm(points, alarmPct) {
    if (!points || points.length < 5) return null;
    var last = points[points.length - 1].e;
    var w = points.filter(function (p) { return p.e >= last - 900 && p.pct != null; });
    if (w.length < 5) return null;
    var n = w.length, sx = 0, sy = 0, sxx = 0, sxy = 0;
    w.forEach(function (p) { sx += p.e; sy += p.pct; sxx += p.e * p.e; sxy += p.e * p.pct; });
    var den = n * sxx - sx * sx;
    if (den === 0) return null;
    var slope = (n * sxy - sx * sy) / den;
    if (slope >= 0) return null;
    var left = (w[n - 1].pct - alarmPct) / -slope;
    return left < 0 ? 0 : Math.round(left);
  }

  function round1(x) { return Math.floor(x * 10 + 0.5) / 10; }
  // Health = full ÷ design × 100, one decimal. Raw is kept; shown is capped at 100. Relative-capacity batteries → null.
  function health(full, design) {
    if (full == null || design == null || full <= 100 || design <= 100) return { raw: null, shown: null };
    var raw = round1(full / design * 100);
    return { raw: raw, shown: Math.min(100, raw) };
  }

  function pad(n) { return (n < 10 ? '0' : '') + n; }
  function fmtElapsed(sec) {
    sec = Math.max(0, Math.round(sec || 0));
    return Math.floor(sec / 3600) + ':' + pad(Math.floor(sec % 3600 / 60)) + ':' + pad(sec % 60);
  }
  function fmtHm(sec) {
    sec = Math.max(0, Math.round(sec || 0));
    return Math.floor(sec / 3600) + ':' + pad(Math.floor(sec % 3600 / 60));
  }
  // ISO 8601 with offset (bug 15). offsetMin = minutes east of UTC (+480 for Manila).
  function isoLocal(ms, offsetMin) {
    if (offsetMin == null) offsetMin = -new Date(ms).getTimezoneOffset();
    var d = new Date(ms + offsetMin * 60000), a = Math.abs(offsetMin);
    return d.getUTCFullYear() + '-' + pad(d.getUTCMonth() + 1) + '-' + pad(d.getUTCDate()) + 'T' +
      pad(d.getUTCHours()) + ':' + pad(d.getUTCMinutes()) + ':' + pad(d.getUTCSeconds()) +
      (offsetMin < 0 ? '-' : '+') + pad(Math.floor(a / 60)) + ':' + pad(a % 60);
  }

  // RFC 4180 row (no line ending).
  function csvRow(cells) {
    return cells.map(function (c) {
      if (c == null) return '';
      var s = String(c);
      return /[",\r\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
    }).join(',');
  }
  function escapeHtml(s) {
    return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }
  // {{key}} → escaped value; {{keyHtml}} → raw (already-escaped markup). Unknown slots → '' so none are left.
  function fillReport(tpl, data) {
    return tpl.replace(/\{\{(\w+)\}\}/g, function (_, k) {
      var v = data[k];
      if (v == null) return '';
      return /Html$/.test(k) ? String(v) : escapeHtml(v);
    });
  }

  // Same list as Laptap Diagnostics' Test-BiosPlaceholder.
  var PLACEHOLDER = /^(to be filled by o\.?e\.?m\.?|default string|system (product name|manufacturer|version|serial number)|not (applicable|specified|available)|none|n\/?a|unknown|oem|0+|1234567890?)$/i;
  function isPlaceholderSerial(s) { return s == null || !String(s).trim() || PLACEHOLDER.test(String(s).trim()); }
  function sanitizeSerial(s) {
    var t = String(s == null ? '' : s).toUpperCase().replace(/[^A-Z0-9-]/g, '');
    return t || 'UNKNOWN';
  }

  // Sanity bounds (idea from oucdc/Power-Sentinel, MIT; no code copied). Bad values become null, never a guess.
  function validSample(s, fullMwh) {
    var o = {};
    for (var k in s) if (Object.prototype.hasOwnProperty.call(s, k)) o[k] = s[k];
    if (o.rateMw != null && (o.rateMw < 0 || o.rateMw > 300000)) o.rateMw = null;
    if (o.mwh != null && (o.mwh < 0 || (fullMwh != null && fullMwh > 0 && o.mwh > 1.5 * fullMwh))) o.mwh = null;
    if (o.pct != null && (o.pct < 0 || o.pct > 100)) o.pct = null;
    return o;
  }

  function endReason(kind, pct, sec) {
    var at = (pct == null ? 'n/a' : pct + '%') + ' after ' + fmtHm(sec);
    switch (kind) {
      case 'alarm': return 'Ended at alarm';
      case 'stop': return 'Stopped early';
      case 'hibernated': return 'Hibernated at ' + at;
      case 'shutdown': return 'Shut down at ' + at;
      case 'shutdown_user': return 'Shut down by user at ' + at;
      case 'logger_stopped': return 'Logger stopped at ' + at;
      default: return 'Stopped early';
    }
  }

  // Power state words (bug 13).
  function powerState(ac, charging, pct) {
    if (!ac) return 'Draining';
    if (charging || (pct != null && pct < 100)) return 'Plugged in, charging';
    return 'Plugged in, full';
  }

  // ---------------- v15: the state band, the live log ----------------

  // Which band to show and what it says (plan §3). The exe decides; this only reads /api/state.
  // Returns { mode: 'ready'|'count'|'logging'|'paused'|'done', checks: [{id, ok, text}], digit, showStart, sub }.
  function bandView(s) {
    s = s || {};
    var sess = s.session, ck = s.checks || {}, n = s.now || {}, r = s.result;
    if (sess && sess.status === 'running') return { mode: 'logging', checks: [], digit: null, showStart: false, sub: '' };
    if (sess && sess.status === 'paused') return { mode: 'paused', checks: [], digit: null, showStart: false, sub: 'Unplug to continue' };
    if (sess && sess.status === 'done') {
      var saved = r && r.files && r.files.html;
      return { mode: 'done', checks: [], digit: null, showStart: false,
        sub: ((r && r.reason) || 'Test finished') + ' · ' + (saved ? 'report saved to the Desktop' : 'report not saved to the Desktop') };
    }
    var pctText = ck.pct96 ? 'Charged to 96% or more'
      : 'Charge to 96% or more' + (n.pct == null ? '' : ' (now ' + n.pct + '%)') + (ck.chargeLimitHint ? ' · check the battery charge-limit setting' : '');
    var checks = [
      { id: 'battery', ok: !!ck.battery, text: ck.battery ? 'Battery found' : 'No battery found' },
      { id: 'pct96', ok: !!ck.pct96, text: pctText },
      { id: 'unplugged', ok: !!ck.unplugged, text: 'Charger unplugged' }
    ];
    if (s.countdownEndsMs != null) {
      var left = s.countdownEndsMs - (s.nowMs == null ? s.countdownEndsMs : s.nowMs);
      return { mode: 'count', checks: checks, digit: Math.max(1, Math.min(3, Math.ceil(left / 1000))), showStart: false, sub: '' };
    }
    return { mode: 'ready', checks: checks, digit: null, showStart: s.armed === false, sub: '' };
  }

  // One log row per reading and per event, newest first. A reading taken because of an event (same ms) is shown
  // once, as the event row with that reading's numbers (M1). Rows: {rev, ms, type ('reading' or the event type),
  // note, fromMs, toMs, pct, mwh, rateMw, elapsedS}.
  function mergeRows(samples, events) {
    var byMs = {}, rows = [];
    (events || []).forEach(function (e) { byMs[e.ms] = true; });
    (samples || []).forEach(function (x) {
      if (byMs[x.ms]) return;
      rows.push({ rev: x.rev, ms: x.ms, type: 'reading', note: '', fromMs: null, toMs: null, pct: x.pct, mwh: x.mwh, rateMw: x.rateMw, elapsedS: x.elapsedS });
    });
    var sampleAt = {};
    (samples || []).forEach(function (x) { sampleAt[x.ms] = x; });
    (events || []).forEach(function (e) {
      var x = sampleAt[e.ms];
      rows.push({ rev: e.rev, ms: e.ms, type: e.type, note: e.note || '', fromMs: e.fromMs, toMs: e.toMs,
        pct: x ? x.pct : e.pct, mwh: x ? x.mwh : e.mwh, rateMw: x ? x.rateMw : e.rateMw, elapsedS: e.elapsedS });
    });
    rows.sort(function (a, b) { return b.ms - a.ms || b.rev - a.rev; });
    return rows;
  }

  // DOM cap (I12): at most `cap` rows in the table (samples + events together). Returns how many of the oldest DOM
  // rows to drop when `adding` more arrive. The in-memory samples (chart) and the CSV are never capped.
  function capRows(domRows, adding, cap) {
    cap = cap || 20000;
    var over = domRows + adding - cap;
    return over > 0 ? Math.min(over, domRows) : 0;
  }

  // ---------------- v15.1: one stage, one modal, chips (plan §6) ----------------

  var ACTIVE_UPDATE = { checking: 1, downloading: 1, verifying: 1 };

  // Which stage fills the window (§6.1–6.5). Returns { stage, now } where stage is one of
  // nobatt · low · charge · waitupd · unplug · countdown · ready · logging · paused · done,
  // and `now` is the checklist item to highlight ('b' | 'p' | 'a' | null). showStart: the big Start (G4).
  function stageView(s) {
    s = s || {};
    var sess = s.session, ck = s.checks || {};
    if (sess && sess.status === 'running') return { stage: 'logging', now: null, showStart: false };
    if (sess && sess.status === 'paused') return { stage: 'paused', now: null, showStart: false };
    if (sess && sess.status === 'done') return { stage: 'done', now: null, showStart: false };
    // An exe without nextStep (older contract): the same order from the checks, with no update hold.
    var ns = s.nextStep !== undefined ? s.nextStep
      : !ck.battery ? 'battery' : !ck.pct96 ? 'charge' : !ck.unplugged ? 'unplug' : null;
    switch (ns) {
      case 'battery': return { stage: 'nobatt', now: 'b', showStart: false };
      case 'charge': return { stage: ck.unplugged ? 'low' : 'charge', now: 'p', showStart: false };
      case 'wait-update': return { stage: 'waitupd', now: null, showStart: false };
      case 'unplug': return { stage: 'unplug', now: 'a', showStart: false };
    }
    if (s.countdownEndsMs != null) return { stage: 'countdown', now: null, showStart: false };
    return { stage: 'ready', now: null, showStart: s.armed === false };
  }

  // A test is on screen (Countdown, Logging, Paused): no update modal, only the header chip (§6.3).
  function testOnScreen(stage) { return stage === 'countdown' || stage === 'logging' || stage === 'paused'; }

  // Which update modal to show (v15.1 §4.3). opts: { checkingForMs, hidden: { phase: true }, restarting }.
  // checking shows only after 1 s (G1); never while a test is on screen. "restarting" only after Restart now.
  function modalView(s, opts) {
    s = s || {}; opts = opts || {};
    if (opts.restarting) return 'restarting';
    var u = (s.app || {}).update || {}, ph = u.phase || 'idle', st = stageView(s).stage;
    if (testOnScreen(st) || ph === 'idle') return null;
    if (opts.hidden && opts.hidden[ph]) return null;
    if (ph === 'checking') return (opts.checkingForMs || 0) > 1000 ? 'checking' : null;
    return ph;
  }

  // The header chip (v15.1 §4.2, §4.3): "Update vX installs after this test" during a test, the hidden download
  // "Downloading vX 62%", or after OK on "Update ready" "vX installs next time". Null = no chip.
  function chipView(s, opts) {
    s = s || {}; opts = opts || {};
    var app = s.app || {}, u = app.update || {}, st = stageView(s).stage, hidden = opts.hidden || {};
    if (testOnScreen(st)) {
      if (u.version && (u.phase === 'downloading' || u.phase === 'verifying' || u.phase === 'ready')) return { kind: 'update', text: 'Update v' + u.version + ' installs after this test' };
      return null;
    }
    if (u.phase === 'downloading' && hidden.downloading)
      return { kind: 'download', text: 'Downloading v' + u.version + ' ' + (u.percent || 0) + '%', percent: u.percent || 0 };
    if (u.phase === 'ready' && hidden.ready) return { kind: 'ready', text: 'v' + u.version + ' installs next time' };
    return null;
  }

  // §2.3: Restart now shows only when the logger is idle: Ready with no countdown, or Done with the alarm acknowledged
  // (the exe checks again and refuses otherwise).
  function canRestart(s) {
    s = s || {};
    var st = stageView(s).stage, sess = s.session;
    if (st === 'done') return !(sess && sess.alarm && !sess.alarmAck);
    return st !== 'countdown' && !testOnScreen(st);
  }

  // v15.2.1: the automatic restart runs only when an update is ready and the logger is idle (same rule as Restart now).
  function autoRestart(s) {
    var u = s && s.app && s.app.update;
    return !!(u && u.phase === 'ready' && canRestart(s));
  }
  // "3.." → "3.. 2.." → "3.. 2.. 1.." (the owner's words), from the ms left.
  function autoRestartWords(leftMs) { return ['3.. 2.. 1..', '3.. 2..', '3..'][countdownDigit(leftMs) - 1]; }

  // §3: heartbeat cadence. Visible: every 5 s (the exe exits ~15 s after a visible page goes quiet). Hidden: every
  // 30 s, so even Chrome's 1-a-minute throttling of hidden timers stays inside the exe's 90 s hidden window.
  function heartbeatEvery(hidden) { return hidden ? 30000 : 5000; }

  // Countdown words (F6): "3..." → "3... 2..." → "3... 2... 1...", from countdownEndsMs − nowMs.
  function countdownDigit(leftMs) { return Math.max(1, Math.min(3, Math.ceil((leftMs == null ? 3000 : leftMs) / 1000))); }
  function countdownWords(leftMs) { return ['3... 2... 1...', '3... 2...', '3...'][countdownDigit(leftMs) - 1]; }

  // "3 h 10 min" / "42 min"
  function fmtHMin(sec) {
    if (sec == null) return null;
    sec = Math.max(0, Math.round(sec));
    var h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60);
    return h ? h + ' h ' + m + ' min' : m + ' min';
  }

  var api = {
    intervalFor: intervalFor, detectGap: detectGap, canStart: canStart, segments: segments,
    countedTime: countedTime, countedDrop: countedDrop, countedEnergy: countedEnergy, estFull: estFull,
    estFullEnergy: estFullEnergy, avgW: avgW, etaToAlarm: etaToAlarm, health: health, fmtElapsed: fmtElapsed,
    fmtHm: fmtHm, isoLocal: isoLocal, csvRow: csvRow, escapeHtml: escapeHtml, fillReport: fillReport,
    sanitizeSerial: sanitizeSerial, isPlaceholderSerial: isPlaceholderSerial, validSample: validSample,
    endReason: endReason, powerState: powerState, bandView: bandView, mergeRows: mergeRows, capRows: capRows,
    stageView: stageView, testOnScreen: testOnScreen, modalView: modalView, chipView: chipView,
    countdownDigit: countdownDigit, countdownWords: countdownWords, canRestart: canRestart,
    autoRestart: autoRestart, autoRestartWords: autoRestartWords, heartbeatEvery: heartbeatEvery,
    fmtHMin: fmtHMin, ACTIVE_UPDATE: ACTIVE_UPDATE
  };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.LaptapLogic = api;
})(this);
