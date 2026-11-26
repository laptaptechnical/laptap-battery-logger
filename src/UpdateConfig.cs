// src/UpdateConfig.cs
// Where updates come from (plan §5.2). HTTPS only; an empty BaseUrl turns the updater off.
// A TESTHOOKS build (tests only, never released) may also point at a local http://127.0.0.1 server and use its own
// data folder and mutex names, so the end-to-end test never touches a real logger on this PC.
using System;

public static class UpdateConfig
{
    public const string ReleaseBaseUrl = "https://github.com/laptaptechnical/laptap-battery-logger";
    public const string Product = "laptap-battery-logger";
    public const string AssetName = "Laptap-Battery-Logger.exe";
    public const long MaxExeBytes = 5L * 1024 * 1024;
    public const int CheckDeadlineMs = 6000;        // per request, wall clock (C3)
    public const int DownloadDeadlineMs = 60000;
    public const int MaxReleaseJump = 1000;         // I3: a stolen key can't publish release 2147483647

    public static string BaseUrl
    {
        get
        {
#if TESTHOOKS
            string t = Environment.GetEnvironmentVariable("LAPTAP_BL_TEST_BASE");
            if (!string.IsNullOrEmpty(t)) return t.TrimEnd('/');
#endif
            return ReleaseBaseUrl;
        }
    }

    // HTTPS only. A TESTHOOKS build also allows http://127.0.0.1 / localhost for the local end-to-end test.
    public static bool UrlAllowed(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        Uri u;
        if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
        if (u.Scheme == Uri.UriSchemeHttps) return true;
#if TESTHOOKS
        if (u.Scheme == Uri.UriSchemeHttp && (u.Host == "127.0.0.1" || u.Host == "localhost")) return true;
#endif
        return false;
    }

    // Test isolation: own data folder + mutex names (TESTHOOKS only).
    public static string TestNamespace
    {
        get
        {
#if TESTHOOKS
            string ns = Environment.GetEnvironmentVariable("LAPTAP_BL_TEST_NS");
            if (!string.IsNullOrEmpty(ns)) return ns;
#endif
            return "";
        }
    }
}
