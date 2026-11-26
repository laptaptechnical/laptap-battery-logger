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

  var api = {
    intervalFor: intervalFor, detectGap: detectGap, canStart: canStart, segments: segments,
    countedTime: countedTime, countedDrop: countedDrop, countedEnergy: countedEnergy, estFull: estFull,
    estFullEnergy: estFullEnergy, avgW: avgW, etaToAlarm: etaToAlarm, health: health, fmtElapsed: fmtElapsed,
    fmtHm: fmtHm, isoLocal: isoLocal, csvRow: csvRow, escapeHtml: escapeHtml, fillReport: fillReport,
    sanitizeSerial: sanitizeSerial, isPlaceholderSerial: isPlaceholderSerial, validSample: validSample,
    endReason: endReason, powerState: powerState
  };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.LaptapLogic = api;
})(this);
