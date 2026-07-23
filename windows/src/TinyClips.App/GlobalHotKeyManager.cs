using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace TinyClips.App;

/// <summary>
/// Registers system-wide hotkeys via Win32 <c>RegisterHotKey</c>. Because a tray-only
/// app has no long-lived foreground window, this owns a dedicated background thread that
/// creates a message-only window, registers the hotkeys against it and pumps a message
/// loop. <c>WM_HOTKEY</c> notifications are marshalled back to the UI dispatcher.
/// </summary>
internal sealed partial class GlobalHotKeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_CLOSE = 0x0010;
    private const int WM_QUIT = 0x0012;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint PM_NOREMOVE = 0x0000;
    private static readonly nint HWND_MESSAGE = new(-3);

    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<int, Action> _callbacks = new();
    private readonly List<PendingHotKey> _pending = new();
    private readonly List<int> _registeredIds = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _messageQueueReady = new(false);

    private Thread? _thread;
    private uint _threadId;
    private nint _hwnd;
    private WndProcDelegate? _wndProc; // held to keep the native callback alive
    private GlobalHotKeyRegistrationResult _startResult = GlobalHotKeyRegistrationResult.Success;
    private int _nextId = 1;
    private int _shutdownRequested;
    private bool _disposed;

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);
    private sealed record PendingHotKey(
        int Id,
        string Name,
        int Modifiers,
        uint VirtualKey);

    public GlobalHotKeyManager(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Queues a hotkey to register when <see cref="Start"/> is called.</summary>
    public void Add(string name, int modifiers, uint virtualKey, Action callback)
    {
        if (virtualKey == 0)
        {
            return;
        }

        var id = _nextId++;
        _callbacks[id] = callback;
        _pending.Add(new PendingHotKey(id, name, modifiers, virtualKey));
    }

    public GlobalHotKeyRegistrationResult Start()
    {
        if (_thread is not null)
        {
            return _startResult;
        }

        if (_pending.Count == 0)
        {
            return GlobalHotKeyRegistrationResult.Success;
        }

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "TinyClips.GlobalHotKeys",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            var timeoutResult = GlobalHotKeyRegistrationResult.Failed(
                new GlobalHotKeyRegistrationFailure(
                    "TinyClips hotkey service",
                    0,
                    "Timed out while starting the Windows hotkey service."));
            RequestShutdownAndWait();
            _startResult = timeoutResult;
            return timeoutResult;
        }

        return _startResult;
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        PeekMessageW(out _, 0, 0, 0, PM_NOREMOVE);
        _messageQueueReady.Set();
        if (Volatile.Read(ref _shutdownRequested) != 0)
        {
            return;
        }

        var className = "TinyClipsHotKeyWindow_" + Guid.NewGuid().ToString("N");
        var hInstance = GetModuleHandleW(null);
        _wndProc = WndProc;

        var wndClass = new WNDCLASSW
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = className,
        };

        if (RegisterClassW(ref wndClass) == 0)
        {
            _startResult = GlobalHotKeyRegistrationResult.Failed(
                CreateNativeFailure("TinyClips hotkey service", "Could not create the hotkey window class."));
            _ready.Set();
            return;
        }

        _hwnd = CreateWindowExW(0, className, string.Empty, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, hInstance, 0);
        if (_hwnd == 0)
        {
            _startResult = GlobalHotKeyRegistrationResult.Failed(
                CreateNativeFailure("TinyClips hotkey service", "Could not create the hotkey window."));
            _ready.Set();
            return;
        }

        var failures = new List<GlobalHotKeyRegistrationFailure>();
        foreach (var hotKey in _pending)
        {
            if (RegisterHotKey(
                _hwnd,
                hotKey.Id,
                (uint)hotKey.Modifiers | MOD_NOREPEAT,
                hotKey.VirtualKey))
            {
                _registeredIds.Add(hotKey.Id);
            }
            else
            {
                failures.Add(CreateNativeFailure(
                    hotKey.Name,
                    "Windows rejected this shortcut, usually because another app already uses it."));
            }
        }

        _startResult = failures.Count == 0
            ? GlobalHotKeyRegistrationResult.Success
            : new GlobalHotKeyRegistrationResult(failures);
        _ready.Set();

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY)
            {
                var id = (int)msg.wParam;
                if (_callbacks.TryGetValue(id, out var callback))
                {
                    _dispatcher.TryEnqueue(() => callback());
                }
            }

            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        UnregisterAll();
        DestroyHotKeyWindow();
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_CLOSE)
        {
            UnregisterAll();
            DestroyHotKeyWindow();
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static GlobalHotKeyRegistrationFailure CreateNativeFailure(string name, string message)
        => new(name, Marshal.GetLastWin32Error(), message);

    private void UnregisterAll()
    {
        if (_hwnd == 0)
        {
            return;
        }

        foreach (var id in _registeredIds)
        {
            UnregisterHotKey(_hwnd, id);
        }

        _registeredIds.Clear();
    }

    private void DestroyHotKeyWindow()
    {
        if (_hwnd == 0)
        {
            return;
        }

        DestroyWindow(_hwnd);
        _hwnd = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var stopped = RequestShutdownAndWait();
        if (stopped)
        {
            _messageQueueReady.Dispose();
            _ready.Dispose();
        }
    }

    private bool RequestShutdownAndWait()
    {
        Volatile.Write(ref _shutdownRequested, 1);

        var thread = _thread;
        if (thread is null || !thread.IsAlive)
        {
            return true;
        }

        if (thread == Thread.CurrentThread)
        {
            return false;
        }

        if (_hwnd == 0 || !PostMessageW(_hwnd, WM_CLOSE, 0, 0))
        {
            if (_messageQueueReady.Wait(TimeSpan.FromSeconds(1)) && _threadId != 0)
            {
                PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            }
        }

        return thread.Join(TimeSpan.FromSeconds(5));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSW
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageW(
        out MSG lpMsg,
        nint hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax,
        uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

internal sealed record GlobalHotKeyRegistrationFailure(
    string Name,
    int NativeErrorCode,
    string Message);

internal sealed record GlobalHotKeyRegistrationResult(
    IReadOnlyList<GlobalHotKeyRegistrationFailure> Failures)
{
    public bool IsSuccess => Failures.Count == 0;

    public static GlobalHotKeyRegistrationResult Success { get; } = new([]);

    public static GlobalHotKeyRegistrationResult Failed(GlobalHotKeyRegistrationFailure failure)
        => new([failure]);
}
