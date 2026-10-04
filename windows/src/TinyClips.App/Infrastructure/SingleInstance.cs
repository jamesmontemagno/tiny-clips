using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>
/// Keeps Tiny Clips to one process per user. A second launch (Start menu, "Open with", the
/// startup task, or the executable itself) hands its activation to the process that is already
/// running and exits, instead of starting another tray icon that competes for the global hotkeys.
/// </summary>
internal static class SingleInstance
{
    private const string Key = "TinyClips.SingleInstance";

    // A running instance that cannot accept the activation within this time is hung. Starting a
    // second copy next to it would only add a second tray icon without working hotkeys, so the
    // new process still exits.
    private static readonly TimeSpan RedirectTimeout = TimeSpan.FromSeconds(10);

    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<AppActivationArguments> Pending = new();
    private static Action<AppActivationArguments>? _handler;

    /// <summary>
    /// Registers this process as the single instance, or forwards its activation to the one that
    /// already is. Returns <see langword="true"/> when the activation was forwarded and the
    /// caller must exit without starting the app.
    /// </summary>
    public static bool TryRedirectToRunningInstance()
    {
        try
        {
            var current = AppInstance.GetCurrent();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var owner = AppInstance.FindOrRegisterForKey(Key);
                if (owner.IsCurrent)
                {
                    current.Activated += OnActivated;
                    return false;
                }

                try
                {
                    // This process was just started by the user, so it may hand the right to come
                    // to the foreground to the instance that will show the window.
                    AllowSetForegroundWindow(owner.ProcessId);
                    var activation = current.GetActivatedEventArgs();
                    var redirect = Task.Run(() => owner.RedirectActivationToAsync(activation).AsTask());
                    if (!redirect.Wait(RedirectTimeout))
                    {
                        CrashDiagnostics.Log(
                            nameof(SingleInstance),
                            new TimeoutException($"The running Tiny Clips instance (pid {owner.ProcessId}) did not accept the activation within {RedirectTimeout.TotalSeconds:0} s."),
                            handled: true);
                    }

                    return true;
                }
                catch (Exception ex) when (attempt == 0)
                {
                    // The owner exited between the lookup and the hand-off; try to take its place.
                    CrashDiagnostics.Log(nameof(SingleInstance), ex, handled: true);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            // Without the app-lifecycle API the app can still run; it just is not single-instance.
            CrashDiagnostics.Log(nameof(SingleInstance), ex, handled: true);
            return false;
        }
    }

    /// <summary>
    /// Sets the callback that receives activations forwarded by later launches, and delivers any
    /// that arrived before the app was ready. The callback runs on a background thread.
    /// </summary>
    public static void SetActivationHandler(Action<AppActivationArguments> handler)
    {
        lock (Gate)
        {
            _handler = handler;
        }

        while (Pending.TryDequeue(out var activation))
        {
            handler(activation);
        }
    }

    /// <summary>
    /// Gives up the single-instance registration so a launch during shutdown starts a fresh
    /// process instead of being forwarded to one that is about to exit.
    /// </summary>
    public static void Release()
    {
        try
        {
            lock (Gate)
            {
                _handler = null;
            }

            AppInstance.GetCurrent().UnregisterKey();
        }
        catch (Exception ex)
        {
            CrashDiagnostics.Log(nameof(SingleInstance), ex, handled: true);
        }
    }

    private static void OnActivated(object? sender, AppActivationArguments activation)
    {
        Action<AppActivationArguments>? handler;
        lock (Gate)
        {
            handler = _handler;
            if (handler is null)
            {
                Pending.Enqueue(activation);
                return;
            }
        }

        handler(activation);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
