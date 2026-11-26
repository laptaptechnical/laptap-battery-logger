// Battery-Logger/v14/src/Battery.cs
// Windows battery data without admin:
//  - static: powercfg /batteryreport /xml (design/full/cycles per battery), WMI unit + serial, brightness, plan, saver;
//  - live: GetSystemPowerStatus every second, WMI root\wmi:BatteryStatus for a full sample.
// Never root\wmi:BatteryStaticData (fails without admin) or Win32_Battery capacities (empty).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Xml;

public class BatteryInfo
{
    public string Id, Manufacturer, Chemistry;
    public double? DesignMwh, FullMwh, Cycles;
    public bool Relative;
}

public class StaticInfo
{
    public string Manufacturer = "", Model = "", Serial = "", SerialSource = "BIOS";
    public List<BatteryInfo> Batteries = new List<BatteryInfo>();
    public bool ReportOk;
    public double? Brightness;
    public string PowerPlan = "n/a", Windows = "";
    public bool Saver;
    public int CriticalPct = 5;

    public double? TotalFull()
    {
        double s = 0; if (Batteries.Count == 0) return null;
        foreach (BatteryInfo b in Batteries) { if (!b.FullMwh.HasValue || b.Relative) return null; s += b.FullMwh.Value; }
        return s;
    }
    public double? TotalDesign()
    {
        double s = 0; if (Batteries.Count == 0) return null;
        foreach (BatteryInfo b in Batteries) { if (!b.DesignMwh.HasValue || b.Relative) return null; s += b.DesignMwh.Value; }
        return s;
    }
}

public class Sample
{
    public long Ms;
    public long Rev;
    public double ElapsedS;
    public string Event = "reading";
    public double? Pct, Mwh, FullMwh, RateMw, Mv;
    public string RateSrc = "";
    public bool Ac, Charging, Saver;
    public double?[] B = new double?[0];
    public string Note = "";
}

public static class Battery
{
    // ---------- static read ----------

    // Parses powercfg's battery report XML (namespace http://schemas.microsoft.com/battery/2012).
    public static List<BatteryInfo> ParseReport(string xml)
    {
        List<BatteryInfo> list = new List<BatteryInfo>();
        XmlDocument doc = new XmlDocument();
        doc.XmlResolver = null;
        doc.LoadXml(xml);
        XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
        string uri = doc.DocumentElement != null ? doc.DocumentElement.NamespaceURI : "";
        ns.AddNamespace("b", uri);
        string pfx = uri.Length > 0 ? "b:" : "";
        XmlNodeList nodes = doc.SelectNodes("/" + pfx + "BatteryReport/" + pfx + "Batteries/" + pfx + "Battery", ns);
        foreach (XmlNode n in nodes)
        {
            BatteryInfo b = new BatteryInfo();
            b.Id = Text(n, pfx + "Id", ns);
            b.Manufacturer = Text(n, pfx + "Manufacturer", ns);
            b.Chemistry = Text(n, pfx + "Chemistry", ns);
            b.DesignMwh = NumOrNull(Text(n, pfx + "DesignCapacity", ns));
            b.FullMwh = NumOrNull(Text(n, pfx + "FullChargeCapacity", ns));
            double? cyc = NumOrNull(Text(n, pfx + "CycleCount", ns));
            b.Cycles = cyc.HasValue && cyc.Value > 0 ? cyc : null;     // 0 or missing = not reported
            b.Relative = Text(n, pfx + "RelativeCapacity", ns) == "1"
                || (b.DesignMwh.HasValue && b.DesignMwh.Value <= 100) || (b.FullMwh.HasValue && b.FullMwh.Value <= 100);
            if (b.DesignMwh.HasValue && b.DesignMwh.Value <= 0) b.DesignMwh = null;
            if (b.FullMwh.HasValue && b.FullMwh.Value <= 0) b.FullMwh = null;
            list.Add(b);
        }
        return list;
    }

    static string Text(XmlNode n, string path, XmlNamespaceManager ns)
    {
        XmlNode c = n.SelectSingleNode(path, ns);
        return c == null ? "" : c.InnerText.Trim();
    }
    public static double? NumOrNull(string s)
    {
        double d;
        if (string.IsNullOrEmpty(s) || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return null;
        return d;
    }

    // Runs a console tool with no window and returns stdout ("" on failure or timeout).
    public static string RunHidden(string exe, string args, int timeoutMs)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                System.Threading.Tasks.Task<string> read = p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ""; }
                return read.Wait(2000) ? read.Result : "";
            }
        }
        catch { return ""; }
    }

    static string Powercfg() { return Path.Combine(Environment.SystemDirectory, "powercfg.exe"); }

    public static List<BatteryInfo> ReadReport(out bool ok)
    {
        ok = false;
        string f = Path.Combine(Path.GetTempPath(), "laptap-battery-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            RunHidden(Powercfg(), "/batteryreport /xml /output \"" + f + "\" /duration 1", 15000);
            if (!File.Exists(f)) return new List<BatteryInfo>();
            List<BatteryInfo> l = ParseReport(File.ReadAllText(f));
            ok = l.Count > 0;
            return l;
        }
        catch { return new List<BatteryInfo>(); }
        finally { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    }

    static ManagementObjectCollection Query(string scope, string wql)
    {
        ManagementObjectSearcher s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
        return s.Get();
    }

    static string Str(object o) { return o == null ? "" : o.ToString().Trim(); }

    public static StaticInfo ReadStatic(bool fullReport)
    {
        StaticInfo si = new StaticInfo();
        try
        {
            using (ManagementObjectCollection c = Query(@"root\cimv2", "SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                foreach (ManagementObject o in c) using (o) { si.Manufacturer = Str(o["Manufacturer"]); si.Model = Str(o["Model"]); }
            if (si.Manufacturer.ToUpperInvariant().Contains("LENOVO"))
                using (ManagementObjectCollection c = Query(@"root\cimv2", "SELECT Version FROM Win32_ComputerSystemProduct"))
                    foreach (ManagementObject o in c) using (o) { string v = Str(o["Version"]); if (v.Length > 0 && !Rules.IsPlaceholderSerial(v)) si.Model = v; }
            using (ManagementObjectCollection c = Query(@"root\cimv2", "SELECT SerialNumber FROM Win32_BIOS"))
                foreach (ManagementObject o in c) using (o) si.Serial = Str(o["SerialNumber"]);
        }
        catch { }
        if (Rules.IsPlaceholderSerial(si.Serial)) { si.Serial = Environment.MachineName; si.SerialSource = "computer name"; }

        bool ok = false;
        if (fullReport) si.Batteries = ReadReport(out ok);
        si.ReportOk = ok;
        if (!ok)
        {
            // Fallback: full capacity from WMI, design unknown.
            try
            {
                using (ManagementObjectCollection c = Query(@"root\wmi", "SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity"))
                    foreach (ManagementObject o in c) using (o)
                    {
                        BatteryInfo b = new BatteryInfo();
                        b.Id = Str(o["InstanceName"]); b.FullMwh = NumOrNull(Str(o["FullChargedCapacity"]));
                        b.Relative = b.FullMwh.HasValue && b.FullMwh.Value <= 100;
                        si.Batteries.Add(b);
                    }
            }
            catch { }
        }
        si.Brightness = ReadBrightness();
        si.PowerPlan = ReadPlan();
        si.CriticalPct = ReadCriticalPct();
        si.Windows = WindowsVersion();
        Native.SYSTEM_POWER_STATUS ps;
        if (Native.GetSystemPowerStatus(out ps)) si.Saver = (ps.SystemStatusFlag & 1) == 1;
        return si;
    }

    // Re-read only full capacity (M7: the gauge may re-learn during a run).
    public static Dictionary<string, double?> ReadFullByInstance()
    {
        Dictionary<string, double?> d = new Dictionary<string, double?>();
        try
        {
            using (ManagementObjectCollection c = Query(@"root\wmi", "SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity"))
                foreach (ManagementObject o in c) using (o) d[Str(o["InstanceName"])] = NumOrNull(Str(o["FullChargedCapacity"]));
        }
        catch { }
        return d;
    }

    public static double? ReadBrightness()
    {
        try
        {
            using (ManagementObjectCollection c = Query(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                foreach (ManagementObject o in c) using (o) return NumOrNull(Str(o["CurrentBrightness"]));
        }
        catch { }
        return null;
    }

    static readonly Regex PlanName = new Regex(@"\(([^)]*)\)\s*\*?\s*$");
    public static string ParsePlan(string s)
    {
        Match m = PlanName.Match((s ?? "").Trim());
        return m.Success ? m.Groups[1].Value.Trim() : "n/a";
    }
    static string ReadPlan() { return ParsePlan(RunHidden(Powercfg(), "/getactivescheme", 5000)); }

    static readonly Regex DcIndex = new Regex(@"DC Power Setting Index:\s*0x([0-9a-fA-F]+)");
    public static int ParseCritical(string s)
    {
        Match m = DcIndex.Match(s ?? "");
        if (!m.Success) return 5;
        int v = Convert.ToInt32(m.Groups[1].Value, 16);
        return v >= 0 && v <= 100 ? v : 5;
    }
    static int ReadCriticalPct() { return ParseCritical(RunHidden(Powercfg(), "/q SCHEME_CURRENT SUB_BATTERY BATLEVELCRIT", 5000)); }

    public static string WindowsVersion()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (k != null)
                {
                    string build = Str(k.GetValue("CurrentBuildNumber"));
                    string disp = Str(k.GetValue("DisplayVersion"));
                    int b; int.TryParse(build, out b);
                    string name = b >= 22000 ? "Windows 11" : "Windows 10";
                    return name + (disp.Length > 0 ? " " + disp : "") + " (build " + build + ")";
                }
            }
        }
        catch { }
        return Environment.OSVersion.VersionString;
    }

    // ---------- live ----------

    public static bool PowerStatus(out bool ac, out double? pct, out bool saver, out bool present, out bool critical)
    {
        Native.SYSTEM_POWER_STATUS ps;
        ac = false; pct = null; saver = false; present = true; critical = false;
        if (!Native.GetSystemPowerStatus(out ps)) return false;
        ac = ps.ACLineStatus == 1;
        pct = ps.BatteryLifePercent == 255 ? (double?)null : ps.BatteryLifePercent;
        saver = (ps.SystemStatusFlag & 1) == 1;
        present = ps.BatteryFlag != 128;
        critical = ps.BatteryFlag != 255 && (ps.BatteryFlag & 4) != 0;
        return true;
    }

    static ManagementObjectSearcher statusSearcher;

    // One full sample: WMI BatteryStatus (all batteries) + the latest syscall values. Sanity bounds applied.
    public static Sample ReadLive(double? fullMwh)
    {
        Sample s = new Sample();
        s.Ms = Rules.NowMs();
        bool ac, saver, present, crit; double? pct;
        PowerStatus(out ac, out pct, out saver, out present, out crit);
        s.Ac = ac; s.Pct = pct; s.Saver = saver;
        List<double?> per = new List<double?>();
        double total = 0, rate = 0, mv = 0; bool anyMwh = false, anyRate = false, charging = false, discharging = false; int mvN = 0;
        try
        {
            if (statusSearcher == null)
                statusSearcher = new ManagementObjectSearcher(new ManagementScope(@"root\wmi"),
                    new ObjectQuery("SELECT InstanceName, RemainingCapacity, ChargeRate, DischargeRate, Voltage, PowerOnline, Charging, Discharging FROM BatteryStatus"));
            using (ManagementObjectCollection c = statusSearcher.Get())
            {
                foreach (ManagementObject o in c)
                {
                    using (o)
                    {
                        double? rem = NumOrNull(Str(o["RemainingCapacity"]));
                        per.Add(rem);
                        if (rem.HasValue) { total += rem.Value; anyMwh = true; }
                        bool ch = Str(o["Charging"]) == "True", dis = Str(o["Discharging"]) == "True";
                        charging |= ch; discharging |= dis;
                        double? r = NumOrNull(Str(dis ? o["DischargeRate"] : (ch ? o["ChargeRate"] : null)));
                        if (r.HasValue && r.Value > 0) { rate += r.Value; anyRate = true; }
                        double? v = NumOrNull(Str(o["Voltage"]));
                        if (v.HasValue && v.Value > 0) { mv += v.Value; mvN++; }
                    }
                }
            }
        }
        catch { }
        s.B = per.ToArray();
        s.Charging = charging;
        double? m = anyMwh ? total : (double?)null, rt = anyRate ? rate : (double?)null, p = s.Pct;
        Rules.ValidSample(ref rt, ref m, ref p, fullMwh);
        s.Mwh = m; s.RateMw = rt; s.Pct = p; s.FullMwh = fullMwh;
        s.RateSrc = rt.HasValue ? "wmi" : "";
        s.Mv = mvN > 0 ? mv / mvN : (double?)null;
        return s;
    }
}
