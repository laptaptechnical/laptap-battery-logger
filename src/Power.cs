// src/Power.cs
// v16 §4 (C1): suspend / resume notifications without SystemEvents (Main is an STA thread that never pumps messages,
// so SystemEvents would never fire). RegisterSuspendResumeNotification with a DEVICE_NOTIFY_CALLBACK needs no window
// (Windows 8+). The callback never touches the session: it hands a command to the main thread (I6).
// Fallback when registration fails: a background thread with its own hidden window and message loop for
// WM_POWERBROADCAST. (A message-only window gets no broadcasts, so it is a hidden top-level window.)
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

public static class PowerNotify
{
    public const int PBT_APMSUSPEND = 4, PBT_APMRESUMESUSPEND = 7, PBT_APMRESUMEAUTOMATIC = 18;
    const int DEVICE_NOTIFY_CALLBACK = 2, WM_POWERBROADCAST = 0x218;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS { public DeviceNotifyCallbackRoutine Callback; public IntPtr Context; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr RegisterSuspendResumeNotification(IntPtr hRecipient, int flags);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterSuspendResumeNotification(IntPtr handle);

    // Rooted for the process lifetime: the OS holds a pointer to them.
    static DeviceNotifyCallbackRoutine callback;
    static IntPtr paramsMem = IntPtr.Zero, handle = IntPtr.Zero;
    static Thread fallbackThread;
    static Action<int> onEvent = delegate { };
    public static string Mode = "off";   // "callback" | "window" | "off" (for error.txt / tests)

    // Maps a power broadcast to the command the main thread runs: "suspend" | "resume-power" | null.
    public static string CommandFor(int pbt)
    {
        if (pbt == PBT_APMSUSPEND) return "suspend";
        if (pbt == PBT_APMRESUMESUSPEND || pbt == PBT_APMRESUMEAUTOMATIC) return "resume-power";   // both, idempotent
        return null;
    }

    public static void Start(Action<int> handler)
    {
        onEvent = handler ?? delegate { };
        try
        {
            callback = delegate(IntPtr ctx, uint type, IntPtr setting) { try { onEvent((int)type); } catch { } return 0; };
            DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS p = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS();
            p.Callback = callback; p.Context = IntPtr.Zero;
            paramsMem = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS)));
            Marshal.StructureToPtr(p, paramsMem, false);
            handle = RegisterSuspendResumeNotification(paramsMem, DEVICE_NOTIFY_CALLBACK);
            if (handle != IntPtr.Zero) { Mode = "callback"; return; }
        }
        catch { }
        try { if (paramsMem != IntPtr.Zero) Marshal.FreeHGlobal(paramsMem); } catch { }
        paramsMem = IntPtr.Zero;
        StartWindow();
    }

    public static void Stop()
    {
        try { if (handle != IntPtr.Zero) UnregisterSuspendResumeNotification(handle); } catch { }
        handle = IntPtr.Zero;
        try { if (paramsMem != IntPtr.Zero) Marshal.FreeHGlobal(paramsMem); } catch { }
        paramsMem = IntPtr.Zero;
    }

    class PowerWindow : NativeWindow
    {
        public PowerWindow() { CreateHandle(new CreateParams()); }   // hidden top-level (no WS_VISIBLE)
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST) { try { onEvent(m.WParam.ToInt32()); } catch { } }
            base.WndProc(ref m);
        }
    }

    static void StartWindow()
    {
        fallbackThread = new Thread(delegate()
        {
            try { PowerWindow w = new PowerWindow(); Mode = "window"; Application.Run(); GC.KeepAlive(w); }
            catch { Mode = "off"; }
        });
        fallbackThread.IsBackground = true; fallbackThread.Name = "power"; fallbackThread.SetApartmentState(ApartmentState.STA);
        fallbackThread.Start();
    }
}
