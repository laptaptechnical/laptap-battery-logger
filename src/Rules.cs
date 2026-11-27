// src/Rules.cs
// Pure numeric rules: the C# copy of src/web/logic.js. tests/fixtures/rules.json is run against both.
// C# 5 only (built with the .NET Framework csc): no $"", no ?., no nameof.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

public class Seg
{
    public long StartMs, EndMs;
    public double? StartPct, EndPct, StartMwh, EndMwh;
}

public class Ev
{
    public long Ms;
    public string Type;
    public double? Pct, Mwh;
    public Ev() { }
    public Ev(long ms, string type, double? pct, double? mwh) { Ms = ms; Type = type; Pct = pct; Mwh = mwh; }
}

public static class Rules
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int IntervalFor(double? pct, int alarmPct, bool running)
    {
        if (!running || !pct.HasValue) return 60;
        if (pct.Value <= alarmPct + 3) return 10;
        if (pct.Value <= 20) return 30;
        return 60;
    }

    public static bool DetectGap(long? lastMs, long nowMs, int intervalS)
    {
        if (!lastMs.HasValue) return false;
        return nowMs - lastMs.Value > (intervalS + 90) * 1000L;
    }

    public static bool CanStart(double? pct) { return pct.HasValue && pct.Value >= 96; }

    // v16 §7 (I4): an open/close event while already open/closed is a no-op.
    static readonly HashSet<string> Open = new HashSet<string> { "unplugged", "charger_out", "gap_end", "resume", "wake" };
    static readonly HashSet<string> Close = new HashSet<string> { "charger_in", "gap", "pause", "sleep", "alarm", "stop", "end", "hibernated", "battery_ran_out" };

    public static List<Seg> Segments(IList<Ev> evs)
    {
        List<Seg> outp = new List<Seg>();
        Seg cur = null;
        foreach (Ev e in evs)
        {
            if (Open.Contains(e.Type) && cur == null) { cur = new Seg(); cur.StartMs = e.Ms; cur.StartPct = e.Pct; cur.StartMwh = e.Mwh; }
            else if (Close.Contains(e.Type) && cur != null)
            {
                cur.EndMs = e.Ms; cur.EndPct = e.Pct; cur.EndMwh = e.Mwh; outp.Add(cur); cur = null;
            }
        }
        return outp;
    }

    public static double CountedTime(IList<Seg> segs)
    {
        double s = 0; foreach (Seg g in segs) s += (g.EndMs - g.StartMs) / 1000.0; return s;
    }
    public static double CountedDrop(IList<Seg> segs)
    {
        double s = 0;
        foreach (Seg g in segs) if (g.StartPct.HasValue && g.EndPct.HasValue) s += g.StartPct.Value - g.EndPct.Value;
        return s;
    }
    public static double? CountedEnergy(IList<Seg> segs)
    {
        if (segs.Count == 0) return null;
        double s = 0;
        foreach (Seg g in segs)
        {
            if (!g.StartMwh.HasValue || !g.EndMwh.HasValue) return null;
            s += g.StartMwh.Value - g.EndMwh.Value;
        }
        return s;
    }
    // JS Math.round for positive and negative halves: floor(x + 0.5).
    public static double JsRound(double x) { return Math.Floor(x + 0.5); }

    public static double? EstFull(IList<Seg> segs)
    {
        double d = CountedDrop(segs);
        if (d < 10) return null;
        return JsRound(CountedTime(segs) * 100 / d);
    }
    public static double? AvgW(IList<Seg> segs)
    {
        double? e = CountedEnergy(segs); double h = CountedTime(segs) / 3600;
        if (!e.HasValue || h <= 0) return null;
        return e.Value / 1000 / h;
    }
    public static double? EstFullEnergy(double? fullMwh, double? w)
    {
        if (!fullMwh.HasValue || !w.HasValue || w.Value <= 0) return null;
        return JsRound(fullMwh.Value / 1000 / w.Value * 3600);
    }

    // points: [counted seconds, pct], oldest first.
    public static double? EtaToAlarm(IList<double[]> points, int alarmPct)
    {
        if (points == null || points.Count < 5) return null;
        double last = points[points.Count - 1][0];
        List<double[]> w = new List<double[]>();
        foreach (double[] p in points) if (p[0] >= last - 900) w.Add(p);
        if (w.Count < 5) return null;
        double n = w.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (double[] p in w) { sx += p[0]; sy += p[1]; sxx += p[0] * p[0]; sxy += p[0] * p[1]; }
        double den = n * sxx - sx * sx;
        if (den == 0) return null;
        double slope = (n * sxy - sx * sy) / den;
        if (slope >= 0) return null;
        double left = (w[w.Count - 1][1] - alarmPct) / -slope;
        return left < 0 ? 0 : JsRound(left);
    }

    public static double Round1(double x) { return Math.Floor(x * 10 + 0.5) / 10; }

    // [raw, shown]; relative-capacity batteries (≤ 100) → nulls.
    public static double?[] Health(double? full, double? design)
    {
        if (!full.HasValue || !design.HasValue || full.Value <= 100 || design.Value <= 100) return new double?[] { null, null };
        double raw = Round1(full.Value / design.Value * 100);
        return new double?[] { raw, Math.Min(100, raw) };
    }

    static string P2(long n) { return n.ToString("00", Inv); }
    public static string FmtElapsed(double sec)
    {
        long s = (long)Math.Max(0, JsRound(sec));
        return (s / 3600).ToString(Inv) + ":" + P2(s % 3600 / 60) + ":" + P2(s % 60);
    }
    public static string FmtHm(double sec)
    {
        long s = (long)Math.Max(0, JsRound(sec));
        return (s / 3600).ToString(Inv) + ":" + P2(s % 3600 / 60);
    }

    static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static long NowMs() { return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds; }
    public static int LocalOffsetMin(long ms) { return (int)TimeZoneInfo.Local.GetUtcOffset(Epoch.AddMilliseconds(ms)).TotalMinutes; }
    public static DateTime ToLocal(long ms) { return Epoch.AddMilliseconds(ms).ToLocalTime(); }
    public static long ToMs(DateTime utc) { return (long)(utc.ToUniversalTime() - Epoch).TotalMilliseconds; }

    public static string IsoLocal(long ms, int offsetMin)
    {
        DateTime d = Epoch.AddMilliseconds(ms + offsetMin * 60000L);
        int a = Math.Abs(offsetMin);
        return d.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv) + (offsetMin < 0 ? "-" : "+") + P2(a / 60) + ":" + P2(a % 60);
    }
    public static string IsoLocal(long ms) { return IsoLocal(ms, LocalOffsetMin(ms)); }

    public static string Num(object v)
    {
        if (v == null) return "";
        if (v is double) return ((double)v).ToString("R", Inv);
        if (v is float) return ((float)v).ToString("R", Inv);
        if (v is IFormattable) return ((IFormattable)v).ToString(null, Inv);
        return v.ToString();
    }

    public static string CsvRow(IList cells)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0) sb.Append(',');
            string s = Num(cells[i]);
            if (s.IndexOfAny(new char[] { '"', ',', '\r', '\n' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            sb.Append(s);
        }
        return sb.ToString();
    }

    public static string EscapeHtml(string s)
    {
        if (s == null) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
    }

    static readonly Regex Slot = new Regex(@"\{\{(\w+)\}\}");
    public static string FillReport(string tpl, IDictionary<string, object> data)
    {
        return Slot.Replace(tpl, delegate(Match m)
        {
            string k = m.Groups[1].Value;
            object v;
            if (!data.TryGetValue(k, out v) || v == null) return "";
            return k.EndsWith("Html", StringComparison.Ordinal) ? Num(v) : EscapeHtml(Num(v));
        });
    }

    // Same list as Laptap Diagnostics' Test-BiosPlaceholder.
    static readonly Regex Placeholder = new Regex(@"^(to be filled by o\.?e\.?m\.?|default string|system (product name|manufacturer|version|serial number)|not (applicable|specified|available)|none|n/?a|unknown|oem|0+|1234567890?)$", RegexOptions.IgnoreCase);
    public static bool IsPlaceholderSerial(string s)
    {
        return s == null || s.Trim().Length == 0 || Placeholder.IsMatch(s.Trim());
    }
    public static string SanitizeSerial(string s)
    {
        string t = Regex.Replace((s ?? "").ToUpperInvariant(), "[^A-Z0-9-]", "");
        return t.Length == 0 ? "UNKNOWN" : t;
    }

    // Sanity bounds (idea from oucdc/Power-Sentinel, MIT; no code copied). Bad values → null, never a guess.
    public static void ValidSample(ref double? rateMw, ref double? mwh, ref double? pct, double? fullMwh)
    {
        if (rateMw.HasValue && (rateMw.Value < 0 || rateMw.Value > 300000)) rateMw = null;
        if (mwh.HasValue && (mwh.Value < 0 || (fullMwh.HasValue && fullMwh.Value > 0 && mwh.Value > 1.5 * fullMwh.Value))) mwh = null;
        if (pct.HasValue && (pct.Value < 0 || pct.Value > 100)) pct = null;
    }

    public static string EndReason(string kind, double? pct, double sec)
    {
        string at = (pct.HasValue ? Num(pct.Value) + "%" : "n/a") + " after " + FmtHm(sec);
        switch (kind)
        {
            case "alarm": return "Ended at alarm";
            case "stop": return "Stopped early";
            case "hibernated": return "Hibernated at " + at;
            case "battery_ran_out": return "Battery ran out: Windows hibernated at " + at;   // v16 D7
            case "shutdown": return "Shut down at " + at;
            case "shutdown_user": return "Shut down by user at " + at;
            case "logger_stopped": return "Logger stopped at " + at;
            default: return "Stopped early";
        }
    }

    public static string PowerState(bool ac, bool charging, double? pct)
    {
        if (!ac) return "Draining";
        if (charging || (pct.HasValue && pct.Value < 100)) return "Plugged in, charging";
        return "Plugged in, full";
    }
}
