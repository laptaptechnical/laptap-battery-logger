// Battery-Logger/v14/src/Native.cs
// P/Invoke: keep-awake (SetThreadExecutionState), the 1 s power syscall (GetSystemPowerStatus),
// and the read-only keep-awake check (CallNtPowerInformation SystemExecutionState).
using System;
using System.Runtime.InteropServices;

public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0 offline, 1 online, 255 unknown
        public byte BatteryFlag;         // 128 = no system battery, 4 = critical, 255 unknown
        public byte BatteryLifePercent;  // 0-100, 255 unknown
        public byte SystemStatusFlag;    // bit 0 = battery saver on
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public const uint ES_CONTINUOUS = 0x80000000;
    public const uint ES_SYSTEM_REQUIRED = 0x00000001;
    public const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);

    const int SystemExecutionState = 16;

    [DllImport("powrprof.dll")]
    static extern uint CallNtPowerInformation(int level, IntPtr inBuf, uint inLen, out uint outBuf, uint outLen);

    // Returns the system-wide execution state flags, or null if the call fails (not proven without admin).
    public static uint? ExecutionState()
    {
        try
        {
            uint v;
            uint rc = CallNtPowerInformation(SystemExecutionState, IntPtr.Zero, 0, out v, 4);
            if (rc != 0) return null;
            return v;
        }
        catch { return null; }
    }

    public static string ExecutionStateText(uint? v)
    {
        if (!v.HasValue) return "n/a";
        string s = "";
        if ((v.Value & ES_SYSTEM_REQUIRED) != 0) s += "SYSTEM ";
        if ((v.Value & ES_DISPLAY_REQUIRED) != 0) s += "DISPLAY ";
        return s.Length == 0 ? "none" : s.Trim();
    }
}
