using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TinyClips.Core.Editing.Themes;

namespace TinyClips.App.ViewModels;

/// <summary>One row of the Editor Themes list.</summary>
public sealed partial record EditorThemeListItem(string Id, string Name, bool IsActive)
{
    public string AccessibleName => IsActive ? $"{Name}, active" : Name;
}

/// <summary>
/// Settings → Editor Themes. Every edit saves through <see cref="EditorThemeStore"/> immediately.
/// The store can change on any thread when an agent saves a theme, so changes come back through
/// <see cref="UiDispatcher"/>.
/// </summary>
public sealed partial class EditorThemesSettingsViewModel : ObservableObject
{
    private readonly EditorThemeStore _store;
    private EditorTheme? _theme;
    private object? _selectedItem;
    private string _nameText = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _reloading;

    public EditorThemesSettingsViewModel(EditorThemeStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;
        Reload();
        SelectedItem = Themes.FirstOrDefault(item => item.IsActive) ?? Themes.FirstOrDefault();
    }

    /// <summary>Runs work on the UI thread. The section sets this; by default work runs in place.</summary>
    public Action<Action> UiDispatcher { get; set; } = action => action();

    public ObservableCollection<EditorThemeListItem> Themes { get; } = [];

    /// <summary>Typed as object because that is what a list's SelectedItem binds to.</summary>
    public object? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_reloading || ReferenceEquals(_selectedItem, value))
            {
                return;
            }

            _selectedItem = value;
            LoadSelection();
        }
    }

    /// <summary>The theme being edited.</summary>
    public EditorTheme? Theme => _theme;

    public bool HasSelection => _theme is not null;

    public bool HasNoSelection => _theme is null;

    public bool CanSetActive => _theme is not null && _store.ActiveThemeId != _theme.Id;

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>Two-way so that closing the InfoBar clears the message.</summary>
    public bool HasError
    {
        get => _errorMessage.Length > 0;
        set
        {
            if (!value)
            {
                ErrorMessage = string.Empty;
            }
        }
    }

    public void New()
    {
        var number = Themes.Count + 1;
        Add(EditorTheme.CreateDefault(string.Empty, $"Theme {number}"));
    }

    public void Duplicate()
    {
        if (_theme is { } theme)
        {
            var name = $"{theme.Name} copy";
            Add(theme with { Id = string.Empty, Name = name.Length > EditorTheme.MaximumNameLength ? theme.Name : name });
        }
    }

    public void Delete()
    {
        if (_theme is { } theme)
        {
            var index = Themes.ToList().FindIndex(item => item.Id == theme.Id);
            if (Run(() => _store.Delete(theme.Id)))
            {
                Select(Themes.ElementAtOrDefault(Math.Min(index, Themes.Count - 1))?.Id);
            }
        }
    }

    public void SetActive()
    {
        if (_theme is { } theme)
        {
            Run(() => _store.SetActive(theme.Id));
        }
    }

    public void Import(string json)
    {
        EditorTheme? imported = null;
        if (Run(() => imported = _store.Import(json)))
        {
            Select(imported!.Id);
        }
    }

    /// <summary>The selected theme's JSON, or null with nothing selected.</summary>
    public string? ExportSelected() => _store.Export(_theme?.Id);

    /// <summary>A suggested export file name.</summary>
    public string ExportFileName =>
        string.Concat((_theme?.Name ?? "Theme").Select(c => Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) >= 0 ? '-' : c));

    public void ReportError(string message) => ErrorMessage = message;

    public void Detach() => _store.Changed -= OnStoreChanged;

    private void Add(EditorTheme theme)
    {
        EditorTheme? saved = null;
        if (Run(() => saved = _store.Save(theme)))
        {
            Select(saved!.Id);
        }
    }

    private void Select(string? id) => SelectedItem = Themes.FirstOrDefault(item => item.Id == id);

    private void OnStoreChanged(object? sender, EventArgs e) => UiDispatcher(Reload);

    /// <summary>Brings the list up to date, keeping the selection unless its theme is gone.</summary>
    private void Reload()
    {
        var active = _store.ActiveThemeId;
        var items = _store.Themes
            .OrderBy(theme => theme.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(theme => new EditorThemeListItem(theme.Id, theme.Name, theme.Id == active))
            .ToList();
        if (!items.SequenceEqual(Themes))
        {
            var selectedId = (_selectedItem as EditorThemeListItem)?.Id;
            _reloading = true;
            Themes.Clear();
            foreach (var item in items)
            {
                Themes.Add(item);
            }

            _selectedItem = items.Find(item => item.Id == selectedId);
            _reloading = false;
            OnPropertyChanged(nameof(SelectedItem));
        }

        LoadSelection();
    }

    private void LoadSelection()
    {
        var latest = _store.Get((_selectedItem as EditorThemeListItem)?.Id);
        if (latest?.Id != _theme?.Id)
        {
            _nameText = latest?.Name ?? string.Empty;
        }
        else if (latest is not null && latest.Name != _nameText.Trim())
        {
            _nameText = latest.Name;
        }

        _theme = latest;
        OnPropertyChanged(string.Empty);
    }

    private void Edit(Func<EditorTheme, EditorTheme> change)
    {
        if (_theme is not { } theme)
        {
            return;
        }

        var edited = change(theme);
        if (edited != theme && Run(() => _store.Save(edited)))
        {
            ErrorMessage = string.Empty;
        }
    }

    /// <summary>Runs a store change and turns its expected failures into the page's error.</summary>
    private bool Run(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (EditorThemeFormatException ex)
        {
            ErrorMessage = $"That isn't a valid editor theme. {ex.Message}";
        }
        catch (EditorThemeLimitException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Tiny Clips couldn't save the themes. {ex.Message}";
        }

        return false;
    }
}
