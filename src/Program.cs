// Battery-Logger/v14/src/Program.cs
// Laptap Battery Logger v14: one exe. Self-check → single instance → local server → Edge app window →
// recovery → static read → main loop (1 s tick + commands). The main thread owns keep-awake and the session.
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

[assembly: AssemblyTitle("Laptap Battery Logger")]
[assembly: AssemblyProduct("Laptap Battery Logger")]
[assembly: AssemblyCompany("Laptap Enterprises, Inc.")]
[assembly: AssemblyDescription("Battery run-down test logger")]
[assembly: AssemblyVersion("14.0.0.0")]
[assembly: AssemblyFileVersion("14.0.0.0")]

static class Program
{
    const string Title = "Battery Logger";
    static Mutex mutex;
    static Server server;
    static bool quiet;   // --qa-no-window: never put a box on screen

    [STAThread]
    static int Main(string[] args)
    {
        int qaAlarm = 0; bool qaNoWindow = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--qa-alarm-pct" && i + 1 < args.Length) { int v; if (int.TryParse(args[i + 1], out v) && v >= 1 && v <= 95) qaAlarm = v; i++; }
            else if (args[i] == "--qa-no-window") { qaNoWindow = true; quiet = true; }
            else if (args[i] == "--qa-out" && i + 1 < args.Length) { if (Directory.Exists(args[i + 1])) Report.QaOutDir = args[i + 1]; i++; }
        }
        try { Directory.CreateDirectory(Engine.BaseDir); } catch { }

        bool owned;
        try
        {
            mutex = new Mutex(false, @"Local\LaptapBatteryLogger");
            try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        }
        catch (Exception ex) { return Fail("could not check for a running logger (" + ex.Message + ")", ex); }

        if (!owned) return Reconnect(qaNoWindow);
        try { File.Delete(Path.Combine(Engine.BaseDir, "helper.json")); } catch { }   // M10: never hand out a stale token

        try
        {
            // ---- self-check (I5) ----
            bool ac, saver, present, crit; double? pct;
            if (!Battery.PowerStatus(out ac, out pct, out saver, out present, out crit)) return Fail("Windows did not report the power status", null);
            if (!present) return Fail(null, null, "No battery found on this computer.");
            string desk = Report.Desktop();
            if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk)) return Fail("the Desktop folder was not found", null);

            Engine en = new Engine();
            en.QaAlarmPct = qaAlarm; en.QaNoWindow = qaNoWindow;
            if (qaAlarm > 0) en.AlarmPct = qaAlarm;
            string index = Resource("index.html"), logic = Resource("logic.js");   // into memory once (I1)
            if (index == null || logic == null) return Fail("the page is missing from the exe", null);
            en.IndexHtml = index;

            server = new Server();
            server.IndexHtml = Encoding.UTF8.GetBytes(index);
            server.LogicJs = Encoding.UTF8.GetBytes(logic);
            server.GetSnapshot = delegate { return en.Snapshot; };
            server.OnCrash = delegate(Exception ex) { Fail("the local server stopped (" + ex.Message + ")", ex); Environment.Exit(1); };
            if (!server.Start()) return Fail("no free local port between " + Server.FirstPort + " and " + Server.LastPort, null);
            WriteHelperJson(server.Port, server.Token);

            string url = "http://localhost:" + server.Port.ToString(CultureInfo.InvariantCulture) + "/#t=" + server.Token;
            en.OpenWindow = delegate { OpenWindow(url); };
            en.OpenFile = delegate(string f) { ProcessStartInfo p = new ProcessStartInfo(f); p.UseShellExecute = true; Process.Start(p); };
            en.Publish();
            if (!qaNoWindow) { try { OpenWindow(url); } catch (Exception ex) { LogError("could not open the window", ex); } }   // M11

            en.Recover();
            if (en.S == null || en.S.Status == "done") { StaticInfo si = Battery.ReadStatic(true); if (en.S == null) en.Info = si; else if (en.S.StartInfo == null) en.Info = si; }
            en.Tick();
            en.Publish();

            // ---- main loop (M3) ----
            Stopwatch tick = Stopwatch.StartNew();
            while (true)
            {
                Command c;
                if (server.Commands.TryTake(out c, 1000))
                {
                    try { c.Result = en.Command(c.Name, c.Body); }
                    catch (Exception ex) { c.Result = new Dictionary<string, object>(); c.Result["ok"] = false; c.Result["error"] = ex.Message; LogError("command " + c.Name, ex); }
                    c.Done.Set();
                }
                if (tick.ElapsedMilliseconds >= 950) { tick.Restart(); try { en.Tick(); } catch (Exception ex) { LogError("tick", ex); } }   // M1
                if (en.ShouldExit()) break;
            }
            server.Stop();
            try { File.Delete(Path.Combine(Engine.BaseDir, "helper.json")); } catch { }
            try { mutex.ReleaseMutex(); } catch { }
            return 0;
        }
        catch (Exception ex) { return Fail("unexpected error (" + ex.Message + ")", ex); }
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

    static void WriteHelperJson(int port, string token)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["port"] = port; d["token"] = token; d["pid"] = Process.GetCurrentProcess().Id;
        File.WriteAllText(Path.Combine(Engine.BaseDir, "helper.json"), new JavaScriptSerializer().Serialize(d), new UTF8Encoding(false));
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
