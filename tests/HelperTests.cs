// Battery-Logger/v14/tests/HelperTests.cs
// Console test runner (no framework), compiled by build.ps1 with the same csc + all src/*.cs (/main:HelperTests).
// Usage: HelperTests.exe <fixturesDir> <builtExePath> <indexHtmlPath>. Exit code = number of failures.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

static class HelperTests
{
    static int pass, fail;
    static string section = "";

    static void Ok(bool cond, string what)
    {
        if (cond) pass++;
        else { fail++; Console.WriteLine("  FAIL [" + section + "] " + what); }
    }
    static void Eq(object want, object got, string what)
    {
        bool same;
        if (want == null || got == null) same = want == null && got == null;
        else if (IsNum(want) && IsNum(got)) same = Math.Abs(Convert.ToDouble(want, CultureInfo.InvariantCulture) - Convert.ToDouble(got, CultureInfo.InvariantCulture)) < 1e-9;
        else same = Convert.ToString(want, CultureInfo.InvariantCulture) == Convert.ToString(got, CultureInfo.InvariantCulture);
        Ok(same, what + ": want " + Show(want) + ", got " + Show(got));
    }
    static bool IsNum(object o) { return o is int || o is long || o is double || o is decimal || o is float; }
    static string Show(object o) { return o == null ? "null" : "\"" + Convert.ToString(o, CultureInfo.InvariantCulture) + "\""; }
    static double? D(object o) { return o == null ? (double?)null : Convert.ToDouble(o, CultureInfo.InvariantCulture); }
    static void Section(string s) { section = s; Console.WriteLine("- " + s); }

    static int Main(string[] args)
    {
        string fx = args.Length > 0 ? args[0] : "tests\\fixtures";
        string exe = args.Length > 1 ? args[1] : "dist\\Laptap Battery Logger.exe";
        string index = args.Length > 2 ? args[2] : "src\\web\\index.html";
        JavaScriptSerializer js = new JavaScriptSerializer();
        try
        {
            RulesFixtures((Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(Path.Combine(fx, "rules.json"))));
            BatteryReports(fx);
            EventFixtures((Dictionary<string, object>)js.DeserializeObject(File.ReadAllText(Path.Combine(fx, "events.json"))));
            Placeholders();
            RequestChecks();
            ReportFill(File.ReadAllText(index));
            ContractShapes(File.ReadAllText(index));
            AuditFixes(File.ReadAllText(index));
            V15Tests.Run(delegate(bool c, string w) { Ok(c, w); }, delegate(object a, object b, string w) { Eq(a, b, w); }, delegate(string n) { Section(n); }, exe);
            CsvRoundTrip();
            Parsers();
            PeFlags(exe);
        }
        catch (Exception ex) { fail++; Console.WriteLine("  CRASH [" + section + "] " + ex); }
        Console.WriteLine("HelperTests: " + pass + " passed, " + fail + " failed");
        return fail;
    }

    static object[] A(object o) { return (object[])o; }

    static void RulesFixtures(Dictionary<string, object> f)
    {
        Section("Rules.cs vs rules.json");
        foreach (object[] c in A(f["intervalFor"])) Eq(c[3], Rules.IntervalFor(D(c[0]), Convert.ToInt32(c[1]), (bool)c[2]), "intervalFor " + Show(c[0]));
        foreach (object[] c in A(f["detectGap"])) Eq(c[3], Rules.DetectGap(c[0] == null ? (long?)null : Convert.ToInt64(c[0]), Convert.ToInt64(c[1]), Convert.ToInt32(c[2])), "detectGap " + Show(c[1]));
        foreach (object[] c in A(f["canStart"])) Eq(c[1], Rules.CanStart(D(c[0])), "canStart " + Show(c[0]));
        foreach (Dictionary<string, object> c in A(f["segmentCases"]))
        {
            List<Ev> evs = new List<Ev>();
            foreach (Dictionary<string, object> e in A(c["events"])) evs.Add(new Ev(Convert.ToInt64(e["ms"]), (string)e["type"], D(e["pct"]), D(e["mwh"])));
            List<Seg> s = Rules.Segments(evs);
            string n = (string)c["name"];
            Eq(c["segments"], s.Count, n + " segments");
            Eq(c["countedTime"], Rules.CountedTime(s), n + " countedTime");
            Eq(c["countedDrop"], Rules.CountedDrop(s), n + " countedDrop");
            Eq(c["countedEnergy"], Rules.CountedEnergy(s), n + " countedEnergy");
            Eq(c["estFull"], Rules.EstFull(s), n + " estFull");
            Eq(c["avgW"], Rules.AvgW(s), n + " avgW");
            Eq(c["estFullEnergy"], Rules.EstFullEnergy(D(c["fullMwh"]), Rules.AvgW(s)), n + " estFullEnergy");
        }
        foreach (object[] c in A(f["health"]))
        {
            double?[] h = Rules.Health(D(c[0]), D(c[1]));
            Eq(c[2], h[0], "health raw " + Show(c[0])); Eq(c[3], h[1], "health shown " + Show(c[0]));
        }
        foreach (Dictionary<string, object> c in A(f["etaToAlarm"])) Eq(c["expect"], Rules.EtaToAlarm(EtaPoints((string)c["points"]), Convert.ToInt32(c["alarm"])), "eta " + c["points"]);
        foreach (object[] c in A(f["fmtElapsed"])) Eq(c[1], Rules.FmtElapsed(Convert.ToDouble(c[0])), "fmtElapsed " + Show(c[0]));
        foreach (object[] c in A(f["fmtHm"])) Eq(c[1], Rules.FmtHm(Convert.ToDouble(c[0])), "fmtHm " + Show(c[0]));
        foreach (object[] c in A(f["isoLocal"])) Eq(c[2], Rules.IsoLocal(Convert.ToInt64(c[0]), Convert.ToInt32(c[1])), "isoLocal " + Show(c[1]));
        foreach (object[] c in A(f["csvRow"])) Eq(c[1], Rules.CsvRow(A(c[0])), "csvRow");
        foreach (object[] c in A(f["escapeHtml"])) Eq(c[1], Rules.EscapeHtml((string)c[0]), "escapeHtml");
        foreach (object[] c in A(f["fillReport"]))
        {
            string o = Rules.FillReport((string)c[0], (Dictionary<string, object>)c[1]);
            Eq(c[2], o, "fillReport"); Ok(!o.Contains("{{"), "fillReport leaves no slot");
        }
        foreach (object[] c in A(f["sanitizeSerial"])) Eq(c[1], Rules.SanitizeSerial((string)c[0]), "sanitizeSerial " + Show(c[0]));
        foreach (object[] c in A(f["isPlaceholderSerial"])) Eq(c[1], Rules.IsPlaceholderSerial((string)c[0]), "isPlaceholderSerial " + Show(c[0]));
        foreach (object[] c in A(f["validSample"]))
        {
            Dictionary<string, object> s = (Dictionary<string, object>)c[0], w = (Dictionary<string, object>)c[2];
            double? r = D(s["rateMw"]), m = D(s["mwh"]), p = D(s["pct"]);
            Rules.ValidSample(ref r, ref m, ref p, D(c[1]));
            Eq(w["rateMw"], r, "validSample rate"); Eq(w["mwh"], m, "validSample mwh"); Eq(w["pct"], p, "validSample pct");
        }
        foreach (object[] c in A(f["endReason"])) Eq(c[3], Rules.EndReason((string)c[0], D(c[1]), Convert.ToDouble(c[2])), "endReason " + c[0]);
        foreach (object[] c in A(f["powerState"])) Eq(c[3], Rules.PowerState((bool)c[0], (bool)c[1], D(c[2])), "powerState");
    }

    static List<double[]> EtaPoints(string kind)
    {
        List<double[]> p = new List<double[]>();
        if (kind.StartsWith("linear")) for (int e = 0; e <= 900; e += 60) p.Add(new double[] { e, 50 - e / 120.0 });
        else if (kind.StartsWith("four")) for (int e = 0; e < 240; e += 60) p.Add(new double[] { e, 90 - e / 60.0 });
        else for (int e = 0; e <= 900; e += 60) p.Add(new double[] { e, 80 });
        return p;
    }

    static void BatteryReports(string fx)
    {
        Section("battery report XML");
        List<BatteryInfo> one = Battery.ParseReport(File.ReadAllText(Path.Combine(fx, "report-1battery.xml")));
        Eq(1, one.Count, "1 battery count");
        Eq(51528.0, one[0].DesignMwh, "design"); Eq(47378.0, one[0].FullMwh, "full"); Eq(null, one[0].Cycles, "cycle 0 = n/a");
        Eq("Notebook", one[0].Manufacturer, "maker"); Eq("LION", one[0].Chemistry, "chemistry"); Ok(!one[0].Relative, "not relative");
        Eq(91.9, Rules.Health(one[0].FullMwh, one[0].DesignMwh)[1], "health 91.9");

        List<BatteryInfo> two = Battery.ParseReport(File.ReadAllText(Path.Combine(fx, "report-2batteries.xml")));
        Eq(2, two.Count, "2 batteries");
        Eq(412.0, two[0].Cycles, "cycles b0"); Eq(389.0, two[1].Cycles, "cycles b1");
        StaticInfo si = new StaticInfo(); si.Batteries = two;
        Eq(58896.0, si.TotalFull(), "total full"); Eq(70720.0, si.TotalDesign(), "total design");
        Eq(83.3, Rules.Health(si.TotalFull(), si.TotalDesign())[1], "total health = ΣFull ÷ ΣDesign (bug 14)");
        Eq(90.0, Rules.Health(two[0].FullMwh, two[0].DesignMwh)[1], "b0 health");
        Eq(80.0, Rules.Health(two[1].FullMwh, two[1].DesignMwh)[1], "b1 health");

        List<BatteryInfo> c0 = Battery.ParseReport(File.ReadAllText(Path.Combine(fx, "report-cycle0.xml")));
        Eq(null, c0[0].Cycles, "cycle 0 → n/a");

        List<BatteryInfo> rel = Battery.ParseReport(File.ReadAllText(Path.Combine(fx, "report-relative.xml")));
        Ok(rel[0].Relative, "relative capacity flagged");
        si = new StaticInfo(); si.Batteries = rel;
        Eq(null, si.TotalFull(), "relative: no Wh"); Eq(null, Rules.Health(si.TotalFull(), si.TotalDesign())[1], "relative: health n/a");

        List<BatteryInfo> nd = Battery.ParseReport(File.ReadAllText(Path.Combine(fx, "report-nodesign.xml")));
        Eq(null, nd[0].DesignMwh, "missing design = null"); Eq(40000.0, nd[0].FullMwh, "full still read");
        Eq(null, Rules.Health(nd[0].FullMwh, nd[0].DesignMwh)[1], "missing design: health n/a");
    }

    static void EventFixtures(Dictionary<string, object> f)
    {
        Section("event-log classification");
        PowerEvent x = PowerEvents.FromXml((string)f["xmlSample"], "");
        Eq("Microsoft-Windows-Kernel-Power", x.Provider, "xml provider"); Eq(42, x.Id, "xml id");
        Eq("5", x.Data["TargetState"], "xml TargetState"); Eq(Convert.ToInt64(f["xmlSampleMs"]), x.Ms, "xml time");
        foreach (Dictionary<string, object> c in A(f["cases"]))
        {
            List<PowerEvent> evs = new List<PowerEvent>();
            foreach (Dictionary<string, object> e in A(c["events"]))
            {
                PowerEvent p = new PowerEvent(); p.Provider = (string)e["provider"]; p.Id = Convert.ToInt32(e["id"]); p.Message = (string)e["message"];
                foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)e["data"]) p.Data[kv.Key] = (string)kv.Value;
                evs.Add(p);
            }
            string n = (string)c["name"];
            Eq(c["gap"], PowerEvents.ClassifyGap(evs), n + " gap");
            Eq(c["critical"], PowerEvents.IsCritical(evs), n + " critical");
            Eq(c["recovery"], PowerEvents.ClassifyRecovery(evs), n + " recovery");
        }
    }

    static void Placeholders()
    {
        Section("placeholder serials");
        foreach (string s in new string[] { "To be filled by O.E.M.", "Default string", "System Serial Number", "0000000000", "", "   ", "None", "OEM", "Not Specified" })
            Ok(Rules.IsPlaceholderSerial(s), "placeholder: " + Show(s));
        foreach (string s in new string[] { "5CB4190FVX", "PF2ABCDE", "R90Z1234" }) Ok(!Rules.IsPlaceholderSerial(s), "real: " + s);
        Eq("5CB4190FVX", Rules.SanitizeSerial("5cb4190fvx "), "file-name serial");
    }

    static void RequestChecks()
    {
        Section("request checker");
        string tok = "0123456789abcdef0123456789abcdef";
        Eq(0, Server.CheckRequest("GET", true, "localhost:47600", 47600, "/", null, tok), "page, no token needed");
        Eq(0, Server.CheckRequest("GET", true, "localhost:47600", 47600, "/logic.js", null, tok), "logic.js, no token needed");
        Eq(0, Server.CheckRequest("GET", true, "localhost:47600", 47600, "/api/state", tok, tok), "api with token");
        Eq(0, Server.CheckRequest("POST", true, "LOCALHOST:47600", 47600, "/api/start", tok, tok), "host case-insensitive");
        Eq(403, Server.CheckRequest("GET", false, "localhost:47600", 47600, "/", null, tok), "non-loopback → 403");
        Eq(403, Server.CheckRequest("GET", false, "localhost:47600", 47600, "/api/state", tok, tok), "non-loopback api → 403");
        Eq(400, Server.CheckRequest("GET", true, "evil.example:47600", 47600, "/api/state", tok, tok), "wrong Host → 400");
        Eq(400, Server.CheckRequest("GET", true, "127.0.0.1:47600", 47600, "/", null, tok), "IP Host → 400");
        Eq(400, Server.CheckRequest("GET", true, "localhost:47601", 47600, "/", null, tok), "wrong port in Host → 400");
        Eq(403, Server.CheckRequest("GET", true, "localhost:47600", 47600, "/api/state", null, tok), "missing token → 403");
        Eq(403, Server.CheckRequest("POST", true, "localhost:47600", 47600, "/api/stop", "ffff", tok), "wrong token → 403");
        Eq(403, Server.CheckRequest("OPTIONS", true, "localhost:47600", 47600, "/api/stop", tok, tok), "OPTIONS → 403");
        Eq(404, Server.CheckRequest("GET", true, "localhost:47600", 47600, "/secret.txt", null, tok), "unknown path → 404");
        foreach (string ct in new string[] { "text/html; charset=utf-8", "application/json; charset=utf-8", "text/plain; charset=utf-8" })
            foreach (string k in Server.ResponseHeaders(ct).Keys) Ok(!k.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase), "no CORS header: " + k);
        Ok(Server.NewToken().Length == 32, "token is 32 hex");
        Ok(Server.NewToken() != Server.NewToken(), "token is random");

        Dictionary<string, object> snap = new Dictionary<string, object>();
        Dictionary<string, object> a = new Dictionary<string, object>(); a["rev"] = 3L;
        Dictionary<string, object> b = new Dictionary<string, object>(); b["rev"] = 7L;
        snap["samples"] = new Dictionary<string, object>[] { a, b }; snap["events"] = new Dictionary<string, object>[0]; snap["rev"] = 7L;
        Eq(1, ((IList)Server.Filter(snap, 5)["samples"]).Count, "state?since=5 sends only newer rows");
    }

    static Engine FakeDone(string indexHtml)
    {
        Engine en = new Engine();
        en.IndexHtml = indexHtml;
        en.S = new SessionData();
        en.S.Id = "test"; en.S.StartedMs = 1795654800000; en.S.UnplugMs = 1795654800000; en.S.Status = "done";
        en.S.StartInfo = new StaticInfo();
        en.S.StartInfo.Manufacturer = "HP"; en.S.StartInfo.Model = "<script>alert(1)</script>"; en.S.StartInfo.Serial = "5CB4190FVX";
        BatteryInfo bi = new BatteryInfo(); bi.Id = "BAT"; bi.Manufacturer = "Notebook"; bi.Chemistry = "LION"; bi.DesignMwh = 51528; bi.FullMwh = 47378;
        en.S.StartInfo.Batteries.Add(bi);
        en.S.Segments.Add(new Seg { StartMs = 1795654800000, EndMs = 1795654800000 + 3600000, StartPct = 98, EndPct = 60, StartMwh = 46000, EndMwh = 28000 });
        en.S.Gaps.Add(new GapInfo { FromMs = 1795654800000 + 3600000, ToMs = 1795654800000 + 4800000, Reason = "slept" });
        en.S.StartPct = 98; en.S.EndPct = 60; en.S.EndMs = 1795654800000 + 4800000; en.S.EndKind = "stop"; en.S.EndText = "Stopped early";
        en.S.MinW = 14.2; en.S.MaxW = 21.7;
        for (int i = 0; i < 40; i++) en.S.Events.Add(new EventRow { Ms = 1795654800000 + i * 60000, Type = i == 0 ? "start" : "saver_on", Label = i == 0 ? "Test started" : "Reading <b>", Kind = "ev", Pct = 98 - i });
        Sample sm = new Sample(); sm.Ms = 1795654800000; sm.Pct = 98; sm.Mwh = 46000; sm.Rev = 1; en.Samples.Add(sm);
        return en;
    }

    static void ReportFill(string indexHtml)
    {
        Section("report template (api-contract.md slots)");
        string tpl = Report.Template(indexHtml);
        foreach (string slot in Report.Slots) Ok(tpl.Contains("{{" + slot + "}}"), "template has {{" + slot + "}}");
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(tpl, @"\{\{(\w+)\}\}"))
            Ok(Array.IndexOf(Report.Slots, m.Groups[1].Value) >= 0, "template slot {{" + m.Groups[1].Value + "}} is one the exe fills");
        Engine en = FakeDone(indexHtml);
        Dictionary<string, object> data = Report.ReportData(en);
        foreach (string slot in Report.Slots) Ok(data.ContainsKey(slot), "exe fills " + slot);
        string html = Rules.FillReport(tpl, data);
        Ok(!html.Contains("{{"), "no slot left in the filled report");
        Ok(!html.Contains("<script>alert"), "model name is escaped");
        Ok(html.Contains("&lt;script&gt;alert(1)&lt;/script&gt;"), "escaped model is shown");
        Ok(html.Contains("15 more in the CSV"), "first 25 events + n more");
        Ok(html.Contains("91.9%"), "health in the report");
        Ok(html.Contains("1 gap · 0:20 (slept)"), "gap shown with reason: " + Report.GapsPauses(en.S));
        Ok(html.Contains("98% → 60%"), "start → end");
        Ok(!html.ToLowerInvariant().Contains("warranty") && !html.Contains("brand-new"), "brand truth rules");
        Ok(html.Contains("<svg"), "chart in the report");
        Eq("Battery 5CB4190FVX 2026-11-26 0900", Path.GetFileName(Report.FreeBase(Path.GetTempPath(), "Battery 5CB4190FVX 2026-11-26 0900", new string[] { ".zz-none" })), "file name");
    }

    static void HasKeys(object o, string what, params string[] keys)
    {
        Dictionary<string, object> d = o as Dictionary<string, object>;
        Ok(d != null, what + " is an object");
        if (d == null) return;
        foreach (string k in keys) Ok(d.ContainsKey(k), what + "." + k);
        foreach (string k in d.Keys) Ok(Array.IndexOf(keys, k) >= 0, what + " has no extra key " + k);
    }

    static void ContractShapes(string indexHtml)
    {
        Section("GET /api/state shapes (api-contract.md)");
        Engine en = FakeDone(indexHtml);
        en.Publish();
        Dictionary<string, object> s = Server.Filter(en.Snapshot, 0);
        HasKeys(s, "state", "rev", "version", "nowMs", "checks", "armed", "countdownEndsMs", "app", "unit", "batteries", "now", "session", "samples", "events", "result");
        Eq(AppVersion.Version, s["version"], "version");
        HasKeys(s["unit"], "unit", "maker", "model", "serial", "computerName");
        foreach (object b in (IList)s["batteries"]) HasKeys(b, "batteries[]", "id", "maker", "chemistry", "designMwh", "fullMwh", "fullEndMwh", "cycles");
        HasKeys(s["now"], "now", "pct", "mwh", "fullMwh", "rateMw", "ac", "charging", "saver");
        HasKeys(s["session"], "session", "id", "status", "startedMs", "alarmPct", "alarmPctMax", "alarm", "alarmAck", "segments");
        foreach (object g in (IList)((Dictionary<string, object>)s["session"])["segments"]) HasKeys(g, "segments[]", "startMs", "endMs", "startPct", "endPct", "startMwh", "endMwh");
        foreach (object x in (IList)s["samples"]) HasKeys(x, "samples[]", "rev", "ms", "elapsedS", "pct", "mwh", "fullMwh", "rateMw", "rateSrc", "voltageMv", "ac", "saver");
        Ok(((IList)s["events"]).Count == 0, "no events in the fake engine's published list (added directly)");
        EventRow e = new EventRow { Rev = 9, Ms = 1, Type = "gap", Note = "slept", FromMs = 1, ToMs = 2 };
        HasKeys(Engine.EventDict(e), "events[]", "rev", "ms", "elapsedS", "type", "pct", "mwh", "rateMw", "note", "fromMs", "toMs");
        HasKeys(s["result"], "result", "kind", "reason", "startEnd", "alarmAck", "runtimeS", "startPct", "endPct", "estFullS", "estFullEnergyS",
            "avgW", "minW", "maxW", "healthPct", "fullChangedMwh", "gapCount", "gapS", "pauseCount", "pauseS", "saver", "brightnessStart",
            "brightnessEnd", "plan", "windows", "files");
        Dictionary<string, object> r = (Dictionary<string, object>)s["result"];
        HasKeys(r["files"], "result.files", "html", "csv");
        Eq("Stopped early", r["reason"], "result.reason"); Eq("98% → 60%", r["startEnd"], "result.startEnd");
        Eq(3600.0, r["runtimeS"], "result.runtimeS"); Eq(91.9, r["healthPct"], "result.healthPct (raw)");
        Eq(1, r["gapCount"], "result.gapCount"); Eq(1200.0, r["gapS"], "result.gapS");
        Eq("on from 97% at 0:00", r["saver"], "result.saver text");
        Eq(18.0, r["avgW"], "result.avgW = 18000 mWh / 1 h");
        Ok(new JavaScriptSerializer().Serialize(s).Length > 0, "serializes");
    }

    static void AuditFixes(string indexHtml)
    {
        Section("audit I1 / I3");
        string tmp = Path.Combine(Path.GetTempPath(), "laptap-bl-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        string oldBase = Engine.BaseDir;
        try
        {
            Engine.BaseDir = tmp;   // session.json goes here, not into the real %LOCALAPPDATA%
            Engine en = FakeDone(indexHtml);
            en.S.Alarm = true; en.S.AlarmAck = false; en.S.EndKind = "alarm"; en.S.EndText = "Ended at alarm";
            en.S.ReportPath = Path.Combine(tmp, "Battery TEST.html");
            File.WriteAllText(en.S.ReportPath, Report.Html(en));
            Ok(File.ReadAllText(en.S.ReportPath).Contains("Alarm not acknowledged"), "I1 precondition: report written before OK says not acknowledged");
            Ok(Report.Html(en).StartsWith("<!DOCTYPE html>"), "M8: report has a doctype");
            en.AckAlarm();
            string after = File.ReadAllText(en.S.ReportPath);
            Ok(!after.Contains("Alarm not acknowledged"), "I1: OK re-saves the Desktop report without the note");
            Ok(after.Contains("Ended at alarm"), "I1: re-saved report is complete");
            Ok(en.S.AlarmAck, "I1: session marked acknowledged");

            // I3: a Desktop that can't be written gives null + the error, never a silent success.
            Report.QaOutDir = Path.Combine(tmp, "no-such-folder");
            Report.LastError = null;
            Ok(Report.SaveAll(en) == null, "I3: SaveAll returns null when the Desktop can't be written");
            Ok(Report.LastError != null, "I3: the error is kept for error.txt");
            Ok(Report.SaveCsv(en, false) == null, "I3: SaveCsv returns null too");
        }
        finally
        {
            Engine.BaseDir = oldBase; Report.QaOutDir = null;
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    static void CsvRoundTrip()
    {
        Section("CSV");
        Sample s = new Sample(); s.Ms = 1795654800000; s.ElapsedS = 61; s.Event = "reading"; s.Pct = 87; s.Mwh = 41000; s.FullMwh = 47378; s.RateMw = 8500; s.RateSrc = "wmi";
        s.Mv = 12001; s.Ac = false; s.Saver = true; s.B = new double?[] { 41000 }; s.Note = "a, \"b\"";
        string line = Engine.CsvLine(s);
        Ok(line.StartsWith(Rules.IsoLocal(1795654800000) + ",61,reading,87,41000,47378,8500,wmi,12001,0,1,41000,,"), "csv columns: " + line);
        List<string> c = Engine.ParseCsvLine(line);
        Eq(14, c.Count, "14 columns"); Eq("a, \"b\"", c[13], "note escaping round trip");
        string tmp = Path.Combine(Path.GetTempPath(), "laptap-bl-test-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            File.WriteAllText(tmp, new Engine().CsvHeader() + "\r\n" + line + "\r\n");
            List<Sample> back = Engine.ReadSamplesCsv(tmp);
            Eq(1, back.Count, "read back"); Eq(1795654800000L, back[0].Ms, "time read back"); Eq(41000.0, back[0].Mwh, "mwh read back");
        }
        finally { try { File.Delete(tmp); } catch { } }
        Eq("time,elapsed_s,event,windows_pct,remaining_mwh,full_mwh,rate_mw,rate_src,voltage_mv,ac,saver,b0_mwh,b1_mwh,note", new Engine().CsvHeader(), "header");
    }

    static void Parsers()
    {
        Section("powercfg parsers");
        Eq("Balanced", Battery.ParsePlan("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)"), "plan name");
        Eq("n/a", Battery.ParsePlan(""), "plan missing");
        Eq(7, Battery.ParseCritical("    Current AC Power Setting Index: 0x00000005\r\n    Current DC Power Setting Index: 0x00000007"), "critical DC level");
        Eq(5, Battery.ParseCritical("nothing"), "critical default 5");
    }

    static void PeFlags(string exe)
    {
        Section("PE flags");
        Ok(File.Exists(exe), "built exe exists: " + exe);
        if (!File.Exists(exe)) return;
        byte[] b = File.ReadAllBytes(exe);
        int pe = BitConverter.ToInt32(b, 0x3C);
        ushort ch = BitConverter.ToUInt16(b, pe + 22);
        Ok((ch & 0x0400) != 0, "IMAGE_FILE_REMOVABLE_RUN_FROM_SWAP set");
        Ok((ch & 0x0800) != 0, "IMAGE_FILE_NET_RUN_FROM_SWAP set");
    }
}
