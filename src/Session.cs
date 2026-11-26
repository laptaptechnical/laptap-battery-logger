// Battery-Logger/v14/src/Session.cs
// A test's life (plan §3.4): start, counted segments, charger pauses, sleep gaps, alarm, end, recovery.
// Everything here runs on the MAIN thread only (it owns keep-awake and is the only writer of the session).
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Media;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;

public class GapInfo { public long FromMs, ToMs; public string Reason; }
public class PauseInfo { public long FromMs; public long? ToMs; }

public class EventRow
{
    public long Ms, Rev;
    public string Type, Label, Kind = "ev";   // kind: ev | gap | alarm (table style in the report)
    public double? Pct, Mwh, RateMw;
    public double ElapsedS;
    public string Note = "";
    public long? FromMs, ToMs;
}

public class SessionData
{
    public string Id, Status = "waiting", Version = "14.0.0";
    public long? WaitingSinceMs;
    public int AlarmPct = 15;
    public long StartedMs;
    public long? UnplugMs, EndMs, LastSampleMs;
    public double? StartPct, StartMwh, EndPct, EndMwh, LastPct, LastMwh;
    public string EndKind, EndText;
    public bool Alarm, AlarmAck;
    public List<Seg> Segments = new List<Seg>();
    public Seg OpenSeg;
    public List<GapInfo> Gaps = new List<GapInfo>();
    public List<PauseInfo> Pauses = new List<PauseInfo>();
    public List<EventRow> Events = new List<EventRow>();
    public StaticInfo StartInfo;
    public Dictionary<string, double?> EndFull;
    public double? BrightnessStart, BrightnessEnd;
    public bool SaverAtStart;
    public double? SaverOnPct;
    public long? SaverOnMs;
    public string ExecStart = "n/a", ExecEnd = "n/a";
    public string ReportPath, CsvPath;
    public double? MinW, MaxW;

    public double CountedNow(long now)
    {
        double s = Rules.CountedTime(Segments);
        if (OpenSeg != null) s += (now - OpenSeg.StartMs) / 1000.0;
        return s;
    }
}

// ---------- Windows System log: why was there a gap / why did the logger stop ----------
public class PowerEvent
{
    public string Provider; public int Id; public long Ms; public string Message = "";
    public Dictionary<string, string> Data = new Dictionary<string, string>();
}

public static class PowerEvents
{
    // [A] Not proven on real hardware: the critical-battery wording in Kernel-Power 42's rendered "Sleep Reason".
    static readonly Regex CriticalReason = new Regex(@"Sleep Reason:[^\r\n]*(Battery Critical|Critical Battery|Low Battery|Battery Low)", RegexOptions.IgnoreCase);

    static string D(PowerEvent e, string k) { string v; return e.Data.TryGetValue(k, out v) ? v : ""; }

    // "slept" | "hibernated" | "unknown"
    public static string ClassifyGap(IList<PowerEvent> evs)
    {
        foreach (PowerEvent e in evs)
        {
            if (e.Provider == "Microsoft-Windows-Kernel-Power" && e.Id == 42)
            {
                string ts = D(e, "TargetState");
                if (ts == "4") return "slept";
                if (ts == "5") return "hibernated";
            }
        }
        foreach (PowerEvent e in evs)
        {
            if (e.Provider == "Microsoft-Windows-Kernel-Power" && (e.Id == 506 || e.Id == 507)) return "slept";
            if (e.Provider == "Microsoft-Windows-Kernel-Power" && e.Id == 107)
            {
                string ts = D(e, "TargetState");
                if (ts == "5") return "hibernated";
                if (ts == "4") return "slept";
            }
            if (e.Provider == "Microsoft-Windows-Power-Troubleshooter" && e.Id == 1) return "slept";
        }
        return "unknown";
    }

    public static bool IsCritical(IList<PowerEvent> evs)
    {
        foreach (PowerEvent e in evs)
            if (e.Provider == "Microsoft-Windows-Kernel-Power" && e.Id == 42 && CriticalReason.IsMatch(e.Message ?? "")) return true;
        return false;
    }

    // Recovery (M6): "shutdown" (41/6008) | "shutdown_user" (1074) | "hibernated" (critical) | "logger_stopped"
    public static string ClassifyRecovery(IList<PowerEvent> evs)
    {
        foreach (PowerEvent e in evs)
            if ((e.Provider == "Microsoft-Windows-Kernel-Power" && e.Id == 41) || (e.Provider == "EventLog" && e.Id == 6008)) return "shutdown";
        foreach (PowerEvent e in evs) if (e.Provider == "User32" && e.Id == 1074) return "shutdown_user";
        if (IsCritical(evs)) return "hibernated";
        return "logger_stopped";
    }

    public static PowerEvent FromXml(string xml, string message)
    {
        PowerEvent e = new PowerEvent();
        XmlDocument d = new XmlDocument(); d.XmlResolver = null; d.LoadXml(xml);
        XmlNamespaceManager ns = new XmlNamespaceManager(d.NameTable);
        ns.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");
        XmlNode p = d.SelectSingleNode("//e:System/e:Provider", ns);
        if (p != null && p.Attributes["Name"] != null) e.Provider = p.Attributes["Name"].Value;
        XmlNode id = d.SelectSingleNode("//e:System/e:EventID", ns);
        if (id != null) int.TryParse(id.InnerText, out e.Id);
        XmlNode t = d.SelectSingleNode("//e:System/e:TimeCreated", ns);
        DateTime dt;
        if (t != null && t.Attributes["SystemTime"] != null && DateTime.TryParse(t.Attributes["SystemTime"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out dt))
            e.Ms = Rules.ToMs(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        foreach (XmlNode n in d.SelectNodes("//e:EventData/e:Data", ns))
            if (n.Attributes["Name"] != null) e.Data[n.Attributes["Name"].Value] = n.InnerText;
        e.Message = message ?? "";
        return e;
    }

    public static List<PowerEvent> Read(long fromMs, long toMs)
    {
        List<PowerEvent> list = new List<PowerEvent>();
        string f = Rules.ToLocal(fromMs).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        string t = Rules.ToLocal(toMs).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        string q = "*[System[TimeCreated[@SystemTime>='" + f + "' and @SystemTime<='" + t + "'] and (" +
            "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41 or EventID=42 or EventID=107 or EventID=506 or EventID=507)) or " +
            "(Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1) or " +
            "(Provider[@Name='EventLog'] and EventID=6008) or (Provider[@Name='User32'] and EventID=1074))]]";
        try
        {
            using (EventLogReader r = new EventLogReader(new EventLogQuery("System", PathType.LogName, q)))
            {
                for (EventRecord rec = r.ReadEvent(); rec != null; rec = r.ReadEvent())
                {
                    using (rec)
                    {
                        string msg = "";
                        try { msg = rec.FormatDescription() ?? ""; } catch { }
                        try { list.Add(FromXml(rec.ToXml(), msg)); } catch { }
                    }
                }
            }
        }
        catch { }
        return list;
    }
}

// ---------- the engine ----------
public class Engine
{
    public static string BaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaptapBatteryLogger");
    public string SessionsDir { get { return Path.Combine(BaseDir, "sessions"); } }

    public SessionData S;
    public List<Sample> Samples = new List<Sample>();
    public StaticInfo Info = new StaticInfo();
    public Sample Latest;
    public int AlarmPct = 15;
    public int QaAlarmPct = 0;
    public bool QaNoWindow;
    public Action OpenWindow = delegate { };
    public Action<string> OpenFile = delegate { };
    public string IndexHtml = "";

    long rev;
    bool? lastAc;
    bool lastSaver;
    long lastFullMs, lastTickMs;
    public long LastHeartbeatMs, LaunchMs;
    bool keepAwake, reopened;
    long lastBeepMs;
    bool curAc, curCharging; double? curPct;
    FileStream csv;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly List<Dictionary<string, object>> sampleDicts = new List<Dictionary<string, object>>();
    readonly List<Dictionary<string, object>> eventDicts = new List<Dictionary<string, object>>();
    public volatile Dictionary<string, object> Snapshot = new Dictionary<string, object>();

    public Engine() { json.MaxJsonLength = int.MaxValue; LaunchMs = Rules.NowMs(); LastHeartbeatMs = LaunchMs; }

    bool Active { get { return S != null && S.Status != "done"; } }

    // ---------- 1 s tick (plan §3.3) ----------
    public void Tick()
    {
        long now = Rules.NowMs();
        bool ac, saver, present, crit; double? pct;
        Battery.PowerStatus(out ac, out pct, out saver, out present, out crit);
        bool changed = false;

        if (Active && lastTickMs > 0)
        {
            if (S.Status == "running" && Rules.DetectGap(lastFullMs, now, Rules.IntervalFor(S.LastPct, S.AlarmPct, true)))
            { HandleGap(lastTickMs, now, ac); changed = true; }
            else if (S.Status != "running" && now - lastTickMs > 120000)
            { string why = GapReason(lastTickMs, now); AddEvent("gap", GapLabel(lastTickMs, now, why), "gap", null, why, lastTickMs, now); changed = true; }
        }
        lastTickMs = now;
        curPct = pct;

        if (lastAc.HasValue && ac != lastAc.Value) { lastAc = ac; curAc = ac; OnAcChange(ac); changed = true; }
        lastAc = ac; curAc = ac;

        if (Active && saver != lastSaver)
        {
            Sample s = FullSample(null);
            if (saver && !S.SaverOnPct.HasValue) { S.SaverOnPct = s.Pct; S.SaverOnMs = s.Ms; }
            AddEvent(saver ? "saver_on" : "saver_off", saver ? "Battery saver on" : "Battery saver off", "ev", s);
            changed = true;
        }
        lastSaver = saver;

        if (Active)
        {
            int iv = Rules.IntervalFor(pct, S.AlarmPct, S.Status == "running");
            if (now - lastFullMs >= iv * 1000L) { FullSample(null); changed = true; }
        }
        else if (now - LastHeartbeatMs < 120000 && now - lastFullMs >= 60000) { DisplaySample(); changed = true; }

        AlarmFallback(now);
        if (changed) Publish();
    }

    public bool ShouldExit()
    {
        long now = Rules.NowMs();
        if (Active) return false;
        if (S != null && S.Alarm && !S.AlarmAck && S.EndMs.HasValue && now - S.EndMs.Value < 31 * 60000L) return false;
        return now - Math.Max(LastHeartbeatMs, LaunchMs) > 180000;
    }

    void AlarmFallback(long now)
    {
        if (S == null || !S.Alarm || S.AlarmAck || !S.EndMs.HasValue) return;
        long since = now - S.EndMs.Value;
        if (since < 60000 || since > 30 * 60000L) return;
        if (!reopened) { reopened = true; if (!QaNoWindow) { try { OpenWindow(); } catch { } } }
        if (now - lastBeepMs >= 5000) { lastBeepMs = now; try { SystemSounds.Exclamation.Play(); } catch { } }
    }

    // ---------- samples ----------
    double? FullMwh()
    {
        if (S != null && S.StartInfo != null) { double? f = S.StartInfo.TotalFull(); if (f.HasValue) return f; }
        return Info.TotalFull();
    }

    void DisplaySample()
    {
        Latest = Battery.ReadLive(FullMwh());
        lastFullMs = Latest.Ms; curCharging = Latest.Charging;
    }

    Sample FullSample(string ev)
    {
        Sample s = Battery.ReadLive(FullMwh());
        lastFullMs = s.Ms; curCharging = s.Charging;
        if (!s.Ac && !s.RateMw.HasValue && S != null && S.OpenSeg != null && s.Mwh.HasValue)
        {
            // Rate missing while discharging: ΔmWh/Δt over the last ≥ 5 min of this segment.
            for (int i = 0; i < Samples.Count; i++)
            {
                Sample o = Samples[i];
                if (o.Ms >= S.OpenSeg.StartMs && o.Mwh.HasValue && s.Ms - o.Ms >= 300000)
                {
                    double mw = (o.Mwh.Value - s.Mwh.Value) / ((s.Ms - o.Ms) / 3600000.0);
                    if (mw >= 0 && mw <= 300000) { s.RateMw = Math.Round(mw); s.RateSrc = "calc"; }
                }
                else if (o.Ms >= S.OpenSeg.StartMs) break;
            }
        }
        Latest = s;
        if (S == null) return s;
        s.ElapsedS = Math.Round(S.CountedNow(s.Ms));
        s.Event = ev ?? "reading";
        s.Rev = ++rev;
        Samples.Add(s);
        sampleDicts.Add(SampleDict(s));
        S.LastSampleMs = s.Ms; S.LastPct = s.Pct; S.LastMwh = s.Mwh;
        if (S.OpenSeg != null && !s.Ac && s.RateMw.HasValue)
        {
            double w = s.RateMw.Value / 1000.0;
            if (!S.MinW.HasValue || w < S.MinW.Value) S.MinW = w;
            if (!S.MaxW.HasValue || w > S.MaxW.Value) S.MaxW = w;
        }
        if (ev == null || ev == "gap_end") AppendCsv(s);   // event readings are written by AddEvent
        if (S.Status == "running" && !s.Ac && s.Pct.HasValue && s.Pct.Value <= S.AlarmPct) End("alarm", true);
        return s;
    }

    // api-contract.md: samples[] row
    public static Dictionary<string, object> SampleDict(Sample s)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["rev"] = s.Rev; d["ms"] = s.Ms; d["elapsedS"] = s.ElapsedS; d["pct"] = s.Pct; d["mwh"] = s.Mwh; d["fullMwh"] = s.FullMwh;
        d["rateMw"] = s.RateMw; d["rateSrc"] = string.IsNullOrEmpty(s.RateSrc) ? null : s.RateSrc;
        d["voltageMv"] = s.Mv.HasValue ? (object)Math.Round(s.Mv.Value) : null; d["ac"] = s.Ac; d["saver"] = s.Saver;
        return d;
    }

    public string CsvHeader() { return "time,elapsed_s,event,windows_pct,remaining_mwh,full_mwh,rate_mw,rate_src,voltage_mv,ac,saver,b0_mwh,b1_mwh,note"; }
    public static string CsvLine(Sample s)
    {
        return Rules.CsvRow(new object[] { Rules.IsoLocal(s.Ms), Math.Round(s.ElapsedS), s.Event, s.Pct, s.Mwh, s.FullMwh, s.RateMw, s.RateSrc,
            s.Mv.HasValue ? (object)Math.Round(s.Mv.Value) : null, s.Ac ? 1 : 0, s.Saver ? 1 : 0,
            s.B.Length > 0 ? s.B[0] : null, s.B.Length > 1 ? s.B[1] : null, s.Note });
    }

    void AppendCsv(Sample s)
    {
        try
        {
            if (csv == null) return;
            byte[] b = Encoding.UTF8.GetBytes(CsvLine(s) + "\r\n");
            csv.Write(b, 0, b.Length);
            csv.Flush(true);   // write-through: a dying battery keeps the last line
        }
        catch { }
    }

    void OpenCsv(string dir, bool header)
    {
        string f = Path.Combine(dir, "samples.csv");
        csv = new FileStream(f, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        if (header)
        {
            byte[] b = Encoding.UTF8.GetBytes(CsvHeader() + "\r\n");
            csv.Write(b, 0, b.Length); csv.Flush(true);
        }
    }

    // ---------- events ----------
    void AddEvent(string type, string label, string kind, Sample s) { AddEvent(type, label, kind, s, "", null, null); }

    void AddEvent(string type, string label, string kind, Sample s, string note, long? fromMs, long? toMs)
    {
        if (S == null) return;
        long now = s != null ? s.Ms : Rules.NowMs();
        EventRow e = new EventRow();
        e.Ms = now; e.Type = type; e.Label = label; e.Kind = kind; e.Rev = ++rev; e.Note = note ?? ""; e.FromMs = fromMs; e.ToMs = toMs;
        e.ElapsedS = Math.Round(S.CountedNow(now));
        if (s != null) { e.Pct = s.Pct; e.Mwh = s.Mwh; e.RateMw = s.RateMw; }
        S.Events.Add(e);
        eventDicts.Add(EventDict(e));
        if (s != null)
        {
            // The event row also goes into the CSV (same reading, its own event name).
            Sample c = new Sample();
            c.Ms = s.Ms; c.ElapsedS = e.ElapsedS; c.Event = type; c.Pct = s.Pct; c.Mwh = s.Mwh; c.FullMwh = s.FullMwh; c.RateMw = s.RateMw;
            c.RateSrc = s.RateSrc; c.Mv = s.Mv; c.Ac = s.Ac; c.Saver = s.Saver; c.B = s.B; c.Note = label;
            AppendCsv(c);
        }
        else
        {
            Sample c = new Sample(); c.Ms = now; c.ElapsedS = e.ElapsedS; c.Event = type; c.Note = label; c.RateSrc = "";
            AppendCsv(c);
        }
        SaveSession();
    }

    // api-contract.md: events[] row
    public static Dictionary<string, object> EventDict(EventRow e)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["rev"] = e.Rev; d["ms"] = e.Ms; d["elapsedS"] = e.ElapsedS; d["type"] = e.Type;
        d["pct"] = e.Pct; d["mwh"] = e.Mwh; d["rateMw"] = e.RateMw; d["note"] = e.Note ?? ""; d["fromMs"] = e.FromMs; d["toMs"] = e.ToMs;
        return d;
    }

    public void SaveSession()
    {
        if (S == null) return;
        try
        {
            string dir = Path.Combine(SessionsDir, S.Id);
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "session.json"), tmp = f + ".tmp";
            File.WriteAllText(tmp, json.Serialize(S), new UTF8Encoding(false));
            if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f);
        }
        catch { }
    }

    // ---------- segments ----------
    void OpenSegment(Sample s)
    {
        S.OpenSeg = new Seg(); S.OpenSeg.StartMs = s.Ms; S.OpenSeg.StartPct = s.Pct; S.OpenSeg.StartMwh = s.Mwh;
    }
    void CloseSegment(long ms, double? pct, double? mwh)
    {
        if (S.OpenSeg == null) return;
        S.OpenSeg.EndMs = ms; S.OpenSeg.EndPct = pct; S.OpenSeg.EndMwh = mwh;
        if (S.OpenSeg.EndMs < S.OpenSeg.StartMs) S.OpenSeg.EndMs = S.OpenSeg.StartMs;
        S.Segments.Add(S.OpenSeg); S.OpenSeg = null;
    }

    void OnAcChange(bool ac)
    {
        if (!Active) { if (S == null || S.Status == "done") DisplaySample(); return; }
        if (!ac)
        {
            if (S.Status == "waiting")
            {
                Sample s = FullSample("unplugged");
                S.UnplugMs = s.Ms; S.StartPct = s.Pct; S.StartMwh = s.Mwh; S.Status = "running"; S.WaitingSinceMs = null;
                OpenSegment(s);
                AddEvent("unplugged", "Unplugged", "ev", s);
            }
            else if (S.Status == "paused")
            {
                Sample s = FullSample("charger_out");
                if (S.Pauses.Count > 0 && !S.Pauses[S.Pauses.Count - 1].ToMs.HasValue) S.Pauses[S.Pauses.Count - 1].ToMs = s.Ms;
                S.Status = "running";
                OpenSegment(s);
                AddEvent("charger_out", "Unplugged · resumed", "ev", s);
            }
        }
        else if (S.Status == "running")
        {
            Sample s = FullSample("charger_in");
            if (S.Status != "running") return;   // the sample itself may have ended the test
            CloseSegment(s.Ms, s.Pct, s.Mwh);
            PauseInfo p = new PauseInfo(); p.FromMs = s.Ms; S.Pauses.Add(p);
            S.Status = "paused";
            AddEvent("charger_in", "Charger plugged in · paused", "ev", s);
        }
    }

    string GapReason(long from, long to) { return PowerEvents.ClassifyGap(PowerEvents.Read(from - 60000, to + 60000)); }
    static string GapLabel(long from, long to, string why)
    {
        return "Gap " + Rules.FmtHm((to - from) / 1000.0) + " · " + (why == "unknown" ? "unknown" : "laptop " + why);
    }

    void HandleGap(long fromMs, long toMs, bool acNow)
    {
        long segEnd = S.LastSampleMs.HasValue ? S.LastSampleMs.Value : fromMs;
        CloseSegment(segEnd, S.LastPct, S.LastMwh);
        List<PowerEvent> evs = PowerEvents.Read(fromMs - 60000, toMs + 60000);
        string reason = PowerEvents.ClassifyGap(evs);
        GapInfo g = new GapInfo(); g.FromMs = fromMs; g.ToMs = toMs; g.Reason = reason; S.Gaps.Add(g);
        AddEvent("gap", GapLabel(fromMs, toMs, reason), "gap", null, reason, fromMs, toMs);
        int crit = S.StartInfo != null ? S.StartInfo.CriticalPct : 5;
        // C3: a worn battery that hit Windows' critical level hibernated; never sit paused waiting for an unplug.
        if (acNow || PowerEvents.IsCritical(evs) || (S.LastPct.HasValue && S.LastPct.Value <= crit))
        {
            End("hibernated", false);
            return;
        }
        Sample s = FullSample("gap_end");
        if (S.Status == "running") OpenSegment(s);
    }

    // ---------- commands (from the HTTP thread, run here on the main thread) ----------
    public Dictionary<string, object> Command(string name, Dictionary<string, object> body)
    {
        Dictionary<string, object> r = new Dictionary<string, object>();
        r["ok"] = true;
        switch (name)
        {
            case "heartbeat": LastHeartbeatMs = Rules.NowMs(); break;
            case "start": { string err = Start(); if (err != null) { r["ok"] = false; r["error"] = err; } break; }
            case "stop": if (Active) End("stop", true); else { r["ok"] = false; r["error"] = "No test is running."; } break;
            case "ack-alarm": AckAlarm(); break;
            case "alarm-pct":
                {
                    int p = 0; object v;
                    if (body != null && body.TryGetValue("pct", out v) && v != null) int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out p);
                    if ((p >= 10 && p <= 50) || (QaAlarmPct > 0 && p == QaAlarmPct))
                    {
                        AlarmPct = p;
                        if (Active) { S.AlarmPct = p; SaveSession(); }
                    }
                    else { r["ok"] = false; r["error"] = "Alarm must be 10 to 50%."; }
                    break;
                }
            case "save-csv":
                {
                    if (S == null) { r["ok"] = false; r["error"] = "No test yet."; break; }
                    string path = Report.SaveCsv(this, false);
                    if (path == null) { r["ok"] = false; r["error"] = "Could not save to the Desktop."; Program.LogError("CSV not saved to the Desktop", Report.LastError); } else r["file"] = path;
                    break;
                }
            default: r["ok"] = false; r["error"] = "Unknown command."; break;
        }
        Publish();
        return r;
    }

    public void AckAlarm()
    {
        if (S == null) return;
        bool was = S.AlarmAck;
        S.AlarmAck = true;
        SaveSession();
        if (!was && S.Alarm && S.Status == "done") Report.Resave(this);   // I1
    }

    string Start()
    {
        if (Active) return "A test is already running.";
        bool ac, saver, present, crit; double? pct;
        Battery.PowerStatus(out ac, out pct, out saver, out present, out crit);
        if (!present) return "No battery found on this computer.";
        if (!Rules.CanStart(pct)) return "Charge to at least 96% first.";

        StaticInfo si = Battery.ReadStatic(true);
        Info = si;
        if (csv != null) { try { csv.Dispose(); } catch { } csv = null; }
        S = new SessionData();
        S.Id = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        S.StartedMs = Rules.NowMs(); S.AlarmPct = AlarmPct; S.StartInfo = si;
        S.BrightnessStart = si.Brightness; S.SaverAtStart = si.Saver;
        if (si.Saver) S.SaverOnPct = pct;
        Samples.Clear(); sampleDicts.Clear(); eventDicts.Clear(); reopened = false;
        string dir = Path.Combine(SessionsDir, S.Id);
        Directory.CreateDirectory(dir);
        OpenCsv(dir, true);
        SaveSession();

        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);
        keepAwake = true;
        S.ExecStart = Native.ExecutionStateText(Native.ExecutionState());

        Sample s = FullSample("start");
        AddEvent("start", "Test started", "ev", s);
        if (ac) { S.Status = "waiting"; S.WaitingSinceMs = s.Ms; AddEvent("waiting_unplug", "Waiting for unplug", "ev", s); }
        else
        {
            S.Status = "running"; S.UnplugMs = s.Ms; S.StartPct = s.Pct; S.StartMwh = s.Mwh;
            OpenSegment(s);
            SaveSession();
        }
        lastSaver = saver;
        return null;
    }

    public void End(string kind, bool live)
    {
        if (S == null || S.Status == "done") return;
        long now = live ? Rules.NowMs() : (S.LastSampleMs ?? Rules.NowMs());
        if (S.OpenSeg != null)
        {
            if (live && Latest != null && Latest.Ms >= S.OpenSeg.StartMs) CloseSegment(Latest.Ms, Latest.Pct, Latest.Mwh);
            else CloseSegment(S.LastSampleMs ?? now, S.LastPct, S.LastMwh);
        }
        if (S.Pauses.Count > 0 && !S.Pauses[S.Pauses.Count - 1].ToMs.HasValue) S.Pauses[S.Pauses.Count - 1].ToMs = now;
        S.EndMs = now;
        S.EndPct = live && Latest != null ? Latest.Pct : S.LastPct;
        S.EndMwh = live && Latest != null ? Latest.Mwh : S.LastMwh;
        if (S.StartPct == null) { S.StartPct = S.EndPct; }
        S.EndKind = kind;
        S.EndText = Rules.EndReason(kind, S.EndPct, Rules.CountedTime(S.Segments));
        S.Alarm = kind == "alarm";
        S.Status = "done";
        if (kind == "alarm") AddEvent("alarm", "Alarm at " + Rules.Num(S.EndPct) + "%", "alarm", Latest);
        else if (kind == "stop") AddEvent("stop", "Stopped by tech", "ev", Latest);
        else if (live) AddEvent(kind, S.EndText, "ev", null);                 // "hibernated" after a gap (C3)
        else AddEvent("recovered", S.EndText, "ev", null, kind, null, null); // an old test finished at start-up (§3.7)

        if (live && keepAwake)
        {
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);   // same (main) thread that set it
            keepAwake = false;
            S.ExecEnd = Native.ExecutionStateText(Native.ExecutionState());
        }
        if (live)
        {
            S.EndFull = Battery.ReadFullByInstance();
            S.BrightnessEnd = Battery.ReadBrightness();
        }
        string[] paths = Report.SaveAll(this);
        if (paths != null)
        {
            S.ReportPath = paths[0]; S.CsvPath = paths[1];
            AddEvent("report_saved", "Report saved", "ev", null);
            if (!QaNoWindow) { try { OpenFile(paths[0]); } catch { } }
        }
        else
        {
            // I3: say it plainly; the data is still in the session folder.
            AddEvent("report_failed", "Report not saved to the Desktop", "alarm", null, Report.LastError != null ? Report.LastError.Message : "", null, null);
            Program.LogError("report not saved to the Desktop", Report.LastError);
        }
        SaveSession();
        if (csv != null) { try { csv.Dispose(); } catch { } csv = null; }
        Publish();
    }

    // ---------- recovery (§3.7) ----------
    public void Recover()
    {
        try
        {
            if (!Directory.Exists(SessionsDir)) return;
            foreach (string dir in Directory.GetDirectories(SessionsDir))
            {
                string f = Path.Combine(dir, "session.json");
                if (!File.Exists(f)) continue;
                SessionData d;
                try { d = json.Deserialize<SessionData>(File.ReadAllText(f)); } catch { continue; }
                if (d == null || d.Status == "done") continue;
                S = d;
                Samples = ReadSamplesCsv(Path.Combine(dir, "samples.csv"));
                sampleDicts.Clear();
                foreach (Sample s in Samples) { s.Rev = ++rev; sampleDicts.Add(SampleDict(s)); }
                eventDicts.Clear();
                foreach (EventRow e in S.Events) { e.Rev = ++rev; eventDicts.Add(EventDict(e)); }
                if (Samples.Count > 0)
                {
                    Sample last = Samples[Samples.Count - 1];
                    if (!S.LastSampleMs.HasValue || last.Ms > S.LastSampleMs.Value) { S.LastSampleMs = last.Ms; S.LastPct = last.Pct; S.LastMwh = last.Mwh; }
                }
                long from = S.LastSampleMs ?? S.StartedMs;
                string kind = PowerEvents.ClassifyRecovery(PowerEvents.Read(from - 60000, Rules.NowMs()));
                try { OpenCsv(dir, false); } catch { }
                End(kind, false);
                if (csv != null) { try { csv.Dispose(); } catch { } csv = null; }
            }
        }
        catch { }
    }

    public static List<Sample> ReadSamplesCsv(string path)
    {
        List<Sample> l = new List<Sample>();
        if (!File.Exists(path)) return l;
        string[] lines;
        try { using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (StreamReader r = new StreamReader(fs)) lines = r.ReadToEnd().Split('\n'); }
        catch { return l; }
        for (int i = 1; i < lines.Length; i++)
        {
            List<string> c = ParseCsvLine(lines[i].TrimEnd('\r'));
            if (c.Count < 11) continue;
            DateTimeOffset t;
            if (!DateTimeOffset.TryParse(c[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
            Sample s = new Sample();
            s.Ms = t.ToUnixTimeMilliseconds();
            double e; double.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out e); s.ElapsedS = e;
            s.Event = c[2]; s.Pct = Battery.NumOrNull(c[3]); s.Mwh = Battery.NumOrNull(c[4]); s.FullMwh = Battery.NumOrNull(c[5]);
            s.RateMw = Battery.NumOrNull(c[6]); s.RateSrc = c[7]; s.Mv = Battery.NumOrNull(c[8]); s.Ac = c[9] == "1"; s.Saver = c[10] == "1";
            s.Note = c.Count > 13 ? c[13] : "";
            if (s.Pct.HasValue || s.Mwh.HasValue) l.Add(s);
        }
        return l;
    }

    public static List<string> ParseCsvLine(string line)
    {
        List<string> o = new List<string>();
        StringBuilder sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (q) { if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(ch); }
            else if (ch == '"') q = true;
            else if (ch == ',') { o.Add(sb.ToString()); sb.Length = 0; }
            else sb.Append(ch);
        }
        o.Add(sb.ToString());
        return o;
    }

    // ---------- snapshot for GET /api/state (api-contract.md; immutable, swapped in for the HTTP thread) ----------
    public int AlarmPctMax { get { return Math.Max(50, QaAlarmPct); } }

    public void Publish()
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["rev"] = rev; d["version"] = "14.0.0";
        StaticInfo si = (S != null && S.StartInfo != null) ? S.StartInfo : Info;
        Dictionary<string, object> unit = new Dictionary<string, object>();
        unit["maker"] = si.Manufacturer; unit["model"] = si.Model;
        unit["serial"] = si.SerialSource == "BIOS" ? si.Serial : null; unit["computerName"] = Environment.MachineName;
        d["unit"] = unit;
        d["batteries"] = BatteryList(si, S);
        Dictionary<string, object> nw = new Dictionary<string, object>();
        nw["pct"] = curPct.HasValue ? curPct : (Latest != null ? Latest.Pct : null);
        nw["mwh"] = Latest != null ? Latest.Mwh : null; nw["fullMwh"] = FullMwh();
        nw["rateMw"] = Latest != null && !curAc ? Latest.RateMw : null;
        nw["ac"] = curAc; nw["charging"] = curCharging; nw["saver"] = lastSaver;
        d["now"] = nw;
        if (S != null)
        {
            Dictionary<string, object> ss = new Dictionary<string, object>();
            ss["id"] = S.Id; ss["status"] = S.Status; ss["startedMs"] = S.StartedMs;
            ss["waitingSinceMs"] = S.Status == "waiting" ? S.WaitingSinceMs : null;
            ss["alarmPct"] = S.AlarmPct; ss["alarmPctMax"] = AlarmPctMax; ss["alarm"] = S.Alarm; ss["alarmAck"] = S.AlarmAck;
            List<object> segs = new List<object>();
            foreach (Seg g in S.Segments) segs.Add(SegDict(g, true));
            if (S.OpenSeg != null) segs.Add(SegDict(S.OpenSeg, false));
            ss["segments"] = segs;
            d["session"] = ss;
            d["samples"] = sampleDicts.ToArray();
            d["events"] = eventDicts.ToArray();
            d["result"] = S.Status == "done" ? Report.Summary(this) : null;
        }
        else { d["session"] = null; d["samples"] = new Dictionary<string, object>[0]; d["events"] = new Dictionary<string, object>[0]; d["result"] = null; }
        Snapshot = d;
    }

    static Dictionary<string, object> SegDict(Seg g, bool closed)
    {
        Dictionary<string, object> x = new Dictionary<string, object>();
        x["startMs"] = g.StartMs; x["endMs"] = closed ? (object)g.EndMs : null;
        x["startPct"] = g.StartPct; x["endPct"] = closed ? g.EndPct : null;
        x["startMwh"] = g.StartMwh; x["endMwh"] = closed ? g.EndMwh : null;
        return x;
    }

    // batteries[]: relative-capacity batteries get no Wh; cycles null when 0 or missing; fullEndMwh = re-read at End.
    public static List<object> BatteryList(StaticInfo si, SessionData s)
    {
        List<object> bats = new List<object>();
        List<double?> ends = new List<double?>();
        if (s != null && s.EndFull != null) foreach (double? v in s.EndFull.Values) ends.Add(v);
        for (int i = 0; i < si.Batteries.Count; i++)
        {
            BatteryInfo b = si.Batteries[i];
            Dictionary<string, object> x = new Dictionary<string, object>();
            x["id"] = b.Id; x["maker"] = b.Manufacturer; x["chemistry"] = b.Chemistry;
            x["designMwh"] = b.Relative ? null : b.DesignMwh; x["fullMwh"] = b.Relative ? null : b.FullMwh;
            double? end = ends.Count == si.Batteries.Count ? ends[i] : null;
            x["fullEndMwh"] = b.Relative || !end.HasValue || end.Value <= 100 ? null : end;
            x["cycles"] = b.Cycles;
            bats.Add(x);
        }
        return bats;
    }
}
