// src/Program.cs
// Laptap Battery Logger: one exe. Update mutex → repair / staged swap (plan §5.4 B) → single instance → self-check →
// local server → Edge app window → recovery → static read → background update check (§5.4 A) → main loop.
// The main thread owns keep-awake and the session.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

#if TESTHOOKS
[assembly: AssemblyTitle("Laptap Enterprises, Inc. - Battery Logger v" + AppVersion.Version + " --test")]
[assembly: AssemblyProduct("Laptap Enterprises, Inc. - Battery Logger v" + AppVersion.Version + " --test")]
#else
[assembly: AssemblyTitle("Laptap Enterprises, Inc. - Battery Logger v" + AppVersion.Version + " --stable")]
[assembly: AssemblyProduct("Laptap Enterprises, Inc. - Battery Logger v" + AppVersion.Version + " --stable")]
#endif
[assembly: AssemblyCompany("Laptap Enterprises, Inc.")]
[assembly: AssemblyDescription("Battery run-down test logger")]

static class Program
{
#if TESTHOOKS
    const string Title = "Laptap Enterprises, Inc. - Battery Logger v" + AppVersion.Version + " --test";
#else
    const string Title = "Laptap Enterprises, Inc. - Battery Logger v" + AppVersion.Version + " --stable";
#endif
    static string BuildChannel
    {
        get
        {
#if TESTHOOKS
            return "test";
#else
            return "stable";
#endif
        }
    }

    static string ChannelizePage(string index)
    {
        return index.Replace("<meta name=\"laptap-channel\" content=\"stable\">",
            "<meta name=\"laptap-channel\" content=\"" + BuildChannel + "\">");
    }

    static Mutex mutex, updateMutex;
    static bool ownsUpdateMutex;
    static Server server;
    static bool quiet;   // --qa-no-window: never put a box on screen
    static string MainMutexName { get { return @"Local\LaptapBatteryLogger" + UpdateConfig.TestNamespace; } }
    static string UpdateMutexName { get { return @"Local\LaptapBatteryLogger-update" + UpdateConfig.TestNamespace; } }
    static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }

    [STAThread]
    static int Main(string[] args)
    {
        int qaAlarm = 0, afterExit = 0; bool qaNoWindow = false, noUpdate = false, selfTest = false; string updatedNonce = null, verifyDir = null, keysFile = null;
        List<string> passOn = new List<string>();   // QA switches handed to a swapped-in exe
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--qa-alarm-pct" && i + 1 < args.Length) { int v; if (int.TryParse(args[i + 1], out v) && v >= 1 && v <= 95) qaAlarm = v; passOn.Add(args[i]); passOn.Add(args[i + 1]); i++; }
            else if (args[i] == "--qa-no-window") { qaNoWindow = true; quiet = true; passOn.Add(args[i]); }
            else if (args[i] == "--qa-out" && i + 1 < args.Length) { if (Directory.Exists(args[i + 1])) Report.QaOutDir = args[i + 1]; passOn.Add(args[i]); passOn.Add("\"" + args[i + 1] + "\""); i++; }
            else if (args[i] == "--updated" && i + 1 < args.Length) { updatedNonce = args[i + 1]; i++; }
            else if (args[i] == "--no-update") noUpdate = true;
            else if (args[i] == "--verify-release" && i + 1 < args.Length) { verifyDir = args[i + 1]; i++; }
            else if (args[i] == "--keys-file" && i + 1 < args.Length) { keysFile = args[i + 1]; i++; }
            else if (args[i] == "--self-test") selfTest = true;
            else if (args[i] == "--after-exit" && i + 1 < args.Length) { int v; if (int.TryParse(args[i + 1], out v)) afterExit = v; i++; }   // v15.1 Restart now
        }
        // v15.1 §2.3: started by Restart now. Wait (up to 30 s) for the old logger to exit, then start exactly like a
        // double-click, so the v15 start-time swap installs the staged update.
        if (afterExit > 0) WaitForExit(afterExit, 30000);
        if (UpdateConfig.TestNamespace.Length > 0) Engine.BaseDir = Engine.BaseDir + "-test-" + UpdateConfig.TestNamespace;
        try { Directory.CreateDirectory(Engine.BaseDir); } catch { }

        // --verify-release <dir>: read-only check, no window, exit code only (0 = good).
        if (verifyDir != null)
        {
            string why; int rc = Updater.VerifyRelease(verifyDir, keysFile, out why);
            try { Console.WriteLine(why); } catch { }
            return rc;
        }
        // --self-test: resources, engine and local server work (release.ps1 runs it before signing, U1). 0 = good.
        if (selfTest) { quiet = true; return SelfTest(); }

        string updatedFrom = null;
        if (updatedNonce != null)
        {
            // Child of an update (C1, C2): say "alive" before anything else, then wait for the old process's mutex.
            Updater.Announce(updatedNonce, "alive");
            updatedFrom = Updater.TakeUpdatedFrom(updatedNonce);
        }
        else
        {
            // Every other launch waits while an update hand-off is in progress (C1), then repairs / swaps.
            try
            {
                updateMutex = new Mutex(false, UpdateMutexName);
                try { ownsUpdateMutex = updateMutex.WaitOne(30000); } catch (AbandonedMutexException) { ownsUpdateMutex = true; }
            }
            catch { ownsUpdateMutex = false; }
        }

        bool owned;
        try
        {
            mutex = new Mutex(false, MainMutexName);
            int wait = updatedNonce != null ? 30000 : 0;
            try { owned = mutex.WaitOne(wait); } catch (AbandonedMutexException) { owned = true; }
        }
        catch (Exception ex) { ReleaseUpdateMutex(); return Fail("could not check for a running logger (" + ex.Message + ")", ex); }

        if (updatedNonce != null)
        {
            if (!owned) { Updater.Log("updated child: main mutex not free in 30 s"); return 1; }
            childNonce = updatedNonce;   // U1: "owner" is written once the server is up and the first page poll was answered
        }

        if (!owned) { ReleaseUpdateMutex(); return Reconnect(qaNoWindow); }

        // ---- B: repair + staged swap, holding both mutexes (plan §5.4 B) ----
        if (updatedNonce == null && !noUpdate)
        {
            int handoff = TrySwap(string.Join(" ", passOn.ToArray()));
            if (handoff >= 0) return handoff;   // the new exe took over (or we restarted after a rollback)
        }
        ReleaseUpdateMutex();
        try { File.Delete(Path.Combine(Engine.BaseDir, "helper.json")); } catch { }   // M10: never hand out a stale token

        try
        {
            // ---- self-check (I5) ----
            bool ac, saver, present, crit; double? pct;
            if (!Battery.PowerStatus(out ac, out pct, out saver, out present, out crit)) return Fail("Windows did not report the power status", null);
            // G3 (v15.1): no battery opens the window on the "No battery found" screen; only the battery check fails.
            string desk = Report.Desktop();
            if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk)) { RefusedByLaptop(); return Fail("the Desktop folder was not found", null); }

            Engine en = new Engine();
            en.QaAlarmPct = qaAlarm; en.QaNoWindow = qaNoWindow; en.UpdatedFrom = updatedFrom;
            if (qaAlarm > 0) en.AlarmPct = qaAlarm;
            string index = Resource("index.html"), logic = Resource("logic.js");   // into memory once (I1)
            if (index == null || logic == null) return Fail("the page is missing from the exe", null);
            index = ChannelizePage(index);
            en.IndexHtml = index;

            server = new Server();
            server.IndexHtml = Encoding.UTF8.GetBytes(index);
            server.LogicJs = Encoding.UTF8.GetBytes(logic);
            server.GetSnapshot = delegate { return en.Snapshot; };
            server.GetApp = en.AppDict;
            // v16 §1/§6: the embedded logo and the packaged video (served from the resource stream, never copied to memory)
            server.LogoSvg = ResourceBytes("brand/logo.svg");
            server.IconIco = ResourceBytes("brand/favicon.ico");
            server.MediaExt = MediaResource();
            server.OpenMedia = delegate { return server.MediaExt == null ? null : Assembly.GetExecutingAssembly().GetManifestResourceStream("loop." + server.MediaExt); };
            en.MediaInfo = MediaInfoDict(server.MediaExt);
            Updater.Changed = delegate { en.RequestPublish = true; };
            string passArgs = string.Join(" ", passOn.ToArray());
            en.SpawnAfterExit = delegate { return SpawnAfterExit(passArgs); };
            server.OnFirstState = delegate { if (childNonce != null) Updater.Announce(childNonce, "owner"); };
            en.LastPollMs = delegate { return server.LastStateGetMs; };
            server.OnCrash = delegate(Exception ex) { Fail("the local server stopped (" + ex.Message + ")", ex); Environment.Exit(1); };
            if (!server.Start()) return Fail("no free local port between " + Server.FirstPort + " and " + Server.LastPort, null);
            WriteHelperJson(server.Port, server.Token);

            string url = "http://localhost:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/#t=" + server.Token;
            en.OpenWindow = delegate { OpenWindow(url); };
            en.OpenFile = delegate(string f) { ProcessStartInfo p = new ProcessStartInfo(f); p.UseShellExecute = true; Process.Start(p); };
            en.Publish();
            if (!qaNoWindow) { try { OpenWindow(url); } catch (Exception ex) { LogError("could not open the window", ex); } }   // M11

            en.Recover();
            // v16 §4 (C1, I6): suspend / resume → a command for the main thread; the callback waits ≤ 1.5 s on a suspend.
            PowerNotify.Start(delegate(int pbt)
            {
                string cmd = PowerNotify.CommandFor(pbt);
                if (cmd == null) return;
                Command c = new Command(); c.Name = cmd; c.Body = new Dictionary<string, object>(); c.Body["ms"] = Rules.NowMs();
                try { server.Commands.Add(c); } catch { return; }
                if (cmd == "suspend") c.Done.Wait(1500);
            });
            if (en.S == null || en.S.Status == "done") { StaticInfo si = Battery.ReadStatic(true); if (en.S == null) en.Info = si; else if (en.S.StartInfo == null) en.Info = si; }
            en.Tick();
            en.Publish();

            // ---- A: background update check; offline probes retry while idle and pause during a test ----
            bool stopping = false;
            if (!noUpdate)
            {
                Updater.CheckOptions o = new Updater.CheckOptions();
                o.ExePath = ExePath; o.OwnRelease = AppVersion.Release; o.BuildDateUtcMs = AppVersion.BuildDateUtcMs;
                o.BaseUrl = UpdateConfig.BaseUrl; o.UpdatedThisStart = updatedNonce != null;
                o.Idle = delegate { Engine e = en; return e.S == null || e.S.Status == "done"; };
                o.Stopping = delegate { return stopping; };
                ThreadStart check = delegate
                {
                    try
                    {
                        while (!stopping)
                        {
                            string r = Updater.CheckAndStage(o);
                            Updater.Log("check: " + r); en.RequestPublish = true;
                            if (r != "check-failed") return;

                            while (!stopping)
                            {
                                if (!o.Idle()) { Thread.Sleep(1000); continue; }
                                if (Updater.CanReachUpdateService(o.BaseUrl)) { Updater.Note = null; break; }
                                Thread.Sleep(UpdateConfig.OfflineRetryIntervalMs);
                            }
                        }
                    }
                    catch (Exception ex) { Updater.Log("check crashed: " + ex.Message); Updater.SetPhase("idle", null, null); }
                };
                int retried = 0;
                en.RetryUpdate = delegate   // Try again (v15.1): once per start
                {
                    if (Interlocked.Exchange(ref retried, 1) == 1) return;
                    Thread rt = new Thread(check); rt.IsBackground = true; rt.Name = "update-retry"; rt.Priority = ThreadPriority.BelowNormal; rt.Start();
                };
                Thread ut = new Thread(check);
                ut.IsBackground = true; ut.Name = "update"; ut.Priority = ThreadPriority.BelowNormal;
                ut.Start();
            }

            // ---- main loop (M3) ----
            Stopwatch tick = Stopwatch.StartNew();
            while (true)
            {
                Command c;
                if (server.Commands.TryTake(out c, en.WaitMs()))
                {
                    try { c.Result = en.Command(c.Name, c.Body); }
                    catch (Exception ex) { c.Result = new Dictionary<string, object>(); c.Result["ok"] = false; c.Result["error"] = ex.Message; LogError("command " + c.Name, ex); }
                    c.Done.Set();
                }
                if (tick.ElapsedMilliseconds >= 950 || en.CountdownDue()) { tick.Restart(); try { en.Tick(); } catch (Exception ex) { LogError("tick", ex); } }   // M1
                if (en.RequestPublish) { en.RequestPublish = false; en.Publish(); }
                if (en.ShouldExit()) break;
            }
            stopping = true;
            PowerNotify.Stop();
            server.Stop();
            DeleteHelperIfOurs();   // audit I3: never another process's helper.json
            try { mutex.ReleaseMutex(); } catch { }
            return 0;
        }
        catch (Exception ex) { return Fail("unexpected error (" + ex.Message + ")", ex); }
    }

    static string childNonce;   // set in a run started with --updated

    // v15.1 §2.3 Restart now: "<own exe> --after-exit <own pid>" (+ the QA switches, empty for staff). null = started.
    internal static Func<string, ProcessStartInfo, bool> StartProcess = delegate(string what, ProcessStartInfo p) { return Process.Start(p) != null; };
    internal static string SpawnAfterExit(string passArgs)
    {
        try
        {
            ProcessStartInfo p = new ProcessStartInfo(ExePath, "--after-exit " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                (string.IsNullOrEmpty(passArgs) ? "" : " " + passArgs));
            p.UseShellExecute = false;
            if (!StartProcess("after-exit", p)) return "Could not restart the logger.";
            return null;
        }
        catch (Exception ex) { LogError("restart now", ex); return "Could not restart the logger."; }
    }

    // --after-exit: true once the pid is gone (or was never there); false after the wait (the start carries on anyway).
    internal static bool WaitForExit(int pid, int ms)
    {
        try { using (Process p = Process.GetProcessById(pid)) return p.WaitForExit(ms); }
        catch (ArgumentException) { return true; }    // not found = already gone
        catch { return true; }
    }

    // audit I3: delete helper.json only when it names this process.
    internal static void DeleteHelperIfOurs()
    {
        string f = Path.Combine(Engine.BaseDir, "helper.json");
        try
        {
            if (!File.Exists(f)) return;
            Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
            object pid;
            if (d != null && d.TryGetValue("pid", out pid) && Convert.ToInt32(pid, CultureInfo.InvariantCulture) != Process.GetCurrentProcess().Id) return;
            File.Delete(f);
        }
        catch { try { File.Delete(f); } catch { } }
    }

    // A refusal that is the laptop's fault (no battery, no Desktop) is not the release's: no rollback (C2).
    static void RefusedByLaptop() { if (childNonce != null) Updater.Announce(childNonce, "refused"); }

    // U1: load both resources, build the engine, start the server on a free port, read /api/state and the page back.
    static int SelfTest()
    {
        Server s = null;
        try
        {
            string index = Resource("index.html"), logic = Resource("logic.js");
            if (index == null || logic == null || !index.Contains("<template id=\"report\">") || ResourceBytes("brand/favicon.ico") == null) return 2;
            index = ChannelizePage(index);
            Engine en = new Engine(); en.IndexHtml = index; en.QaNoWindow = true;
            en.Publish();
            s = new Server();
            s.IndexHtml = Encoding.UTF8.GetBytes(index); s.LogicJs = Encoding.UTF8.GetBytes(logic);
            s.GetSnapshot = delegate { return en.Snapshot; };
            if (!s.Start()) return 3;
            string bas = "http://localhost:" + s.Port.ToString(CultureInfo.InvariantCulture);
            System.Net.HttpWebRequest rq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(bas + "/api/state");
            rq.Headers["X-Laptap-Token"] = s.Token; rq.Timeout = 10000; rq.Proxy = null;
            string st;
            using (System.Net.WebResponse r = rq.GetResponse()) using (StreamReader sr = new StreamReader(r.GetResponseStream())) st = sr.ReadToEnd();
            if (!st.Contains("\"version\":\"" + AppVersion.Version + "\"")) return 4;
            System.Net.HttpWebRequest pg = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(bas + "/");
            pg.Timeout = 10000; pg.Proxy = null;
            string page;
            using (System.Net.WebResponse r = pg.GetResponse()) using (StreamReader sr = new StreamReader(r.GetResponseStream())) page = sr.ReadToEnd();
            if (!page.Contains("<script src=\"logic.js\"></script>")) return 5;
            return 0;
        }
        catch (Exception ex) { LogError("self-test", ex); return 9; }
        finally { if (s != null) s.Stop(); }
    }

    static void ReleaseUpdateMutex()
    {
        if (ownsUpdateMutex) { try { updateMutex.ReleaseMutex(); } catch { } ownsUpdateMutex = false; }
    }

    // Plan §5.4 B. Returns -1 to carry on starting normally, or an exit code when another process took over.
    static int TrySwap(string passArgs)
    {
        string exe = ExePath;
        try
        {
            Updater.Repair(exe);
            bool del;
            Manifest m = Updater.StagedOk(exe, AppVersion.Release, out del);
            if (m == null) { if (del) { Updater.DeleteStaged(exe); Updater.Log("staged copy refused and deleted"); } return -1; }
            if (Updater.AnySessionNotDone()) { Updater.Log("staged " + m.Version + " waits: a test is not finished"); return -1; }
            if (!ownsUpdateMutex) return -1;
            if (!Updater.Swap(exe)) return -1;
            Updater.Log("swapped to " + m.Version + "; handing off");
            IChild child;
            string fail = Updater.HandOff(exe, passArgs, new ProcessLauncher(), delegate { try { mutex.ReleaseMutex(); } catch { } }, 60000, out child);   // U1: owner waits for Edge + the first poll
            if (fail == null) { ReleaseUpdateMutex(); return 0; }   // the new exe owns everything now
            Updater.Log("hand-off failed: " + fail);
            Updater.Rollback(exe, child, m.Release, fail);
            try { mutex.ReleaseMutex(); } catch { }
            ReleaseUpdateMutex();
            try { ProcessStartInfo p = new ProcessStartInfo(exe, "--no-update " + passArgs); p.UseShellExecute = false; Process.Start(p); }
            catch (Exception ex) { LogError("restart after a failed update", ex); }
            return 0;
        }
        catch (Exception ex) { Updater.Log("swap step: " + ex.Message); return -1; }
    }

    internal static byte[] ResourceBytes(string name)
    {
        try
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) return null;
                using (MemoryStream m = new MemoryStream()) { s.CopyTo(m); return m.ToArray(); }
            }
        }
        catch { return null; }
    }

    // v16 §6: which packaged video is embedded ("webm" wins over "mp4"), or null.
    internal static string MediaResource()
    {
        foreach (string ext in new string[] { "webm", "mp4" })
        {
            try { using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("loop." + ext)) if (s != null) return ext; } catch { }
        }
        return null;
    }

    internal static Dictionary<string, object> MediaInfoDict(string ext)
    {
        Dictionary<string, object> m = Engine.MediaNone();
        if (ext == null) return m;
        m["packaged"] = ext;
        try { using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("loop." + ext)) m["sizeBytes"] = s.Length; } catch { }
        return m;
    }

    static string Resource(string name)
    {
        try
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) return null;
                using (StreamReader r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }
        catch { return null; }
    }

    // v15.1: port, token, pid, version, release; written atomically (temp + replace).
    internal static void WriteHelperJson(int port, string token)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["port"] = port; d["token"] = token; d["pid"] = Process.GetCurrentProcess().Id;
        d["version"] = AppVersion.Version; d["release"] = AppVersion.Release;
        string f = Path.Combine(Engine.BaseDir, "helper.json"), tmp = f + ".tmp";
        File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(d), new UTF8Encoding(false));
        if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f);
    }

    // Second launch: reuse the running logger (M4). Its helper.json may still be on the way (powercfg), so retry ~10 s.
    static int Reconnect(bool noWindow)
    {
        string f = Path.Combine(Engine.BaseDir, "helper.json");
        for (int i = 0; i < 20; i++)
        {
            try
            {
                if (File.Exists(f))
                {
                    Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                    string url = "http://localhost:" + Convert.ToString(d["port"], CultureInfo.InvariantCulture) + "/#t=" + Convert.ToString(d["token"], CultureInfo.InvariantCulture);
                    if (!noWindow) OpenWindow(url);
                    return 0;
                }
            }
            catch { }
            Thread.Sleep(500);
        }
        return Fail("the running logger did not answer", null);
    }

    public static string EdgePath()
    {
        foreach (Microsoft.Win32.RegistryKey root in new Microsoft.Win32.RegistryKey[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
                {
                    if (k == null) continue;
                    string p = k.GetValue("") as string;
                    if (!string.IsNullOrEmpty(p) && File.Exists(p.Trim('"'))) return p.Trim('"');
                }
            }
            catch { }
        }
        return null;
    }

    // Edge in its own app window with a separate profile (I3); else the default browser.
    static void OpenWindow(string url)
    {
        string edge = EdgePath();
        if (edge != null)
        {
            string prof = Path.Combine(Engine.BaseDir, "edge");
            ProcessStartInfo p = new ProcessStartInfo(edge, "--app=" + url + " --user-data-dir=\"" + prof + "\" --no-first-run --no-default-browser-check --start-maximized --autoplay-policy=no-user-gesture-required");
            p.UseShellExecute = false;
            Process.Start(p);
            return;
        }
        ProcessStartInfo d = new ProcessStartInfo(url); d.UseShellExecute = true; Process.Start(d);
    }

    internal static void LogError(string what, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Engine.BaseDir);
            File.AppendAllText(Path.Combine(Engine.BaseDir, "error.txt"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + what + "\r\n  Windows: " + Battery.WindowsVersion() +
                "\r\n  Logger: " + Assembly.GetExecutingAssembly().GetName().Version + "\r\n" + (ex != null ? "  " + ex + "\r\n" : ""), Encoding.UTF8);
        }
        catch { }
    }

    static int Fail(string reason, Exception ex) { return Fail(reason, ex, null); }

    static int Fail(string reason, Exception ex, string fullText)
    {
        string msg = fullText ?? ("Battery Logger can't start: " + reason + ".");
        LogError(msg, ex);
        if (!quiet) { try { MessageBox.Show(msg, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { } }
        return 1;
    }
}
