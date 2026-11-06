using TinyClips.App.ViewModels;
using TinyClips.Core.Editing.Themes;
using Windows.UI;

namespace TinyClips.App.Tests;

public sealed class EditorThemesSettingsViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsEditorThemesVm", Guid.NewGuid().ToString("N"));
    private readonly EditorThemeStore _store;

    public EditorThemesSettingsViewModelTests()
    {
        _store = new EditorThemeStore(Path.Combine(_directory, EditorThemeStore.FileName));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void NewSavesAndSelectsATheme()
    {
        var vm = new EditorThemesSettingsViewModel(_store);
        Assert.True(vm.HasNoSelection);

        vm.New();

        Assert.True(vm.HasSelection);
        Assert.Single(_store.Themes);
        Assert.Equal(_store.Themes[0].Id, vm.Theme!.Id);
    }

    [Fact]
    public void EditsSaveImmediately()
    {
        var vm = new EditorThemesSettingsViewModel(_store);
        vm.New();

        vm.ShapeStrokeWidth = 9;
        vm.LineColor = Color.FromArgb(255, 1, 2, 3);
        vm.ArrowStyleIndex = 2;

        var saved = _store.Themes[0];
        Assert.Equal(9, saved.Shape.StrokeWidth);
        Assert.Equal("#FF010203", saved.Line.Color);
        Assert.Equal("curvedRight", saved.Line.ArrowStyle);
    }

    [Fact]
    public void AClearedNumberBoxLeavesTheValue()
    {
        var vm = new EditorThemesSettingsViewModel(_store);
        vm.New();

        vm.FontSize = double.NaN;

        Assert.Equal(16, _store.Themes[0].Text.FontSize);
    }

    [Fact]
    public void ClearingTheFillEndsTheGradient()
    {
        var vm = new EditorThemesSettingsViewModel(_store);
        vm.New();
        vm.ShapeFillColor = Color.FromArgb(255, 1, 2, 3);
        vm.HasShapeGradient = true;
        Assert.NotNull(_store.Themes[0].Shape.GradientEndColor);

        vm.ShapeFillColor = Color.FromArgb(0, 0, 0, 0);

        Assert.Null(_store.Themes[0].Shape.GradientEndColor);
        Assert.False(vm.HasShapeGradient);
    }

    [Fact]
    public void ABlankNameReportsAnErrorAndKeepsTheSavedName()
    {
        var vm = new EditorThemesSettingsViewModel(_store);
        vm.New();
        var name = vm.Theme!.Name;

        vm.NameText = "   ";

        Assert.True(vm.HasError);
        Assert.Equal(name, _store.Themes[0].Name);

        vm.NameText = "Brand ";

        Assert.False(vm.HasError);
        Assert.Equal("Brand", _store.Themes[0].Name);
        Assert.Equal("Brand ", vm.NameText);
    }

    [Fact]
    public void ABadImportIsReported_AndAGoodOneIsSelected()
    {
        var vm = new EditorThemesSettingsViewModel(_store);

        vm.Import("""{ "name": "A", "line": { "width": 500 } }""");
        Assert.True(vm.HasError);
        Assert.Contains("line.width", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(_store.Themes);

        vm.Import("""{ "id": "a", "name": "A" }""");
        Assert.Equal("a", vm.Theme!.Id);
    }

    [Fact]
    public void DeleteMovesToANeighbor_AndClearsActive()
    {
        var vm = new EditorThemesSettingsViewModel(_store);
        vm.New();
        vm.SetActive();
        Assert.False(vm.CanSetActive);
        vm.Duplicate();

        vm.SelectedItem = vm.Themes.Single(item => item.IsActive);
        vm.Delete();

        Assert.Null(_store.ActiveThemeId);
        Assert.Single(_store.Themes);
        Assert.Equal(_store.Themes[0].Id, vm.Theme!.Id);
    }

    [Fact]
    public void ChangesMadeElsewhereAppear()
    {
        var vm = new EditorThemesSettingsViewModel(_store);
        vm.New();
        var id = vm.Theme!.Id;

        _store.Save(vm.Theme with { Name = "From an agent", Line = vm.Theme.Line with { Width = 7 } });
        _store.Save(EditorTheme.CreateDefault("other", "Other"));

        Assert.Equal(2, vm.Themes.Count);
        Assert.Equal(id, vm.Theme!.Id);
        Assert.Equal(7, vm.LineWidth);
        Assert.Equal("From an agent", vm.NameText);
    }

    [Fact]
    public void AfterDetachTheStoreNoLongerReachesTheViewModel()
    {
        var vm = new EditorThemesSettingsViewModel(_store);
        vm.Detach();

        _store.Save(EditorTheme.CreateDefault("a", "A"));

        Assert.Empty(vm.Themes);
    }
}
