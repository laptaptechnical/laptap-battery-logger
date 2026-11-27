// tests/V151Tests.cs
// v15.1.0 C# tests (plan v15.1 §6.1, reduced scope): nextStep, the update hold with its single 60 s cap, update phases
// (local server), Restart now (idle only, spawns --after-exit through a seam, then exits), --after-exit's wait,
// the quick idle exit (15 s visible / 90 s hidden, never during a test), the new endpoints, pausedSinceMs + live,
// and helper.json deleted only by its owner. The v15 suite (TrySwap, HandOff, badRelease…) runs unchanged beside it.
// Compiled into HelperTests.exe (with /define:TESTHOOKS); called from HelperTests.Main.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

static class V151Tests
{
    static Action<bool, string> Ok;
    static Action<object, object, string> Eq;
    static Action<string> Section;
    static string tmpRoot;

    public static void Run(Action<bool, string> ok, Action<object, object, string> eq, Action<string> section)
    {
        Ok = ok; Eq = eq; Section = section;
        tmpRoot = Path.Combine(Path.GetTempPath(), "laptap-bl-v151-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        string oldBase = Engine.BaseDir;
        Action oldChanged = Updater.Changed;
        try
        {
            Engine.BaseDir = Path.Combine(tmpRoot, "data");
            Directory.CreateDirectory(Engine.BaseDir);
            Report.QaOutDir = Path.Combine(tmpRoot, "desk"); Directory.CreateDirectory(Report.QaOutDir);
            NextStepAndHold();
            RestartNow();
            AfterExit();
            ExitRules();
            Endpoints();
            PausedAndLive();
            Phases();
        }
        finally
        {
            Updater.Progress = new UpdateProgress(); Updater.Changed = oldChanged;
            Engine.BaseDir = oldBase; Report.QaOutDir = null;
            Environment.SetEnvironmentVariable("LAPTAP_BL_TEST_PUBKEYS", null);
            try { Directory.Delete(tmpRoot, true); } catch { }
        }
    }

    // ---------------- helpers ----------------
    static Engine NewEngine(FakeHw hw, long[] lastPoll)
    {
        Engine en = new Engine();
        en.Hw = hw; en.QaNoWindow = true;
        en.LastPollMs = delegate { return lastPoll[0]; };
        en.IndexHtml = "<template id=\"report\">{{reason}}</template>";
        return en;
    }
    static void Step(Engine en, FakeHw hw, long[] poll, int ms) { hw.T += ms; poll[0] = hw.T; en.Tick(); }
    static void Phase(string phase)
    {
        UpdateProgress p = new UpdateProgress();
        p.Phase = phase; p.Version = phase == "idle" ? null : "15.9.0";
        Updater.Progress = p;
    }
    static void ClearSessions()
    {
        string d = Path.Combine(Engine.BaseDir, "sessions");
        try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
    }
    static Dictionary<string, object> State(Engine en) { en.Publish(); return en.Snapshot; }
    static Dictionary<string, object> D(object o) { return (Dictionary<string, object>)o; }
    static void StartTest(Engine en, FakeHw hw, long[] poll)
    {
        en.Tick(); hw.Ac = false; Step(en, hw, poll, 1000);
        for (int i = 0; i < 4 && (en.S == null || en.S.Status != "running"); i++) Step(en, hw, poll, 1000);
    }
    static void ToAlarm(Engine en, FakeHw hw, long[] poll)
    {
        hw.Pct = 14; hw.Mwh = 7000; Step(en, hw, poll, 60000); Step(en, hw, poll, 10000);
    }

    // ---------------- nextStep + the update hold (§2.2, §5, G2/I7, G3) ----------------
    static void NextStepAndHold()
    {
        Section("v15.1 nextStep + update hold");
        ClearSessions();
        FakeHw hw = new FakeHw(); long[] poll = { hw.T };
        Engine en = NewEngine(hw, poll);
        Phase("idle");
        hw.Present = false; hw.Pct = null; en.Tick();
        Eq("battery", State(en)["nextStep"], "no battery → battery (G3: the engine runs, nothing fatal)");
        Ok(en.IsReady && !en.CountdownEndsMs.HasValue, "no battery: nothing starts");
        hw.Present = true; hw.Pct = 80; hw.Ac = true; Step(en, hw, poll, 1000);
        Eq("charge", State(en)["nextStep"], "80% plugged → charge");
        hw.Ac = false; Step(en, hw, poll, 1000);
        Eq("charge", State(en)["nextStep"], "80% unplugged → charge (the page shows the low screen)");
        hw.Ac = true; hw.Pct = 100; Step(en, hw, poll, 1000);
        Eq("unplug", State(en)["nextStep"], "100% plugged, no update → unplug (row 31)");
        Phase("downloading"); Step(en, hw, poll, 1000);
        Eq("wait-update", State(en)["nextStep"], "downloading → wait-update, before unplug");
        hw.Pct = 80; Step(en, hw, poll, 1000);
        Eq("charge", State(en)["nextStep"], "charge comes before wait-update");
        hw.Present = false; Step(en, hw, poll, 1000);
        Eq("battery", State(en)["nextStep"], "battery comes first");
        hw.Present = true; hw.Pct = 100;
        foreach (string ph in new string[] { "checking", "ready", "failed", "idle" })
        {
            Phase(ph); Step(en, hw, poll, 1000);
            Eq("unplug", State(en)["nextStep"], ph + " never holds");
        }

        // downloading and verifying hold the countdown; ready / failed never; one 60 s cap from when the hold began
        foreach (string ph in new string[] { "downloading", "verifying" })
        {
            FakeHw h = new FakeHw(); long[] p = { h.T }; Engine e = NewEngine(h, p);
            Phase(ph);
            e.Tick(); h.Ac = false; Step(e, h, p, 1000); Step(e, h, p, 1000);
            Ok(!e.CountdownEndsMs.HasValue && e.IsReady, "no countdown while " + ph);
            Eq("wait-update", State(e)["nextStep"], ph + " unplugged → wait-update");
            Phase("idle"); Step(e, h, p, 1000);
            Ok(e.CountdownEndsMs.HasValue, "the countdown begins once " + ph + " ends");
            e.Command("cancel", null);
        }
        foreach (string ph in new string[] { "ready", "failed", "checking" })
        {
            FakeHw h = new FakeHw(); long[] p = { h.T }; Engine e = NewEngine(h, p);
            Phase(ph);
            e.Tick(); h.Ac = false; Step(e, h, p, 1000);
            Ok(e.CountdownEndsMs.HasValue, ph + " doesn't hold the countdown");
            e.Command("cancel", null);
        }
        {
            FakeHw h = new FakeHw(); long[] p = { h.T }; Engine e = NewEngine(h, p);
            Phase("downloading");
            e.Tick(); h.Ac = false; Step(e, h, p, 1000);
            Ok(!e.CountdownEndsMs.HasValue, "downloading holds");
            for (int i = 0; i < 19; i++) Step(e, h, p, 1000);
            Phase("verifying"); Step(e, h, p, 1000);
            Ok(!e.CountdownEndsMs.HasValue, "downloading → verifying is one hold (21 s in)");
            for (int i = 0; i < 23; i++) Step(e, h, p, 1000);
            Ok(!e.CountdownEndsMs.HasValue, "still held at 44 s");
            Step(e, h, p, 1000); Step(e, h, p, 1000);
            Ok(e.CountdownEndsMs.HasValue, "the hold lifts 45 s after it began, though still verifying (one cap; inside the 60 s latch, build-audit I2)");
            Ok(Engine.HoldCapMs < Engine.LatchMaxAgeMs, "the hold cap is below the unplug latch age");
            e.Publish(); Eq(null, e.Snapshot["nextStep"], "after the cap nextStep is null");
            Step(e, h, p, 3000);
            Ok(e.S != null && e.S.Status == "running", "a test may start while the update carries on");
            Eq("verifying", Updater.Progress.Phase, "the update was left alone");
            e.Command("stop", null);
        }
        Phase("idle");
        ClearSessions();
    }

    // ---------------- Restart now (§2.3) ----------------
    static void RestartNow()
    {
        Section("v15.1 Restart now (idle only, --after-exit)");
        ClearSessions();
        FakeHw hw = new FakeHw(); long[] poll = { hw.T };
        Engine en = NewEngine(hw, poll);
        int spawned = 0; en.SpawnAfterExit = delegate { spawned++; return null; };
        en.Tick();
        Phase("downloading");
        Dictionary<string, object> r = en.Command("update-restart", null);
        Eq(false, r["ok"], "refused unless phase is ready"); Eq("No update is ready.", r["error"], "text");
        Phase("ready");
        hw.Ac = false; Step(en, hw, poll, 1000);
        Ok(en.CountdownEndsMs.HasValue, "countdown");
        r = en.Command("update-restart", null);
        Eq(false, r["ok"], "refused during the countdown"); Eq("Finish the test first.", r["error"], "plan wording");
        Step(en, hw, poll, 3000);
        Ok(en.S != null && en.S.Status == "running", "logging");
        Eq(false, en.Command("update-restart", null)["ok"], "refused while Logging");
        hw.Ac = true; Step(en, hw, poll, 1000);
        Eq(false, en.Command("update-restart", null)["ok"], "refused while Paused");
        hw.Ac = false; Step(en, hw, poll, 1000);
        ToAlarm(en, hw, poll);
        Ok(en.S.Status == "running" && en.S.Alarm && !en.S.AlarmAck, "alarm is active without ending logging");
        Eq(false, en.Command("update-restart", null)["ok"], "refused until the alarm is acknowledged");
        Eq(0, spawned, "nothing spawned by any refusal");
        Ok(!en.QuitAtMs.HasValue, "still running");
        en.Command("ack-alarm", null);
        Eq(false, en.Command("update-restart", null)["ok"], "still refused while logging after acknowledging the alarm");
        en.Command("stop", null);
        string sd = Path.Combine(Path.Combine(Engine.BaseDir, "sessions"), "other"); Directory.CreateDirectory(sd);
        File.WriteAllText(Path.Combine(sd, "session.json"), "{\"Status\":\"running\"}");
        Eq(false, en.Command("update-restart", null)["ok"], "refused while another session on disk isn't done");
        Directory.Delete(sd, true);
        r = en.Command("update-restart", null);
        Eq(true, r["ok"], "accepted in Done after manually stopping and acknowledging the alarm");
        Eq(1, spawned, "--after-exit <pid> started before the answer");
        Eq(true, en.Command("update-restart", null)["ok"], "a second Restart now answers ok (build-audit M1)");
        Eq(1, spawned, "… and spawns nothing more");
        Ok(!en.ShouldExit(), "not before the answer has gone out");
        hw.T += 600; Ok(en.ShouldExit(), "then the logger exits (0.5 s later)");

        // from Ready
        ClearSessions();
        FakeHw h2 = new FakeHw(); long[] p2 = { h2.T }; Engine e2 = NewEngine(h2, p2);
        int s2 = 0; e2.SpawnAfterExit = delegate { s2++; return null; };
        e2.Tick(); Phase("ready");
        Eq(true, e2.Command("update-restart", null)["ok"], "accepted in Ready with no countdown");
        h2.T += 600; Ok(s2 == 1 && e2.ShouldExit(), "spawned, exits");
        // a pending quit never leaves a running test (build-audit M2)
        FakeHw h9 = new FakeHw(); long[] p9 = { h9.T }; Engine e9 = NewEngine(h9, p9);
        StartTest(e9, h9, p9); e9.QuitAtMs = h9.T;
        h9.T += 1000; Ok(e9.S != null && e9.S.Status == "running" && !e9.ShouldExit(), "Active is checked before QuitAtMs");
        e9.Command("stop", null);
        // the spawn fails → refused, keeps running
        Engine e3 = NewEngine(new FakeHw(), new long[] { 0 }); e3.Tick();
        e3.SpawnAfterExit = delegate { return "Could not restart the logger."; };
        r = e3.Command("update-restart", null);
        Ok(!(bool)r["ok"] && (string)r["error"] == "Could not restart the logger.", "spawn failure → refused");
        Ok(!e3.QuitAtMs.HasValue, "… and the logger keeps running");

        // the spawn itself: own exe, --after-exit <own pid>, + the QA switches only
        ProcessStartInfo seen = null;
        Func<string, ProcessStartInfo, bool> real = Program.StartProcess;
        Program.StartProcess = delegate(string what, ProcessStartInfo p) { seen = p; return true; };
        try
        {
            Eq(null, Program.SpawnAfterExit(""), "spawn ok");
            Eq("--after-exit " + Process.GetCurrentProcess().Id, seen.Arguments, "args: --after-exit <own pid>, nothing else for staff");
            Ok(!seen.UseShellExecute, "no shell");
            Program.SpawnAfterExit("--qa-no-window");
            Ok(seen.Arguments.EndsWith(" --qa-no-window"), "QA switches carried (tests only)");
            Program.StartProcess = delegate(string what, ProcessStartInfo p) { throw new InvalidOperationException("x"); };
            Eq("Could not restart the logger.", Program.SpawnAfterExit(""), "a start failure is reported, not thrown");
        }
        finally { Program.StartProcess = real; }

        // helper.json: version + release, written atomically; deleted only when it names this process (audit I3)
        string f = Path.Combine(Engine.BaseDir, "helper.json");
        File.WriteAllText(f, "{\"port\":1,\"token\":\"old\",\"pid\":1}");
        Program.WriteHelperJson(47611, "abc123");
        Dictionary<string, object> hj = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
        Eq(47611, hj["port"], "port"); Eq("abc123", hj["token"], "token"); Eq(Process.GetCurrentProcess().Id, hj["pid"], "pid");
        Eq(AppVersion.Version, hj["version"], "helper.json version"); Eq(AppVersion.Release, hj["release"], "helper.json release");
        Ok(!File.Exists(f + ".tmp"), "no temp file left (replaced atomically)");
        File.WriteAllText(f, "{\"port\":1,\"token\":\"t\",\"pid\":2}");
        Program.DeleteHelperIfOurs(); Ok(File.Exists(f), "another process's helper.json is kept");
        File.WriteAllText(f, "{\"port\":1,\"token\":\"t\",\"pid\":" + Process.GetCurrentProcess().Id + "}");
        Program.DeleteHelperIfOurs(); Ok(!File.Exists(f), "our helper.json is deleted on exit");
        Phase("idle");
        ClearSessions();
    }

    // ---------------- --after-exit (§2.3) ----------------
    static void AfterExit()
    {
        Section("v15.1 --after-exit wait");
        Stopwatch sw = Stopwatch.StartNew();
        Ok(Program.WaitForExit(int.MaxValue - 7, 30000) && sw.ElapsedMilliseconds < 2000, "pid not found → carries on at once");
        ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul");
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        using (Process p = Process.Start(psi))
        {
            sw = Stopwatch.StartNew();
            Ok(Program.WaitForExit(p.Id, 30000), "waits for the pid to exit");
            Ok(sw.ElapsedMilliseconds >= 300 && sw.ElapsedMilliseconds < 10000, "… and only until then (" + sw.ElapsedMilliseconds + " ms)");
        }
        sw = Stopwatch.StartNew();
        Ok(!Program.WaitForExit(Process.GetCurrentProcess().Id, 400), "a pid that doesn't exit → false after the wait");
        Ok(sw.ElapsedMilliseconds >= 350 && sw.ElapsedMilliseconds < 3000, "the timeout path returns (" + sw.ElapsedMilliseconds + " ms); the start carries on");
    }

    // ---------------- quick idle exit (§3) ----------------
    static void ExitRules()
    {
        Section("v15.1 exit rules (15 s visible / 90 s hidden)");
        ClearSessions();
        Dictionary<string, object> vis = new Dictionary<string, object>(); vis["visible"] = true;
        Dictionary<string, object> hid = new Dictionary<string, object>(); hid["visible"] = false;

        FakeHw hw = new FakeHw(); hw.T = Rules.NowMs(); long[] poll = { 0 };
        Engine en = NewEngine(hw, poll); en.Tick();
        hw.T += 170000; Ok(!en.ShouldExit(), "no page yet: the 3 min start-up grace");
        hw.T += 20000; Ok(en.ShouldExit(), "no page in 3 min → exits");

        FakeHw h2 = new FakeHw(); h2.T = Rules.NowMs(); long[] p2 = { 0 };
        Engine e2 = NewEngine(h2, p2); e2.Tick();
        e2.Command("heartbeat", vis);
        h2.T += 14000; Ok(!e2.ShouldExit(), "visible heartbeat, 14 s silence → still running");
        h2.T += 2000; Ok(e2.ShouldExit(), "visible heartbeat then silence → exits at ~15 s");

        FakeHw h3 = new FakeHw(); h3.T = Rules.NowMs(); long[] p3 = { 0 };
        Engine e3 = NewEngine(h3, p3); e3.Tick();
        e3.Command("heartbeat", hid);
        h3.T += 60000; Ok(!e3.ShouldExit(), "hidden heartbeat: still running past 15 s (60 s)");
        h3.T += 29000; Ok(!e3.ShouldExit(), "… and at 89 s");
        h3.T += 2000; Ok(e3.ShouldExit(), "hidden heartbeat then silence → exits by 90 s");

        FakeHw h4 = new FakeHw(); h4.T = Rules.NowMs(); long[] p4 = { 0 };
        Engine e4 = NewEngine(h4, p4); e4.Tick();
        e4.Command("heartbeat", null);
        h4.T += 16000; Ok(e4.ShouldExit(), "a heartbeat with no body counts as visible (older pages)");
        FakeHw h5 = new FakeHw(); h5.T = Rules.NowMs(); long[] p5 = { 0 };
        Engine e5 = NewEngine(h5, p5); e5.Tick();
        p5[0] = h5.T;   // a token'd GET /api/state is contact too
        h5.T += 10000; Ok(!e5.ShouldExit(), "a state poll counts as contact");
        h5.T += 6000; Ok(e5.ShouldExit(), "then 15 s of silence → exits");

        // never during a test, nor within the unacknowledged-alarm window after manual stop
        FakeHw h6 = new FakeHw(); h6.T = Rules.NowMs(); long[] p6 = { h6.T };
        Engine e6 = NewEngine(h6, p6);
        StartTest(e6, h6, p6);
        Ok(e6.S != null && e6.S.Status == "running", "logging");
        e6.Command("heartbeat", vis);
        long t6 = h6.T; h6.T += 10 * 60000; Ok(!e6.ShouldExit(), "never while Logging, however long the silence"); h6.T = t6;   // only the clock moves: no tick
        h6.Ac = true; Step(e6, h6, p6, 1000); t6 = h6.T; h6.T += 10 * 60000;
        Ok(e6.S.Status == "paused" && !e6.ShouldExit(), "never while Paused"); h6.T = t6;
        h6.Ac = false; Step(e6, h6, p6, 1000); ToAlarm(e6, h6, p6);
        Ok(e6.S.Status == "running" && e6.S.Alarm && !e6.S.AlarmAck, "alarm did not end logging");
        e6.Command("stop", null);
        h6.T += 20 * 60000; Ok(!e6.ShouldExit(), "never within the unacknowledged-alarm window (31 min)");
        h6.T += 12 * 60000; Ok(e6.ShouldExit(), "after it, the quiet rule applies");

        // a countdown with no contact for 15 s is cleared (still armed), and no session is created
        ClearSessions();
        FakeHw h7 = new FakeHw(); long[] p7 = { h7.T };
        Engine e7 = NewEngine(h7, p7);
        e7.Tick(); h7.Ac = false; Step(e7, h7, p7, 1000);
        Ok(e7.CountdownEndsMs.HasValue, "countdown (a page was watching)");
        h7.T += 16000; e7.Tick();   // the laptop stalled / the window closed: no contact for 16 s
        Ok(!e7.CountdownEndsMs.HasValue && e7.S == null, "no contact for 15 s → countdown cleared, no session");
        Ok(e7.Armed, "still armed");
        string sess = Path.Combine(Engine.BaseDir, "sessions");
        Ok(!Directory.Exists(sess) || Directory.GetDirectories(sess).Length == 0, "nothing written to disk");
        ClearSessions();
    }

    // ---------------- endpoints (§5) ----------------
    static void Endpoints()
    {
        Section("v15.1 endpoints");
        ClearSessions();
        foreach (string c in new string[] { "update-restart", "update-retry", "open-report" }) Ok(Server.IsCommand(c), "POST /api/" + c);
        foreach (string c in new string[] { "quit", "newer-waiting", "closing" }) Ok(!Server.IsCommand(c), c + " is not in 15.1.0 (moved to 15.2)");
        FakeHw hw = new FakeHw(); long[] poll = { hw.T };
        Engine en = NewEngine(hw, poll);
        List<string> opened = new List<string>(); en.OpenFile = delegate(string f) { opened.Add(f); };
        int retried = 0; en.RetryUpdate = delegate { retried++; };
        en.Tick();
        Eq(false, en.Command("open-report", null)["ok"], "open-report refused in Ready");
        Phase("idle");
        Eq(false, en.Command("update-retry", null)["ok"], "update-retry refused unless failed");
        Phase("failed");
        Eq(true, en.Command("update-retry", null)["ok"], "update-retry from failed");
        Eq(1, retried, "the check runs again"); Eq("idle", Updater.Progress.Phase, "phase back to idle");
        StartTest(en, hw, poll);
        Eq(false, en.Command("open-report", null)["ok"], "open-report refused while Logging");
        en.Command("stop", null);
        Eq(true, en.Command("open-report", null)["ok"], "open-report in Done");
        Ok(opened.Count == 1 && opened[0] == en.S.ReportPath, "the Desktop report opened with the shell");
        Phase("idle");
        ClearSessions();
    }

    // ---------------- pausedSinceMs + live (§5) ----------------
    static void PausedAndLive()
    {
        Section("v15.1 pausedSinceMs + live");
        ClearSessions();
        FakeHw hw = new FakeHw(); long[] poll = { hw.T };
        Engine en = NewEngine(hw, poll);
        Dictionary<string, object> lv = D(State(en)["live"]);
        Ok(lv["avgW"] == null && lv["etaToAlarmS"] == null && lv["healthPct"] == null, "live all null in Ready");
        StartTest(en, hw, poll);
        Eq(null, D(State(en)["session"])["pausedSinceMs"], "pausedSinceMs null while Logging");
        Func<int> counted = delegate { return en.Samples.FindAll(delegate(Sample x) { return !x.Ac && x.Pct.HasValue; }).Count; };
        int guard = 0;
        while (counted() < 4 && guard++ < 10) { hw.Pct -= 1; hw.Mwh -= 500; Step(en, hw, poll, 60000); }
        Eq(4, counted(), "4 counted samples");
        lv = D(State(en)["live"]);
        Ok(lv["avgW"] == null && lv["etaToAlarmS"] == null && lv["healthPct"] == null, "live null until 5 samples");
        for (int i = 0; i < 4; i++) { hw.Pct -= 1; hw.Mwh -= 500; Step(en, hw, poll, 60000); }
        lv = D(State(en)["live"]);
        Ok(lv["avgW"] != null, "avgW after 5+ samples (" + lv["avgW"] + ")");
        Ok(lv["etaToAlarmS"] != null, "etaToAlarmS after 5+ samples (" + lv["etaToAlarmS"] + ")");
        Eq(91.9, lv["healthPct"], "healthPct raw = the result's rule (47378 / 51528)");
        hw.Ac = true; Step(en, hw, poll, 1000);
        long plugged = hw.T;
        Ok(en.S.Status == "paused", "paused");
        object ps = D(State(en)["session"])["pausedSinceMs"];
        Ok(ps != null && Convert.ToInt64(ps) <= plugged && Convert.ToInt64(ps) >= plugged - 1000, "pausedSinceMs = the plug-in time");
        Eq(null, D(State(en)["live"])["etaToAlarmS"], "no ETA while paused");
        hw.Ac = false; Step(en, hw, poll, 1000);
        Eq(null, D(State(en)["session"])["pausedSinceMs"], "pausedSinceMs null again after unplug");
        en.Command("stop", null);
        string sj = File.ReadAllText(Path.Combine(Path.Combine(Path.Combine(Engine.BaseDir, "sessions"), en.S.Id), "session.json"));
        Ok(!sj.Contains("pausedSinceMs"), "nothing new is stored in session.json (audit M7)");
        ClearSessions();
    }

    // ---------------- update phases against a local server (§2.1) ----------------
    static void Phases()
    {
        Section("v15.1 update phases");
        ClearSessions();
        byte[] key = KeyTools.NewKey();
        Environment.SetEnvironmentVariable("LAPTAP_BL_TEST_PUBKEYS", KeyTools.Hex(KeyTools.PublicX(key)) + ":" + KeyTools.Hex(KeyTools.PublicY(key)));
        string dir = Path.Combine(tmpRoot, "phases"); Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "Laptap Battery Logger.exe"); File.WriteAllBytes(exe, new byte[] { 0x4d, 0x5a });
        List<string> seen = new List<string>(); List<int> pcts = new List<int>();
        Updater.Changed = delegate
        {
            UpdateProgress p = Updater.Progress;
            if (seen.Count == 0 || seen[seen.Count - 1] != p.Phase) seen.Add(p.Phase);
            if (p.Phase == "downloading") pcts.Add(p.Percent);
        };
        V15Tests.MiniServer srv = new V15Tests.MiniServer();
        try
        {
            Phase("idle"); seen.Clear();
            Eq("no-update", Updater.CheckAndStage(V15Tests.Opts(exe, srv.Base)), "no release");
            Eq("checking idle", string.Join(" ", seen.ToArray()), "checking → idle when there's nothing to do");

            byte[] asset = new byte[300000]; new Random(5).NextBytes(asset); asset[0] = 0x4d; asset[1] = 0x5a;
            string af = Path.Combine(tmpRoot, "asset151.exe"); File.WriteAllBytes(af, asset);
            byte[] m = V15Tests.ManifestBytes(2, "15.9.0", UpdateConfig.Product, "v15.9.0", Updater.Sha256Hex(af), asset.Length);
            srv.Files["/releases/latest/download/update.json"] = m;
            srv.Files["/releases/latest/download/update.json.sig"] = Encoding.ASCII.GetBytes(Convert.ToBase64String(KeyTools.Sign(key, m)));
            srv.Files["/releases/download/v15.9.0/Laptap-Battery-Logger.exe"] = asset;
            Phase("idle"); seen.Clear(); pcts.Clear();
            Eq("staged", Updater.CheckAndStage(V15Tests.Opts(exe, srv.Base)), "good release staged");
            Eq("checking downloading verifying ready", string.Join(" ", seen.ToArray()), "checking → downloading → verifying → ready");
            bool rising = pcts.Count > 0;
            for (int i = 1; i < pcts.Count; i++) if (pcts[i] < pcts[i - 1]) rising = false;
            Ok(rising, "percent rises in order (" + string.Join(",", pcts.ConvertAll(delegate(int x) { return x.ToString(); }).ToArray()) + ")");
            Eq(100, pcts.Count > 0 ? pcts[pcts.Count - 1] : -1, "percent ends at 100");
            Eq("ready", Updater.Progress.Phase, "ready"); Eq("15.9.0", Updater.Progress.Version, "version");
            Eq("15.9.0", D(Updater.Progress.ToDict())["version"], "app.update.version");
            Ok(!D(Updater.Progress.ToDict()).ContainsKey("restartAtMs"), "no restartAtMs in 15.1.0");
            Phase("idle"); seen.Clear();
            Eq("staged", Updater.CheckAndStage(V15Tests.Opts(exe, srv.Base)), "already staged");
            Eq("ready", Updater.Progress.Phase, "already staged → ready at once");
            bool del; Ok(Updater.StagedOk(exe, 1, out del) != null, "the staged copy passes the v15 start-time check (TrySwap installs it)");
            Updater.DeleteStaged(exe);

            byte[] wrong = (byte[])asset.Clone(); wrong[1000] ^= 0xff;
            srv.Files["/releases/download/v15.9.0/Laptap-Battery-Logger.exe"] = wrong;
            Phase("idle"); seen.Clear();
            Eq("download-failed", Updater.CheckAndStage(V15Tests.Opts(exe, srv.Base)), "hash mismatch refused");
            Eq("failed", Updater.Progress.Phase, "phase failed");
            Eq("Update refused: the download didn't match", D(Updater.Progress.ToDict())["note"], "app.update.note");
            Ok(seen.Contains("verifying"), "it failed in verifying");
            Ok(!File.Exists(exe + ".update") && !File.Exists(exe + ".part"), "nothing staged");
            Ok(!Updater.IsBad(2), "a failed download never marks the release bad");
            srv.Files.Remove("/releases/download/v15.9.0/Laptap-Battery-Logger.exe");
            Phase("idle");
            Eq("download-failed", Updater.CheckAndStage(V15Tests.Opts(exe, srv.Base)), "missing asset");
            Eq("Update check failed (no internet?)", Updater.Progress.Note, "download failure note");
        }
        finally { srv.Stop(); Updater.Changed = delegate { }; Phase("idle"); }
    }
}
