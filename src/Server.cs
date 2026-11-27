// src/Server.cs
// HttpListener on http://localhost:<port>/ only (never + or *). Serves the embedded page and the JSON API.
// Every request: loopback remote address (else 403), Host localhost:<port> (else 400), OPTIONS → 403,
// /api/* needs the per-launch X-Laptap-Token (else 403). No CORS headers, ever.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

public class Command
{
    public string Name;
    public Dictionary<string, object> Body;
    public Dictionary<string, object> Result;
    public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
}

public class Server
{
    public const int FirstPort = 47600, LastPort = 47619;
    public int Port;
    public string Token;
    public readonly BlockingCollection<Command> Commands = new BlockingCollection<Command>();
    public Func<Dictionary<string, object>> GetSnapshot = delegate { return new Dictionary<string, object>(); };
    public Func<Dictionary<string, object>> GetApp;   // v15.1: app.* at serve time (the download % between publishes)
    public Action<Exception> OnCrash = delegate { };
    public byte[] IndexHtml = new byte[0], LogicJs = new byte[0];
    // ---- v16 §6: the brand logo and the packaged video (C2, C3, M5) ----
    public byte[] LogoSvg;                                   // /brand/logo.svg (null → 404)
    public string MediaExt;                                  // "webm" | "mp4" | null (no packaged video → /media/* 404)
    public Func<Stream> OpenMedia = delegate { return null; };   // a seekable stream of the embedded video
    public const long MediaChunk = 1048576;                  // each media response is capped at 1 MB; Edge asks for the next range
    long lastStateGetMs;   // the countdown only auto-starts while a page is polling (plan §3)
    public long LastStateGetMs { get { return Interlocked.Read(ref lastStateGetMs); } }
    public Action OnFirstState = delegate { };   // U1: the updated child says "owner" after the first answered poll
    int firstStateDone;

    HttpListener listener;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();

    public Server() { json.MaxJsonLength = int.MaxValue; Token = NewToken(); }

    public static string NewToken()
    {
        byte[] b = new byte[16];
        using (RNGCryptoServiceProvider r = new RNGCryptoServiceProvider()) r.GetBytes(b);
        StringBuilder sb = new StringBuilder();
        foreach (byte x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    // Pure request check (tested in HelperTests). 0 = allowed, else the HTTP status to answer.
    public static int CheckRequest(string method, bool remoteIsLoopback, string host, int port, string path, string token, string expectedToken)
    {
        if (!remoteIsLoopback) return 403;
        if (!string.Equals(host ?? "", "localhost:" + port.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)) return 400;
        if (method == "OPTIONS") return 403;
        if (method != "GET" && method != "POST") return 405;
        if (path != null && path.StartsWith("/api/", StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(token) || !FixedEquals(token, expectedToken)) return 403;
            return 0;
        }
        if (method == "GET" && (path == "/" || path == "/index.html" || path == "/logic.js")) return 0;
        // v16: no token for the logo and the video (an <img>/<video> can't send one); loopback + Host still apply.
        if (method == "GET" && (path == "/brand/logo.svg" || path == "/media/loop.webm" || path == "/media/loop.mp4")) return 0;
        return 404;
    }

    static bool FixedEquals(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        int d = 0;
        for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
        return d == 0;
    }

    // Headers on every response. Never any Access-Control-* header.
    public static Dictionary<string, string> ResponseHeaders(string contentType)
    {
        Dictionary<string, string> h = new Dictionary<string, string>();
        h["Content-Type"] = contentType;
        h["Cache-Control"] = "no-store";
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; media-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'";
        return h;
    }

    // v16 §6: a Range header → the bytes to send. Returns 206 (start..end inclusive, capped at cap bytes) or 416.
    // No header (a full GET) is answered like "bytes=0-" (capped). Suffix ranges ("bytes=-500") count from the end.
    public static int ParseRange(string header, long length, long cap, out long start, out long end)
    {
        start = 0; end = -1;
        if (length <= 0) return 416;
        string h = (header ?? "").Trim();
        if (h.Length == 0) h = "bytes=0-";
        if (!h.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || h.IndexOf(',') >= 0) return 416;
        string spec = h.Substring(6).Trim();
        int dash = spec.IndexOf('-');
        if (dash < 0) return 416;
        string a = spec.Substring(0, dash).Trim(), b = spec.Substring(dash + 1).Trim();
        long x, y;
        if (a.Length == 0)
        {
            if (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out y) || y <= 0) return 416;
            start = Math.Max(0, length - y); end = length - 1;
        }
        else
        {
            if (!long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out x) || x >= length) return 416;
            start = x;
            end = b.Length == 0 ? length - 1 : (long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out y) ? Math.Min(y, length - 1) : -1);
            if (end < start) return 416;
        }
        if (end - start + 1 > cap) end = start + cap - 1;
        return 206;
    }

    // Off the server thread (C2): a slow or stalled media read can never block /api/state or a POST.
    void ServeMedia(HttpListenerContext ctx)
    {
        try
        {
            HttpListenerResponse r = ctx.Response;
            string type = MediaExt == "mp4" ? "video/mp4" : "video/webm";
            using (Stream s = OpenMedia())
            {
                if (s == null) { Send(ctx, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404")); return; }
                long len = s.Length, a, b;
                int code = ParseRange(ctx.Request.Headers["Range"], len, MediaChunk, out a, out b);
                foreach (KeyValuePair<string, string> h in ResponseHeaders(type)) { if (h.Key == "Content-Type") r.ContentType = h.Value; else r.Headers[h.Key] = h.Value; }
                r.Headers["Cache-Control"] = "private, max-age=3600";
                r.Headers["Accept-Ranges"] = "bytes";
                if (code == 416)
                {
                    r.StatusCode = 416; r.Headers["Content-Range"] = "bytes */" + len.ToString(CultureInfo.InvariantCulture);
                    r.ContentLength64 = 0; r.OutputStream.Close(); return;
                }
                r.StatusCode = 206;
                r.Headers["Content-Range"] = "bytes " + a.ToString(CultureInfo.InvariantCulture) + "-" + b.ToString(CultureInfo.InvariantCulture) + "/" + len.ToString(CultureInfo.InvariantCulture);
                long n = b - a + 1;
                r.ContentLength64 = n;
                s.Seek(a, SeekOrigin.Begin);
                byte[] buf = new byte[65536];
                while (n > 0)
                {
                    int got = s.Read(buf, 0, (int)Math.Min(buf.Length, n));
                    if (got <= 0) break;
                    r.OutputStream.Write(buf, 0, got);
                    n -= got;
                }
                r.OutputStream.Close();
            }
        }
        catch (HttpListenerException) { try { ctx.Response.Abort(); } catch { } }   // the player dropped the connection
        catch (IOException) { try { ctx.Response.Abort(); } catch { } }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    public bool Start()
    {
        for (int p = FirstPort; p <= LastPort; p++)
        {
            HttpListener l = new HttpListener();
            l.Prefixes.Add("http://localhost:" + p.ToString(CultureInfo.InvariantCulture) + "/");
            try { l.Start(); listener = l; Port = p; break; }
            catch { try { l.Close(); } catch { } }
        }
        if (listener == null) return false;
        Thread t = new Thread(Loop);
        t.IsBackground = true; t.Name = "http";
        t.Start();
        return true;
    }

    public void Stop() { try { if (listener != null) listener.Close(); } catch { } }

    void Loop()
    {
        try
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch (HttpListenerException) { return; } catch (ObjectDisposedException) { return; } catch (InvalidOperationException) { return; }
                HttpListenerContext c = ctx;
                string p = null; try { p = c.Request.Url.AbsolutePath; } catch { }
                if (p != null && p.StartsWith("/media/", StringComparison.Ordinal))
                {
                    // v16 (C2): media goes to the ThreadPool and the loop returns at once.
                    ThreadPool.QueueUserWorkItem(delegate { try { Handle(c); } catch { try { c.Response.Abort(); } catch { } } });
                    continue;
                }
                try { Handle(ctx); } catch { try { ctx.Response.Abort(); } catch { } }
            }
        }
        catch (Exception ex) { OnCrash(ex); }
    }

    void Send(HttpListenerContext ctx, int status, string type, byte[] body)
    {
        HttpListenerResponse r = ctx.Response;
        r.StatusCode = status;
        foreach (KeyValuePair<string, string> h in ResponseHeaders(type))
        {
            if (h.Key == "Content-Type") r.ContentType = h.Value; else r.Headers[h.Key] = h.Value;
        }
        r.ContentLength64 = body.Length;
        r.OutputStream.Write(body, 0, body.Length);
        r.OutputStream.Close();
    }

    void SendJson(HttpListenerContext ctx, int status, object o)
    {
        Send(ctx, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json.Serialize(o)));
    }

    void Handle(HttpListenerContext ctx)
    {
        HttpListenerRequest q = ctx.Request;
        string path = q.Url.AbsolutePath;
        int code = CheckRequest(q.HttpMethod, q.RemoteEndPoint != null && IPAddress.IsLoopback(q.RemoteEndPoint.Address),
            q.Headers["Host"], Port, path, q.Headers["X-Laptap-Token"], Token);
        if (code != 0) { Send(ctx, code, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(code.ToString(CultureInfo.InvariantCulture))); return; }

        if (path == "/" || path == "/index.html") { Send(ctx, 200, "text/html; charset=utf-8", IndexHtml); return; }
        if (path == "/logic.js") { Send(ctx, 200, "text/javascript; charset=utf-8", LogicJs); return; }
        if (path == "/brand/logo.svg")
        {
            if (LogoSvg == null) { Send(ctx, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404")); return; }
            Send(ctx, 200, "image/svg+xml", LogoSvg); return;
        }
        if (path == "/media/loop.webm" || path == "/media/loop.mp4")
        {
            if (MediaExt == null || path != "/media/loop." + MediaExt) { Send(ctx, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404")); return; }
            ServeMedia(ctx); return;
        }

        if (q.HttpMethod == "GET" && path == "/api/state")
        {
            Interlocked.Exchange(ref lastStateGetMs, Rules.NowMs());
            long since = 0; long.TryParse(q.QueryString["since"] ?? "0", out since);
            Dictionary<string, object> st = Filter(GetSnapshot(), since);
            if (GetApp != null) { try { st["app"] = GetApp(); } catch { } }
            SendJson(ctx, 200, st);
            if (Interlocked.Exchange(ref firstStateDone, 1) == 0) { try { OnFirstState(); } catch { } }
            return;
        }
        if (q.HttpMethod == "POST")
        {
            string name = path.Substring("/api/".Length);
            if (!IsCommand(name))
            { SendJson(ctx, 404, Err("Unknown command.")); return; }
            Dictionary<string, object> body = null;
            if (q.HasEntityBody && q.ContentLength64 <= 4096)
            {
                string txt;
                using (StreamReader r = new StreamReader(q.InputStream, Encoding.UTF8)) txt = r.ReadToEnd();
                try { body = json.Deserialize<Dictionary<string, object>>(txt); } catch { body = null; }
            }
            Command c = new Command(); c.Name = name; c.Body = body;
            Commands.Add(c);
            // Start runs powercfg (up to 15 s); everything else answers fast.
            if (!c.Done.Wait(name == "stop" ? 30000 : 5000)) { SendJson(ctx, 503, Err("Busy, try again.")); return; }
            SendJson(ctx, 200, c.Result);
            return;
        }
        Send(ctx, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404"));
    }

    public static bool IsCommand(string name)
    {
#if TESTHOOKS
        if (name == "start") return true;   // test hook only; the release build has no such route
#endif
        return name == "heartbeat" || name == "stop" || name == "ack-alarm" || name == "alarm-pct" || name == "save-csv"
            || name == "arm" || name == "cancel" || name == "new"
            || name == "update-restart" || name == "update-retry" || name == "open-report"   // v15.1
            || name == "start-anyway" || name == "pause" || name == "resume" || name == "video";   // v16 (suspend / resume-power are internal)
    }

    static Dictionary<string, object> Err(string m)
    {
        Dictionary<string, object> d = new Dictionary<string, object>(); d["ok"] = false; d["error"] = m; return d;
    }

    // Only the samples/events newer than `since` are sent.
    public static Dictionary<string, object> Filter(Dictionary<string, object> snap, long since)
    {
        Dictionary<string, object> d = new Dictionary<string, object>(snap);
        d["nowMs"] = Rules.NowMs();   // the page shows the countdown from countdownEndsMs - nowMs
        foreach (string k in new string[] { "samples", "events" })
        {
            object v;
            if (!snap.TryGetValue(k, out v) || !(v is Dictionary<string, object>[])) continue;
            List<object> l = new List<object>();
            foreach (Dictionary<string, object> x in (Dictionary<string, object>[])v)
                if (Convert.ToInt64(x["rev"], CultureInfo.InvariantCulture) > since) l.Add(x);
            d[k] = l;
        }
        return d;
    }
}
