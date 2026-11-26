// src/Report.cs
// The end-of-test result: summary numbers (segment-based, C2), the one-page HTML report filled from the
// <template id="report"> inside the embedded index.html, and the CSV. Saved to the Desktop only (D4, D14).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

public static class Report
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string Wh(double? mwh) { return mwh.HasValue ? (mwh.Value / 1000).ToString("0.0", Inv) : "n/a"; }
    static string W(double? w) { return w.HasValue ? w.Value.ToString("0.0", Inv) + " W" : "n/a"; }
    static string P(double? p) { return p.HasValue ? Rules.Num(p.Value) + "%" : "n/a"; }

    public static string Template(string indexHtml)
    {
        const string open = "<template id=\"report\">";
        int a = indexHtml.IndexOf(open, StringComparison.Ordinal);
        int b = a < 0 ? -1 : indexHtml.IndexOf("</template>", a, StringComparison.Ordinal);
        if (a < 0 || b < 0) return "<html><body><pre>{{result}}\n{{runtime}}</pre></body></html>";
        return indexHtml.Substring(a + open.Length, b - a - open.Length).Trim();
    }

    // api-contract.md: result {…}. Numbers raw; reason / startEnd / saver are the exe's formatted texts.
    public static Dictionary<string, object> Summary(Engine en)
    {
        SessionData S = en.S;
        Dictionary<string, object> d = new Dictionary<string, object>();
        double run = Rules.CountedTime(S.Segments);
        double? avg = Rules.AvgW(S.Segments);
        StaticInfo si = S.StartInfo ?? en.Info;
        double? full = si.TotalFull(), design = si.TotalDesign();
        double?[] h = Rules.Health(full, design);
        d["kind"] = S.EndKind;
        d["reason"] = S.EndText;
        d["startEnd"] = P(S.StartPct) + " → " + P(S.EndPct);
        d["alarmAck"] = !S.Alarm || S.AlarmAck;
        d["runtimeS"] = run;
        d["startPct"] = S.StartPct; d["endPct"] = S.EndPct;
        d["estFullS"] = Rules.EstFull(S.Segments);
        d["estFullEnergyS"] = Rules.EstFullEnergy(full, avg);
        d["avgW"] = avg.HasValue ? (object)Math.Round(avg.Value, 2) : null;
        d["minW"] = S.MinW.HasValue ? (object)Math.Round(S.MinW.Value, 2) : null;
        d["maxW"] = S.MaxW.HasValue ? (object)Math.Round(S.MaxW.Value, 2) : null;
        d["healthPct"] = h[0];
        double? endFull = EndFullTotal(S);
        if (endFull.HasValue && full.HasValue && Math.Abs(endFull.Value - full.Value) > full.Value * 0.005)
        {
            Dictionary<string, object> fc = new Dictionary<string, object>(); fc["from"] = full; fc["to"] = endFull; d["fullChangedMwh"] = fc;
        }
        else d["fullChangedMwh"] = null;
        double gapS = 0; foreach (GapInfo g in S.Gaps) gapS += (g.ToMs - g.FromMs) / 1000.0;
        double pauseS = 0; foreach (PauseInfo p in S.Pauses) pauseS += ((p.ToMs ?? (S.EndMs ?? p.FromMs)) - p.FromMs) / 1000.0;
        d["gapCount"] = S.Gaps.Count; d["gapS"] = Math.Round(gapS);
        d["pauseCount"] = S.Pauses.Count; d["pauseS"] = Math.Round(pauseS);
        d["saver"] = SaverText(S);
        d["brightnessStart"] = S.BrightnessStart; d["brightnessEnd"] = S.BrightnessEnd;
        d["plan"] = si.PowerPlan; d["windows"] = si.Windows;
        Dictionary<string, object> files = new Dictionary<string, object>(); files["html"] = S.ReportPath; files["csv"] = S.CsvPath;
        d["files"] = files;
        return d;
    }

    static double? EndFullTotal(SessionData S)
    {
        if (S.EndFull == null || S.EndFull.Count == 0) return null;
        double s = 0;
        foreach (double? v in S.EndFull.Values) { if (!v.HasValue || v.Value <= 100) return null; s += v.Value; }
        return s;
    }

    static string SaverText(SessionData S)
    {
        if (S.SaverAtStart) return "on from the start";
        foreach (EventRow e in S.Events)
            if (e.Type == "saver_on") return "on from " + P(e.Pct) + " at " + Rules.FmtHm(e.ElapsedS);
        return "off";
    }

    // The report template slots (api-contract.md "Report template").
    public static readonly string[] Slots = { "unitModel", "unitSerial", "computerName", "date", "startTime", "endTime", "reason", "startEnd",
        "runtime", "estFull", "estFullEnergy", "avgW", "minW", "maxW", "healthPct", "batteryRowsHtml", "gapsPauses", "saver", "brightness",
        "plan", "windows", "alarmNote", "chartSvgHtml", "eventRowsHtml", "eventsMore", "version" };

    static string Hm(object sec) { return sec == null ? "n/a" : Rules.FmtHm(Convert.ToDouble(sec, Inv)); }

    public static Dictionary<string, object> ReportData(Engine en)
    {
        SessionData S = en.S;
        StaticInfo si = S.StartInfo ?? en.Info;
        Dictionary<string, object> r = Summary(en);
        Dictionary<string, object> d = new Dictionary<string, object>();
        DateTime start = Rules.ToLocal(S.UnplugMs ?? S.StartedMs), end = Rules.ToLocal(S.EndMs ?? Rules.NowMs());
        d["unitModel"] = si.Model.Length > 0 ? si.Model : si.Manufacturer;
        d["unitSerial"] = si.SerialSource == "BIOS" ? si.Serial : "";
        d["computerName"] = Environment.MachineName;
        d["date"] = start.ToString("yyyy-MM-dd", Inv);
        d["startTime"] = start.ToString("HH:mm", Inv);
        d["endTime"] = end.ToString("HH:mm", Inv);
        d["reason"] = r["reason"]; d["startEnd"] = r["startEnd"];
        d["runtime"] = Hm(r["runtimeS"]);
        d["estFull"] = Hm(r["estFullS"]); d["estFullEnergy"] = Hm(r["estFullEnergyS"]);
        d["avgW"] = W(S.Segments.Count > 0 ? Rules.AvgW(S.Segments) : null); d["minW"] = W(S.MinW); d["maxW"] = W(S.MaxW);
        double?[] h = Rules.Health(si.TotalFull(), si.TotalDesign());
        d["healthPct"] = h[1].HasValue ? h[1].Value.ToString("0.0", Inv) + "%" : "n/a";   // shown value: min(100, raw)
        List<double?> ends = new List<double?>();
        if (S.EndFull != null) foreach (double? v in S.EndFull.Values) ends.Add(v);
        StringBuilder bat = new StringBuilder();
        for (int i = 0; i < si.Batteries.Count; i++)
        {
            BatteryInfo b = si.Batteries[i];
            double?[] bh = b.Relative ? new double?[] { null, null } : Rules.Health(b.FullMwh, b.DesignMwh);
            string fullTxt = b.Relative ? "n/a" : Wh(b.FullMwh);
            double? e = ends.Count == si.Batteries.Count ? ends[i] : null;
            if (!b.Relative && e.HasValue && b.FullMwh.HasValue && Math.Abs(e.Value - b.FullMwh.Value) > b.FullMwh.Value * 0.005) fullTxt += " → " + Wh(e);
            bat.Append("<tr><td>").Append(Rules.EscapeHtml(b.Id)).Append("</td><td>").Append(Rules.EscapeHtml(b.Manufacturer))
               .Append("</td><td>").Append(Rules.EscapeHtml(b.Chemistry)).Append("</td><td class=\"n\">").Append(b.Relative ? "n/a" : Wh(b.DesignMwh))
               .Append("</td><td class=\"n\">").Append(fullTxt).Append("</td><td class=\"n\">").Append(b.Cycles.HasValue ? Rules.Num(b.Cycles.Value) : "n/a")
               .Append("</td><td class=\"n\">").Append(bh[1].HasValue ? bh[1].Value.ToString("0.0", Inv) + "%" : "n/a").Append("</td></tr>");
        }
        d["batteryRowsHtml"] = bat.ToString();
        d["gapsPauses"] = GapsPauses(S);
        d["saver"] = r["saver"];
        d["brightness"] = (S.BrightnessStart.HasValue || S.BrightnessEnd.HasValue) ? P(S.BrightnessStart) + " → " + P(S.BrightnessEnd) : "n/a";
        d["plan"] = si.PowerPlan; d["windows"] = si.Windows;
        d["alarmNote"] = S.Alarm && !S.AlarmAck ? "Alarm not acknowledged" : "";
        d["chartSvgHtml"] = Chart(en.Samples, S, 1000, 220);
        StringBuilder ev = new StringBuilder();
        int n = 0;
        foreach (EventRow x in S.Events)
        {
            if (n++ >= 25) break;
            ev.Append("<tr class=\"").Append(x.Kind).Append("\"><td>").Append(Rules.ToLocal(x.Ms).ToString("HH:mm:ss", Inv)).Append("</td><td>")
              .Append(Rules.EscapeHtml(x.Label)).Append("</td><td class=\"n\">").Append(x.Pct.HasValue ? Rules.Num(x.Pct.Value) : "")
              .Append("</td><td class=\"n\">").Append(x.Mwh.HasValue ? Wh(x.Mwh) : "").Append("</td><td class=\"n\">")
              .Append(x.RateMw.HasValue ? (x.RateMw.Value / 1000).ToString("0.0", Inv) : "").Append("</td><td class=\"n\">")
              .Append(Rules.FmtElapsed(x.ElapsedS)).Append("</td></tr>");
        }
        d["eventRowsHtml"] = ev.ToString();
        d["eventsMore"] = S.Events.Count > 25 ? (S.Events.Count - 25) + " more in the CSV" : "";
        d["version"] = AppVersion.Version;
        return d;
    }

    // "1 gap · 0:30 (slept) · 1 pause · 1:00" or "None".
    public static string GapsPauses(SessionData S)
    {
        List<string> parts = new List<string>();
        if (S.Gaps.Count > 0)
        {
            double gs = 0; List<string> why = new List<string>();
            foreach (GapInfo g in S.Gaps) { gs += (g.ToMs - g.FromMs) / 1000.0; if (!why.Contains(g.Reason)) why.Add(g.Reason); }
            parts.Add(S.Gaps.Count + (S.Gaps.Count == 1 ? " gap" : " gaps"));
            parts.Add(Rules.FmtHm(gs) + " (" + string.Join(", ", why.ToArray()) + ")");
        }
        if (S.Pauses.Count > 0)
        {
            double ps = 0;
            foreach (PauseInfo p in S.Pauses) ps += ((p.ToMs ?? (S.EndMs ?? p.FromMs)) - p.FromMs) / 1000.0;
            parts.Add(S.Pauses.Count + (S.Pauses.Count == 1 ? " pause" : " pauses"));
            parts.Add(Rules.FmtHm(ps));
        }
        return parts.Count == 0 ? "None" : string.Join(" · ", parts.ToArray());
    }

    // Static SVG chart: % (area + line), W (thin line, right axis), alarm line, gaps shaded, pauses hatched.
    // x = wall time since the clock started, so gaps and pauses show as real spans.
    public static string Chart(List<Sample> samples, SessionData S, int Wd, int Ht)
    {
        double L = 34, R = 40, T = 8, B = 20;
        long t0 = S.UnplugMs ?? S.StartedMs;
        long tEnd = S.EndMs ?? (samples.Count > 0 ? samples[samples.Count - 1].Ms : t0);
        double span = Math.Max(3600, (tEnd - t0) / 1000.0);
        double maxW = 20;
        foreach (Sample s in samples) if (!s.Ac && s.RateMw.HasValue && s.RateMw.Value / 1000 > maxW) maxW = Math.Ceiling(s.RateMw.Value / 10000) * 10;
        Func<double, double> x = delegate(double sec) { return L + Math.Max(0, sec) / span * (Wd - L - R); };
        Func<double, double> y = delegate(double p) { return T + (1 - p / 100) * (Ht - T - B); };
        Func<double, double> yw = delegate(double w) { return T + (1 - w / maxW) * (Ht - T - B); };
        StringBuilder g = new StringBuilder();
        g.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(Wd).Append(' ').Append(Ht).Append("\" width=\"100%\" role=\"img\" aria-label=\"Charge and power over time\">");
        g.Append("<defs><pattern id=\"hatch\" width=\"6\" height=\"6\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\"><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"6\" stroke=\"#9aa3ad\" stroke-width=\"2\"/></pattern></defs>");
        foreach (int p in new int[] { 0, 25, 50, 75, 100 })
            g.AppendFormat(Inv, "<line x1=\"{0}\" x2=\"{1}\" y1=\"{2:0.#}\" y2=\"{2:0.#}\" stroke=\"#dfe3e8\"/><text x=\"{3}\" y=\"{4:0.#}\" font-size=\"10\" text-anchor=\"end\" fill=\"#6b737c\">{5}</text>", L, Wd - R, y(p), L - 6, y(p) + 4, p);
        foreach (double w in new double[] { 0, maxW / 2, maxW })
            g.AppendFormat(Inv, "<text x=\"{0}\" y=\"{1:0.#}\" font-size=\"10\" fill=\"#b86e00\">{2:0}W</text>", Wd - R + 6, yw(w) + 4, w);
        int hours = (int)Math.Ceiling(span / 3600), step = hours > 12 ? 2 : 1;
        for (int hh = 0; hh <= hours; hh += step)
            g.AppendFormat(Inv, "<text x=\"{0:0.#}\" y=\"{1}\" font-size=\"10\" text-anchor=\"middle\" fill=\"#6b737c\">{2}h</text>", x(hh * 3600), Ht - 5, hh);
        foreach (GapInfo gp in S.Gaps)
            g.AppendFormat(Inv, "<rect x=\"{0:0.#}\" y=\"{1}\" width=\"{2:0.#}\" height=\"{3}\" fill=\"#eef1f4\"/>", x((gp.FromMs - t0) / 1000.0), T, Math.Max(2, x((gp.ToMs - t0) / 1000.0) - x((gp.FromMs - t0) / 1000.0)), Ht - T - B);
        foreach (PauseInfo pp in S.Pauses)
        {
            long to = pp.ToMs ?? tEnd;
            g.AppendFormat(Inv, "<rect x=\"{0:0.#}\" y=\"{1}\" width=\"{2:0.#}\" height=\"{3}\" fill=\"url(#hatch)\" opacity=\".5\"/>", x((pp.FromMs - t0) / 1000.0), T, Math.Max(2, x((to - t0) / 1000.0) - x((pp.FromMs - t0) / 1000.0)), Ht - T - B);
        }
        g.AppendFormat(Inv, "<line x1=\"{0}\" x2=\"{1}\" y1=\"{2:0.#}\" y2=\"{2:0.#}\" stroke=\"#c23934\" stroke-dasharray=\"4 4\"/>", L, Wd - R, y(S.AlarmPct));
        StringBuilder pl = new StringBuilder(), wl = new StringBuilder();
        double firstX = -1, lastX = 0;
        foreach (Sample s in samples)
        {
            if (s.Ms < t0 || !s.Pct.HasValue) continue;
            double xx = x((s.Ms - t0) / 1000.0);
            if (firstX < 0) firstX = xx;
            lastX = xx;
            pl.AppendFormat(Inv, "{0:0.#},{1:0.#} ", xx, y(s.Pct.Value));
            if (!s.Ac && s.RateMw.HasValue) wl.AppendFormat(Inv, "{0:0.#},{1:0.#} ", xx, yw(s.RateMw.Value / 1000));
        }
        if (pl.Length > 0)
        {
            g.AppendFormat(Inv, "<path d=\"M{0:0.#},{1:0.#} L{2} L{3:0.#},{1:0.#} Z\" fill=\"#0070d2\" opacity=\".08\"/>", firstX, y(0), pl.ToString().Trim().Replace(" ", " L"), lastX);
            g.Append("<polyline fill=\"none\" stroke=\"#0070d2\" stroke-width=\"2\" points=\"").Append(pl.ToString().Trim()).Append("\"/>");
        }
        if (wl.Length > 0) g.Append("<polyline fill=\"none\" stroke=\"#b86e00\" stroke-width=\"1.2\" points=\"").Append(wl.ToString().Trim()).Append("\"/>");
        g.Append("</svg>");
        return g.ToString();
    }

    // QA only (--qa-out <dir>): write the report + CSV there instead of the Desktop, so test runs leave the Desktop alone.
    public static string QaOutDir;
    public static string Desktop() { return QaOutDir ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }

    static string BaseName(Engine en)
    {
        StaticInfo si = en.S.StartInfo ?? en.Info;
        return "Battery " + Rules.SanitizeSerial(si.Serial) + " " + Rules.ToLocal(en.S.UnplugMs ?? en.S.StartedMs).ToString("yyyy-MM-dd HHmm", Inv);
    }

    // First free "<base>", "<base> (2)", ... for which none of the extensions exist.
    public static string FreeBase(string dir, string baseName, string[] exts)
    {
        for (int i = 1; i < 1000; i++)
        {
            string b = Path.Combine(dir, i == 1 ? baseName : baseName + " (" + i + ")");
            bool taken = false;
            foreach (string e in exts) if (File.Exists(b + e)) taken = true;
            if (!taken) return b;
        }
        return Path.Combine(dir, baseName + " " + Guid.NewGuid().ToString("N").Substring(0, 6));
    }

    public static string CsvText(Engine en)
    {
        string f = Path.Combine(Path.Combine(en.SessionsDir, en.S.Id), "samples.csv");
        try
        {
            if (File.Exists(f))
                using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader r = new StreamReader(fs, Encoding.UTF8)) return r.ReadToEnd();
        }
        catch { }
        StringBuilder sb = new StringBuilder(en.CsvHeader() + "\r\n");
        foreach (Sample s in en.Samples) sb.Append(Engine.CsvLine(s)).Append("\r\n");
        return sb.ToString();
    }

    static readonly UTF8Encoding Bom = new UTF8Encoding(true);

    // The last Desktop write failure (I3): Controlled Folder Access, an offline OneDrive Desktop, a read-only Desktop.
    public static Exception LastError;

    public static string SaveCsv(Engine en, bool final)
    {
        try
        {
            string b = FreeBase(Desktop(), BaseName(en), new string[] { ".csv" });
            File.WriteAllText(b + ".csv", CsvText(en), Bom);
            return b + ".csv";
        }
        catch (Exception ex) { LastError = ex; return null; }
    }

    public static string Html(Engine en) { return "<!DOCTYPE html>\n" + Rules.FillReport(Template(en.IndexHtml), ReportData(en)); }

    public static string[] SaveAll(Engine en)
    {
        try
        {
            string b = FreeBase(Desktop(), BaseName(en), new string[] { ".html", ".csv" });
            File.WriteAllText(b + ".html", Html(en), new UTF8Encoding(false));
            File.WriteAllText(b + ".csv", CsvText(en), Bom);
            return new string[] { b + ".html", b + ".csv" };
        }
        catch (Exception ex) { LastError = ex; return null; }
    }

    // I1: after OK, rewrite the same report file so it no longer says "Alarm not acknowledged".
    public static bool Resave(Engine en)
    {
        if (en.S == null || string.IsNullOrEmpty(en.S.ReportPath)) return false;
        try { File.WriteAllText(en.S.ReportPath, Html(en), new UTF8Encoding(false)); return true; }
        catch (Exception ex) { LastError = ex; return false; }
    }
}
