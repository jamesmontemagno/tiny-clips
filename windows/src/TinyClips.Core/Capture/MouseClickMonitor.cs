using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinyClips.Core.Capture;

/// <summary>
/// Records global mouse-button-down events (with a relative timestamp and screen
/// position) for the duration of a recording, so they can be replayed as visual
/// overlays on captured frames. Uses a low-level mouse hook (<c>WH_MOUSE_LL</c>)
/// on a dedicated thread with its own message loop, which is required for the hook
/// to receive callbacks.
/// </summary>
public sealed class MouseClickMonitor : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_QUIT = 0x0012;

    private readonly object _gate = new();
    private readonly List<MouseClickSample> _clicks = new();
    private readonly Stopwatch _clock = new();

    private LowLevelMouseProc? _proc;
    private nint _hookHandle;
    private Thread? _thread;
    private uint _threadId;
    private bool _disposed;

    /// <summary>Begins listening for mouse clicks. Idempotent while running.</summary>
    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _clicks.Clear();
        _clock.Restart();

        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => RunHookLoop(ready))
        {
            IsBackground = true,
            Name = "TinyClips.MouseClickMonitor",
        };
        _thread.Start();
        ready.Wait(2000);
    }

    private void RunHookLoop(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        _proc = HookCallback;
        _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        ready.Set();

        if (_hookHandle == 0)
        {
            return;
        }

        while (GetMessage(out MSG msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            int message = (int)wParam;
            if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                double t = _clock.Elapsed.TotalSeconds;
                var button = message switch
                {
                    WM_RBUTTONDOWN => MouseClickButton.Right,
                    WM_MBUTTONDOWN => MouseClickButton.Middle,
                    _ => MouseClickButton.Left,
                };
                lock (_gate)
                {
                    _clicks.Add(new MouseClickSample(t, data.pt.x, data.pt.y, button, RecordingTimeline.SystemRelativeNow()));
                }
            }
        }

        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    /// <summary>Returns a snapshot of all clicks recorded so far.</summary>
    public IReadOnlyList<MouseClickSample> GetClicks()
    {
        lock (_gate)
        {
            return _clicks.ToArray();
        }
    }

    public void Stop()
    {
        if (_thread == null)
        {
            return;
        }

        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        }

        _thread.Join(2000);
        _thread = null;

        if (_hookHandle != 0)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = 0;
        }

        _proc = null;
        _clock.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, nint wParam, nint lParam);
}
