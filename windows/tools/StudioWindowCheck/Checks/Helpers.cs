using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyClips.App.Controls.Studio;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Capture;
using TinyClips.Tools.StudioWindowCheck.Host;
using Windows.Foundation;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// What several groups of checks need: driving a control, reading a control, and reading pixels.
internal sealed partial class WindowChecks
{
    // How far a colour on screen may be from the colour that was asked for, per channel.
    private const double ColorTolerance = 10;

    // ---------------------------------------------------------------------------------------
    // Driving
    // ---------------------------------------------------------------------------------------

    private static bool Invoke(Editor editor, string automationId) => Find(editor, automationId)?.Invoke() ?? false;

    /// <summary>Sets a slider through the range pattern, as a screen reader does.</summary>
    private static bool SetSlider(Editor editor, string automationId, double value) => Find(editor, automationId)?.SetRange(value) ?? false;

    private static double SliderValue(Editor editor, string automationId) => Find(editor, automationId)?.Range?.Value ?? double.NaN;

    private static string NameOf(Editor editor, string automationId, double seconds = 2) => Find(editor, automationId, seconds)?.Name ?? string.Empty;

    /// <summary>
    /// Chooses an item of a combo box by setting the control's selected index, on the UI thread.
    /// The list is never opened: an open list is a window of its own, in front of other windows.
    /// </summary>
    private bool SetCombo(Editor editor, string automationId, int index) => OnUi(() =>
    {
        if (Descendant<ComboBox>(editor.Window.Content, automationId) is not { } combo || index >= combo.Items.Count)
        {
            return false;
        }

        combo.SelectedIndex = index;
        return true;
    });

    /// <summary>
    /// What the window does with a key it is handed: its own method for that, which the window's
    /// key handler calls once it has mapped the key. It asks StudioShortcuts what the key means in
    /// the window as it is now, and runs that. The key itself is not pressed.
    /// </summary>
    private StudioShortcutAction Key(Editor editor, StudioShortcutKey key, bool control = false) =>
        OnUi(() => editor.Window.RunShortcut(key, isControlDown: control, isShiftDown: false, isAltDown: false, isRepeat: false));

    /// <summary>Presses the window's own close button through UI Automation: the Close button of its title bar.</summary>
    private static bool PressClose(Editor editor)
    {
        var close = editor.Root.Children().SelectMany(child => child.Children()).FirstOrDefault(child => child.Id == "Close" && child.ControlType == ControlTypeNames.Button);
        return close?.Invoke() ?? editor.Root.CloseWindow();
    }

    /// <summary>The question the window is asking, as soon as it is in the tree. A ContentDialog is a window inside the window.</summary>
    private static UiaElement? DialogElement(Editor editor) =>
        editor.Root.FindAll(ControlTypeNames.Window).FirstOrDefault(window => window.Find("PrimaryButton") is not null);

    private static bool HasDialog(Editor editor) => DialogElement(editor) is not null;

    /// <summary>The question the window is asking, waited for. Null when no question appeared.</summary>
    private static UiaElement? Dialog(Editor editor, double seconds = 3) =>
        Until(() => DialogElement(editor), found => found is not null, seconds);

    /// <summary>
    /// Tells the window that it is the active window, or that it no longer is, as when the user
    /// comes back to it or goes on to another window. See <see cref="Native.TellActivation"/>.
    /// </summary>
    private void TellActive(Editor editor, bool isActive) => OnUi(() => Native.TellActivation(editor.Handle, isActive));

    /// <summary>The automation id, or failing that the name, of the element that has the keyboard focus inside the window, as XAML sees it.</summary>
    private string FocusedId(Editor editor) => OnUi(() =>
    {
        if (editor.Window.Content?.XamlRoot is not { } root || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is not DependencyObject focused)
        {
            return string.Empty;
        }

        // A part of a control's template, such as a button of a dialog, has only the name its template gave it.
        var id = AutomationProperties.GetAutomationId(focused);
        return id.Length > 0 ? id : (focused as FrameworkElement)?.Name ?? string.Empty;
    });

    /// <summary>
    /// Puts the keyboard focus on a control, as XAML sees it: the window does not have the
    /// keyboard, and its request for it is refused like every other. Returns where the focus is afterwards.
    /// </summary>
    private string FocusOn(Editor editor, string automationId)
    {
        OnUi(() =>
        {
            Descendant<Microsoft.UI.Xaml.Controls.Control>(editor.Window.Content, automationId)?.Focus(FocusState.Keyboard);
        });
        return FocusedId(editor);
    }

    /// <summary>Waits until the window is gone.</summary>
    private static bool WindowGone(Editor editor, double seconds = 5) => Until(() => !Native.Exists(editor.Handle), gone => gone, seconds);

    /// <summary>Applies an edit to the project the checks expect, the way the editor should have applied it.</summary>
    private static void Expect(Editor editor, Func<StudioProject, StudioProject> edit) => editor.Expected = edit(editor.Expected);

    private static StudioProject WithScene(StudioProject project, Func<StudioScene, StudioScene> edit) =>
        project with { Scenes = [edit(project.Scenes[0])] };

    /// <summary>How often the process asked for the keyboard focus or for activation since a moment, and was refused.</summary>
    private static int FocusRequestsSince(double moment) => ForegroundGuard.Events().Count(e => e.At >= moment);

    // ---------------------------------------------------------------------------------------
    // The window's own elements, read on the UI thread
    // ---------------------------------------------------------------------------------------

    /// <summary>The element with an automation id among what a window shows. UI thread.</summary>
    private static T? Descendant<T>(DependencyObject? root, string automationId)
        where T : FrameworkElement
    {
        if (root is null)
        {
            return null;
        }

        if (root is T match && AutomationProperties.GetAutomationId(match) == automationId)
        {
            return match;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            if (Descendant<T>(VisualTreeHelper.GetChild(root, index), automationId) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Where the overlay's camera handle is, in pixels of a screenshot, or null when it is hidden.
    /// The handle draws nothing until the pointer is over it, so it cannot be read from pixels:
    /// its place comes from the XAML tree.
    /// </summary>
    private StudioFrameRect? HandleRect(Editor editor, Shot shot)
    {
        var inContent = OnUi<Rect?>(() =>
        {
            if (Descendant<StudioCanvasHost>(editor.Window.Content, "StudioPreview") is not { } canvasHost
                || canvasHost.Children.OfType<StudioPreviewOverlay>().FirstOrDefault() is not { } overlay
                || overlay.FindName("BubbleHandle") is not FrameworkElement { Visibility: Visibility.Visible } handle)
            {
                return null;
            }

            return handle.TransformToVisual(editor.Window.Content).TransformBounds(new Rect(0, 0, handle.ActualWidth, handle.ActualHeight));
        });
        return inContent is { } rect ? InShot(editor, shot, rect) : null;
    }

    /// <summary>The numbers behind the handle's place, in effective pixels, for the detail of a check that did not hold. UI thread.</summary>
    private string HandleNumbers(Editor editor) => OnUi(() =>
    {
        if (Descendant<StudioCanvasHost>(editor.Window.Content, "StudioPreview") is not { } host
            || host.Children.OfType<StudioPreviewOverlay>().FirstOrDefault() is not { } overlay
            || overlay.FindName("BubbleHandle") is not FrameworkElement handle)
        {
            return "no overlay";
        }

        var viewModel = editor.Window.ViewModel;
        var overlayAt = overlay.TransformToVisual(host).TransformPoint(default);
        var panelAt = host.Children[0].TransformToVisual(host).TransformPoint(default);
        var panel = (FrameworkElement)host.Children[0];
        return $"host {F(host.ActualWidth, "0.##")}x{F(host.ActualHeight, "0.##")}, canvas rect {host.CanvasRect}; "
            + $"panel {panel.GetType().Name} {F(panel.ActualWidth, "0.##")}x{F(panel.ActualHeight, "0.##")} at ({F(panelAt.X, "0.##")},{F(panelAt.Y, "0.##")}); "
            + $"overlay {F(overlay.ActualWidth, "0.##")}x{F(overlay.ActualHeight, "0.##")} at ({F(overlayAt.X, "0.##")},{F(overlayAt.Y, "0.##")}); "
            + $"handle Canvas.Left {F(Canvas.GetLeft(handle), "0.##")}, Canvas.Top {F(Canvas.GetTop(handle), "0.##")}, {F(handle.ActualWidth, "0.##")}x{F(handle.ActualHeight, "0.##")}; "
            + $"view model canvas {F(viewModel.CanvasWidth)}x{F(viewModel.CanvasHeight)}, bubble {(viewModel.BubbleRect is { } b ? R(b) : "none")}";
    });

    /// <summary>What the editor says about itself, for the detail of a check that did not hold. Read on the UI thread.</summary>
    private string StateOf(Editor editor) => OnUi(() =>
    {
        var viewModel = editor.Window.ViewModel;
        return $"ready {viewModel.IsReady}, editable {viewModel.IsEditable}, exporting {viewModel.IsExporting}, playing {viewModel.IsPlaying}, playhead {F(viewModel.Playhead, "0.####")}, error \"{viewModel.ErrorMessage}\"";
    });

    // ---------------------------------------------------------------------------------------
    // Pixels
    // ---------------------------------------------------------------------------------------

    private static Rgb Hex(string color) => new(
        int.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static bool Near(Rgb a, Rgb b, double tolerance = ColorTolerance) => a.R >= 0 && b.R >= 0 && a.Distance(b) <= tolerance;

    private static double Luma(Rgb color) => (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);

    /// <summary>The colour of the default gradient at a point of the canvas: from the first colour in the top left corner to the second in the bottom right.</summary>
    private static Rgb GradientAt(Rgb from, Rgb to, Box canvas, double x, double y)
    {
        double width = canvas.Width;
        double height = canvas.Height;
        var t = Math.Clamp((((x - canvas.X) * width) + ((y - canvas.Y) * height)) / ((width * width) + (height * height)), 0, 1);
        return new Rgb(from.R + ((to.R - from.R) * t), from.G + ((to.G - from.G) * t), from.B + ((to.B - from.B) * t));
    }

    /// <summary>Where the layout puts the screen recording and the camera, in pixels of a screenshot.</summary>
    private static (StudioFrameRect? Screen, StudioResolvedCamera? Camera) Layers(Box canvas, StudioProject project)
    {
        var frame = StudioLayoutResolver.Resolve(project, 0, canvas.Width, canvas.Height);
        StudioFrameRect? screen = frame.Screen is { } s ? Shift(s.Rect, canvas) : null;
        StudioResolvedCamera? camera = frame.Camera is { } c ? c with { Rect = Shift(c.Rect, canvas) } : null;
        return (screen, camera);
    }

    private static StudioFrameRect Shift(StudioFrameRect rect, Box canvas) => new(rect.X + canvas.X, rect.Y + canvas.Y, rect.Width, rect.Height);

    /// <summary>The smallest rectangle around the pixels of a box that differ between two screenshots, or null when none does.</summary>
    private static Box? DifferenceBox(Shot a, Shot b, Box within, int threshold = 24)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return null;
        }

        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = Math.Max(0, within.Y); y < Math.Min(a.Height, within.Bottom); y++)
        {
            var row = y * a.Width * 4;
            for (var x = Math.Max(0, within.X); x < Math.Min(a.Width, within.Right); x++)
            {
                var at = row + (x * 4);
                if (Math.Abs(a.Bgra[at] - b.Bgra[at]) > threshold
                    || Math.Abs(a.Bgra[at + 1] - b.Bgra[at + 1]) > threshold
                    || Math.Abs(a.Bgra[at + 2] - b.Bgra[at + 2]) > threshold)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        return right < 0 ? null : new Box(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>How many pixels of a box differ between two screenshots.</summary>
    private static int DifferenceCount(Shot a, Shot b, Box within, int threshold = 24)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return -1;
        }

        var count = 0;
        for (var y = Math.Max(0, within.Y); y < Math.Min(a.Height, within.Bottom); y++)
        {
            var row = y * a.Width * 4;
            for (var x = Math.Max(0, within.X); x < Math.Min(a.Width, within.Right); x++)
            {
                var at = row + (x * 4);
                if (Math.Abs(a.Bgra[at] - b.Bgra[at]) > threshold
                    || Math.Abs(a.Bgra[at + 1] - b.Bgra[at + 1]) > threshold
                    || Math.Abs(a.Bgra[at + 2] - b.Bgra[at + 2]) > threshold)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>A check's name as part of a file name: its first words, in small letters, with hyphens between them.</summary>
    private static string Slug(string name)
    {
        var slug = new System.Text.StringBuilder();
        foreach (var letter in name)
        {
            if (slug.Length >= 60)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(letter))
            {
                slug.Append(char.ToLowerInvariant(letter));
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().Trim('-');
    }

    /// <summary>
    /// Keeps a picture that a check did not hold on, in the out folder, so that a person can
    /// look at what the check saw. The name is the check's, the same in every run, so a later
    /// run replaces the picture. Returns what to add to the check's detail.
    /// </summary>
    /// <param name="notes">Lines to keep beside the picture, in a text file of the same name.</param>
    private string Kept(Shot? shot, string name, IReadOnlyList<string>? notes = null)
    {
        if (shot is null)
        {
            return string.Empty;
        }

        try
        {
            var file = $"failed-{name}.png";
            shot.Save(Path.Combine(_output, file));
            if (notes is not null)
            {
                File.WriteAllLines(Path.Combine(_output, $"failed-{name}.txt"), notes);
                return $"; the picture is kept as {file}, and what was read in it line by line as failed-{name}.txt";
            }

            return $"; the picture is kept as {file}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"; the picture could not be kept ({ex.Message})";
        }
    }

    private static string R(StudioFrameRect rect) => string.Create(CultureInfo.InvariantCulture, $"{rect.Width:0.#}x{rect.Height:0.#} at ({rect.X:0.#},{rect.Y:0.#})");

    private static double EdgeDistance(StudioFrameRect a, StudioFrameRect b) => Math.Max(
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)),
        Math.Max(Math.Abs((a.X + a.Width) - (b.X + b.Width)), Math.Abs((a.Y + a.Height) - (b.Y + b.Height))));
}
