// src/Update.cs
// Signed self-update (plan §5). A: check + download in the background while Ready/Done, staged next to the exe.
// B: at the next start, before anything else: repair, verify the staged copy again, swap, hand off to the new exe.
// Design reused from the in-house Batch Installer v7 self-update (signed manifest, hash per file, two keys, release
// counter). Swap-by-rename idea from mitsuhiko/self-replace (Apache-2.0); no code copied. C# 5 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

public class Manifest
{
    public int Format, Release;
    public string Product, Version, Tag, Published, Asset, Sha256;
    public long Size;
    public byte[] Raw;
}

// v15.1 §2.1: one immutable snapshot of the updater's phase, published by the background thread and read by
// /api/state as app.update. idle · checking · downloading · verifying · ready · failed. Nothing else in the updater changed.
public class UpdateProgress
{
    public string Phase = "idle", Version, Note;
    public long Bytes, Total;
    public int Percent, Release;
    public long PhaseSinceMs, DownloadStartMs;

    public UpdateProgress Copy() { return (UpdateProgress)MemberwiseClone(); }

    public Dictionary<string, object> ToDict()
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["phase"] = Phase; d["version"] = Version; d["bytes"] = Phase == "downloading" ? (object)Bytes : null;
        d["total"] = Phase == "downloading" ? (object)Total : null; d["percent"] = Phase == "downloading" ? (object)Percent : null;
        d["note"] = Phase == "failed" ? Note : null;
        return d;
    }
}

public interface IChild { int Id { get; } bool HasExited { get; } void Kill(); }
public interface ILauncher { IChild Start(string exe, string args); }

public class ProcessChild : IChild
{
    readonly Process p;
    public ProcessChild(Process p) { this.p = p; }
    public int Id { get { return p.Id; } }
    public bool HasExited { get { try { return p.HasExited; } catch { return true; } } }
    public void Kill() { try { if (!p.HasExited) p.Kill(); } catch { } }
}

public class ProcessLauncher : ILauncher
{
    public IChild Start(string exe, string args)
    {
        ProcessStartInfo psi = new ProcessStartInfo(exe, args);
        psi.UseShellExecute = false;
        psi.WorkingDirectory = Path.GetDirectoryName(exe);
        Process p = Process.Start(psi);
        if (p == null) throw new FileNotFoundException("the new exe did not start", exe);
        return new ProcessChild(p);
    }
}

public static class Updater
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Regex TagRe = new Regex(@"^v\d+\.\d+\.\d+$");
    static readonly Regex VerRe = new Regex(@"^\d+\.\d+\.\d+$");
    static readonly Regex AssetRe = new Regex(@"^[A-Za-z0-9._-]+$");
    static readonly Regex HexRe = new Regex(@"^[0-9a-fA-F]{64}$");

    // The last note for the page's version tooltip (null = nothing to say).
    public static volatile string Note;
    public static volatile UpdateProgress Progress = new UpdateProgress();
    public static Action Changed = delegate { };   // the engine republishes /api/state

    // One line per phase change in update.log (§2.4); percent updates are not logged.
    public static void SetPhase(string phase, string version, string note)
    {
        UpdateProgress p = Progress.Copy();
        bool changed = p.Phase != phase;
        p.Phase = phase; p.Version = version ?? p.Version; p.Note = note;
        p.PhaseSinceMs = Rules.NowMs();
        if (phase == "downloading") { p.DownloadStartMs = p.PhaseSinceMs; p.Bytes = 0; p.Percent = 0; }
        if (phase == "idle") { p.Version = null; p.Release = 0; }
        Progress = p;
        if (changed) Log("phase: " + phase + (version != null ? " " + version : "") + (note != null ? " (" + note + ")" : ""));
        try { Changed(); } catch { }
    }
    public static void SetDownload(long bytes, long total)
    {
        UpdateProgress p = Progress.Copy();
        p.Bytes = bytes; p.Total = total; p.Percent = total > 0 ? (int)Math.Min(100, bytes * 100 / total) : 0;
        Progress = p;
        try { Changed(); } catch { }
    }
    static void Fail(string version, string note) { Note = note; SetPhase("failed", version, note); }
    public static bool TlsSet;

    // ---------- manifest ----------
    public static Manifest Parse(byte[] raw, out string err)
    {
        err = null;
        try
        {
            Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(raw));
            if (d == null) { err = "not a manifest"; return null; }
            Manifest m = new Manifest();
            m.Raw = raw;
            m.Format = ToInt(Get(d, "format")); m.Product = Get(d, "product") as string; m.Release = ToInt(Get(d, "release"));
            m.Version = Get(d, "version") as string; m.Tag = Get(d, "tag") as string; m.Published = Get(d, "published") as string;
            Dictionary<string, object> exe = Get(d, "exe") as Dictionary<string, object>;
            if (exe != null) { m.Asset = Get(exe, "asset") as string; m.Sha256 = Get(exe, "sha256") as string; m.Size = ToLong(Get(exe, "size")); }
            if (m.Product == null || m.Version == null || m.Tag == null || exe == null) { err = "not a manifest"; return null; }
            return m;
        }
        catch { err = "not a manifest"; return null; }
    }
    static object Get(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) ? v : null; }
    static int ToInt(object o) { try { return o == null ? 0 : Convert.ToInt32(o, Inv); } catch { return 0; } }
    static long ToLong(object o) { try { return o == null ? 0 : Convert.ToInt64(o, Inv); } catch { return 0; } }

    // ECDSA P-256 over SHA-256 of the exact manifest bytes; signature = base64 of the 64-byte P1363 r‖s.
    // 3.5-era APIs only (ComputeHash + VerifyHash), so it works on every Windows 10 .NET (I2).
    public static bool VerifySig(byte[] raw, string sigText, List<byte[]> keyBlobs)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String((sigText ?? "").Trim()); } catch { return false; }
        if (sig.Length != 64 || keyBlobs == null) return false;
        byte[] hash;
        using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(raw);
        foreach (byte[] blob in keyBlobs)
        {
            try
            {
                using (CngKey k = CngKey.Import(blob, CngKeyBlobFormat.EccPublicBlob))
                using (ECDsaCng e = new ECDsaCng(k))
                    if (e.VerifyHash(hash, sig)) return true;
            }
            catch { }
        }
        return false;
    }

    // §5.2 accept rules. null = acceptable.
    public static string CheckRules(Manifest m, int ownRelease)
    {
        if (m.Product != UpdateConfig.Product) return "wrong product";
        if (m.Format != 1) return "unknown format";
        if (m.Release <= ownRelease) return "not newer";
        if (m.Release > ownRelease + UpdateConfig.MaxReleaseJump) return "release jump too big";
        if (!VerRe.IsMatch(m.Version ?? "")) return "bad version";
        if (!TagRe.IsMatch(m.Tag ?? "") || m.Tag != "v" + m.Version) return "bad tag";
        if (m.Asset == null || !AssetRe.IsMatch(m.Asset)) return "bad asset name";
        if (m.Sha256 == null || !HexRe.IsMatch(m.Sha256)) return "bad hash";
        if (m.Size <= 0 || m.Size > UpdateConfig.MaxExeBytes) return "bad size";
        return null;
    }

    public static string Sha256Hex(string path)
    {
        using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (SHA256 sha = SHA256.Create())
        {
            byte[] h = sha.ComputeHash(f);
            StringBuilder sb = new StringBuilder();
            foreach (byte b in h) sb.Append(b.ToString("x2", Inv));
            return sb.ToString();
        }
    }

    public static bool FileMatches(string path, Manifest m)
    {
        try { return File.Exists(path) && new FileInfo(path).Length == m.Size && string.Equals(Sha256Hex(path), m.Sha256, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // ---------- small helpers ----------
    public static string DataDir { get { return Engine.BaseDir; } }

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            string f = Path.Combine(DataDir, "update.log");
            if (File.Exists(f) && new FileInfo(f).Length > 256 * 1024) File.Delete(f);
            File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv) + "  " + line + "\r\n", Encoding.UTF8);
        }
        catch { }
    }

    static void SetNote(string n) { Note = n; Log("note: " + n); }

    // Bad-release record: { "release": n, "untilMs": ms }
    public static bool IsBad(int release)
    {
        try
        {
            string f = Path.Combine(DataDir, "update-bad.json");
            if (!File.Exists(f)) return false;
            Dictionary<string, object> d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
            return ToInt(Get(d, "release")) == release && ToLong(Get(d, "untilMs")) > Rules.NowMs();
        }
        catch { return false; }
    }
    public static void MarkBad(int release)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["release"] = release; d["untilMs"] = Rules.NowMs() + 24L * 3600 * 1000;
            File.WriteAllText(Path.Combine(DataDir, "update-bad.json"), new JavaScriptSerializer().Serialize(d));
        }
        catch { }
    }

    // U4: a session left unfinished for more than 48 h (crashed long ago, never recovered) no longer blocks updates.
    public const long StaleSessionMs = 48L * 3600 * 1000;

    public static bool AnySessionNotDone()
    {
        try
        {
            string sd = Path.Combine(DataDir, "sessions");
            if (!Directory.Exists(sd)) return false;
            JavaScriptSerializer js = new JavaScriptSerializer(); js.MaxJsonLength = int.MaxValue;
            foreach (string dir in Directory.GetDirectories(sd))
            {
                string f = Path.Combine(dir, "session.json");
                if (!File.Exists(f)) continue;
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalMilliseconds > StaleSessionMs) { Log("ignoring a stale unfinished session: " + f); continue; }
                try
                {
                    Dictionary<string, object> d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                    if (d == null) continue;
                    string st = Get(d, "Status") as string;
                    if (st != "done") return true;
                }
                catch { return true; }   // unreadable = treat as not done (safe side: no update)
            }
        }
        catch { }
        return false;
    }

    // I1: TLS 1.2 only, set before the first request. A csc-built exe without TargetFramework runs with legacy defaults.
    public static void PrepareTls()
    {
        try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; TlsSet = true; } catch { TlsSet = false; }
    }

    // ---------- HTTP with a hard wall-clock deadline (C3: DNS / WPAD can't stall it) ----------
    public class GetResult { public byte[] Body; public int Status; public string Error; }

    public static GetResult Get(string url, long maxBytes, int deadlineMs)
    {
        GetResult r = new GetResult();
        HttpWebRequest[] holder = new HttpWebRequest[1];
        Thread t = new Thread(delegate()
        {
            try
            {
                HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
                holder[0] = rq;
                rq.AllowAutoRedirect = true;   // never pin GitHub's download host (M10)
                rq.UserAgent = "LaptapBatteryLogger/" + AppVersion.Version;
                rq.Timeout = deadlineMs; rq.ReadWriteTimeout = deadlineMs;
                using (HttpWebResponse resp = (HttpWebResponse)rq.GetResponse())
                {
                    r.Status = (int)resp.StatusCode;
                    using (Stream s = resp.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[16384]; int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                            if (ms.Length > maxBytes) { r.Error = "too big"; return; }
                        }
                        r.Body = ms.ToArray();
                    }
                }
            }
            catch (WebException we)
            {
                HttpWebResponse hr = we.Response as HttpWebResponse;
                if (hr != null) r.Status = (int)hr.StatusCode;
                r.Error = we.Status.ToString();
            }
            catch (Exception ex) { r.Error = ex.GetType().Name; }
        });
        t.IsBackground = true;
        t.Start();
        if (!t.Join(deadlineMs))
        {
            try { if (holder[0] != null) holder[0].Abort(); } catch { }
            GetResult late = new GetResult(); late.Error = "deadline";
            return late;
        }
        return r;
    }

    // Streams to <path> with write-through, a size cap and a deadline. True = got exactly the bytes.
    public static bool Download(string url, string path, long maxBytes, int deadlineMs, out string err) { return Download(url, path, maxBytes, deadlineMs, null, out err); }

    public static bool Download(string url, string path, long maxBytes, int deadlineMs, Action<long> progress, out string err)
    {
        string e2 = null; bool ok = false;
        HttpWebRequest[] holder = new HttpWebRequest[1];
        Thread t = new Thread(delegate()
        {
            try
            {
                HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
                holder[0] = rq;
                rq.AllowAutoRedirect = true; rq.UserAgent = "LaptapBatteryLogger/" + AppVersion.Version;
                rq.Timeout = deadlineMs; rq.ReadWriteTimeout = deadlineMs;
                using (HttpWebResponse resp = (HttpWebResponse)rq.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (FileStream f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                {
                    byte[] buf = new byte[65536]; int n; long total = 0;
                    Stopwatch tick = Stopwatch.StartNew();
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += n;
                        if (total > maxBytes) { e2 = "too big"; return; }
                        f.Write(buf, 0, n);
                        if (progress != null && tick.ElapsedMilliseconds >= 250) { tick.Restart(); progress(total); }
                    }
                    if (progress != null) progress(total);
                    f.Flush(true);
                    ok = true;
                }
            }
            catch (Exception ex) { e2 = ex.GetType().Name; }
        });
        t.IsBackground = true;
        t.Start();
        if (!t.Join(deadlineMs)) { try { if (holder[0] != null) holder[0].Abort(); } catch { } t.Join(2000); err = "deadline"; return false; }
        err = e2;
        return ok && e2 == null;
    }

    // ---------- A: background check + download (runs on its own thread, once per start) ----------
    public class CheckOptions
    {
        public string ExePath;
        public int OwnRelease;
        public long BuildDateUtcMs;
        public string BaseUrl;
        public bool UpdatedThisStart;
        public Func<bool> Idle = delegate { return true; };   // Ready or Done
        public Func<bool> Stopping = delegate { return false; };
    }

    // Returns a short outcome code (for tests and update.log).
    public static string CheckAndStage(CheckOptions o)
    {
        if (string.IsNullOrEmpty(o.BaseUrl)) { Log("skip: updater off"); return "off"; }
        if (!UpdateConfig.UrlAllowed(o.BaseUrl)) { Log("skip: base URL is not HTTPS"); return "not-https"; }
        if (o.UpdatedThisStart) { Log("skip: just updated"); return "just-updated"; }
        if (AnySessionNotDone()) { SetNote("Update waits: an unfinished test"); return "session"; }
        while (!o.Idle()) { if (o.Stopping()) return "stopping"; Thread.Sleep(2000); }
        if (DateTime.UtcNow < Epoch.AddMilliseconds(o.BuildDateUtcMs).Date)
        {
            SetNote("Clock is wrong (" + DateTime.UtcNow.ToString("yyyy-MM-dd", Inv) + "), update skipped");
            return "clock";
        }
        List<byte[]> keys = UpdateKeys.Blobs();
        if (keys.Count == 0) { Log("skip: no public keys in this build"); return "no-keys"; }

        string part = o.ExePath + ".part";
        if (File.Exists(part))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(part) < TimeSpan.FromMinutes(2)) { Log("skip: another copy is downloading"); return "part-fresh"; }
            try { File.Delete(part); Log("deleted an old .part"); } catch { }
        }

        PrepareTls();
        SetPhase("checking", null, null);
        string r0 = CheckAndStageNet(o, keys, part);
        UpdateProgress now = Progress;
        if (now.Phase == "checking") SetPhase("idle", null, null);   // nothing to do: straight back to idle
        return r0;
    }

    static string CheckAndStageNet(CheckOptions o, List<byte[]> keys, string part)
    {
        string latest = o.BaseUrl + "/releases/latest/download/";
        GetResult man = Get(latest + "update.json", 64 * 1024, UpdateConfig.CheckDeadlineMs);
        if (man.Status == 404) { Log("no releases yet (404)"); return "no-update"; }   // M4: quiet
        if (man.Body == null) { SetNote("Update check failed (no internet?)"); Log("manifest: " + man.Error); return "check-failed"; }
        GetResult sig = Get(latest + "update.json.sig", 4 * 1024, UpdateConfig.CheckDeadlineMs);
        if (sig.Body == null) { SetNote("Update check failed (no internet?)"); Log("sig: " + sig.Error); return "check-failed"; }

        string perr;
        Manifest m = Parse(man.Body, out perr);
        if (m == null) { SetNote("Update check failed (no internet?)"); Log("manifest did not parse (captive portal?)"); return "check-failed"; }
        if (!VerifySig(man.Body, Encoding.ASCII.GetString(sig.Body), keys)) { SetNote("Update refused: bad signature"); return "bad-signature"; }
        string rule = CheckRules(m, o.OwnRelease);
        if (rule == "not newer") { Log("up to date (release " + m.Release + ")"); return "no-update"; }
        if (rule != null) { Log("refused: " + rule); return "refused"; }
        if (IsBad(m.Release)) { Log("skip: release " + m.Release + " failed here in the last 24 h"); return "bad-release"; }

        string staged = o.ExePath + ".update";
        if (File.Exists(staged) && File.Exists(staged + ".json"))
        {
            string e3; Manifest sm = Parse(File.ReadAllBytes(staged + ".json"), out e3);
            if (sm != null && sm.Release == m.Release && FileMatches(staged, m)) { Log("already staged " + m.Version); SetReady(m); return "staged"; }
        }

        // Writable? Creating the .part is the test (read-only stick, Program Files).
        try { using (new FileStream(part, FileMode.CreateNew, FileAccess.Write)) { } }
        catch (Exception ex) { SetNote("Folder is read-only"); Log("not writable: " + ex.Message); return "read-only"; }

        string exeUrl = o.BaseUrl + "/releases/download/" + m.Tag + "/" + m.Asset;
        string derr;
        SetPhase("downloading", m.Version, null);
        SetDownload(0, m.Size);
        long size = m.Size;
        bool got = Download(exeUrl, part, m.Size, UpdateConfig.DownloadDeadlineMs, delegate(long b) { SetDownload(b, size); }, out derr);
        if (!got)
        {
            try { File.Delete(part); } catch { }
            Log("download failed: " + derr);
            Fail(m.Version, "Update check failed (no internet?)");
            return "download-failed";
        }
        SetPhase("verifying", m.Version, null);
        if (!FileMatches(part, m))
        {
            try { File.Delete(part); } catch { }
            Log("download failed: hash or size mismatch");
            Fail(m.Version, "Update refused: the download didn't match");
            return "download-failed";
        }
        try
        {
            foreach (string f in new string[] { staged, staged + ".json", staged + ".json.sig" }) if (File.Exists(f)) File.Delete(f);
            File.Move(part, staged);
            File.WriteAllBytes(staged + ".json", man.Body);
            File.WriteAllBytes(staged + ".json.sig", sig.Body);
        }
        catch (Exception ex) { try { File.Delete(part); } catch { } Log("stage failed: " + ex.Message); Fail(m.Version, "Folder is read-only"); return "stage-failed"; }
        Log("staged " + m.Version + " (release " + m.Release + ")");
        SetReady(m);
        return "staged";
    }

    static void SetReady(Manifest m)
    {
        SetPhase("ready", m.Version, null);
        UpdateProgress p = Progress.Copy(); p.Release = m.Release; Progress = p;
    }

    static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---------- B: at the next start ----------

    // I6: light repair. A lone .old (stick pulled between the two renames) goes back; stale .old/.bad beside a
    // working exe are deleted (retried next start if locked).
    public static void Repair(string exePath)
    {
        string old = exePath + ".old", bad = exePath + ".bad";
        try
        {
            if (!File.Exists(exePath) && File.Exists(old)) { MoveRetry(old, exePath); Log("repaired: .old renamed back"); return; }
            if (File.Exists(exePath))
            {
                if (File.Exists(old)) { try { File.Delete(old); } catch { } }
                if (File.Exists(bad)) { try { File.Delete(bad); } catch { } }
            }
        }
        catch (Exception ex) { Log("repair: " + ex.Message); }
    }

    // The staged copy, verified again (signature, rules, hash, not marked bad). null = nothing usable.
    public static Manifest StagedOk(string exePath, int ownRelease, out bool deleteIt)
    {
        deleteIt = false;
        string u = exePath + ".update";
        if (!File.Exists(u)) return null;
        if (!File.Exists(u + ".json") || !File.Exists(u + ".json.sig")) { deleteIt = true; return null; }
        string e;
        byte[] raw = File.ReadAllBytes(u + ".json");
        Manifest m = Parse(raw, out e);
        if (m == null || !VerifySig(raw, File.ReadAllText(u + ".json.sig"), UpdateKeys.Blobs()) || CheckRules(m, ownRelease) != null
            || !FileMatches(u, m) || IsBad(m.Release)) { deleteIt = true; return null; }
        return m;
    }

    public static void DeleteStaged(string exePath)
    {
        foreach (string f in new string[] { exePath + ".update", exePath + ".update.json", exePath + ".update.json.sig" })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    // I7: Defender may hold a fresh file for a moment; retry ~2 s with backoff.
    public static void MoveRetry(string from, string to)
    {
        int wait = 50;
        for (int i = 0; ; i++)
        {
            try { File.Move(from, to); return; }
            catch (IOException) { if (i >= 6) throw; Thread.Sleep(wait); wait *= 2; }
            catch (UnauthorizedAccessException) { if (i >= 6) throw; Thread.Sleep(wait); wait *= 2; }
        }
    }

    // exe → exe.old, exe.update → exe. On a failed second move, .old goes back. True = swapped.
    public static bool Swap(string exePath)
    {
        string old = exePath + ".old", upd = exePath + ".update";
        try { if (File.Exists(old)) File.Delete(old); } catch { }
        try { MoveRetry(exePath, old); }
        catch (Exception ex) { Log("swap: could not move the running exe: " + ex.Message); return false; }
        try { MoveRetry(upd, exePath); }
        catch (Exception ex)
        {
            Log("swap: could not move the new exe in: " + ex.Message);
            try { MoveRetry(old, exePath); } catch (Exception ex2) { Log("swap: restore failed: " + ex2.Message); }
            return false;
        }
        try { File.Delete(upd + ".json"); File.Delete(upd + ".json.sig"); } catch { }
        return true;
    }

    public static string MarkerPath(string nonce) { return Path.Combine(DataDir, "update-ok-" + nonce); }
    public static string PendingPath(string nonce) { return Path.Combine(DataDir, "update-from-" + nonce); }

    public static string NewNonce()
    {
        byte[] b = new byte[8];
        using (RNGCryptoServiceProvider r = new RNGCryptoServiceProvider()) r.GetBytes(b);
        StringBuilder sb = new StringBuilder();
        foreach (byte x in b) sb.Append(x.ToString("x2", Inv));
        return sb.ToString();
    }

    // Child side (C2): "alive <pid>" first, before any self-check; "owner <pid>" once it holds the main mutex.
    public static void Announce(string nonce, string what)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(MarkerPath(nonce), what + " " + Process.GetCurrentProcess().Id.ToString(Inv));
        }
        catch { }
    }

    // Old side. Starts the new exe and waits for its marker (matching nonce + PID). releaseMain runs once the child
    // is alive, so it can take the main mutex. Returns null on success, else the failure note.
    public static string HandOff(string exePath, string args, ILauncher launcher, Action releaseMain, int waitMs, out IChild child)
    {
        child = null;
        try
        {
            Directory.CreateDirectory(DataDir);
            foreach (string f in Directory.GetFiles(DataDir, "update-ok-*")) { try { File.Delete(f); } catch { } }   // C2: no stale marker
        }
        catch { }
        string nonce = NewNonce();
        try { File.WriteAllText(PendingPath(nonce), AppVersion.Version); } catch { }
        if (!File.Exists(exePath)) return "Update vanished (antivirus?)";
        try { child = launcher.Start(exePath, "--updated " + nonce + (string.IsNullOrEmpty(args) ? "" : " " + args)); }
        catch (Exception ex) { Log("hand-off: start failed: " + ex.Message); return File.Exists(exePath) ? "Update failed to start" : "Update vanished (antivirus?)"; }
        string marker = MarkerPath(nonce);
        string want = child.Id.ToString(Inv);
        bool released = false;
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < waitMs)
        {
            string txt = null;
            try { if (File.Exists(marker)) txt = File.ReadAllText(marker).Trim(); } catch { }
            if (txt != null)
            {
                string[] p = txt.Split(' ');
                if (p.Length == 2 && p[1] == want)
                {
                    if (!released) { released = true; releaseMain(); }
                    if (p[0] == "owner") { try { File.Delete(marker); } catch { } return null; }
                    if (p[0] == "refused") { try { File.Delete(marker); } catch { } Log("hand-off: the laptop refused (no battery / Desktop), not the release"); return null; }
                }
            }
            if (child.HasExited) return File.Exists(exePath) ? "Update failed to start" : "Update vanished (antivirus?)";
            Thread.Sleep(100);
        }
        return "Update failed to start";
    }

    // Rollback after a failed hand-off: kill the child, exe → .bad, .old → exe, mark the release bad for 24 h.
    public static void Rollback(string exePath, IChild child, int release, string note)
    {
        if (child != null) { child.Kill(); for (int i = 0; i < 20 && !child.HasExited; i++) Thread.Sleep(100); }
        string old = exePath + ".old", bad = exePath + ".bad";
        try
        {
            if (File.Exists(old))
            {
                if (File.Exists(exePath)) { try { if (File.Exists(bad)) File.Delete(bad); } catch { } MoveRetry(exePath, bad); }
                MoveRetry(old, exePath);
            }
        }
        catch (Exception ex) { Log("rollback: " + ex.Message); }
        MarkBad(release);
        SetNote(note);
    }

    // Child: who did we update from? (set for the whole run; the page shows the toast once per version).
    public static string TakeUpdatedFrom(string nonce)
    {
        try
        {
            string f = PendingPath(nonce);
            if (!File.Exists(f)) return null;
            string v = File.ReadAllText(f).Trim();
            File.Delete(f);
            return v.Length == 0 ? null : v;
        }
        catch { return null; }
    }

    // --verify-release <dir>: read-only check of a release folder against this exe's keys and version. 0 = good.
    public static int VerifyRelease(string dir, out string why) { return VerifyRelease(dir, null, out why); }

    // keysFile: "X:Y" hex per line = the keys the laptops in the field have now (the previous release's), U2.
    // Without it, this exe's own keys are used.
    public static List<byte[]> KeysFromFile(string path)
    {
        List<byte[]> l = new List<byte[]>();
        foreach (string line in File.ReadAllLines(path))
        {
            string[] xy = line.Trim().Split(':');
            if (xy.Length == 2) l.Add(UpdateKeys.BuildBlob(xy[0], xy[1]));
        }
        return l;
    }

    public static int VerifyRelease(string dir, string keysFile, out string why)
    {
        why = null;
        try
        {
            string mj = Path.Combine(dir, "update.json"), ms = mj + ".sig";
            if (!File.Exists(mj) || !File.Exists(ms)) { why = "update.json or update.json.sig missing"; return 2; }
            byte[] raw = File.ReadAllBytes(mj);
            string e; Manifest m = Parse(raw, out e);
            if (m == null) { why = "update.json does not parse"; return 3; }
            List<byte[]> keys = keysFile != null ? KeysFromFile(keysFile) : UpdateKeys.Blobs();
            if (keys.Count == 0) { why = "no keys to check against"; return 8; }
            if (!VerifySig(raw, File.ReadAllText(ms), keys)) { why = "signature does not verify" + (keysFile != null ? " with the field keys" : ""); return 4; }
            string rule = CheckRules(m, m.Release - 1);
            if (rule != null) { why = rule; return 5; }
            if (m.Release != AppVersion.Release || m.Version != AppVersion.Version) { why = "manifest is not this exe's version/release"; return 6; }
            if (!FileMatches(Path.Combine(dir, m.Asset), m)) { why = "exe hash or size does not match"; return 7; }
            why = "ok " + m.Version + " release " + m.Release;
            return 0;
        }
        catch (Exception ex) { why = ex.Message; return 9; }
    }
}
