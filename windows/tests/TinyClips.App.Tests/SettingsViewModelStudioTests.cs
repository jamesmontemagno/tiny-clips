using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.UI.Xaml;
using TinyClips.App;
using TinyClips.App.Services.Studio;
using TinyClips.App.Settings;
using TinyClips.App.ViewModels.Studio;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;

namespace TinyClips.App.Tests;

/// <summary>
/// Tiny Clips Studio's part of the Settings view model: the switch, the After recording choice,
/// the storage rules, and the list of recordings that only a project holds. The projects are
/// real ones, in a store on a temp folder.
/// </summary>
public sealed class SettingsViewModelStudioTests : IDisposable
{
    private static readonly byte[] Recording = [.. Enumerable.Range(0, 5000).Select(static value => (byte)(value % 251))];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsAppTests", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly MemorySettings _saved = new();
    private readonly CaptureSettings _settings;
    private readonly StudioProjectStore _projects;
    private readonly CountingStore _counted;
    private readonly IStudioProjectStore _store;
    private readonly StudioProjectTracker _tracker = new();
    private readonly StudioProjectCleanupService _cleanup;
    private readonly VideoNames _names;

    public SettingsViewModelStudioTests()
    {
        _settings = new CaptureSettings(_saved);
        _projects = new StudioProjectStore(ProjectsFolder, _clock);
        _store = DispatchProxy.Create<IStudioProjectStore, CountingStore>();
        _counted = (CountingStore)_store;
        _counted.Inner = _projects;
        _cleanup = new StudioProjectCleanupService(
            _store, _settings, _tracker, DispatchProxy.Create<IVideoRecordingService, NothingRecording>());
        _names = new VideoNames(VideosFolder);
    }

    private string ProjectsFolder => Path.Combine(_directory, "Projects");

    private string VideosFolder => Path.Combine(_directory, "Videos");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder that stays behind fails no test.
        }
    }

    // ---- The switch

    [Fact]
    public void TheSwitch_IsOffUntilItIsSwitchedOn_AndIsSavedWithGeneral()
    {
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);

        Assert.False(vm.IsStudioPreviewEnabled);
        Assert.Equal(Visibility.Collapsed, vm.StudioPreviewVisibility);
        Assert.Equal(Visibility.Visible, vm.ShowTrimmerToggleVisibility);
        Assert.Equal(Visibility.Collapsed, vm.StudioDraftsVisibility);

        vm.IsStudioPreviewEnabled = true;

        Assert.True(_settings.StudioPreviewEnabled);
        Assert.Equal(Visibility.Visible, vm.StudioPreviewVisibility);
        Assert.Equal(Visibility.Collapsed, vm.ShowTrimmerToggleVisibility);

        vm.IsStudioPreviewEnabled = false;

        Assert.False(_settings.StudioPreviewEnabled);
        Assert.Equal(Visibility.Collapsed, vm.StudioPreviewVisibility);

        // A Settings window opened later shows what was saved.
        _settings.StudioPreviewEnabled = true;
        Assert.True(CreateViewModel().IsStudioPreviewEnabled);
    }

    [Fact]
    public void WhatAControlWritesBackWhileGeneralIsFirstShown_IsNotSaved_AndIsPutBack()
    {
        _settings.StudioSourceRetentionDays = 14;
        var vm = CreateViewModel();
        var writes = _saved.Writes;

        var showing = vm.BeginSectionRealization(SettingsSectionKind.General);
        vm.IsStudioPreviewEnabled = true;
        vm.StudioSourceRetentionDays = 0;
        vm.StudioStorageCapGigabytes = 0;
        vm.CompleteSectionRealization(showing);

        Assert.Equal(writes, _saved.Writes);
        Assert.False(vm.IsStudioPreviewEnabled);
        Assert.Equal(14, vm.StudioSourceRetentionDays);
        Assert.Equal(CaptureSettings.DefaultStudioStorageCapGigabytes, vm.StudioStorageCapGigabytes);
        Assert.False(_settings.StudioPreviewEnabled);
        Assert.Equal(14, _settings.StudioSourceRetentionDays);
    }

    [Fact]
    public async Task TheSwitch_ReadsNoProject_UntilGeneralHasShownTheNumbers()
    {
        Record("First");
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);

        vm.IsStudioPreviewEnabled = true;
        _tracker.MarkOpened("some-project");
        vm.IsStudioPreviewEnabled = false;

        // A read would be started on another thread, so it is given the time to show up.
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(0, _counted.CallsTo(nameof(IStudioProjectStore.ListSummaries)));
        Assert.Empty(vm.StudioDrafts);
    }

    [Fact]
    public void ShowingASection_RestoresOnlyTheStudioValuesThatAreInIt()
    {
        string[] inGeneral = ["studioPreviewEnabled", "studioSourceRetentionDays", "studioStorageCapGigabytes"];
        var vm = CreateViewModel();
        _saved.Reads.Clear();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Show(vm, SettingsSectionKind.Gif);
        Show(vm, SettingsSectionKind.Screenshot);

        Assert.DoesNotContain("videoAfterRecording", _saved.Reads);
        Assert.All(inGeneral, key => Assert.DoesNotContain(key, _saved.Reads));

        Show(vm, SettingsSectionKind.Video);

        Assert.Contains("videoAfterRecording", _saved.Reads);
        Assert.All(inGeneral, key => Assert.DoesNotContain(key, _saved.Reads));

        _saved.Reads.Clear();
        Show(vm, SettingsSectionKind.General);

        Assert.All(inGeneral, key => Assert.Contains(key, _saved.Reads));
        Assert.DoesNotContain("videoAfterRecording", _saved.Reads);

        // Nothing was different from what is saved, so nothing is said to have changed.
        Assert.DoesNotContain(nameof(SettingsViewModel.IsStudioPreviewEnabled), changed);
        Assert.DoesNotContain(nameof(SettingsViewModel.VideoAfterRecordingIndex), changed);
    }

    // ---- After recording, which stands in for the trimmer switch while Studio is on

    [Fact]
    public void AfterRecording_IsSavedOnlyWhileStudioIsOn()
    {
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        Show(vm, SettingsSectionKind.Video);
        Assert.Equal(1, vm.VideoAfterRecordingIndex);

        // The choice is hidden while Studio is off, so nothing it is bound to may be saved.
        vm.VideoAfterRecordingIndex = 2;
        Assert.Equal(VideoAfterRecording.Trimmer, _settings.VideoAfterRecording);

        vm.IsStudioPreviewEnabled = true;
        Assert.Equal(1, vm.VideoAfterRecordingIndex);

        vm.VideoAfterRecordingIndex = 2;
        Assert.Equal(VideoAfterRecording.Studio, _settings.VideoAfterRecording);
        Assert.False(_settings.ShowTrimmer);
        Assert.True(_settings.IsStudioRecordingEnabled);

        // A ComboBox reports -1 while it has no selection.
        vm.VideoAfterRecordingIndex = -1;
        Assert.Equal(VideoAfterRecording.Studio, _settings.VideoAfterRecording);

        vm.VideoAfterRecordingIndex = 0;
        Assert.Equal(VideoAfterRecording.Save, _settings.VideoAfterRecording);

        vm.VideoAfterRecordingIndex = 1;
        Assert.Equal(VideoAfterRecording.Trimmer, _settings.VideoAfterRecording);
        Assert.True(_settings.ShowTrimmer);
    }

    [Fact]
    public void SwitchingStudioOnOrOff_PutsTheHiddenOneOfTheTwoInStepWithWhatIsSaved()
    {
        _settings.StudioPreviewEnabled = true;
        _settings.VideoAfterRecording = VideoAfterRecording.Studio;
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        Show(vm, SettingsSectionKind.Video);
        Assert.Equal(2, vm.VideoAfterRecordingIndex);
        Assert.False(vm.ShowTrimmer);

        // Off: the trimmer switch is what is on screen, and it is used.
        vm.IsStudioPreviewEnabled = false;
        vm.ShowTrimmer = true;
        Assert.Equal(VideoAfterRecording.Trimmer, _settings.VideoAfterRecording);

        // On again: the choice shows what the trimmer switch made of it, and saves nothing by showing it.
        var writes = _saved.Writes;
        vm.IsStudioPreviewEnabled = true;
        Assert.Equal(1, vm.VideoAfterRecordingIndex);
        Assert.Equal(writes + 1, _saved.Writes);
        Assert.Equal(VideoAfterRecording.Trimmer, _settings.VideoAfterRecording);

        // And the other way round.
        vm.VideoAfterRecordingIndex = 0;
        vm.IsStudioPreviewEnabled = false;
        Assert.False(vm.ShowTrimmer);
        Assert.False(_settings.ShowTrimmer);
    }

    // ---- The storage rules

    [Fact]
    public void TheStorageRules_AreSavedWhileStudioIsOn_AndAnEmptiedBoxGetsItsValueBack()
    {
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        Assert.Equal(CaptureSettings.DefaultStudioSourceRetentionDays, vm.StudioSourceRetentionDays);
        Assert.Equal(CaptureSettings.DefaultStudioStorageCapGigabytes, vm.StudioStorageCapGigabytes);

        // Hidden while Studio is off.
        vm.StudioSourceRetentionDays = 3;
        Assert.Equal(CaptureSettings.DefaultStudioSourceRetentionDays, _settings.StudioSourceRetentionDays);

        vm.IsStudioPreviewEnabled = true;
        vm.StudioSourceRetentionDays = 7;
        vm.StudioStorageCapGigabytes = 2.4;
        Assert.Equal(7, _settings.StudioSourceRetentionDays);
        Assert.Equal(2, _settings.StudioStorageCapGigabytes);

        // A NumberBox reports NaN when its text is cleared.
        vm.StudioSourceRetentionDays = double.NaN;
        vm.StudioStorageCapGigabytes = double.NaN;
        Assert.Equal(7, vm.StudioSourceRetentionDays);
        Assert.Equal(2, vm.StudioStorageCapGigabytes);
        Assert.Equal(7, _settings.StudioSourceRetentionDays);
        Assert.Equal(2, _settings.StudioStorageCapGigabytes);
    }

    // ---- The list of recordings that only their project holds

    [Fact]
    public async Task TheList_HasWhatOnlyAProjectHolds_NewestFirst_ThenWhatCannotBeRead()
    {
        var first = Record("First");
        var second = Record("Second", withScreenRecording: false);
        var exported = Record("Exported");
        Export(exported);
        var lost = Record("Lost");
        File.Delete(Export(lost));
        var broken = Record("Broken");
        File.WriteAllText(_projects.GetPaths(broken).ProjectJsonPath, "{ this is not a project");
        _settings.StudioPreviewEnabled = true;
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);

        await vm.EnsureStudioStorageInitializedAsync();

        Assert.Equal([lost, second, first, broken], vm.StudioDrafts.Select(row => row.Id));
        Assert.Equal(["Lost", "Second", "First", StudioDraftItem.UnreadableName], vm.StudioDrafts.Select(row => row.Name));
        Assert.Equal(
            [StudioDraftItem.ExportMissingNote, string.Empty, string.Empty, StudioDraftItem.UnreadableNote],
            vm.StudioDrafts.Select(row => row.Note));
        Assert.Equal([true, true, true, false], vm.StudioDrafts.Select(row => row.CanOpen));
        Assert.Equal([true, false, true, true], vm.StudioDrafts.Select(row => row.CanSaveRecording));
        Assert.All(vm.StudioDrafts, row => Assert.False(row.IsOpen));
        Assert.Equal(Visibility.Visible, vm.StudioDraftsVisibility);

        // The numbers count every project, also the exported one that is no row.
        Assert.StartsWith("5 projects, ", vm.StudioStorageDisplay);
    }

    [Fact]
    public async Task TheNumbers_AreReadOnce_HoweverOftenGeneralIsShown()
    {
        Record("First");
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);

        await vm.EnsureStudioStorageInitializedAsync();
        await vm.EnsureStudioStorageInitializedAsync();

        Assert.Equal(1, _counted.CallsTo(nameof(IStudioProjectStore.ListSummaries)));
        Assert.StartsWith("1 project, ", vm.StudioStorageDisplay);
    }

    [Fact]
    public async Task WithStudioOff_TheLineUnderTheSwitchSaysWhatIsKept_AndGoesWhenItIsSwitchedOn()
    {
        Record("First");
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);

        await vm.EnsureStudioStorageInitializedAsync();

        Assert.StartsWith("1 Studio project is kept and uses ", vm.StudioKeptNote);
        Assert.EndsWith("It is not cleaned up while Studio is off. Switch Studio on to open or delete it.", vm.StudioKeptNote);
        Assert.Equal(Visibility.Visible, vm.StudioKeptNoteVisibility);

        // The row is there, and nothing of the list is shown while Studio is off.
        Assert.Single(vm.StudioDrafts);
        Assert.Equal(Visibility.Collapsed, vm.StudioDraftsVisibility);

        Record("Second");
        vm.IsStudioPreviewEnabled = true;
        await Eventually(() => vm.StudioDrafts.Count == 2, "the list was not read again when Studio was switched on");

        Assert.Equal(Visibility.Collapsed, vm.StudioKeptNoteVisibility);
        Assert.Equal(Visibility.Visible, vm.StudioDraftsVisibility);
        Assert.StartsWith("2 Studio projects are kept and use ", vm.StudioKeptNote);
        Assert.EndsWith("They are not cleaned up while Studio is off. Switch Studio on to open or delete them.", vm.StudioKeptNote);
    }

    [Fact]
    public async Task WhereStudioWasNeverUsed_ShowingTheSwitchMakesNoFolder_AndSaysNothing()
    {
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);

        await vm.EnsureStudioStorageInitializedAsync();

        Assert.False(Directory.Exists(ProjectsFolder));
        Assert.Equal(string.Empty, vm.StudioKeptNote);
        Assert.Equal(Visibility.Collapsed, vm.StudioKeptNoteVisibility);
        Assert.StartsWith("0 projects, ", vm.StudioStorageDisplay);
        Assert.Empty(vm.StudioDrafts);
    }

    [Fact]
    public async Task AnEditorOpening_BringsItsRowUpToDate_AndTheRowStaysTheSameRow()
    {
        var first = Record("First");
        _settings.StudioPreviewEnabled = true;
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        await vm.EnsureStudioStorageInitializedAsync();
        var row = Assert.Single(vm.StudioDrafts);
        Assert.True(row.CanDelete);

        _tracker.MarkOpened(first);
        await Eventually(() => row.IsOpen, "the row was not told that its project is open in an editor");

        // The same row, so that a button in it keeps the keyboard focus.
        Assert.Same(row, Assert.Single(vm.StudioDrafts));
        Assert.False(row.CanDelete);
        Assert.Equal(StudioDraftItem.OpenInStudioNote, row.DeleteHelpText);
    }

    [Fact]
    public async Task AnAnswerThatComesLate_DoesNotTakeThePlaceOfANewerOne()
    {
        Record("First");
        _settings.StudioPreviewEnabled = true;
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        _counted.HoldTheFirstListOfProjects();

        // The first read has its list, of one project, and is kept from handing it over.
        var early = vm.EnsureStudioStorageInitializedAsync();
        Assert.True(_counted.FirstListWasRead.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        // Meanwhile there is a second project, and an editor opening asks for the list again.
        Record("Second");
        _tracker.MarkOpened("some-project");
        await Eventually(() => vm.StudioDrafts.Count == 2, "the second read never showed its two projects");

        _counted.HandOverTheFirstListOfProjects();
        await early;

        Assert.Equal(2, vm.StudioDrafts.Count);
        Assert.StartsWith("2 projects, ", vm.StudioStorageDisplay);
    }

    [Fact]
    public async Task AWindowClosedWhileItsProjectsAreRead_ShowsNothingOfThem()
    {
        Record("First");
        _settings.StudioPreviewEnabled = true;
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        _counted.HoldTheFirstListOfProjects();
        var reading = vm.EnsureStudioStorageInitializedAsync();
        Assert.True(_counted.FirstListWasRead.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        vm.NotifyClosed();
        _counted.HandOverTheFirstListOfProjects();
        await reading;

        Assert.Empty(vm.StudioDrafts);
        Assert.Equal("Calculating\u2026", vm.StudioStorageDisplay);
    }

    // ---- Delete

    [Fact]
    public async Task Delete_RemovesADraft_AndLeavesOneThatIsOpenInAnEditor()
    {
        var open = Record("Open in an editor");
        var closed = Record("Closed");
        _tracker.MarkOpened(open);
        _settings.StudioPreviewEnabled = true;
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        await vm.EnsureStudioStorageInitializedAsync();
        var openRow = vm.StudioDrafts.Single(row => row.Id == open);
        var closedRow = vm.StudioDrafts.Single(row => row.Id == closed);
        Assert.True(openRow.IsOpen);

        Assert.Null(await vm.DeleteStudioDraftAsync(openRow));
        Assert.True(_projects.Exists(open));

        Assert.Null(await vm.DeleteStudioDraftAsync(closedRow));
        Assert.False(_projects.Exists(closed));
        Assert.Equal([open], vm.StudioDrafts.Select(row => row.Id));
        Assert.StartsWith("1 project, ", vm.StudioStorageDisplay);
    }

    [Fact]
    public async Task Delete_DoesNothingWhileStudioIsOff()
    {
        var first = Record("First");
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        await vm.EnsureStudioStorageInitializedAsync();

        Assert.Null(await vm.DeleteStudioDraftAsync(Assert.Single(vm.StudioDrafts)));

        Assert.True(_projects.Exists(first));
    }

    // ---- Save recording

    [Fact]
    public async Task SaveRecording_SavesAVideoUnderTheNameAnySavedVideoGets_AndLeavesTheProject()
    {
        var first = Record("First");
        _settings.StudioPreviewEnabled = true;
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        await vm.EnsureStudioStorageInitializedAsync();
        var row = Assert.Single(vm.StudioDrafts);

        var (path, error) = await vm.SaveStudioScreenRecordingAsync(row);

        Assert.Null(error);
        Assert.Equal(Path.Combine(VideosFolder, "Saved video 1.mp4"), path);
        Assert.Equal([CaptureType.Video], _names.AskedFor);
        Assert.Equal(Recording, File.ReadAllBytes(path!));
        Assert.Equal([path!], Directory.GetFiles(VideosFolder));
        Assert.Equal(Recording, File.ReadAllBytes(_projects.GetPaths(first).ScreenPath));
        Assert.True(_projects.ListSummaries().Single().IsDraft);
        Assert.True(row.IsSaveRecordingEnabled);
    }

    [Fact]
    public async Task SaveRecording_PressedAgainWhileItCopies_DoesNothing()
    {
        Record("First");
        _settings.StudioPreviewEnabled = true;
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        await vm.EnsureStudioStorageInitializedAsync();
        var row = Assert.Single(vm.StudioDrafts);
        _names.HoldTheFirstName();

        var copying = Task.Run(() => vm.SaveStudioScreenRecordingAsync(row), TestContext.Current.CancellationToken);
        Assert.True(_names.FirstNameAskedFor.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.False(row.IsSaveRecordingEnabled);

        var again = await vm.SaveStudioScreenRecordingAsync(row);
        Assert.Null(again.Path);
        Assert.Null(again.Error);
        Assert.False(row.IsSaveRecordingEnabled);
        Assert.Single(_names.AskedFor);

        _names.GiveTheFirstName();
        var (path, error) = await copying;

        Assert.Null(error);
        Assert.Equal([path!], Directory.GetFiles(VideosFolder));
        Assert.True(row.IsSaveRecordingEnabled);
    }

    [Fact]
    public async Task SaveRecording_WithNoRecordingInTheProject_SaysSo_AndLeavesNoFile()
    {
        Record("Nothing in it", withScreenRecording: false);
        _settings.StudioPreviewEnabled = true;
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        await vm.EnsureStudioStorageInitializedAsync();
        var row = Assert.Single(vm.StudioDrafts);
        Assert.False(row.CanSaveRecording);

        var (path, error) = await vm.SaveStudioScreenRecordingAsync(row);

        Assert.Null(path);
        Assert.Equal($"The screen recording could not be saved: {StudioScreenRecording.NothingToSaveMessage}", error);
        Assert.False(Directory.Exists(VideosFolder));
        Assert.True(row.IsSaveRecordingEnabled);
    }

    [Fact]
    public async Task SaveRecording_DoesNothingWhileStudioIsOff()
    {
        Record("First");
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        await vm.EnsureStudioStorageInitializedAsync();

        var (path, error) = await vm.SaveStudioScreenRecordingAsync(Assert.Single(vm.StudioDrafts));

        Assert.Null(path);
        Assert.Null(error);
        Assert.Empty(_names.AskedFor);
        Assert.False(Directory.Exists(VideosFolder));
    }

    // ---- Clean up now

    [Fact]
    public async Task CleanUpNow_SaysWhatItRemoved_AndShowsTheNumbersAfterIt()
    {
        var old = Record("Old");
        Export(old);
        _clock.Advance(TimeSpan.FromDays(40));
        var recent = Record("Recent");
        Export(recent);
        var draft = Record("Never exported");
        _settings.StudioPreviewEnabled = true;
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        await vm.EnsureStudioStorageInitializedAsync();
        Assert.StartsWith("3 projects, ", vm.StudioStorageDisplay);
        Assert.Equal(Visibility.Collapsed, vm.StudioCleanupStatusVisibility);

        await vm.CleanUpStudioProjectsAsync();

        Assert.Equal("Removed 1 project.", vm.StudioCleanupStatus);
        Assert.Equal(Visibility.Visible, vm.StudioCleanupStatusVisibility);
        Assert.False(_projects.Exists(old));
        Assert.True(_projects.Exists(recent));
        Assert.True(_projects.Exists(draft));
        Assert.StartsWith("2 projects, ", vm.StudioStorageDisplay);

        await vm.CleanUpStudioProjectsAsync();

        Assert.Equal("Nothing needed cleaning up.", vm.StudioCleanupStatus);
    }

    [Fact]
    public async Task CleanUpNow_DoesNothingWhileStudioIsOff()
    {
        var old = Record("Old");
        Export(old);
        _clock.Advance(TimeSpan.FromDays(40));
        var vm = CreateViewModel();
        Show(vm, SettingsSectionKind.General);
        await vm.EnsureStudioStorageInitializedAsync();

        await vm.CleanUpStudioProjectsAsync();

        Assert.Equal(string.Empty, vm.StudioCleanupStatus);
        Assert.True(_projects.Exists(old));
        Assert.Equal(0, _counted.CallsTo(nameof(IStudioProjectStore.Cleanup)));
    }

    // ---- A window that closes, and a view model without Studio

    [Fact]
    public void Closing_LetsGoOfTheServicesThatOutliveTheWindow()
    {
        var vm = CreateViewModel();
        Assert.NotNull(ListenersOf(_tracker, nameof(StudioProjectTracker.Changed)));
        Assert.NotNull(ListenersOf(_cleanup, nameof(StudioProjectCleanupService.CleanupCompleted)));

        vm.NotifyClosed();

        Assert.Null(ListenersOf(_tracker, nameof(StudioProjectTracker.Changed)));
        Assert.Null(ListenersOf(_cleanup, nameof(StudioProjectCleanupService.CleanupCompleted)));
    }

    [Fact]
    public async Task AViewModelMadeWithoutTheStudioServices_SaysItCannotReadTheStorage()
    {
        var vm = new SettingsViewModel(
            _settings, new HotKeys(), new LaunchAtLogin(), new NoMicrophones(), new NoWebcams(), _names,
            new NoAnalytics(), new NoCredentials(), dispatcherQueue: null);
        Show(vm, SettingsSectionKind.General);

        vm.IsStudioPreviewEnabled = true;
        await vm.EnsureStudioStorageInitializedAsync();

        Assert.True(_settings.StudioPreviewEnabled);
        Assert.Equal("Couldn't read Studio project storage.", vm.StudioStorageDisplay);
        Assert.Equal(string.Empty, vm.StudioKeptNote);
        Assert.Empty(vm.StudioDrafts);
        vm.NotifyClosed();
    }

    // ---- What the tests are made of

    private SettingsViewModel CreateViewModel() => new(
        _settings, new HotKeys(), new LaunchAtLogin(), new NoMicrophones(), new NoWebcams(), _names,
        new NoAnalytics(), new NoCredentials(), _store, _cleanup, _tracker, dispatcherQueue: null);

    /// <summary>What the Settings window does when a section is shown for the first time.</summary>
    private static void Show(SettingsViewModel vm, SettingsSectionKind kind) =>
        vm.CompleteSectionRealization(vm.BeginSectionRealization(kind));

    /// <summary>A finished Studio recording that was never exported. Each is an hour newer than the one before.</summary>
    private string Record(string name, bool withScreenRecording = true)
    {
        var paths = _projects.BeginRecording();
        _projects.CompleteRecording(paths.ProjectId, new StudioProjectCreationRequest(
            name,
            new StudioRecordingSourceInfo(1920, 1080, 10),
            null,
            StudioAnchor.BottomRight,
            new StudioClickOverlay(),
            false,
            "1.9.0"));
        if (withScreenRecording)
        {
            File.WriteAllBytes(paths.ScreenPath, Recording);
        }

        _clock.Advance(TimeSpan.FromHours(1));
        return paths.ProjectId;
    }

    /// <summary>Gives a project an exported video, which is there until a test deletes it.</summary>
    private string Export(string projectId)
    {
        var video = Path.Combine(_directory, "Exports", $"{projectId}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(video)!);
        File.WriteAllBytes(video, [1, 2, 3]);
        _projects.RecordExport(projectId, video);
        return video;
    }

    /// <summary>
    /// What listens to an event of a service. The services live as long as the app, so a Settings
    /// window that stays subscribed stays in memory.
    /// </summary>
    private static Delegate? ListenersOf(object service, string eventName) =>
        (Delegate?)service.GetType()
            .GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service);

    /// <summary>A refresh that the view model starts by itself is not handed back, so the test waits for what it shows.</summary>
    private static async Task Eventually(Func<bool> condition, string otherwise)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, otherwise);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class MemorySettings : ISettingsService, ILargeTextSettingsService
    {
        private readonly Dictionary<string, object?> _values = new();

        public int Writes { get; private set; }

        /// <summary>The keys that were read, in the order they were asked for.</summary>
        public List<string> Reads { get; } = [];

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue)
        {
            Reads.Add(key);
            if (!_values.TryGetValue(key, out var value))
            {
                return defaultValue;
            }

            return value is string text && typeof(T).IsEnum
                ? (T)Enum.Parse(typeof(T), text)
                : (T)value!;
        }

        public void Set<T>(string key, T value)
        {
            _values[key] = value;
            Writes++;
        }

        public string GetLargeText(string key, string defaultValue) => Get(key, defaultValue);

        public string GetLargeTextForEditing(string key, string defaultValue) => Get(key, defaultValue);

        public void SetLargeText(string key, string value) => Set(key, value);
    }

    /// <summary>
    /// The names saved videos get, in a folder of the test's own. The first name can be held
    /// back, which keeps the copy it is for from starting.
    /// </summary>
    private sealed class VideoNames(string folder) : IClipStorageService
    {
        private readonly ManualResetEventSlim _mayGiveTheFirstName = new(initialState: true);
        private int _asked;

        public List<CaptureType> AskedFor { get; } = [];

        public ManualResetEventSlim FirstNameAskedFor { get; } = new();

        public void HoldTheFirstName() => _mayGiveTheFirstName.Reset();

        public void GiveTheFirstName() => _mayGiveTheFirstName.Set();

        public string FileExtensionFor(CaptureType type) => ".mp4";

        public string GenerateFilePath(CaptureType type, string? fileExtension = null, string? stemSuffix = null)
        {
            int number;
            lock (AskedFor)
            {
                AskedFor.Add(type);
                number = ++_asked;
            }

            if (number == 1)
            {
                FirstNameAskedFor.Set();
                Assert.True(_mayGiveTheFirstName.Wait(TimeSpan.FromSeconds(30)), "the test never let the first name be given");
            }

            return Path.Combine(folder, $"Saved video {number}.mp4");
        }

        public string OutputDirectory(CaptureType type) => folder;
    }

    private sealed class HotKeys : IHotKeyService
    {
        public HotKeyDefinition GetBinding(HotKeyAction action) => new(HotKeyModifiers.Control, 53);

        public HotKeyDefinition GetStopBinding() => new(HotKeyModifiers.Control, 83);

        public string StopRecordingDisplayString => "Ctrl+S";

        public void SetBinding(HotKeyAction action, HotKeyDefinition binding)
        {
        }

        public HotKeyDefinition DefaultFor(HotKeyAction action) => GetBinding(action);

        public HotKeyValidationResult ValidateBinding(HotKeyAction action, HotKeyDefinition binding) => throw new NotSupportedException();
    }

    private sealed class LaunchAtLogin : ILaunchAtLoginService
    {
        public Task<LaunchAtLoginState> GetStateAsync() => Task.FromResult(LaunchAtLoginState.Disabled);

        public Task<LaunchAtLoginState> SetEnabledAsync(bool enabled) =>
            Task.FromResult(enabled ? LaunchAtLoginState.Enabled : LaunchAtLoginState.Disabled);
    }

    private sealed class NoMicrophones : IAudioDeviceService
    {
        public IReadOnlyList<AudioInputDevice> GetMicrophones() => [];
    }

    private sealed class NoWebcams : IWebcamDeviceEnumerator
    {
        public Task<IReadOnlyList<WebcamDeviceInfo>> GetWebcamDevicesAsync() => Task.FromResult<IReadOnlyList<WebcamDeviceInfo>>([]);
    }

    private sealed class NoAnalytics : IClipAnalyticsService
    {
        public void RecordCapture(CaptureType type) => throw new NotSupportedException();

        public IReadOnlyList<DailyCaptureAnalytics> GetDailyCounts(int days) => [];

        public LifetimeCaptureAnalytics GetLifetimeTotals() => new(0, 0, 0);

        public IReadOnlyList<WeekdayCaptureTotal> GetWeekdayTotals(int days) => [];

        public WeekdayCaptureTotal? GetBusiestWeekday(int days) => null;

        public IReadOnlyList<HourCaptureTotal> GetHourlyTotals() => [];

        public HourCaptureTotal? GetMostActiveHour() => null;

        public void Clear()
        {
        }
    }

    private sealed class NoCredentials : IUploadcareCredentialStore
    {
        public string? GetSecretKey() => throw new InvalidOperationException("Settings must not retrieve secret contents.");

        public bool HasSecretKey() => false;

        public void SaveSecretKey(string secretKey)
        {
        }

        public void RemoveSecretKey()
        {
        }
    }
}

/// <summary>
/// The real project store, with every call to it counted by name. The first list of projects it
/// reads can be held back, so that a test decides when that read is finished.
/// </summary>
public class CountingStore : DispatchProxy
{
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
    private readonly ManualResetEventSlim _mayHandOverTheFirstList = new(initialState: true);

    public IStudioProjectStore Inner { get; set; } = null!;

    public ManualResetEventSlim FirstListWasRead { get; } = new();

    public void HoldTheFirstListOfProjects() => _mayHandOverTheFirstList.Reset();

    public void HandOverTheFirstListOfProjects() => _mayHandOverTheFirstList.Set();

    public int CallsTo(string method)
    {
        lock (_calls)
        {
            return _calls.GetValueOrDefault(method);
        }
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        int number;
        lock (_calls)
        {
            number = _calls[targetMethod.Name] = _calls.GetValueOrDefault(targetMethod.Name) + 1;
        }

        object? result;
        try
        {
            result = targetMethod.Invoke(Inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }

        if (number == 1 && targetMethod.Name == nameof(IStudioProjectStore.ListSummaries))
        {
            FirstListWasRead.Set();
            Assert.True(_mayHandOverTheFirstList.Wait(TimeSpan.FromSeconds(60)), "the test never let the first list of projects be handed over");
        }

        return result;
    }
}

/// <summary>A recorder that is not recording: every answer is the type's default.</summary>
public class NothingRecording : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var type = targetMethod.ReturnType;
        return type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
    }
}
