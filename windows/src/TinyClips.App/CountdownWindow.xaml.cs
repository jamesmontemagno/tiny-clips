using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using TinyClips.Core.Capture;
using Windows.Graphics;
using WinRT.Interop;

namespace TinyClips.App;

/// <summary>
/// A borderless, always-on-top countdown card shown before a capture begins. Counts down
/// from the requested number of seconds and completes once it reaches zero. The window is
/// clipped to a rounded shape (matching the card) and excluded from screen capture so it never
/// appears in a recording, and it hides itself before the countdown task completes so recording
/// starts on a clean frame.
/// </summary>
public sealed partial class CountdownWindow : Window
{
    private const int SizeDip = 132;
    private const int CornerRadiusDip = 8;
    private const uint WdaExcludeFromCapture = 0x11;

    private readonly DispatcherQueueTimer _timer;
    private readonly TaskCompletionSource<bool> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _remaining;

    private CountdownWindow(int seconds)
    {
        InitializeComponent();

        _remaining = Math.Max(1, seconds);
        CountText.Text = _remaining.ToString();
        RootBorder.Opacity = 0;

        ConfigurePresenter();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// Shows a countdown overlay and returns when it finishes. If
    /// <paramref name="cancellationToken"/> is cancelled the countdown is immediately
    /// dismissed and the returned task transitions to the Cancelled state, allowing the
    /// caller to abort the pre-recording flow cleanly.
    /// </summary>
    public static Task RunAsync(int seconds, MonitorInfo? monitor = null, CancellationToken cancellationToken = default)
    {
        var window = new CountdownWindow(seconds);
        window.Activate();

        // Resize/position and clip the window to a rounded square only AFTER it has been
        // shown. Applying SetWindowRgn before the first present leaves the surface blank,
        // which is why the countdown stopped appearing.
        window.CenterOnMonitor(monitor);
        window.AnimateFade(window.RootBorder, 1, 180).Begin();
        window.AnimateCountText(finalSecond: window._remaining == 1);
        window._timer.Start();

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                window._timer.Stop();
                if (window._completed.TrySetCanceled(cancellationToken))
                {
                    try { window.Close(); } catch { }
                }
            });
        }

        return window._completed.Task;
    }

    private async void OnTick(DispatcherQueueTimer sender, object args)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;

            // Hide immediately so the window is gone from the very first recorded frame,
            // then give the compositor a beat before signalling completion.
            await AnimateFadeAsync(RootBorder, 0, 140);
            AppWindow.Hide();
            await Task.Delay(80);
            _completed.TrySetResult(true);
            Close();
            return;
        }

        await AnimateCountTransitionAsync(finalSecond: _remaining == 1);
    }

    private void AnimateCountText(bool finalSecond)
    {
        ApplyCountVisualState(finalSecond);

        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateAnimation(CountText, "Opacity", 1, 220));
        storyboard.Children.Add(CreateAnimation(CountScale, "ScaleX", 1, finalSecond ? 320 : 220));
        storyboard.Children.Add(CreateAnimation(CountScale, "ScaleY", 1, finalSecond ? 320 : 220));
        storyboard.Begin();
    }

    private async Task AnimateCountTransitionAsync(bool finalSecond)
    {
        await AnimateCountOutAsync();
        AnimateCountText(finalSecond);
    }

    private Task AnimateCountOutAsync()
    {
        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateAnimation(CountText, "Opacity", 0, 90));
        storyboard.Children.Add(CreateAnimation(CountScale, "ScaleX", 0.94, 90));
        storyboard.Children.Add(CreateAnimation(CountScale, "ScaleY", 0.94, 90));
        return RunStoryboardAsync(storyboard);
    }

    private void ApplyCountVisualState(bool finalSecond)
    {
        CountText.Text = _remaining.ToString();
        CountText.FontSize = finalSecond ? 78 : 64;
        CountText.FontWeight = finalSecond ? FontWeights.Bold : FontWeights.SemiBold;
        CountText.Opacity = 0;
        CountScale.ScaleX = finalSecond ? 1.35 : 1.18;
        CountScale.ScaleY = finalSecond ? 1.35 : 1.18;
    }

    private Task AnimateFadeAsync(UIElement target, double to, int milliseconds)
    {
        return RunStoryboardAsync(AnimateFade(target, to, milliseconds));
    }

    private static Task RunStoryboardAsync(Storyboard storyboard)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        storyboard.Completed += (_, _) => completion.TrySetResult(true);
        storyboard.Begin();
        return completion.Task;
    }

    private Storyboard AnimateFade(UIElement target, double to, int milliseconds)
    {
        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateAnimation(target, "Opacity", to, milliseconds));
        return storyboard;
    }

    private static DoubleAnimation CreateAnimation(DependencyObject target, string property, double to, int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private void ConfigurePresenter()
    {
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        AppWindow.IsShownInSwitchers = false;
    }

    private void CenterOnMonitor(MonitorInfo? monitor)
    {
        var area = monitor is { WorkAreaWidth: > 0, WorkAreaHeight: > 0 }
            ? new RectInt32(monitor.WorkAreaX, monitor.WorkAreaY, monitor.WorkAreaWidth, monitor.WorkAreaHeight)
            : DisplayArea.Primary?.WorkArea;

        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetScale(hwnd);
        var size = (int)Math.Round(SizeDip * scale);
        AppWindow.Resize(new SizeInt32(size, size));

        if (area is { } work)
        {
            var x = work.X + ((work.Width - size) / 2);
            var y = work.Y + ((work.Height - size) / 2);
            AppWindow.Move(new PointInt32(x, y));
        }

        // Clip the square window to a rounded square (matching the card) and keep it
        // out of recordings.
        var radius = (int)Math.Round(CornerRadiusDip * scale);
        var region = CreateRoundRectRgn(0, 0, size + 1, size + 1, radius, radius);
        SetWindowRgn(hwnd, region, true);
        SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
    }

    private static double GetScale(nint hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint hWnd, nint hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);
}
