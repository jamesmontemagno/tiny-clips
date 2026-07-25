using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Models;
using Windows.System;
using Windows.UI.Core;

namespace TinyClips.App.Settings.Sections;

/// <summary>Screenshot/video/GIF global hotkey display, editing, and reset.</summary>
public sealed partial class HotkeysSettingsSection : UserControl
{
    private readonly IDisposable _realizationScope;

    public SettingsViewModel ViewModel { get; }

    public HotkeysSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        _realizationScope = viewModel.BeginSectionRealization();
        InitializeComponent();
        SectionLifecycle.HookFirstLoad(this, viewModel, _realizationScope);
    }

    private static CaptureType TypeFromTag(object? tag) => (tag as string) switch
    {
        "Video" => CaptureType.Video,
        "Gif" => CaptureType.Gif,
        _ => CaptureType.Screenshot,
    };

    private async void OnEditHotKey(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            await RecordShortcutAsync(TypeFromTag(element.Tag));
        }
    }

    private void OnResetHotKey(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        var type = TypeFromTag(element.Tag);
        var defaultBinding = ViewModel.GetDefaultHotKey(type);
        var validation = ViewModel.ValidateHotKey(type, defaultBinding);
        if (!validation.IsValid)
        {
            ShowSectionStatus(ValidationMessage(validation), InfoBarSeverity.Error);
            return;
        }

        if (TryApplyCandidate(type, defaultBinding, out var errorMessage))
        {
            ShowSectionStatus(
                $"{ActionName(type)} shortcut reset to {defaultBinding.DisplayString}.",
                InfoBarSeverity.Success);
        }
        else
        {
            ShowSectionStatus(errorMessage, InfoBarSeverity.Error);
        }
    }

    private async Task RecordShortcutAsync(CaptureType type)
    {
        var instructions = new TextBlock
        {
            Text = "Focus the shortcut recorder, then press at least one modifier and a non-modifier key.",
            TextWrapping = TextWrapping.Wrap,
        };

        var recorder = new TextBox
        {
            Header = "New shortcut",
            HorizontalTextAlignment = TextAlignment.Center,
            IsReadOnly = true,
            MinWidth = 320,
            Text = "Press a shortcut",
        };
        AutomationProperties.SetAutomationId(recorder, "HotKeyRecorderTextBox");
        AutomationProperties.SetName(recorder, $"New {ActionName(type)} shortcut");
        AutomationProperties.SetHelpText(
            recorder,
            "Press Ctrl, Alt, Shift, or Win together with one non-modifier key.");

        var status = new InfoBar
        {
            IsClosable = false,
            IsOpen = true,
            Message = "Waiting for a shortcut.",
            Severity = InfoBarSeverity.Informational,
        };
        AutomationProperties.SetAutomationId(status, "HotKeyRecorderStatusInfoBar");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);

        var content = new StackPanel
        {
            Spacing = 12,
        };
        content.Children.Add(instructions);
        content.Children.Add(recorder);
        content.Children.Add(status);

        var dialog = new ContentDialog
        {
            Title = $"Set {ActionName(type)} shortcut",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot,
        };

        HotKeyDefinition? candidate = null;

        void SetRecorderStatus(string message, InfoBarSeverity severity)
        {
            status.Message = message;
            status.Severity = severity;
            var peer = FrameworkElementAutomationPeer.FromElement(status)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(status);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        void OnKey(object sender, KeyRoutedEventArgs args)
        {
            var modifiers = CurrentModifiers();
            if (modifiers == 0 && IsDialogNavigationKey(args.Key))
            {
                return;
            }

            args.Handled = true;

            if (IsModifierKey(args.Key))
            {
                candidate = null;
                dialog.IsPrimaryButtonEnabled = false;
                recorder.Text = ModifierPreview(CurrentModifiers());
                SetRecorderStatus(
                    "Now press a non-modifier key while holding the modifier.",
                    InfoBarSeverity.Informational);
                return;
            }

            var proposed = new HotKeyDefinition(modifiers, (uint)args.Key);
            recorder.Text = proposed.DisplayString;
            var validation = ViewModel.ValidateHotKey(type, proposed);
            if (!validation.IsValid)
            {
                candidate = null;
                dialog.IsPrimaryButtonEnabled = false;
                SetRecorderStatus(ValidationMessage(validation), InfoBarSeverity.Error);
                return;
            }

            candidate = proposed;
            dialog.IsPrimaryButtonEnabled = true;
            SetRecorderStatus(
                $"{proposed.DisplayString} is ready. Choose Save to apply it.",
                InfoBarSeverity.Success);
        }

        void OnPrimaryButtonClick(
            ContentDialog sender,
            ContentDialogButtonClickEventArgs args)
        {
            if (candidate is not HotKeyDefinition proposed)
            {
                args.Cancel = true;
                SetRecorderStatus(
                    "Press a valid shortcut before saving.",
                    InfoBarSeverity.Error);
                return;
            }

            if (!TryApplyCandidate(type, proposed, out var errorMessage))
            {
                args.Cancel = true;
                SetRecorderStatus(errorMessage, InfoBarSeverity.Error);
                recorder.Focus(FocusState.Programmatic);
                return;
            }

            ShowSectionStatus(
                $"{ActionName(type)} shortcut changed to {proposed.DisplayString}.",
                InfoBarSeverity.Success);
        }

        void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
            => recorder.Focus(FocusState.Programmatic);

        recorder.KeyDown += OnKey;
        dialog.PrimaryButtonClick += OnPrimaryButtonClick;
        dialog.Opened += OnOpened;
        await dialog.ShowAsync();
        recorder.KeyDown -= OnKey;
        dialog.PrimaryButtonClick -= OnPrimaryButtonClick;
        dialog.Opened -= OnOpened;
    }

    private static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static bool IsDialogNavigationKey(VirtualKey key) =>
        key is VirtualKey.Tab or VirtualKey.Enter or VirtualKey.Escape;

    private static HotKeyModifiers CurrentModifiers()
    {
        HotKeyModifiers modifiers = 0;
        if (IsKeyDown(VirtualKey.Control))
        {
            modifiers |= HotKeyModifiers.Control;
        }

        if (IsKeyDown(VirtualKey.Shift))
        {
            modifiers |= HotKeyModifiers.Shift;
        }

        if (IsKeyDown(VirtualKey.Menu))
        {
            modifiers |= HotKeyModifiers.Alt;
        }

        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
        {
            modifiers |= HotKeyModifiers.Win;
        }

        return modifiers;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private bool TryApplyCandidate(
        CaptureType type,
        HotKeyDefinition candidate,
        out string errorMessage)
    {
        var previous = ViewModel.GetHotKey(type);
        ViewModel.SetHotKey(type, candidate.Modifiers, candidate.VirtualKey);

        if (App.Current is not App app)
        {
            ViewModel.SetHotKey(type, previous.Modifiers, previous.VirtualKey);
            errorMessage = "TinyClips could not access the global hotkey service. The previous shortcut was restored.";
            return false;
        }

        var applyResult = app.ReapplyGlobalHotKeys();
        if (applyResult.IsSuccess)
        {
            errorMessage = string.Empty;
            return true;
        }

        ViewModel.SetHotKey(type, previous.Modifiers, previous.VirtualKey);
        var rollbackResult = app.ReapplyGlobalHotKeys();

        var rejectedNames = string.Join(", ", applyResult.Failures.Select(failure => failure.Name));
        errorMessage =
            $"Windows could not register {rejectedNames}. Another app may already use this shortcut. " +
            "Choose a different combination.";

        if (!rollbackResult.IsSuccess)
        {
            var rollbackNames = string.Join(", ", rollbackResult.Failures.Select(failure => failure.Name));
            errorMessage +=
                $" The previous shortcut was restored in Settings, but Windows could not reactivate {rollbackNames}. " +
                "Close the competing app or restart TinyClips.";
        }

        return false;
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

    private static string ValidationMessage(HotKeyValidationResult result) => result.Error switch
    {
        HotKeyValidationError.ModifierRequired =>
            "Include at least one modifier: Ctrl, Alt, Shift, or Win.",
        HotKeyValidationError.KeyRequired or HotKeyValidationError.ModifierKeyNotAllowed =>
            "Include one non-modifier key with the modifier.",
        HotKeyValidationError.DuplicateBinding =>
            $"That shortcut is already assigned to {ActionName(result.ConflictingCaptureType!.Value)}.",
        HotKeyValidationError.StopRecordingConflict =>
            "That shortcut is reserved for Stop recording (Ctrl+Shift+S). Choose a different combination.",
        _ => string.Empty,
    };

    private static string ModifierPreview(HotKeyModifiers modifiers)
    {
        var tokens = new List<string>();
        if (modifiers.HasFlag(HotKeyModifiers.Control))
        {
            tokens.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotKeyModifiers.Alt))
        {
            tokens.Add("Alt");
        }

        if (modifiers.HasFlag(HotKeyModifiers.Shift))
        {
            tokens.Add("Shift");
        }

        if (modifiers.HasFlag(HotKeyModifiers.Win))
        {
            tokens.Add("Win");
        }

        return tokens.Count == 0 ? "Press a shortcut" : $"{string.Join("+", tokens)}+…";
    }

    private static string ActionName(CaptureType type) => type switch
    {
        CaptureType.Video => "Record video",
        CaptureType.Gif => "Record GIF",
        _ => "Screenshot",
    };
}
