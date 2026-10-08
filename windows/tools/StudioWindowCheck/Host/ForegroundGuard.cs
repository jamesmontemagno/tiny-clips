using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TinyClips.Tools.StudioWindowCheck.Host;

/// <summary>
/// Makes this process unable to bring one of its windows to the front, and keeps a record of
/// every attempt.
/// </summary>
/// <remarks>
/// <para>
/// A WinUI window that moves the keyboard focus to one of its controls also asks Windows for the
/// keyboard focus, and Windows answers that by activating the window. Normally a process in the
/// background is refused the foreground, but not always: after a few minutes without input, for
/// one, Windows lets any process take it. The editor moves focus when a project has opened, when
/// an export starts and ends, and when a dialog opens and closes. On a machine where someone is
/// working, that must never bring a window of this tool to the front.
/// </para>
/// <para>
/// Three things together see to that. The functions that activate a window or give it the
/// keyboard focus are replaced, in this process only, with ones that do nothing and count the
/// call: SetForegroundWindow, SetActiveWindow, SetFocus and SwitchToThisWindow, and the system
/// calls behind the first three. A hook on the UI thread refuses every activation and every
/// change of focus that reaches Windows some other way. And each window of the tool is marked as
/// one the system does not activate by itself (see <see cref="Native.MakeUnactivatable"/>).
/// </para>
/// <para>
/// The tool therefore drives the editor without the keyboard focus ever being in it. What a
/// focused control does with a key is not checked by this tool.
/// </para>
/// </remarks>
internal static unsafe partial class ForegroundGuard
{
    private const int WhCbt = 5;
    private const int HcbtActivate = 5;
    private const int HcbtSetFocus = 9;
    private const uint PageExecuteReadWrite = 0x40;
    private const int PatchLength = 12;

    private static readonly object Gate = new();
    private static readonly List<GuardEvent> Recorded = [];
    private static readonly long Started = Stopwatch.GetTimestamp();
    private static nint _hook;
    private static string _step = "starting";

    /// <summary>What the checks are doing, so that an attempt can be put down to it.</summary>
    public static string Step
    {
        get => Volatile.Read(ref _step);
        set => Volatile.Write(ref _step, value);
    }

    public static double Elapsed => Stopwatch.GetElapsedTime(Started).TotalSeconds;

    /// <summary>Every attempt so far to activate a window of this process or to give it the keyboard focus.</summary>
    public static GuardEvent[] Events()
    {
        lock (Gate)
        {
            return [.. Recorded];
        }
    }

    /// <summary>
    /// Puts the guard in place. Call it on the UI thread before the first window exists. Throws
    /// when any part could not be put in place or does not work: the tool must not go on then.
    /// </summary>
    public static void Install()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("StudioWindowCheck can only keep its windows out of the foreground in an x64 process.");
        }

        var user32 = NativeLibrary.Load("user32.dll");
        var win32u = NativeLibrary.Load("win32u.dll");
        Replace(win32u, "NtUserSetForegroundWindow", &OnSetForegroundWindow);
        Replace(win32u, "NtUserSetActiveWindow", &OnSetActiveWindow);
        Replace(win32u, "NtUserSetFocus", &OnSetFocus);
        Replace(user32, "SetForegroundWindow", &OnSetForegroundWindow);
        Replace(user32, "SetActiveWindow", &OnSetActiveWindow);
        Replace(user32, "SetFocus", &OnSetFocus);
        Replace(user32, "SwitchToThisWindow", &OnSwitchToThisWindow);

        _hook = SetWindowsHookEx(WhCbt, &OnCbt, 0, GetCurrentThreadId());
        if (_hook == 0)
        {
            throw new InvalidOperationException($"The hook that refuses activation could not be set (error {Marshal.GetLastPInvokeError()}).");
        }

        // The replaced functions are called once each, for nothing: a null window. Each call has
        // to arrive at its replacement. Were a function not replaced, the call would do nothing.
        Step = "self-test";
        var before = Events().Length;
        SetForegroundWindow(0);
        SetActiveWindow(0);
        SetFocus(0);
        SwitchToThisWindow(0, 0);
        var seen = Events().Skip(before).Select(e => e.What).ToArray();
        string[] wanted = ["SetForegroundWindow", "SetActiveWindow", "SetFocus", "SwitchToThisWindow"];
        if (!wanted.SequenceEqual(seen))
        {
            throw new InvalidOperationException($"The functions that bring a window to the front could not be switched off: calls arrived as [{string.Join(", ", seen)}].");
        }

        lock (Gate)
        {
            Recorded.Clear();
        }

        Step = "starting";
    }

    private static void Record(string what, nint window)
    {
        var entry = new GuardEvent(Elapsed, what, window, window == 0 ? string.Empty : Native.ClassOf(window), Step);
        lock (Gate)
        {
            if (Recorded.Count < 5000)
            {
                Recorded.Add(entry);
            }
        }
    }

    /// <summary>Overwrites the start of an exported function with a jump to a function of the tool.</summary>
    private static void Replace(nint library, string name, delegate* unmanaged<nint, nint, nint> replacement)
    {
        var address = NativeLibrary.GetExport(library, name);
        Span<byte> jump = stackalloc byte[PatchLength];
        jump[0] = 0x48; // mov rax, replacement
        jump[1] = 0xB8;
        BinaryPrimitives.WriteInt64LittleEndian(jump[2..], (long)replacement);
        jump[10] = 0xFF; // jmp rax
        jump[11] = 0xE0;

        if (!VirtualProtect(address, PatchLength, PageExecuteReadWrite, out var protection))
        {
            throw new InvalidOperationException($"{name} could not be made writable (error {Marshal.GetLastPInvokeError()}).");
        }

        jump.CopyTo(new Span<byte>((void*)address, PatchLength));
        VirtualProtect(address, PatchLength, protection, out _);
        FlushInstructionCache(GetCurrentProcess(), address, PatchLength);
    }

    [UnmanagedCallersOnly]
    private static nint OnSetForegroundWindow(nint window, nint unused)
    {
        Record("SetForegroundWindow", window);
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint OnSetActiveWindow(nint window, nint unused)
    {
        Record("SetActiveWindow", window);
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint OnSetFocus(nint window, nint unused)
    {
        Record("SetFocus", window);
        return 0;
    }

    [UnmanagedCallersOnly]
    private static nint OnSwitchToThisWindow(nint window, nint unused)
    {
        Record("SwitchToThisWindow", window);
        return 0;
    }

    // Called by Windows on the UI thread before it activates a window of that thread or gives one
    // the keyboard focus. A result other than zero refuses it.
    [UnmanagedCallersOnly]
    private static nint OnCbt(int code, nint wParam, nint lParam)
    {
        if (code == HcbtActivate)
        {
            Record("activation refused", wParam);
            return 1;
        }

        if (code == HcbtSetFocus && wParam != 0)
        {
            Record("focus refused", wParam);
            return 1;
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial nint SetWindowsHookEx(int hook, delegate* unmanaged<int, nint, nint, nint> procedure, nint module, uint thread);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(nint address, nuint size, uint protection, out uint oldProtection);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushInstructionCache(nint process, nint address, nuint size);

    // Only for the self-test.
    [LibraryImport("user32.dll")]
    private static partial nint SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint SetActiveWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint window);

    [LibraryImport("user32.dll")]
    private static partial void SwitchToThisWindow(nint window, int altTab);
}

/// <summary>One attempt to activate a window of this process, or to give it the keyboard focus, that was refused.</summary>
/// <param name="At">Seconds since the tool started.</param>
/// <param name="What">The function that was called, or what the hook refused.</param>
/// <param name="Step">What the checks were doing.</param>
internal readonly record struct GuardEvent(double At, string What, nint Window, string WindowClass, string Step);
