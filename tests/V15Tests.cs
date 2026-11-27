// tests/V15Tests.cs
// v15 C# tests (plan §6.2): countdown + checks (fake battery), manifest + signatures (test keys made here),
// keys + backup file, update skip rules, the hard deadline, TLS, and the swap / hand-off in a temp folder.
// Compiled into HelperTests.exe (with /define:TESTHOOKS); called from HelperTests.Main.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

public class FakeHw : Hardware
{
    public bool Ac = true, Present = true, Saver;
    public double? Pct = 100;
    public double Mwh = 47000;
    public long T = 1795654800000;
    public int AwakeOn, AwakeOff;
    public override bool PowerStatus(out bool ac, out double? pct, out bool saver, out bool present, out bool critical)
    { ac = Ac; pct = Pct; saver = Saver; present = Present; critical = false; return true; }
    public override Sample ReadLive(double? fullMwh)
    {
        Sample s = new Sample(); s.Ms = T; s.Pct = Pct; s.Mwh = Mwh; s.FullMwh = fullMwh; s.Ac = Ac; s.Saver = Saver;
        s.RateMw = Ac ? (double?)null : 9000; s.RateSrc = Ac ? "" : "wmi"; return s;
    }
    public override StaticInfo ReadStatic(bool fullReport)
    {
        StaticInfo si = new StaticInfo(); si.Manufacturer = "HP"; si.Model = "EliteBook"; si.Serial = "5CB4190FVX";
        BatteryInfo b = new BatteryInfo(); b.Id = "BAT"; b.DesignMwh = 51528; b.FullMwh = 47378; si.Batteries.Add(b); return si;
    }
    public override Dictionary<string, double?> ReadFullByInstance() { return new Dictionary<string, double?>(); }
    public override double? ReadBrightness() { return 60; }
    public override List<PowerEvent> Events(long fromMs, long toMs) { return new List<PowerEvent>(); }
    public override void KeepAwake(bool on) { if (on) AwakeOn++; else AwakeOff++; }
    public override string ExecState() { return "SYSTEM DISPLAY"; }
    public override long Now() { return T; }
}

class FakeChild : IChild
{
    public int Id { get; set; }
    public bool Exited, Killed;
    public bool HasExited { get { return Exited || Killed; } }
    public void Kill() { Killed = true; }
}

class FakeLauncher : ILauncher
{
    public string Mode;        // "ok" | "silent" | "exit" | "stale" | "throw"
    public FakeChild Child;
    public string Args;
    public IChild Start(string exe, string args)
    {
        Args = args;
        if (Mode == "throw") throw new FileNotFoundException("quarantined", exe);
        Child = new FakeChild(); Child.Id = 40000 + new Random().Next(1000);
        string nonce = args.Split(' ')[1];
        if (Mode == "ok")
        {
            File.WriteAllText(Updater.MarkerPath(nonce), "alive " + Child.Id);
            ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(300); File.WriteAllText(Updater.MarkerPath(nonce), "owner " + Child.Id); });
        }
        else if (Mode == "exit") { File.WriteAllText(Updater.MarkerPath(nonce), "alive " + Child.Id); Child.Exited = true; }
        else if (Mode == "stale") File.WriteAllText(Updater.MarkerPath("0000000000000000"), "owner " + Child.Id);   // wrong nonce
        else if (Mode == "refused") { File.WriteAllText(Updater.MarkerPath(nonce), "alive " + Child.Id); File.WriteAllText(Updater.MarkerPath(nonce), "refused " + Child.Id); }
        return Child;
    }
}

static class V15Tests
{
    static Action<bool, string> Ok;
    static Action<object, object, string> Eq;
    static Action<string> Section;
    static string tmpRoot;

    public static void Run(Action<bool, string> ok, Action<object, object, string> eq, Action<string> section) { Run(ok, eq, section, ""); }

    public static void Run(Action<bool, string> ok, Action<object, object, string> eq, Action<string> section, string builtExe)
    {
        Ok = ok; Eq = eq; Section = section;
        tmpRoot = Path.Combine(Path.GetTempPath(), "laptap-bl-v15-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        string oldBase = Engine.BaseDir;
        try
        {
            Engine.BaseDir = Path.Combine(tmpRoot, "data");
            Directory.CreateDirectory(Engine.BaseDir);
            Report.QaOutDir = Path.Combine(tmpRoot, "desk"); Directory.CreateDirectory(Report.QaOutDir);
            Countdown();
            KeysAndBackup();
            ManifestAndSignatures();
            SkipRulesAndDeadline();
            SwapAndHandOff();
            DoubleLaunchWaits();
            AuditFixes(builtExe);
        }
        finally
        {
            Engine.BaseDir = oldBase; Report.QaOutDir = null;
            Environment.SetEnvironmentVariable("LAPTAP_BL_TEST_PUBKEYS", null);
            try { Directory.Delete(tmpRoot, true); } catch { }
        }
    }

    static Engine NewEngine(FakeHw hw, long[] lastPoll)
    {
        Engine en = new Engine();
        en.Hw = hw; en.QaNoWindow = true;
        en.LastPollMs = delegate { return lastPoll[0]; };
        en.IndexHtml = "<template id=\"report\">{{reason}}</template>";
        return en;
    }

    static void Step(Engine en, FakeHw hw, long[] poll, int ms) { hw.T += ms; poll[0] = hw.T; en.Tick(); }

    // ---------------- countdown + checks (plan §3, E2, E6, I10) ----------------
    static void Countdown()
    {
        Section("v15 countdown + checks");
        FakeHw hw = new FakeHw(); long[] poll = { hw.T };
        Engine en = NewEngine(hw, poll);
        en.Tick();
        Ok(en.IsReady && en.Armed, "starts Ready and armed");
        Ok(!en.CountdownEndsMs.HasValue, "plugged in at 100%: no countdown");
        en.Publish();
        Dictionary<string, object> ck = (Dictionary<string, object>)en.Snapshot["checks"];
        Eq(true, ck["pct96"], "pct96 true while plugged at 100%"); Eq(false, ck["unplugged"], "unplugged false");

        // unplug at 96%: latch, countdown, the % slips to 95 → still starts, from the latch (E6)
        hw.Pct = 96; hw.Mwh = 45000; Step(en, hw, poll, 1000);
        hw.Ac = false; Step(en, hw, poll, 1000);
        long latchMs = hw.T;
        Ok(en.CountdownEndsMs.HasValue, "unplugged at 96% with a page polling → countdown");
        Eq(latchMs + 3000, en.CountdownEndsMs, "countdown ends 3 s after it began");
        hw.Pct = 95; hw.Mwh = 44500; Step(en, hw, poll, 1000);
        Ok(en.CountdownEndsMs.HasValue, "95% after the unplug: still counting (latched at 96)");
        Step(en, hw, poll, 1000); Ok(en.IsReady, "not started before the end");
        Step(en, hw, poll, 1000);
        Ok(en.S != null && en.S.Status == "running", "started at the countdown's end");
        Eq(latchMs, en.S.UnplugMs, "segment 1 begins at the latched unplug time");
        Eq(96.0, en.S.StartPct, "start % = the latched 96");
        Eq(45000.0, en.S.StartMwh, "start mWh = the latched reading");
        Eq(1, hw.AwakeOn, "keep-awake on at start");
        Dictionary<string, object> r = en.Command("arm", null);
        Eq(false, r["ok"], "arm refused while running"); Eq("A test is already running.", r["error"], "refusal text");
        Eq(false, en.Command("cancel", null)["ok"], "cancel refused while running");
        Eq(false, en.Command("new", null)["ok"], "new refused while running");
        en.Command("stop", null);
        Ok(en.S.Status == "done" && hw.AwakeOff == 1, "stop → done, keep-awake off");
        hw.Pct = 100; hw.Ac = false; Step(en, hw, poll, 1000); Step(en, hw, poll, 1000);
        Ok(!en.CountdownEndsMs.HasValue, "Done is never auto-armed");
        Eq("Press New test first.", en.Command("arm", null)["error"], "arm in Done refused");
        Eq(true, en.Command("new", null)["ok"], "new → Ready");
        Ok(en.IsReady && en.Armed, "Ready and armed after New test");

        // Cancel / Start: after a Cancel, segment 1 starts at the countdown's end with the current reading
        FakeHw h2 = new FakeHw(); long[] p2 = { h2.T }; Engine e2 = NewEngine(h2, p2);
        e2.Tick(); h2.Ac = false; Step(e2, h2, p2, 1000);
        Ok(e2.CountdownEndsMs.HasValue, "countdown after unplug");
        Eq(true, e2.Command("cancel", null)["ok"], "cancel ok during the countdown");
        Ok(!e2.CountdownEndsMs.HasValue && !e2.Armed, "Cancel → Ready, disarmed");
        Step(e2, h2, p2, 5000); Ok(!e2.CountdownEndsMs.HasValue, "disarmed: no new countdown");
        e2.Publish(); Eq(false, e2.Snapshot["armed"], "state says armed=false (page shows Start)");
        Eq(true, e2.Command("arm", null)["ok"], "Start (arm) ok"); Step(e2, h2, p2, 1000);
        long cdStart = h2.T;
        Ok(e2.CountdownEndsMs.HasValue, "arm → the same 3 s countdown");
        h2.Pct = 99; h2.Mwh = 46000; Step(e2, h2, p2, 3000);
        Ok(e2.S != null && e2.S.Status == "running", "started after Start + countdown");
        Eq(cdStart + 3000, e2.S.UnplugMs, "after Cancel/Start: segment starts at the countdown's end");
        Eq(99.0, e2.S.StartPct, "after Cancel/Start: current reading");
        e2.Command("stop", null);

        // A check drops mid-countdown (re-plug) → back to Ready; the latch is cleared
        FakeHw h3 = new FakeHw(); long[] p3 = { h3.T }; Engine e3 = NewEngine(h3, p3);
        e3.Tick(); h3.Ac = false; h3.Pct = 97; Step(e3, h3, p3, 1000);
        Ok(e3.CountdownEndsMs.HasValue, "counting");
        h3.Ac = true; h3.Pct = 95; Step(e3, h3, p3, 1000);
        Ok(!e3.CountdownEndsMs.HasValue && e3.IsReady, "re-plug mid-countdown → Ready");
        h3.Ac = false; Step(e3, h3, p3, 1000);
        Ok(!e3.CountdownEndsMs.HasValue, "re-plug cleared the 97% latch: unplugged again at 95% → no countdown");
        e3.Publish(); Eq(false, ((Dictionary<string, object>)e3.Snapshot["checks"])["pct96"], "pct96 false after a fresh 95% unplug");

        // No page polled in 3 s → no countdown (only auto-starts while someone is looking)
        FakeHw h4 = new FakeHw(); long[] p4 = { 0 }; Engine e4 = NewEngine(h4, p4);
        e4.Tick(); h4.Ac = false; h4.T += 5000; e4.Tick(); h4.T += 1000; e4.Tick();
        Ok(!e4.CountdownEndsMs.HasValue, "no poll in the last 3 s → no countdown");
        p4[0] = h4.T; e4.Tick();
        Ok(e4.CountdownEndsMs.HasValue, "a poll arrives → countdown");

        // Charge-limit hint: plugged below 96% with a flat % for 10 min
        FakeHw h5 = new FakeHw(); h5.Pct = 80; long[] p5 = { h5.T }; Engine e5 = NewEngine(h5, p5);
        e5.Tick(); Step(e5, h5, p5, 9 * 60000); e5.Publish();
        Eq(false, ((Dictionary<string, object>)e5.Snapshot["checks"])["chargeLimitHint"], "no hint before 10 min");
        Step(e5, h5, p5, 61000); e5.Publish();
        Eq(true, ((Dictionary<string, object>)e5.Snapshot["checks"])["chargeLimitHint"], "hint after 10 min flat below 96%");

        // state shape additions
        e5.Publish();
        Dictionary<string, object> s = Server.Filter(e5.Snapshot, 0);
        foreach (string k in new string[] { "nowMs", "checks", "armed", "countdownEndsMs", "app" }) Ok(s.ContainsKey(k), "state has " + k);
        Dictionary<string, object> app = (Dictionary<string, object>)s["app"];
        Eq(AppVersion.Version, app["version"], "app.version from VERSION"); Eq(AppVersion.Release, app["release"], "app.release");
        Ok(app.ContainsKey("updatedFrom") && app.ContainsKey("updateNote"), "app.updatedFrom / updateNote");
        Ok(Server.IsCommand("arm") && Server.IsCommand("cancel") && Server.IsCommand("new"), "arm / cancel / new routes");
    }

    // ---------------- keys + backup file (plan §5.3) ----------------
    static void KeysAndBackup()
    {
        Section("v15 keys + backup file");
        byte[] priv = KeyTools.NewKey();
        byte[] blob = UpdateKeys.BuildBlob(KeyTools.Hex(KeyTools.PublicX(priv)), KeyTools.Hex(KeyTools.PublicY(priv)));
        Ok(KeyTools.FixedEquals(blob, KeyTools.PublicBlob(priv)), "blob from X/Y = Windows' own public blob");
        string cs = KeyTools.CSharpKeys(new byte[][] { priv });
        Ok(cs.Contains(KeyTools.Hex(KeyTools.PublicX(priv))) && cs.Contains("new string[] {"), "keys.ps1 C# output carries X/Y");
        byte[] file = KeyTools.ProtectBackup(priv, "correct horse");
        Ok(KeyTools.FixedEquals(KeyTools.UnprotectBackup(file, "correct horse"), priv), "backup file round-trips");
        byte[] bad = (byte[])file.Clone(); bad[4 + 16 + 16 + 3] ^= 0x01;   // a ciphertext byte
        string msg = "";
        try { KeyTools.UnprotectBackup(bad, "correct horse"); } catch (CryptographicException ex) { msg = ex.Message; }
        Ok(msg.Contains("HMAC"), "a tampered byte fails the HMAC before decrypting: " + msg);
        msg = "";
        try { KeyTools.UnprotectBackup(file, "wrong"); } catch (CryptographicException ex) { msg = ex.Message; }
        Ok(msg.Contains("HMAC"), "a wrong passphrase fails the HMAC");
    }

    // ---------------- manifest + signatures (plan §5.2) ----------------
    public static byte[] ManifestBytes(int release, string version, string product, string tag, string sha, long size)
    {
        return Encoding.UTF8.GetBytes("{ \"format\": 1, \"product\": \"" + product + "\", \"release\": " + release + ", \"version\": \"" + version +
            "\", \"tag\": \"" + tag + "\", \"published\": \"2026-11-27T02:00:00Z\", \"exe\": { \"asset\": \"Laptap-Battery-Logger.exe\", \"sha256\": \"" +
            sha + "\", \"size\": " + size + " } }");
    }
    static List<byte[]> Keys(params byte[][] privs)
    {
        List<byte[]> l = new List<byte[]>();
        foreach (byte[] p in privs) l.Add(UpdateKeys.BuildBlob(KeyTools.Hex(KeyTools.PublicX(p)), KeyTools.Hex(KeyTools.PublicY(p))));
        return l;
    }

    static void ManifestAndSignatures()
    {
        Section("v15 manifest + signatures");
        byte[] primary = KeyTools.NewKey(), backup = KeyTools.NewKey(), other = KeyTools.NewKey();
        string sha = new string('a', 64);
        byte[] m = ManifestBytes(2, "15.0.1", UpdateConfig.Product, "v15.0.1", sha, 181760);
        string sig = Convert.ToBase64String(KeyTools.Sign(primary, m));
        Ok(Updater.VerifySig(m, sig, Keys(primary, backup)), "good signature (primary)");
        Ok(Updater.VerifySig(m, Convert.ToBase64String(KeyTools.Sign(backup, m)), Keys(primary, backup)), "backup key verifies too");
        Ok(!Updater.VerifySig(m, Convert.ToBase64String(KeyTools.Sign(other, m)), Keys(primary, backup)), "wrong key refused");
        byte[] flipped = (byte[])m.Clone(); flipped[20] ^= 0x01;
        Ok(!Updater.VerifySig(flipped, sig, Keys(primary, backup)), "flipped manifest byte refused");
        Ok(!Updater.VerifySig(m, "not base64!", Keys(primary)), "garbage signature refused");
        string e; Manifest man = Updater.Parse(m, out e);
        Ok(man != null && man.Release == 2 && man.Tag == "v15.0.1" && man.Size == 181760, "manifest parses");
        Eq(null, Updater.CheckRules(man, 1), "release 2 over own 1: accepted");
        Eq("not newer", Updater.CheckRules(man, 2), "equal release refused");
        Eq("not newer", Updater.CheckRules(man, 3), "lower release refused");
        Manifest jump = Updater.Parse(ManifestBytes(1002, "15.0.1", UpdateConfig.Product, "v15.0.1", sha, 10), out e);
        Eq("release jump too big", Updater.CheckRules(jump, 1), "> own + 1000 refused (I3)");
        Eq(null, Updater.CheckRules(Updater.Parse(ManifestBytes(1001, "15.0.1", UpdateConfig.Product, "v15.0.1", sha, 10), out e), 1), "own + 1000 accepted");
        Eq("wrong product", Updater.CheckRules(Updater.Parse(ManifestBytes(2, "15.0.1", "other", "v15.0.1", sha, 10), out e), 1), "wrong product");
        Eq("bad tag", Updater.CheckRules(Updater.Parse(ManifestBytes(2, "15.0.1", UpdateConfig.Product, "v15.0.2", sha, 10), out e), 1), "tag must equal v+version");
        Eq("bad tag", Updater.CheckRules(Updater.Parse(ManifestBytes(2, "15.0.1", UpdateConfig.Product, "15.0.1", sha, 10), out e), 1), "tag format");
        Eq(null, Updater.CheckRules(Updater.Parse(ManifestBytes(2, "15.0.1", UpdateConfig.Product, "v15.0.1", sha, 20 * 1024 * 1024), out e), 1), "20 MiB update accepted");
        Eq("bad size", Updater.CheckRules(Updater.Parse(ManifestBytes(2, "15.0.1", UpdateConfig.Product, "v15.0.1", sha, 21 * 1024 * 1024), out e), 1), "size > 20 MiB refused");
        Ok(Updater.Parse(Encoding.UTF8.GetBytes("<html>captive portal</html>"), out e) == null, "HTML is not a manifest");
        string f = Path.Combine(tmpRoot, "asset.bin"); File.WriteAllBytes(f, new byte[] { 1, 2, 3 });
        Manifest real = Updater.Parse(ManifestBytes(2, "15.0.1", UpdateConfig.Product, "v15.0.1", Updater.Sha256Hex(f), 3), out e);
        Ok(Updater.FileMatches(f, real), "hash + size match");
        Manifest wrongSize = Updater.Parse(ManifestBytes(2, "15.0.1", UpdateConfig.Product, "v15.0.1", Updater.Sha256Hex(f), 4), out e);
        Ok(!Updater.FileMatches(f, wrongSize), "size mismatch refused");
        Manifest wrongHash = Updater.Parse(ManifestBytes(2, "15.0.1", UpdateConfig.Product, "v15.0.1", sha, 3), out e);
        Ok(!Updater.FileMatches(f, wrongHash), "hash mismatch refused");
    }

    // ---------------- skip rules, 404, deadline, TLS (plan §5.4 A) ----------------
    internal class MiniServer
    {
        public HttpListener L; public int Port; public int Hits; public Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(); public bool Down404;
        public MiniServer()
        {
            for (int p = 47680; p < 47700; p++)
            {
                HttpListener l = new HttpListener(); l.Prefixes.Add("http://localhost:" + p + "/");
                try { l.Start(); L = l; Port = p; break; } catch { try { l.Close(); } catch { } }
            }
            Thread t = new Thread(delegate()
            {
                while (L.IsListening)
                {
                    HttpListenerContext c;
                    try { c = L.GetContext(); } catch { return; }
                    Interlocked.Increment(ref Hits);
                    byte[] b;
                    string path = c.Request.Url.AbsolutePath;
                    if (!Down404 && Files.TryGetValue(path, out b)) { c.Response.StatusCode = 200; c.Response.OutputStream.Write(b, 0, b.Length); }
                    else c.Response.StatusCode = 404;
                    try { c.Response.OutputStream.Close(); } catch { }
                }
            });
            t.IsBackground = true; t.Start();
        }
        public string Base { get { return "http://localhost:" + Port; } }
        public void Stop() { try { L.Close(); } catch { } }
    }

    internal static Updater.CheckOptions Opts(string exe, string baseUrl)
    {
        Updater.CheckOptions o = new Updater.CheckOptions();
        o.ExePath = exe; o.OwnRelease = 1; o.BuildDateUtcMs = Rules.NowMs() - 86400000L; o.BaseUrl = baseUrl;
        return o;
    }

    static void SkipRulesAndDeadline()
    {
        Section("v15 update skip rules / deadline / TLS");
        string sessions = Path.Combine(Engine.BaseDir, "sessions");
        if (Directory.Exists(sessions)) Directory.Delete(sessions, true);   // the countdown tests left a running test behind
        byte[] key = KeyTools.NewKey();
        Environment.SetEnvironmentVariable("LAPTAP_BL_TEST_PUBKEYS", KeyTools.Hex(KeyTools.PublicX(key)) + ":" + KeyTools.Hex(KeyTools.PublicY(key)));
        string dir = Path.Combine(tmpRoot, "stick"); Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "Laptap Battery Logger.exe"); File.WriteAllBytes(exe, new byte[] { 0x4d, 0x5a });
        MiniServer srv = new MiniServer();
        try
        {
            Eq("off", Updater.CheckAndStage(Opts(exe, "")), "empty BaseUrl → updater off");
            Eq("not-https", Updater.CheckAndStage(Opts(exe, "http://example.com/x")), "non-HTTPS BaseUrl refused");
            Ok(Updater.CanReachUpdateService(srv.Base), "a 404 from the update endpoint still means the service is reachable");
            Updater.CheckOptions just = Opts(exe, srv.Base); just.UpdatedThisStart = true;
            Eq("just-updated", Updater.CheckAndStage(just), "skipped right after an update");

            Updater.Note = null; int h0 = srv.Hits;
            Eq("no-update", Updater.CheckAndStage(Opts(exe, srv.Base)), "HTTP 404 (no releases yet) → quiet no update");
            Eq(null, Updater.Note, "404 sets no note (M4)");
            Ok(srv.Hits > h0, "404 path did call the server");

            // a not-done session → no network call (M7)
            string sd = Path.Combine(Path.Combine(Engine.BaseDir, "sessions"), "x"); Directory.CreateDirectory(sd);
            File.WriteAllText(Path.Combine(sd, "session.json"), "{\"Status\":\"waiting\"}");
            h0 = srv.Hits;
            Eq("session", Updater.CheckAndStage(Opts(exe, srv.Base)), "a not-done session (even v14 'waiting') → skip");
            Eq(h0, srv.Hits, "no network call while a test is unfinished");
            Directory.Delete(sd, true);

            Updater.CheckOptions clock = Opts(exe, srv.Base); clock.BuildDateUtcMs = Rules.NowMs() + 3L * 86400000;
            Eq("clock", Updater.CheckAndStage(clock), "clock before the build date → skip");
            Ok((Updater.Note ?? "").StartsWith("Clock is wrong ("), "clock note: " + Updater.Note);

            File.WriteAllBytes(exe + ".part", new byte[] { 1 });
            Eq("part-fresh", Updater.CheckAndStage(Opts(exe, srv.Base)), "fresh .part (< 2 min) → another copy is downloading");
            File.SetLastWriteTimeUtc(exe + ".part", DateTime.UtcNow.AddMinutes(-10));
            Eq("no-update", Updater.CheckAndStage(Opts(exe, srv.Base)), "old .part → deleted and the check goes on");
            Ok(!File.Exists(exe + ".part"), "old .part deleted (I5)");
            Ok(Updater.TlsSet && ServicePointManager.SecurityProtocol == SecurityProtocolType.Tls12, "TLS 1.2 only, set before the first request (I1)");

            // a real release served locally → staged; then a bad signature → refused with the note
            byte[] asset = Encoding.ASCII.GetBytes("MZ pretend new exe " + Guid.NewGuid());
            string assetFile = Path.Combine(tmpRoot, "asset.exe"); File.WriteAllBytes(assetFile, asset);
            byte[] m = ManifestBytes(2, "15.0.1", UpdateConfig.Product, "v15.0.1", Updater.Sha256Hex(assetFile), asset.Length);
            srv.Files["/releases/latest/download/update.json"] = m;
            srv.Files["/releases/latest/download/update.json.sig"] = Encoding.ASCII.GetBytes(Convert.ToBase64String(KeyTools.Sign(key, m)));
            srv.Files["/releases/download/v15.0.1/Laptap-Battery-Logger.exe"] = asset;
            Updater.Note = null;
            Eq("staged", Updater.CheckAndStage(Opts(exe, srv.Base)), "good release → downloaded and staged");
            Ok(File.Exists(exe + ".update") && File.Exists(exe + ".update.json") && File.Exists(exe + ".update.json.sig") && !File.Exists(exe + ".part"), "staged files beside the exe");
            Eq("staged", Updater.CheckAndStage(Opts(exe, srv.Base)), "already staged → no second download");
            bool del; Manifest sm = Updater.StagedOk(exe, 1, out del);
            Ok(sm != null && sm.Release == 2, "staged copy re-verifies at the next start");
            Ok(Updater.StagedOk(exe, 2, out del) == null && del, "staged copy that is not newer → refused, delete");
            Updater.MarkBad(2);
            Ok(Updater.StagedOk(exe, 1, out del) == null, "a release marked bad is not swapped in");
            File.Delete(Path.Combine(Engine.BaseDir, "update-bad.json"));
            Updater.DeleteStaged(exe);

            srv.Files["/releases/latest/download/update.json.sig"] = Encoding.ASCII.GetBytes(Convert.ToBase64String(KeyTools.Sign(KeyTools.NewKey(), m)));
            Eq("bad-signature", Updater.CheckAndStage(Opts(exe, srv.Base)), "signed by an unknown key → refused");
            Eq("Update refused: bad signature", Updater.Note, "bad-signature note");
            Ok(!File.Exists(exe + ".update"), "nothing staged after a bad signature");

            srv.Files["/releases/latest/download/update.json"] = Encoding.UTF8.GetBytes("<html>captive portal</html>");
            Eq("check-failed", Updater.CheckAndStage(Opts(exe, srv.Base)), "captive-portal HTML → check failed");
            Eq("Update check failed (no internet?)", Updater.Note, "not called a bad signature unless it parsed");
        }
        finally { srv.Stop(); }

        // C3: a host that never resolves gives up within the 6 s deadline (DNS can't stall it)
        Stopwatch sw = Stopwatch.StartNew();
        string res = Updater.CheckAndStage(Opts(exe, "https://nonexistent.invalid"));
        sw.Stop();
        Eq("check-failed", res, "unresolvable host → check failed");
        Ok(sw.ElapsedMilliseconds < 6500, "gave up within the deadline (" + sw.ElapsedMilliseconds + " ms)");
        Updater.GetResult g = Updater.Get("http://10.255.255.1:81/never", 1024, 1500);   // unroutable: blackholed connect
        Ok(g.Body == null, "blackholed connect returns nothing");
    }

    // ---------------- swap + hand-off in a temp folder (plan §5.4 B) ----------------
    static string NewStick(string name, out string exe)
    {
        string d = Path.Combine(tmpRoot, name); Directory.CreateDirectory(d);
        exe = Path.Combine(d, "Laptap Battery Logger.exe");
        File.WriteAllText(exe, "OLD");
        File.WriteAllText(exe + ".update", "NEW");
        return d;
    }

    static void SwapAndHandOff()
    {
        Section("v15 swap + hand-off");
        string exe;
        NewStick("swap-ok", out exe);
        Ok(Updater.Swap(exe), "swap succeeds");
        Eq("NEW", File.ReadAllText(exe), "exe is the new one"); Eq("OLD", File.ReadAllText(exe + ".old"), "old kept as .old");
        bool released = false; IChild child;
        FakeLauncher ok = new FakeLauncher(); ok.Mode = "ok";
        File.WriteAllText(Updater.MarkerPath("stale"), "owner 1");
        string fail = Updater.HandOff(exe, "--qa-no-window", ok, delegate { released = true; }, 5000, out child);
        Eq(null, fail, "child said alive then owner → hand-off ok");
        Ok(released, "main mutex released once the child is alive");
        Ok(!File.Exists(Updater.MarkerPath("stale")), "stale markers deleted before the launch (C2)");
        Ok(ok.Args.StartsWith("--updated ") && ok.Args.EndsWith("--qa-no-window"), "child gets --updated <nonce> + the QA switches");
        Updater.Repair(exe);
        Ok(!File.Exists(exe + ".old"), "next start deletes the leftover .old");

        // second move fails (Defender holding the new file) → .old goes back
        NewStick("swap-locked", out exe);
        using (new FileStream(exe + ".update", FileMode.Open, FileAccess.Read, FileShare.None))
            Ok(!Updater.Swap(exe), "second move fails → swap refused");
        Eq("OLD", File.ReadAllText(exe), "running exe restored after the failed swap");

        // no marker within the wait → rollback: exe → .bad, .old → exe, release marked bad
        NewStick("silent", out exe); Updater.Swap(exe);
        FakeLauncher silent = new FakeLauncher(); silent.Mode = "silent";
        fail = Updater.HandOff(exe, "", silent, delegate { }, 1500, out child);
        Eq("Update failed to start", fail, "no marker → failed");
        Updater.Rollback(exe, child, 7, fail);
        Eq("OLD", File.ReadAllText(exe), "rollback restored the old exe");
        Eq("NEW", File.ReadAllText(exe + ".bad"), "the new exe is kept as .bad");
        Ok(silent.Child.Killed, "the child was killed");
        Ok(Updater.IsBad(7), "release marked bad for 24 h");

        // a marker for another nonce is ignored (C2)
        NewStick("stale", out exe); Updater.Swap(exe);
        FakeLauncher stale = new FakeLauncher(); stale.Mode = "stale";
        Eq("Update failed to start", Updater.HandOff(exe, "", stale, delegate { }, 1500, out child), "a marker with another nonce doesn't count");

        // the child exits before "owner"
        NewStick("exits", out exe); Updater.Swap(exe);
        FakeLauncher exits = new FakeLauncher(); exits.Mode = "exit";
        Eq("Update failed to start", Updater.HandOff(exe, "", exits, delegate { }, 3000, out child), "child exits before owner → failed");

        // the new exe vanished (antivirus quarantine) before / at start
        NewStick("vanish", out exe); Updater.Swap(exe); File.Delete(exe);
        Eq("Update vanished (antivirus?)", Updater.HandOff(exe, "", ok, delegate { }, 1500, out child), "exe gone before start → vanished");
        Updater.Rollback(exe, child, 8, "Update vanished (antivirus?)");
        Eq("OLD", File.ReadAllText(exe), "rollback after a vanished exe restores the old one");
        NewStick("throw", out exe); Updater.Swap(exe);
        FakeLauncher thr = new FakeLauncher(); thr.Mode = "throw";
        Ok(Updater.HandOff(exe, "", thr, delegate { }, 1500, out child) != null, "Process.Start throws → failed (rollback path)");

        // light repair: a lone .old (stick pulled between the two renames) goes back
        string d = Path.Combine(tmpRoot, "lone"); Directory.CreateDirectory(d);
        string lone = Path.Combine(d, "Laptap Battery Logger.exe"); File.WriteAllText(lone + ".old", "OLD");
        Updater.Repair(lone);
        Ok(File.Exists(lone) && !File.Exists(lone + ".old"), "lone .old renamed back (I6)");
    }

    // C1: a second launch during the hand-off waits on the update mutex, then finds the main mutex taken → reconnects.
    static void DoubleLaunchWaits()
    {
        Section("v15 double launch during hand-off");
        string ns = Guid.NewGuid().ToString("N").Substring(0, 8);
        string upd = @"Local\LaptapBatteryLogger-update-t" + ns, main = @"Local\LaptapBatteryLogger-t" + ns;
        ManualResetEvent holding = new ManualResetEvent(false), letGo = new ManualResetEvent(false);
        Thread first = new Thread(delegate()
        {
            using (Mutex u = new Mutex(false, upd)) using (Mutex m = new Mutex(false, main))
            {
                u.WaitOne(); m.WaitOne(); holding.Set();
                letGo.WaitOne();
                u.ReleaseMutex();                    // hand-off done: the new exe now owns main (here: still held)
                Thread.Sleep(500);
                m.ReleaseMutex();
            }
        });
        first.Start(); holding.WaitOne();
        Stopwatch sw = Stopwatch.StartNew();
        bool gotUpd, gotMain;
        using (Mutex u2 = new Mutex(false, upd))
        {
            ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(700); letGo.Set(); });
            gotUpd = u2.WaitOne(30000);
            long waited = sw.ElapsedMilliseconds;
            using (Mutex m2 = new Mutex(false, main)) gotMain = m2.WaitOne(0);
            Ok(gotUpd && waited >= 600, "second launch waited for the hand-off (" + waited + " ms)");
            Ok(!gotMain, "then saw a running logger → reconnects (no second logger, no blacklisting)");
            if (gotUpd) u2.ReleaseMutex();
        }
        first.Join();
    }

    // ---------------- build audit fixes: S1, U1, U2, U4 ----------------
    public static void AuditFixes(string builtExe)
    {
        Section("v15 audit S1: stale / cancelled latch");
        // (a) Cancel clears the latch: drain to 80% after a Cancel → Start → no countdown (row 22's 96% rule holds)
        FakeHw h = new FakeHw(); long[] p = { h.T }; Engine e = NewEngine(h, p);
        e.Tick(); h.Ac = false; Step(e, h, p, 1000);
        Ok(e.CountdownEndsMs.HasValue, "unplugged at 100% → countdown");
        e.Command("cancel", null);
        h.Pct = 80; Step(e, h, p, 40 * 60000);
        e.Command("arm", null); Step(e, h, p, 1000); Step(e, h, p, 3000);
        Ok(!e.CountdownEndsMs.HasValue && e.IsReady, "after Cancel the old 100% latch is gone: 80% → no countdown");
        e.Publish(); Eq(false, ((Dictionary<string, object>)e.Snapshot["checks"])["pct96"], "pct96 judged on the current 80%");

        // (b) a latch older than 60 s (nobody watching) is dropped: judged and started on the current reading
        FakeHw h2 = new FakeHw(); long[] p2 = { 0 }; Engine e2 = NewEngine(h2, p2);
        e2.Tick(); h2.Ac = false; h2.T += 1000; e2.Tick();     // unplug, no page polling → no countdown
        h2.Pct = 80; h2.T += 61000; e2.Tick();
        p2[0] = h2.T; h2.T += 1000; e2.Tick(); h2.T += 3000; p2[0] = h2.T; e2.Tick();
        Ok(e2.IsReady && !e2.CountdownEndsMs.HasValue, "hour-old 100% latch, now 80%, page shows up → no countdown, no start");
        FakeHw h3 = new FakeHw(); long[] p3 = { 0 }; Engine e3 = NewEngine(h3, p3);
        e3.Tick(); h3.Ac = false; h3.T += 1000; e3.Tick();
        h3.Pct = 97; h3.Mwh = 45800; h3.T += 30 * 60000; e3.Tick();   // a sleeping/minimised half hour
        p3[0] = h3.T; h3.T += 1000; e3.Tick();
        Ok(e3.CountdownEndsMs.HasValue, "still ≥ 96% now → countdown");
        long cdEnd = e3.CountdownEndsMs.Value;
        h3.T = cdEnd; p3[0] = h3.T; e3.Tick();
        Ok(e3.S != null && e3.S.Status == "running", "started");
        Eq(cdEnd, e3.S.UnplugMs, "stale latch: segment 1 starts at the countdown's end, not the old unplug");
        Eq(97.0, e3.S.StartPct, "stale latch: start % = the current reading");
        e3.Command("stop", null);   // closes its CSV before the next section clears the folder

        Section("v15 audit U1: owner only after the first answered poll; laptop refusals; --self-test");
        Server srv = new Server(); int first = 0;
        srv.GetSnapshot = delegate { return new Dictionary<string, object>(); };
        srv.OnFirstState = delegate { first++; };
        Ok(srv.Start(), "server starts");
        try
        {
            for (int i = 0; i < 3; i++)
            {
                HttpWebRequest rq = (HttpWebRequest)WebRequest.Create("http://localhost:" + srv.Port + "/api/state");
                rq.Headers["X-Laptap-Token"] = srv.Token; rq.Proxy = null;
                using (WebResponse r = rq.GetResponse()) { }
            }
        }
        finally { srv.Stop(); }
        Eq(1, first, "OnFirstState fires once, after the first answered /api/state");
        string exe; NewStick("refused", out exe); Updater.Swap(exe);
        FakeLauncher refused = new FakeLauncher(); refused.Mode = "refused";
        IChild child;
        Eq(null, Updater.HandOff(exe, "", refused, delegate { }, 3000, out child), "child refused for a laptop reason (no battery) → not a failed release, no rollback");
        if (File.Exists(builtExe))
        {
            ProcessStartInfo psi = new ProcessStartInfo(builtExe, "--self-test"); psi.UseShellExecute = false; psi.CreateNoWindow = true;
            using (Process pr = Process.Start(psi)) { bool done = pr.WaitForExit(30000); Ok(done && pr.ExitCode == 0, "built exe --self-test exits 0 (" + (done ? pr.ExitCode.ToString() : "timeout") + ")"); }
        }
        else Ok(false, "built exe for --self-test: " + builtExe);

        Section("v15 audit U2: --verify-release against the field keys");
        byte[] fieldKey = KeyTools.NewKey(), otherKey = KeyTools.NewKey();
        string rel = Path.Combine(tmpRoot, "release"); Directory.CreateDirectory(rel);
        string asset = Path.Combine(rel, UpdateConfig.AssetName); File.WriteAllBytes(asset, Encoding.ASCII.GetBytes("MZ release asset"));
        byte[] man = Encoding.UTF8.GetBytes("{ \"format\": 1, \"product\": \"" + UpdateConfig.Product + "\", \"release\": " + AppVersion.Release + ", \"version\": \"" + AppVersion.Version +
            "\", \"tag\": \"v" + AppVersion.Version + "\", \"published\": \"x\", \"exe\": { \"asset\": \"" + UpdateConfig.AssetName + "\", \"sha256\": \"" + Updater.Sha256Hex(asset) + "\", \"size\": " + new FileInfo(asset).Length + " } }");
        File.WriteAllBytes(Path.Combine(rel, "update.json"), man);
        File.WriteAllText(Path.Combine(rel, "update.json.sig"), Convert.ToBase64String(KeyTools.Sign(fieldKey, man)));
        string kf = Path.Combine(tmpRoot, "field.txt"), kw = Path.Combine(tmpRoot, "wrong.txt");
        File.WriteAllText(kf, KeyTools.Hex(KeyTools.PublicX(otherKey)) + ":" + KeyTools.Hex(KeyTools.PublicY(otherKey)) + "\r\n" + KeyTools.Hex(KeyTools.PublicX(fieldKey)) + ":" + KeyTools.Hex(KeyTools.PublicY(fieldKey)) + "\r\n");
        File.WriteAllText(kw, KeyTools.Hex(KeyTools.PublicX(otherKey)) + ":" + KeyTools.Hex(KeyTools.PublicY(otherKey)) + "\r\n");
        string why;
        Eq(0, Updater.VerifyRelease(rel, kf, out why), "signed by a field key → accepted (" + why + ")");
        Eq(4, Updater.VerifyRelease(rel, kw, out why), "not signed by any field key → refused");

        Section("v15 audit U4: unfinished / unreadable sessions can't block updates forever");
        string sd = Path.Combine(Engine.BaseDir, "sessions");
        if (Directory.Exists(sd)) Directory.Delete(sd, true);
        string bad = Path.Combine(sd, "20260101-000000"); Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "session.json"), "{ not json");
        Ok(Updater.AnySessionNotDone(), "an unreadable session.json counts as unfinished (safe side)");
        FakeHw h4 = new FakeHw(); long[] p4 = { h4.T }; Engine e4 = NewEngine(h4, p4);
        e4.Recover();
        Ok(File.Exists(Path.Combine(bad, "session.json.bad")) && !File.Exists(Path.Combine(bad, "session.json")), "Recover sets an unreadable session.json aside (.bad)");
        Ok(!Updater.AnySessionNotDone(), "after that it no longer blocks updates");
        string old = Path.Combine(sd, "20250101-000000"); Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "session.json"), "{\"Status\":\"running\"}");
        File.SetLastWriteTimeUtc(Path.Combine(old, "session.json"), DateTime.UtcNow.AddDays(-3));
        Ok(!Updater.AnySessionNotDone(), "an unfinished session untouched for > 48 h no longer blocks updates");
        File.SetLastWriteTimeUtc(Path.Combine(old, "session.json"), DateTime.UtcNow);
        Updater.Note = null;
        Eq("session", Updater.CheckAndStage(Opts(Path.Combine(tmpRoot, "x.exe"), "https://example.invalid")), "a fresh unfinished session still waits");
        Eq("Update waits: an unfinished test", Updater.Note, "…and says so in the version tooltip");
        try { Directory.Delete(sd, true); } catch { }
    }
}
