// tests/V16Tests.cs
// v16 C# tests (plan v16 §8.1): Start anyway, the per-reason pause flags, sleep / wake (command seam, both orders,
// backstop, critical → battery_ran_out, duplicate resume), segments with the new events, batteries joined by
// InstanceName, the CSV by header name (old + new layouts), /media ranges + the stalled-media check, the logo + CSP.
// Compiled into HelperTests.exe (with /define:TESTHOOKS); called from HelperTests.Main.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

class FakeHw16 : FakeHw
{
    public List<BatLive> Per = new List<BatLive>();
    public List<PowerEvent> Evs = new List<PowerEvent>();
    public StaticInfo Static;
    public override Sample ReadLive(double? fullMwh)
    {
        Sample s = base.ReadLive(fullMwh);
        foreach (BatLive b in Per) { BatLive c = new BatLive(); c.Id = b.Id; c.Mwh = b.Mwh; c.RateMw = b.RateMw; c.State = b.State; c.Mv = b.Mv; s.Per.Add(c); }
        return s;
    }
    public override List<PowerEvent> Events(long fromMs, long toMs) { return new List<PowerEvent>(Evs); }
    public override StaticInfo ReadStatic(bool fullReport) { return Static ?? base.ReadStatic(fullReport); }
}

class StallStream : MemoryStream
{
    public ManualResetEvent Go = new ManualResetEvent(false);
    public StallStream(byte[] b) : base(b) { }
    public override int Read(byte[] buffer, int offset, int count) { Go.WaitOne(10000); return base.Read(buffer, offset, count); }
}

static class V16Tests
{
    static Action<bool, string> Ok;
    static Action<object, object, string> Eq;
    static Action<string> Section;
    static string tmpRoot;

    public static void Run(Action<bool, string> ok, Action<object, object, string> eq, Action<string> section)
    {
        Ok = ok; Eq = eq; Section = section;
        tmpRoot = Path.Combine(Path.GetTempPath(), "laptap-bl-v16-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        string oldBase = Engine.BaseDir;
        try
        {
            Engine.BaseDir = Path.Combine(tmpRoot, "data"); Directory.CreateDirectory(Engine.BaseDir);
            Report.QaOutDir = Path.Combine(tmpRoot, "desk"); Directory.CreateDirectory(Report.QaOutDir);
            Updater.Progress = new UpdateProgress();
            StartAnyway();
            PauseFlags();
            Sleep();
            Segments();
            Batteries();
            CsvReader();
            ServerMedia();
            Brand();
        }
        finally
        {
            Engine.BaseDir = oldBase; Report.QaOutDir = null;
            try { Directory.Delete(tmpRoot, true); } catch { }
        }
    }

    // ---------------- helpers ----------------
    static Engine NewEngine(FakeHw hw, long[] poll)
    {
        Engine en = new Engine();
        en.Hw = hw; en.QaNoWindow = true;
        en.LastPollMs = delegate { return poll[0]; };
        en.IndexHtml = "<template id=\"report\">{{reason}}</template>";
        return en;
    }
    static void Step(Engine en, FakeHw hw, long[] poll, int ms) { hw.T += ms; poll[0] = hw.T; en.Tick(); }
    static void ClearSessions() { try { Directory.Delete(Path.Combine(Engine.BaseDir, "sessions"), true); } catch { } }
    static void StartTest(Engine en, FakeHw hw, long[] poll)
    {
        en.Tick(); hw.Ac = false; Step(en, hw, poll, 1000);
        for (int i = 0; i < 4 && (en.S == null || en.S.Status != "running"); i++) Step(en, hw, poll, 1000);
    }
    static List<string> Types(Engine en) { List<string> l = new List<string>(); foreach (EventRow e in en.S.Events) l.Add(e.Type); return l; }
    static int Count(Engine en, string type) { int n = 0; foreach (EventRow e in en.S.Events) if (e.Type == type) n++; return n; }
    static bool InTimeOrder(Engine en) { for (int i = 1; i < en.S.Events.Count; i++) if (en.S.Events[i].Ms < en.S.Events[i - 1].Ms) return false; return true; }
    static List<Ev> Evs(Engine en) { List<Ev> l = new List<Ev>(); foreach (EventRow e in en.S.Events) l.Add(new Ev(e.Ms, e.Type, e.Pct, e.Mwh)); return l; }
    static Dictionary<string, object> D(object o) { return (Dictionary<string, object>)o; }

    // ---------------- §2 Start anyway ----------------
    static void StartAnyway()
    {
        Section("v16 Start anyway");
        ClearSessions();
        FakeHw hw = new FakeHw(); long[] poll = { hw.T };
        Engine en = NewEngine(hw, poll);
        hw.Ac = false; hw.Pct = 82; en.Tick(); en.Publish();
        Eq(true, D(en.Snapshot["checks"])["startAnyway"], "offered: unplugged, 82% (< 96%, > alarm 15%)");
        hw.Pct = 15; en.Tick(); en.Publish();
        Eq(false, D(en.Snapshot["checks"])["startAnyway"], "D2: hidden at the alarm %");
        Dictionary<string, object> r = en.Command("start-anyway", null);
        Ok(!(bool)r["ok"] && ((string)r["error"]).StartsWith("Too low to start, even by hand"), "refused at the alarm %");
        hw.Pct = 97; en.Tick();
        Ok(!(bool)en.Command("start-anyway", null)["ok"], "refused at ≥ 96% (the countdown is the way)");
        hw.Pct = 82; hw.Ac = true; en.Tick();
        Ok(!(bool)en.Command("start-anyway", null)["ok"], "refused while plugged in");
        Ok(en.S == null, "nothing started by any refusal");
        hw.Ac = false; en.Tick();
        Dictionary<string, object> body = new Dictionary<string, object>(); body["pct"] = 99;   // a posted % is ignored
        r = en.Command("start-anyway", body);
        Eq(true, r["ok"], "Start anyway at 82%");
        Ok(en.S != null && en.S.Status == "running", "running at once (no 3-2-1)");
        Eq(82.0, en.S.ManualStart.Pct, "manualStart.pct = the exe's own reading, not the posted 99");
        List<string> t = Types(en);
        Ok(t.IndexOf("unplugged") >= 0 && t.IndexOf("manual_start") > t.IndexOf("unplugged") && t.IndexOf("start") > t.IndexOf("manual_start"), "events: unplugged → manual_start → start (" + string.Join(",", t.ToArray()) + ")");
        Ok(en.S.OpenSeg != null, "segment 1 opens");
        Eq(1, Rules.Segments(Evs(en)).Count == 0 && en.S.OpenSeg != null ? 1 : 0, "Rules.Segments: open (closed only at the end)");
        hw.Pct = 81; Step(en, hw, poll, 60000);
        Ok(en.S.CountedNow(hw.T) > 0, "runtime > 0");
        en.Publish();
        Eq(82.0, D(D(en.Snapshot["session"])["manualStart"])["pct"], "state session.manualStart");
        en.Command("stop", null);
        Eq("Manual start at 82% (below 96%)", Report.ManualNote(en.S), "Done / report line");
        Eq("n/a (too short to estimate)", Report.EstFullText(en.S, null), "M6: a short run can't estimate 100→0%");
        Ok(Rules.Segments(Evs(en)).Count == 1, "Rules.Segments rebuilds the one segment (unplugged … stop)");
        ClearSessions();
    }

    // ---------------- §3 pause flags (I3) ----------------
    static void PauseFlags()
    {
        Section("v16 pause / resume flags");
        Dictionary<string, object> nobody = null;
        // manual → plug → unplug stays paused; Resume needs the charger out (D4)
        ClearSessions();
        FakeHw hw = new FakeHw(); long[] poll = { hw.T }; Engine en = NewEngine(hw, poll);
        StartTest(en, hw, poll);
        Eq(true, en.Command("pause", nobody)["ok"], "pause");
        Ok(en.S.Status == "paused" && en.S.ManualPaused && en.S.OpenSeg == null, "paused by you, segment closed");
        en.Publish(); Eq("manual", ((string[])D(en.Snapshot["session"])["pauseReasons"])[0], "pauseReasons [manual]");
        hw.Ac = true; Step(en, hw, poll, 1000);
        Ok(en.S.ChargerIn && en.S.ManualPaused && en.S.Status == "paused", "plug while paused by you");
        en.Publish(); string[] why = (string[])D(en.Snapshot["session"])["pauseReasons"];
        Ok(why.Length == 2 && why[0] == "manual" && why[1] == "charger", "pauseReasons [manual, charger]");
        Dictionary<string, object> r = en.Command("resume", nobody);
        Ok(!(bool)r["ok"] && (string)r["error"] == "Unplug the charger to resume.", "D4: Resume refused with the charger in");
        hw.Ac = false; Step(en, hw, poll, 1000);
        Ok(en.S.Status == "paused" && en.S.ManualPaused, "unplug never lifts a manual pause");
        Eq(0, Count(en, "charger_out"), "no opening event while still paused (I4)");
        Eq(true, en.Command("resume", nobody)["ok"], "Resume");
        Ok(en.S.Status == "running" && en.S.OpenSeg != null, "running again, a new segment");
        Eq(1, Count(en, "resume"), "one resume event");
        // charger only → unplug resumes
        hw.Ac = true; Step(en, hw, poll, 1000);
        Ok(en.S.Status == "paused" && !en.S.ManualPaused && en.S.ChargerIn, "charger pause");
        hw.Ac = false; Step(en, hw, poll, 1000);
        Ok(en.S.Status == "running", "charger only → unplug resumes");
        // the alarm while paused by you ends the test (D5)
        en.Command("pause", nobody);
        hw.Pct = 14; hw.Mwh = 7000; Step(en, hw, poll, 61000);
        Ok(en.S.Status == "done" && en.S.EndKind == "alarm", "D5: the alarm ends a manually paused test");
        Ok(Rules.Segments(Evs(en)).Count == en.S.Segments.Count, "Rules.Segments = the engine's segments (" + en.S.Segments.Count + ")");
        // manual → sleep → wake stays paused
        ClearSessions();
        FakeHw h2 = new FakeHw(); long[] p2 = { h2.T }; Engine e2 = NewEngine(h2, p2);
        StartTest(e2, h2, p2);
        e2.Command("pause", nobody);
        SleepFor(e2, h2, p2, 10 * 60000, true);
        Ok(e2.S.Status == "paused" && e2.S.ManualPaused && !e2.S.Asleep, "manual → sleep → wake stays paused");
        Eq(0, Count(e2, "wake"), "no opening wake while paused by you");
        Eq(1, Count(e2, "wake_paused"), "a wake_paused row instead");
        e2.Command("stop", null);
        ClearSessions();
    }

    static Dictionary<string, object> Ms(long ms) { Dictionary<string, object> b = new Dictionary<string, object>(); b["ms"] = ms; return b; }

    // suspend now, sleep `ms` (no ticks), then the resume callback (or not) and ticks until the 5 s settle has passed.
    static void SleepFor(Engine en, FakeHw hw, long[] poll, long ms, bool resumeFirst)
    {
        en.Command("suspend", Ms(hw.T));
        hw.T += ms; poll[0] = hw.T;
        if (resumeFirst) en.Command("resume-power", Ms(hw.T));
        en.Tick();
        if (!resumeFirst) en.Command("resume-power", Ms(hw.T));
        for (int i = 0; i < 7; i++) Step(en, hw, poll, 1000);
    }

    // ---------------- §4 sleep / wake ----------------
    static void Sleep()
    {
        Section("v16 sleep / wake");
        foreach (bool resumeFirst in new bool[] { true, false })
        {
            ClearSessions();
            FakeHw hw = new FakeHw(); long[] poll = { hw.T }; Engine en = NewEngine(hw, poll);
            StartTest(en, hw, poll);
            Step(en, hw, poll, 60000);
            double before = en.S.CountedNow(hw.T);
            string order = resumeFirst ? "callback before tick" : "tick before callback";
            en.Command("suspend", Ms(hw.T));
            Ok(en.S.Asleep && en.S.Status == "paused" && en.S.OpenSeg == null, order + ": suspend → asleep, segment closed");
            en.Publish(); Eq("sleep", ((string[])D(en.Snapshot["session"])["pauseReasons"])[0], "pauseReasons [sleep]");
            en.Command("suspend", Ms(hw.T + 10));
            Eq(1, Count(en, "sleep"), order + ": a second suspend is a no-op");
            hw.T += 30 * 60000; poll[0] = hw.T;
            if (resumeFirst) { en.Command("resume-power", Ms(hw.T)); en.Command("resume-power", Ms(hw.T)); }
            en.Tick();
            if (!resumeFirst) { en.Command("resume-power", Ms(hw.T)); en.Command("resume-power", Ms(hw.T)); }
            Ok(en.S.Asleep, order + ": still asleep during the 5 s settle");
            for (int i = 0; i < 7; i++) Step(en, hw, poll, 1000);
            Ok(!en.S.Asleep && en.S.Status == "running" && en.S.OpenSeg != null, order + ": wake → running, a new segment");
            Eq(1, Count(en, "sleep"), order + ": one sleep"); Eq(1, Count(en, "wake"), order + ": one wake (duplicate resume = no-op)");
            Eq(0, Count(en, "gap"), order + ": no separate gap event");
            Ok(InTimeOrder(en), order + ": events in time order");
            Ok(en.S.CountedNow(hw.T) - before < 20, order + ": the 30 min asleep isn't counted");
            Eq(1, en.S.Gaps.Count, order + ": one sleep pause recorded");
            en.Publish(); Ok(Convert.ToInt64(D(en.Snapshot["session"])["sleepMs"]) >= 29 * 60000, "session.sleepMs");
            en.Command("stop", null);
            string csv = File.ReadAllText(Path.Combine(Path.Combine(Path.Combine(Engine.BaseDir, "sessions"), en.S.Id), "samples.csv"));
            Ok(csv.Contains(",sleep,") && csv.Contains(",wake,") && csv.Contains(",gap,"), order + ": CSV has sleep + wake + the compatibility gap row");
            Ok(Rules.Segments(Evs(en)).Count == en.S.Segments.Count, order + ": Rules.Segments = the engine's segments");
        }

        // RESUMEAUTOMATIC alone (no RESUMESUSPEND): handled; the resume callback lost entirely: the tick gap wakes it
        ClearSessions();
        FakeHw h3 = new FakeHw(); long[] p3 = { h3.T }; Engine e3 = NewEngine(h3, p3);
        StartTest(e3, h3, p3);
        Eq("resume-power", PowerNotify.CommandFor(PowerNotify.PBT_APMRESUMEAUTOMATIC), "PBT_APMRESUMEAUTOMATIC → resume");
        Eq("resume-power", PowerNotify.CommandFor(PowerNotify.PBT_APMRESUMESUSPEND), "PBT_APMRESUMESUSPEND → resume");
        Eq("suspend", PowerNotify.CommandFor(PowerNotify.PBT_APMSUSPEND), "PBT_APMSUSPEND → suspend");
        Eq(null, PowerNotify.CommandFor(10), "other broadcasts ignored");
        e3.Command("suspend", Ms(h3.T));
        h3.T += 15 * 60000; p3[0] = h3.T; e3.Tick();
        for (int i = 0; i < 7; i++) Step(e3, h3, p3, 1000);
        Ok(!e3.S.Asleep && e3.S.Status == "running", "a lost resume callback: the tick gap wakes it anyway");
        e3.Command("stop", null);

        // backstop only (Modern Standby: no suspend notification) → one sleep pair
        ClearSessions();
        FakeHw h4 = new FakeHw(); long[] p4 = { h4.T }; Engine e4 = NewEngine(h4, p4);
        StartTest(e4, h4, p4);
        Step(e4, h4, p4, 60000);
        long gapStart = e4.S.LastSampleMs.Value;
        h4.T += 40 * 60000; p4[0] = h4.T; e4.Tick();
        Eq(1, Count(e4, "sleep"), "backstop: one sleep"); Eq(1, Count(e4, "wake"), "backstop: one wake");
        Eq(0, Count(e4, "gap"), "backstop: no separate gap pause");
        EventRow sl = e4.S.Events.Find(delegate(EventRow x) { return x.Type == "sleep"; });
        Eq(gapStart, sl.Ms, "backstop: sleep stamped at the gap's real start");
        Ok(InTimeOrder(e4), "backstop: events in time order");
        Ok(e4.S.Status == "running", "backstop: running again");
        e4.Command("resume-power", Ms(h4.T)); Step(e4, h4, p4, 6000);
        Eq(1, Count(e4, "wake"), "a late resume after the backstop is a no-op");
        e4.Command("stop", null);

        // critical → battery_ran_out (D7)
        ClearSessions();
        FakeHw16 h5 = new FakeHw16(); long[] p5 = { h5.T }; Engine e5 = NewEngine(h5, p5);
        StartTest(e5, h5, p5);
        Step(e5, h5, p5, 60000);
        PowerEvent k = new PowerEvent(); k.Provider = "Microsoft-Windows-Kernel-Power"; k.Id = 42; k.Message = "The system is entering sleep.\r\nSleep Reason: Battery Critical";
        k.Data["TargetState"] = "5"; h5.Evs.Add(k);
        SleepFor(e5, h5, p5, 20 * 60000, true);
        Ok(e5.S.Status == "done" && e5.S.EndKind == "battery_ran_out", "critical hibernate → battery_ran_out");
        Ok(e5.S.EndText.StartsWith("Battery ran out: Windows hibernated at "), "result: " + e5.S.EndText);
        Eq(1, Count(e5, "battery_ran_out"), "battery_ran_out event");
        Ok(e5.S.ReportPath != null, "the report is still saved");
        ClearSessions();
    }

    // ---------------- §7 segments ----------------
    static void Segments()
    {
        Section("v16 segments with the new events");
        List<Ev> l = new List<Ev>();
        l.Add(new Ev(0, "unplugged", 100, 50000)); l.Add(new Ev(60, "pause", 99, 49500)); l.Add(new Ev(70, "charger_in", 99, 49500));
        l.Add(new Ev(80, "resume", 99, 49500)); l.Add(new Ev(90, "wake", 99, 49500));   // already open: no-op
        l.Add(new Ev(100, "sleep", 98, 49000)); l.Add(new Ev(110, "sleep", 98, 49000));   // already closed: no-op
        l.Add(new Ev(200, "wake", 98, 49000)); l.Add(new Ev(260, "charger_in", 97, 48500));
        l.Add(new Ev(300, "charger_out", 97, 48500)); l.Add(new Ev(400, "battery_ran_out", 5, 2000));
        List<Seg> s = Rules.Segments(l);
        Eq(4, s.Count, "unplugged–pause, resume–sleep, wake–charger_in, charger_out–battery_ran_out");
        Eq(60L, s[0].EndMs, "pause closes"); Eq(80L, s[1].StartMs, "resume opens"); Eq(100L, s[1].EndMs, "sleep closes");
        Eq(200L, s[2].StartMs, "wake opens"); Eq(400L, s[3].EndMs, "battery_ran_out closes");
    }

    // ---------------- §5 batteries (I7) ----------------
    static void Batteries()
    {
        Section("v16 batteries by InstanceName");
        List<KeyValuePair<string, double?>> full = new List<KeyValuePair<string, double?>>();
        full.Add(new KeyValuePair<string, double?>("ACPI\\PNP0C0A\\2_0", 30000));
        full.Add(new KeyValuePair<string, double?>("ACPI\\PNP0C0A\\1_0", 40000));   // shuffled WMI order
        Dictionary<string, double?> cyc = new Dictionary<string, double?>(); cyc["ACPI\\PNP0C0A\\1_0"] = 120; cyc["ACPI\\PNP0C0A\\2_0"] = 80;
        List<BatteryInfo> rep = new List<BatteryInfo>();
        BatteryInfo r1 = new BatteryInfo(); r1.Id = "A"; r1.Manufacturer = "SMP"; r1.Chemistry = "LIon"; r1.DesignMwh = 45000; rep.Add(r1);
        BatteryInfo r2 = new BatteryInfo(); r2.Id = "B"; r2.Manufacturer = "LGC"; r2.Chemistry = "LIon"; r2.DesignMwh = 35000; rep.Add(r2);
        List<BatteryInfo> j = Battery.JoinStatic(full, cyc, rep);
        Eq("ACPI\\PNP0C0A\\1_0", j[0].Id, "sorted by InstanceName (Battery 1 stable)");
        Eq(40000.0, j[0].FullMwh, "full joined by InstanceName, not by order");
        Eq(120.0, j[0].Cycles, "cycles by InstanceName");
        Eq("SMP", j[0].Manufacturer, "powercfg rows by index when the counts agree");
        rep.RemoveAt(1);
        List<BatteryInfo> j2 = Battery.JoinStatic(full, cyc, rep);
        Ok(j2[0].Manufacturer == null && j2[0].DesignMwh == null && j2[1].Chemistry == null, "mismatched powercfg count → blank fields");
        Eq(30000.0, j2[1].FullMwh, "WMI full still joined");
        Ok(Battery.JoinStatic(new List<KeyValuePair<string, double?>>(), cyc, rep).Count == 1, "no WMI rows → the powercfg rows (v15)");
        BatLive lv = Battery.LiveRow("x", 20000, false, true, 0, 6200, 11800);
        Ok(lv.State == "discharging" && lv.RateMw == 6200 && lv.Mv == 11800, "live row: discharging, rate, mV");

        // dynamic CSV header, removal keeps the columns, added battery logged
        ClearSessions();
        FakeHw16 hw = new FakeHw16(); long[] poll = { hw.T };
        StaticInfo si = new StaticInfo(); si.Manufacturer = "HP"; si.Model = "ZBook"; si.Serial = "S1";
        BatteryInfo b1 = new BatteryInfo(); b1.Id = "I1"; b1.DesignMwh = 50000; b1.FullMwh = 45000; si.Batteries.Add(b1);
        BatteryInfo b2 = new BatteryInfo(); b2.Id = "I2"; b2.DesignMwh = 40000; b2.FullMwh = 30000; si.Batteries.Add(b2);
        hw.Static = si;
        BatLive l1 = Battery.LiveRow("I1", 21400, false, true, 0, 6200, 11800), l2 = Battery.LiveRow("I2", 16800, false, true, 0, 3600, 11700);
        hw.Per.Add(l1); hw.Per.Add(l2);
        Engine en = NewEngine(hw, poll);
        StartTest(en, hw, poll);
        Eq(2, en.S.BatteryMap.Count, "battery map fixed at the start");
        string head = en.CsvHeader();
        Ok(head.Contains("b1_mwh,b1_rate_mw,b1_state,b1_mv,b2_mwh,b2_rate_mw,b2_state,b2_mv,note"), "dynamic CSV header: " + head);
        en.Publish();
        Dictionary<string, object> bat0 = D(((List<object>)en.Snapshot["batteries"])[0]);
        Ok(Convert.ToDouble(bat0["nowMwh"]) == 21400 && (string)bat0["state"] == "discharging" && bat0.ContainsKey("rateMw") && bat0.ContainsKey("voltageMv"), "batteries[] nowMwh / rateMw / state / voltageMv");
        hw.Per.RemoveAt(0); Step(en, hw, poll, 60000);
        Eq(1, Count(en, "battery_removed"), "a removed battery is logged (D8)");
        Ok(en.S.Status == "running", "the test continues");
        string line = Engine.CsvLine(en.Samples[en.Samples.Count - 1], en.S.BatteryMap);
        List<string> c = Engine.ParseCsvLine(line);
        List<string> h = Engine.ParseCsvLine(head);
        Eq("", c[h.IndexOf("b1_mwh")], "removed battery → its columns empty");
        Eq("16800", c[h.IndexOf("b2_mwh")], "battery 2 keeps its own columns (no shift)");
        BatLive l3 = Battery.LiveRow("I3", 9000, false, true, 0, 1000, 11000); hw.Per.Add(l3);
        Step(en, hw, poll, 60000);
        Eq(1, Count(en, "battery_added"), "an added battery is logged, no new columns");
        Eq(2, en.S.BatteryMap.Count, "the map is unchanged");
        en.Command("stop", null);
        ClearSessions();
    }

    // ---------------- §7 CSV reader by header (I8) ----------------
    static void CsvReader()
    {
        Section("v16 CSV reader by header name");
        string f = Path.Combine(tmpRoot, "old.csv");
        File.WriteAllText(f, "time,elapsed_s,event,windows_pct,remaining_mwh,full_mwh,rate_mw,rate_src,voltage_mv,ac,saver,b0_mwh,b1_mwh,note\r\n" +
            "2026-11-26T09:00:00+08:00,0,start,98,46000,47000,9000,wmi,12000,0,0,46000,,Test started\r\n" +
            "2026-11-26T09:01:00+08:00,60,reading,97,45800,47000,9100,wmi,11990,0,0,45800,,\r\n");
        List<Sample> o = Engine.ReadSamplesCsv(f);
        Ok(o.Count == 2 && o[1].Pct == 97 && o[1].Mwh == 45800 && o[1].Mv == 11990 && o[0].Note == "Test started", "the old 14-column v14/v15 layout still reads");
        string g = Path.Combine(tmpRoot, "new.csv");
        File.WriteAllText(g, "﻿" + Engine.CsvHeaderFor(new List<string> { "I1", "I2" }) + "\r\n" +
            "2026-11-27T09:00:00+08:00,0,start,82,38000,75000,9800,wmi,11800,0,0,21400,6200,discharging,11800,16800,3600,discharging,11700,Test started\r\n");
        List<Sample> n = Engine.ReadSamplesCsv(g);
        Ok(n.Count == 1 && n[0].Pct == 82 && n[0].Note == "Test started", "the new layout reads by name");
        Ok(n[0].Per.Count == 2 && n[0].Per[1].Mwh == 16800 && n[0].Per[1].State == "discharging", "per-battery columns read");
        string h = Path.Combine(tmpRoot, "moved.csv");   // columns in another order: still right
        File.WriteAllText(h, "event,time,windows_pct,note\r\nreading,2026-11-27T09:00:00+08:00,77,x\r\n");
        List<Sample> m = Engine.ReadSamplesCsv(h);
        Ok(m.Count == 1 && m[0].Pct == 77 && m[0].Event == "reading", "never by position");
    }

    // ---------------- §6 server: logo, media ranges, stalled media ----------------
    static int Get(int port, string path, string token, string range, out WebHeaderCollection hdr, out byte[] body, int timeoutMs)
    {
        hdr = null; body = null;
        HttpWebRequest rq = (HttpWebRequest)WebRequest.Create("http://localhost:" + port + path);
        rq.Proxy = null; rq.Timeout = timeoutMs; rq.ReadWriteTimeout = timeoutMs;
        if (token != null) rq.Headers["X-Laptap-Token"] = token;
        if (range != null)   // "bytes=A-" | "bytes=A-B" | "bytes=-N"
        {
            string sp = range.Substring(6); int d = sp.IndexOf('-');
            if (d == 0) rq.AddRange("bytes", -long.Parse(sp.Substring(1)));
            else if (d == sp.Length - 1) rq.AddRange("bytes", long.Parse(sp.Substring(0, d)));
            else rq.AddRange("bytes", long.Parse(sp.Substring(0, d)), long.Parse(sp.Substring(d + 1)));
        }
        try
        {
            using (HttpWebResponse r = (HttpWebResponse)rq.GetResponse())
            using (MemoryStream m = new MemoryStream()) { r.GetResponseStream().CopyTo(m); hdr = r.Headers; body = m.ToArray(); return (int)r.StatusCode; }
        }
        catch (WebException ex) { HttpWebResponse r = ex.Response as HttpWebResponse; if (r == null) return 0; hdr = r.Headers; return (int)r.StatusCode; }
    }

    static void ServerMedia()
    {
        Section("v16 server: /media ranges, /brand/logo.svg, stalled media");
        long a, b;
        Eq(206, Server.ParseRange(null, 3000000, Server.MediaChunk, out a, out b), "full GET → capped 206");
        Ok(a == 0 && b == Server.MediaChunk - 1, "… bytes 0-1048575 (1 MB cap)");
        Eq(206, Server.ParseRange("bytes=0-", 500, Server.MediaChunk, out a, out b), "bytes=0-"); Ok(a == 0 && b == 499, "… whole small file");
        Eq(206, Server.ParseRange("bytes=1000-1999", 3000000, Server.MediaChunk, out a, out b), "middle"); Ok(a == 1000 && b == 1999, "… 1000-1999");
        Eq(206, Server.ParseRange("bytes=-500", 3000000, Server.MediaChunk, out a, out b), "suffix"); Ok(a == 2999500 && b == 2999999, "… the last 500");
        Eq(416, Server.ParseRange("bytes=3000000-", 3000000, Server.MediaChunk, out a, out b), "past the end → 416");
        Eq(416, Server.ParseRange("bytes=5-2", 3000000, Server.MediaChunk, out a, out b), "end before start → 416");
        Eq(416, Server.ParseRange("items=0-1", 3000000, Server.MediaChunk, out a, out b), "not bytes → 416");
        Eq(0, Server.CheckRequest("GET", true, "localhost:47600", 47600, "/brand/logo.svg", null, "t"), "/brand/logo.svg needs no token");
        Eq(0, Server.CheckRequest("GET", true, "localhost:47600", 47600, "/brand/favicon.ico", null, "t"), "/brand/favicon.ico needs no token");
        Eq(0, Server.CheckRequest("GET", true, "localhost:47600", 47600, "/media/loop.webm", null, "t"), "/media/loop.webm needs no token");
        Eq(403, Server.CheckRequest("GET", false, "localhost:47600", 47600, "/media/loop.webm", null, "t"), "loopback still required");
        Eq(400, Server.CheckRequest("GET", true, "evil:47600", 47600, "/brand/logo.svg", null, "t"), "Host still checked");
        Ok(Server.ResponseHeaders("x")["Content-Security-Policy"].Contains("media-src 'self' blob:"), "CSP: media-src 'self' blob:");
        foreach (string c in new string[] { "start-anyway", "pause", "resume", "video" }) Ok(Server.IsCommand(c), "POST /api/" + c);
        foreach (string c in new string[] { "suspend", "resume-power" }) Ok(!Server.IsCommand(c), c + " is internal");

        byte[] video = new byte[2500000]; new Random(3).NextBytes(video);
        StallStream stall = null;
        Server s = new Server();
        s.LogoSvg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        s.IconIco = new byte[] { 0, 0, 1, 0 };
        s.MediaExt = "webm";
        bool stalled = false;
        s.OpenMedia = delegate { if (stalled) { stall = new StallStream(video); return stall; } return new MemoryStream(video); };
        s.GetSnapshot = delegate { Dictionary<string, object> d = new Dictionary<string, object>(); d["rev"] = 1; return d; };
        Ok(s.Start(), "server up");
        try
        {
            WebHeaderCollection h; byte[] body;
            Eq(200, Get(s.Port, "/brand/logo.svg", null, null, out h, out body, 5000), "GET /brand/logo.svg");
            Eq("image/svg+xml", h["Content-Type"], "image/svg+xml");
            Eq(200, Get(s.Port, "/brand/favicon.ico", null, null, out h, out body, 5000), "GET /brand/favicon.ico");
            Eq("image/x-icon", h["Content-Type"], "favicon content type"); Eq(4, body.Length, "favicon body");
            Eq(206, Get(s.Port, "/media/loop.webm", null, null, out h, out body, 5000), "GET /media/loop.webm (no Range) → 206");
            Eq("video/webm", h["Content-Type"], "video/webm"); Eq("bytes", h["Accept-Ranges"], "Accept-Ranges: bytes");
            Eq("bytes 0-1048575/2500000", h["Content-Range"], "capped Content-Range"); Eq(1048576, body.Length, "1 MB body");
            Eq("private, max-age=3600", h["Cache-Control"], "media cache header"); Eq("nosniff", h["X-Content-Type-Options"], "nosniff");
            Eq(206, Get(s.Port, "/media/loop.webm", null, "bytes=2000000-", out h, out body, 5000), "a later range");
            Eq("bytes 2000000-2499999/2500000", h["Content-Range"], "… to the end"); Eq(500000, body.Length, "… 500,000 bytes");
            Ok(body[0] == video[2000000] && body[499999] == video[2499999], "… the right bytes");
            Eq(416, Get(s.Port, "/media/loop.webm", null, "bytes=9000000-", out h, out body, 5000), "out of range → 416");
            Eq(404, Get(s.Port, "/media/loop.mp4", null, null, out h, out body, 5000), "the other format → 404");
            // C2: a stalled media response never blocks /api/state
            stalled = true;
            Thread t = new Thread(delegate() { WebHeaderCollection hh; byte[] bb; Get(s.Port, "/media/loop.webm", null, null, out hh, out bb, 12000); });
            t.IsBackground = true; t.Start();
            Thread.Sleep(300);
            Stopwatch sw = Stopwatch.StartNew();
            int code = Get(s.Port, "/api/state", s.Token, null, out h, out body, 3000);
            sw.Stop();
            Ok(code == 200 && sw.ElapsedMilliseconds < 1000, "/api/state answers in " + sw.ElapsedMilliseconds + " ms while a media response is stalled");
            if (stall != null) stall.Go.Set();
            t.Join(5000);
        }
        finally { s.Stop(); }
        Server none = new Server(); none.MediaExt = null;
        Ok(none.Start(), "server with no packaged video");
        try { WebHeaderCollection h; byte[] body; Eq(404, Get(none.Port, "/media/loop.webm", null, null, out h, out body, 5000), "no packaged video → /media/* 404"); }
        finally { none.Stop(); }
    }

    // ---------------- §1 brand ----------------
    static void Brand()
    {
        Section("v16 brand");
        string repo = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
        string logo = Path.Combine(repo, "src", "web", "brand", "logo.svg");
        string icon = Path.Combine(repo, "src", "laptap.ico");
        string src = Path.Combine(Path.GetDirectoryName(repo), "laptapenterprises", "public", "laptap_logo_transparent.svg");
        Ok(File.Exists(logo), "src/web/brand/logo.svg exists");
        Ok(File.Exists(icon), "src/laptap.ico exists");
        if (File.Exists(src)) Eq(Updater.Sha256Hex(src), Updater.Sha256Hex(logo), "the logo is byte-identical to the laptapenterprises file");
        else Ok(true, "laptapenterprises not beside this repo: hash checked by build.ps1 when present");
        byte[] embedded = Program.ResourceBytes("brand/logo.svg");
        Ok(embedded != null && embedded.Length == new FileInfo(logo).Length, "the logo is embedded (brand/logo.svg)");
        byte[] embeddedIcon = Program.ResourceBytes("brand/favicon.ico");
        Ok(embeddedIcon != null && embeddedIcon.Length == new FileInfo(icon).Length, "the company icon is embedded as the favicon");
        string img = Report.LogoImgHtml();
        Ok(img.StartsWith("<img class=\"logo\" alt=\"Laptap\" src=\"data:image/svg+xml;base64,"), "the report inlines the logo as base64 (M2)");
        object[] co = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyCompanyAttribute), false);
        Ok(co.Length == 1 && ((System.Reflection.AssemblyCompanyAttribute)co[0]).Company == "Laptap Enterprises, Inc.", "AssemblyCompany = Laptap Enterprises, Inc.");
    }
}
