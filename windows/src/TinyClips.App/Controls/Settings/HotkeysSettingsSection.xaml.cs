using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App.Settings.Sections;

public sealed partial class HotkeysSettingsSection : UserControl
{
    private readonly IDisposable _realizationScope;
    private readonly IHotKeyService _hotKeys;
    private readonly HotKeyEditOperation _edit;

    public SettingsViewModel ViewModel { get; }

    public HotkeysSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _hotKeys = App.Services.GetRequiredService<IHotKeyService>();
        _edit = new HotKeyEditOperation(
            _hotKeys,
            () => ((App)Application.Current).ReapplyGlobalHotKeys(),
            (action, binding) => ViewModel.SetHotKey(action, binding.Modifiers, binding.VirtualKey));
        _realizationScope = viewModel.BeginSectionRealization(SettingsSectionKind.Hotkeys);
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);
    }

    private static HotKeyAction ActionFromTag(object? tag) => (tag as string) switch
    {
        "Screenshot" => HotKeyAction.Screenshot,
        "Video" => HotKeyAction.RecordVideo,
        "Gif" => HotKeyAction.RecordGif,
        "RecognizeText" => HotKeyAction.RecognizeText,
        "ScreenshotRegion" => HotKeyAction.ScreenshotRegion,
        "ScreenshotWindow" => HotKeyAction.ScreenshotWindow,
        "ScreenshotScreen" => HotKeyAction.ScreenshotScreen,
        _ => throw new ArgumentException("Unknown shortcut action.", nameof(tag)),
    };

    private void OnEditHotKey(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            HotKeyRecorder.Show(element, _hotKeys, _edit, ActionFromTag(element.Tag), ShowSectionStatus);
        }
    }

    private void OnResetHotKey(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            var action = ActionFromTag(element.Tag);
            Apply(action, _hotKeys.DefaultFor(action), "reset");
        }
    }

    private void OnDeleteHotKey(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Apply(ActionFromTag(element.Tag), new HotKeyDefinition(HotKeyModifiers.None, 0), "cleared");
        }
    }

    private void Apply(HotKeyAction action, HotKeyDefinition candidate, string verb)
    {
        if (_edit.TryApply(action, candidate, out var error))
        {
            ShowSectionStatus(
                $"{HotKeyEditOperation.ActionName(action)} shortcut {verb}: {candidate.DisplayString}.",
                InfoBarSeverity.Success);
        }
        else
        {
            ShowSectionStatus(error, InfoBarSeverity.Error);
        }
    }

    private void ShowSectionStatus(string message, InfoBarSeverity severity)
    {
        HotKeyStatusInfoBar.Message = message;
        HotKeyStatusInfoBar.Severity = severity;
        HotKeyStatusInfoBar.IsOpen = true;
        var peer = FrameworkElementAutomationPeer.FromElement(HotKeyStatusInfoBar)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(HotKeyStatusInfoBar);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
