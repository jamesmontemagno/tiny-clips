using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Preview;
using TinyClips.Core.Tests;

namespace TinyClips.App.Tests;

/// <summary>
/// What a test of the Studio editor's view model needs: real projects in a store on a temp
/// folder, a preview that does what it is told, and a UI thread and a clock the test runs by
/// hand. Nothing here sleeps, and nothing needs a window.
/// </summary>
public abstract class StudioViewModelTestBase : IDisposable
{
    protected const int Precision = 9;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsAppTests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly StudioTestPreviews _previews = new();
    private readonly IStudioProjectStore _store;

    // What the view model and its session posted to the UI thread. A test runs it.
    private readonly List<Action> _posted = [];

    protected StudioViewModelTestBase()
    {
        Projects = new StudioProjectStore(Path.Combine(_directory, "Projects"), _time);
        _store = DispatchProxy.Create<IStudioProjectStore, CountingStore>();
        ((CountingStore)_store).Inner = Projects;
    }

    /// <summary>The store itself, for writing the project a test opens and for reading it afterwards.</summary>
    protected StudioProjectStore Projects { get; }

    /// <summary>How often the editor saved the project.</summary>
    protected int Saves => ((CountingStore)_store).CallsTo(nameof(IStudioProjectStore.Save));

    /// <summary>How often the editor wrote whether the project is kept, which it writes at once.</summary>
    protected int KeepWrites => ((CountingStore)_store).CallsTo(nameof(IStudioProjectStore.SetKeepSources));

    /// <summary>The preview of the project that was opened last.</summary>
    protected StudioTestPreview Preview => _previews.Opened[^1];

    /// <summary>What the view model handed to screen readers.</summary>
    protected List<string> Announcements { get; } = [];

    /// <summary>
    /// A finished recording ten seconds long, with a camera unless told otherwise, as the
    /// recorder leaves it. <paramref name="change"/> writes into it what a test needs to find.
    /// </summary>
    protected string CreateProject(Func<StudioProject, StudioProject>? change = null, bool camera = true)
    {
        var paths = Projects.BeginRecording();
        Projects.CompleteRecording(paths.ProjectId, new StudioProjectCreationRequest(
            "Recording",
            new StudioRecordingSourceInfo(1920, 1080, 10),
            camera ? new StudioCameraSourceInfo(1280, 720, 10) : null,
            StudioAnchor.BottomRight,
            new StudioClickOverlay(),
            false,
            "1.9.0"));
        File.WriteAllBytes(paths.ScreenPath, [1, 2, 3]);
        if (camera)
        {
            File.WriteAllBytes(paths.CameraPath!, [4, 5, 6]);
        }

        if (change is not null)
        {
            Projects.Save(change(Projects.Load(paths.ProjectId)));
        }

        return paths.ProjectId;
    }

    /// <summary>
    /// Opens a project as the Studio window does: the view model is made, the window's controls
    /// are bound to it while it still says it is loading, and then the project is read. A test
    /// that opens a project which cannot be shown says so.
    /// </summary>
    protected async Task<StudioViewModel> OpenAsync(string projectId, StudioBoundControls? controls = null, bool expectReady = true)
    {
        var viewModel = new StudioViewModel(
            projectId,
            _store,
            _previews,
            new StudioTestExporter(),
            new CaptureSettings(new StudioTestSettings()),
            new StudioTestVideoNames(Path.Combine(_directory, "Videos")),
            Post,
            canFindPeople: true,
            _time);
        viewModel.Announced += (_, e) => Announcements.Add(e.Message);
        controls?.Attach(viewModel);

        var load = viewModel.LoadAsync();
        Pump();
        await load.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Pump();
        Assert.True(viewModel.IsReady == expectReady, expectReady ? viewModel.UnavailableMessage : "the project opened");
        return viewModel;
    }

    /// <summary>Closes the editor as its window does, which saves what is not saved yet.</summary>
    protected async Task CloseAsync(StudioViewModel viewModel)
    {
        var close = viewModel.CloseAsync(deleteProject: false);
        Pump();
        await close.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Pump();
    }

    /// <summary>Runs what was posted to the UI thread, including anything that posts in its turn.</summary>
    protected void Pump()
    {
        while (true)
        {
            Action action;
            lock (_posted)
            {
                if (_posted.Count == 0)
                {
                    return;
                }

                action = _posted[0];
                _posted.RemoveAt(0);
            }

            action();
        }
    }

    /// <summary>Lets time pass, which fires the timers that are due, and runs what they posted.</summary>
    protected void Advance(TimeSpan time)
    {
        _time.Advance(time);
        Pump();
    }

    private void Post(Action action)
    {
        lock (_posted)
        {
            _posted.Add(action);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder that stays behind fails no test.
        }

        GC.SuppressFinalize(this);
    }

    private sealed class StudioTestSettings : ISettingsService
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value;
    }

    private sealed class StudioTestVideoNames(string folder) : IClipStorageService
    {
        private int _asked;

        public string FileExtensionFor(CaptureType type) => ".mp4";

        public string GenerateFilePath(CaptureType type, string? fileExtension = null, string? stemSuffix = null) =>
            Path.Combine(folder, $"Saved video {Interlocked.Increment(ref _asked)}.mp4");

        public string OutputDirectory(CaptureType type) => folder;
    }

    private sealed class StudioTestExporter : IStudioExportService
    {
        public Task ExportAsync(
            StudioProject project,
            StudioEvents events,
            StudioProjectPaths paths,
            string outputPath,
            VideoCodec codec,
            IProgress<double>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task WritePosterAsync(
            StudioProject project,
            StudioEvents events,
            StudioProjectPaths paths,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StudioTestPreviews : IStudioPreviewFactory
    {
        public List<StudioTestPreview> Opened { get; } = [];

        public Task<IStudioPreview> OpenAsync(
            StudioProject project,
            StudioEvents events,
            StudioProjectPaths paths,
            CancellationToken cancellationToken = default)
        {
            var preview = new StudioTestPreview();
            Opened.Add(preview);
            return Task.FromResult<IStudioPreview>(preview);
        }
    }
}

/// <summary>A preview that keeps what it is told, and says where it is only when a test tells it to.</summary>
public sealed class StudioTestPreview : IStudioPreview
{
    public event EventHandler? PositionChanged;

    public event EventHandler? IsPlayingChanged;

#pragma warning disable CS0067 // The contract has it. No test makes a preview fail.
    public event EventHandler<StudioPreviewFailedEventArgs>? Failed;
#pragma warning restore CS0067

    public double Position { get; private set; }

    public bool IsPlaying { get; private set; }

    /// <summary>The project the editor handed over when it opened, before anything could change it.</summary>
    public StudioProject? OpenedWith { get; private set; }

    /// <summary>
    /// Every project the editor handed over after that: one for each change to the project, so
    /// an empty list says that nothing was edited.
    /// </summary>
    public List<StudioProject> Updates { get; } = [];

    public void UpdateProject(StudioProject project)
    {
        if (OpenedWith is null)
        {
            OpenedWith = project;
        }
        else
        {
            Updates.Add(project);
        }
    }

    public void Play() => IsPlaying = true;

    public void Pause() => IsPlaying = false;

    public void Seek(double sourceTime) => Position = sourceTime;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The playhead has got somewhere, as a playing preview says thirty times a second.</summary>
    public void RaisePosition(double position)
    {
        Position = position;
        PositionChanged?.Invoke(null, EventArgs.Empty);
    }

    public void RaiseIsPlaying(bool isPlaying)
    {
        IsPlaying = isPlaying;
        IsPlayingChanged?.Invoke(null, EventArgs.Empty);
    }
}

/// <summary>
/// One value of the view model that a control of the Studio window is bound to both ways:
/// which property, which kind of control and which of its properties, and for a slider the
/// range the markup gives it.
/// </summary>
public sealed record StudioTwoWayBinding(string Property, string Control, string Target, double? Minimum, double? Maximum)
{
    public override string ToString() => $"{Property} ({Control}.{Target})";
}

/// <summary>
/// Stands in for the controls of the Studio window that a value of the view model is bound to
/// both ways, as far as the binding goes. Each holds a value, is told the view model's when the
/// view model says that it changed, and hands back what it then holds.
/// </summary>
/// <remarks>
/// <para>
/// The bindings are read from the markup itself: every .xaml file of the Studio window and of
/// its controls is in this assembly as text. So a value that is bound both ways tomorrow, and
/// written as today's are (<c>{x:Bind ViewModel.X, Mode=TwoWay}</c>), is in these tests without
/// being added to them. One that is written another way fails the first test, which says what
/// to teach the reader: another kind of binding, a path that is not a value of the view model,
/// or a file that makes two-way the default.
/// </para>
/// <para>
/// What this copies from a compiled binding (<c>x:Bind</c> with <c>Mode=TwoWay</c>): a control is
/// given the value of its property when the view model raises <c>PropertyChanged</c> for that
/// property, and every control is for an empty name. When that changes what the control holds,
/// the control's value is written back through the property's setter, although the binding
/// itself put it there. A control that is told the value it holds already hands nothing back.
/// The first time the real controls are given their values, while the window is built, they
/// hand nothing back; here they do, which asks more of the view model and not less.
/// </para>
/// <para>
/// What it leaves out, all of it read from the controls' code and the framework's and none of
/// it checked here. A slider row (<c>StudioSliderRow</c>) holds the value it is told as it is,
/// also one outside its range: its slider pulls such a value inside the range, and the row does
/// not pass that on; only the user moving the slider writes to the row's value. A group of
/// radio buttons holds any index it is told. A drop-down list does not: told an index it has no
/// item for, it puts the old one back, or throws. No project asks that of one, because a choice
/// that a project file does not spell as this version does is read as the default one.
/// </para>
/// <para>
/// A group of radio buttons that is clicked hands back one thing more than a stand-in does: no
/// choice first, as the button that was chosen is unchecked, and then the choice. One test
/// hands back that pair.
/// </para>
/// <para>
/// A control here takes a value it is shown also while it is handing one back. A slider row
/// does too. A group of radio buttons then takes the number and does not move its dot. The view
/// model does not lean on either: where the editor did not take what a control asked for, the
/// view model tells the control again a moment later, and the tests of that ask where nothing
/// else tells the control.
/// </para>
/// </remarks>
public sealed partial class StudioBoundControls
{
    // A setter that edits makes the view model say that everything changed, inside which the
    // controls hand back again. That ends after a step or two, or the bindings never settle.
    private const int DeepestNesting = 40;

    // The markup files are in the assembly under this prefix, by the project file.
    private const string MarkupPrefix = "StudioMarkup.";

    // By the binding itself and not by what it says: two controls of one kind may be bound to
    // one value, and each holds its own.
    private readonly Dictionary<StudioTwoWayBinding, object?> _held = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StudioTwoWayBinding, PropertyInfo> _properties = new(ReferenceEqualityComparer.Instance);
    private StudioViewModel? _viewModel;
    private int _nesting;

    public StudioBoundControls()
    {
        Bindings = ReadMarkup();
        foreach (var binding in Bindings)
        {
            var property = typeof(StudioViewModel).GetProperty(binding.Property, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"The markup binds {binding}, and the view model has no such public property.");
            if (property.GetMethod?.IsPublic != true || property.SetMethod?.IsPublic != true)
            {
                throw new InvalidOperationException($"The markup binds {binding} both ways, and the property cannot be read and written from outside.");
            }

            _properties[binding] = property;

            // What a control holds before anything is bound to it: a slider row 0, a check box
            // or a message bar false, a list no choice.
            _held[binding] = property.PropertyType == typeof(double) ? 0d
                : property.PropertyType == typeof(bool) ? false
                : property.PropertyType == typeof(int) ? -1
                : throw new InvalidOperationException($"{binding} is a {property.PropertyType.Name}. These tests know sliders, check boxes and lists; teach them the new control.");
        }
    }

    /// <summary>Every value the markup binds both ways, in the order of the markup.</summary>
    public IReadOnlyList<StudioTwoWayBinding> Bindings { get; }

    /// <summary>What the controls handed back so far: the property and the value, in order.</summary>
    public List<(string Property, object? Value)> HandedBack { get; } = [];

    /// <summary>The markup files that were read: every .xaml file of the Studio window and of its controls, by name.</summary>
    public static IReadOnlyList<string> MarkupFiles { get; } =
    [
        .. typeof(StudioBoundControls).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(MarkupPrefix, StringComparison.Ordinal))
            .Select(name => name[MarkupPrefix.Length..])
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>How many times <c>Mode=TwoWay</c> is written in the markup, whatever it is written in.</summary>
    public static int CountTwoWayInMarkup() => MarkupFiles.Sum(file => TwoWayMode().Count(ReadMarkupFile(file)));

    /// <summary>The markup files that make a binding two-way without saying so on the binding, which the reader would miss.</summary>
    public static IReadOnlyList<string> FilesWithADefaultBindMode() =>
        [.. MarkupFiles.Where(file => ReadMarkupFile(file).Contains("DefaultBindMode", StringComparison.Ordinal))];

    /// <summary>Binds the controls, which shows each of them its value, as building the window does.</summary>
    public void Attach(StudioViewModel viewModel)
    {
        _viewModel = viewModel;
        viewModel.PropertyChanged += OnPropertyChanged;
        Show(null);
    }

    /// <summary>The binding of a property. A property that two controls are bound to has two; this is the first.</summary>
    public StudioTwoWayBinding Find(string property) =>
        Bindings.FirstOrDefault(binding => binding.Property == property)
        ?? throw new InvalidOperationException($"The markup binds nothing both ways to {property}.");

    /// <summary>What the view model has for a binding.</summary>
    public object? Shown(StudioTwoWayBinding binding) => _properties[binding].GetValue(ViewModel);

    /// <summary>What the control of a binding holds.</summary>
    public object? Held(StudioTwoWayBinding binding) => _held[binding];

    /// <summary>The user moves a slider, ticks a box or chooses from a list: the control holds the new value and writes it to the view model.</summary>
    public void Move(string property, object value) => Move(Find(property), value);

    public void Move(StudioTwoWayBinding binding, object value)
    {
        _held[binding] = value;
        HandBack(binding);
    }

    /// <summary>Every control writes what it holds to the view model once more.</summary>
    public void HandBackEverything()
    {
        foreach (var binding in Bindings)
        {
            HandBack(binding);
        }
    }

    /// <summary>The controls that hold something else than the view model has, which at rest is none.</summary>
    public IReadOnlyList<string> OutOfStep() =>
        [.. Bindings.Where(binding => !Equals(Held(binding), Shown(binding))).Select(binding => $"{binding} holds {Held(binding)} and the view model has {Shown(binding)}")];

    private StudioViewModel ViewModel => _viewModel ?? throw new InvalidOperationException("The controls are not bound yet.");

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e) => Show(e.PropertyName);

    private void Show(string? propertyName)
    {
        if (++_nesting > DeepestNesting)
        {
            throw new InvalidOperationException(
                $"The bindings do not settle: what a control hands back changes what it is shown, {DeepestNesting} times over. Last handed back: {string.Join(", ", HandedBack.TakeLast(6))}.");
        }

        try
        {
            foreach (var binding in Bindings)
            {
                if (string.IsNullOrEmpty(propertyName) || binding.Property == propertyName)
                {
                    var shown = Shown(binding);
                    if (!Equals(shown, _held[binding]))
                    {
                        _held[binding] = shown;
                        HandBack(binding);
                    }
                }
            }
        }
        finally
        {
            _nesting--;
        }
    }

    private void HandBack(StudioTwoWayBinding binding)
    {
        var value = _held[binding];
        HandedBack.Add((binding.Property, value));
        _properties[binding].SetValue(ViewModel, value);
    }

    private static List<StudioTwoWayBinding> ReadMarkup()
    {
        var bindings = new List<StudioTwoWayBinding>();
        foreach (var file in MarkupFiles)
        {
            foreach (var element in XDocument.Parse(ReadMarkupFile(file)).Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    var match = Bind().Match(attribute.Value.Trim());
                    if (!match.Success || !TwoWayMode().IsMatch(match.Groups["rest"].Value))
                    {
                        continue;
                    }

                    var path = match.Groups["path"].Value;
                    const string Root = "ViewModel.";
                    if (!path.StartsWith(Root, StringComparison.Ordinal) || path.IndexOf('.', Root.Length) >= 0)
                    {
                        throw new InvalidOperationException(
                            $"{file} binds {element.Name.LocalName}.{attribute.Name.LocalName} both ways to {path}. These tests know values of the view model; teach them this one.");
                    }

                    bindings.Add(new StudioTwoWayBinding(
                        path[Root.Length..],
                        element.Name.LocalName,
                        attribute.Name.LocalName,
                        Number(element, "Minimum"),
                        Number(element, "Maximum")));
                }
            }
        }

        return bindings;
    }

    private static double? Number(XElement element, string name) =>
        element.Attribute(name) is { } attribute
        && double.TryParse(attribute.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private static string ReadMarkupFile(string name)
    {
        using var stream = typeof(StudioBoundControls).Assembly.GetManifestResourceStream(MarkupPrefix + name)
            ?? throw new InvalidOperationException($"{name} is not in the test assembly. The project file puts it there.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^\{x:Bind\s+(?<path>[^,}\s]+)\s*,(?<rest>[^}]*)\}$", RegexOptions.CultureInvariant)]
    private static partial Regex Bind();

    [GeneratedRegex(@"\bMode\s*=\s*TwoWay\b", RegexOptions.CultureInvariant)]
    private static partial Regex TwoWayMode();
}
