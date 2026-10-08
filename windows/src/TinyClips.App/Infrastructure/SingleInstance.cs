using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>
/// What a later launch handed to the running app: its kind, and for a file activation the paths
/// of its files. Read from the activation while the launch that forwarded it is still running.
/// </summary>
internal readonly record struct ForwardedActivation(ExtendedActivationKind Kind, IReadOnlyList<string?> FilePaths);

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
    private static readonly ConcurrentQueue<ForwardedActivation> Pending = new();
    private static Action<ForwardedActivation>? _handler;

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
            const int attempts = 3;
            for (var attempt = 1; attempt <= attempts; attempt++)
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
                catch (Exception ex)
                {
                    // Usually the owner exited between the lookup and the hand-off; the next
                    // lookup then makes this process the owner.
                    CrashDiagnostics.Log(nameof(SingleInstance), ex, handled: true);
                    Thread.Sleep(250);
                }
            }

            // Another instance still owns the key and could not be reached. Exit rather than start
            // an unregistered copy next to it.
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
    public static void SetActivationHandler(Action<ForwardedActivation> handler)
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
        // The activation lives in the process that forwarded it, which exits as soon as this
        // returns. Whatever is read from it later, on another thread, can find it gone ("The
        // RPC server is unavailable"), and the launch then does nothing. So it is read here.
        ForwardedActivation forwarded;
        try
        {
            forwarded = Read(activation);
        }
        catch (Exception ex)
        {
            CrashDiagnostics.Log(nameof(SingleInstance), ex, handled: true);
            return;
        }

        Deliver(forwarded);
    }

    /// <summary>The kind of an activation and, for a file activation, the paths of its files.</summary>
    internal static ForwardedActivation Read(AppActivationArguments? activation)
    {
        if (activation is null)
        {
            return new ForwardedActivation(ExtendedActivationKind.Launch, []);
        }

        var kind = activation.Kind;
        var paths = kind == ExtendedActivationKind.File && activation.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs
            ? fileArgs.Files.Select(static item => (item as Windows.Storage.IStorageItem)?.Path).ToArray()
            : [];
        return new ForwardedActivation(kind, paths);
    }

    private static void Deliver(ForwardedActivation activation)
    {
        Action<ForwardedActivation>? handler;
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
