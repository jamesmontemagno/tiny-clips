using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.System;
using Windows.UI.Core;

namespace TinyClips.App;

internal static class HotKeyRecorder
{
    public static void Show(
        FrameworkElement anchor,
        IHotKeyService hotKeys,
        HotKeyEditOperation edit,
        HotKeyAction action,
        Action<string, InfoBarSeverity> report)
    {
        var title = $"New {HotKeyEditOperation.ActionName(action)} shortcut";
        var recorder = new TextBox
        {
            Header = title,
            IsReadOnly = true,
            MinWidth = 240,
            Text = "Press a shortcut",
        };
        AutomationProperties.SetAutomationId(recorder, "HotKeyRecorderTextBox");
        AutomationProperties.SetName(recorder, title);
        AutomationProperties.SetHelpText(recorder, "Press Ctrl, Alt, Shift, or Win together with one non-modifier key.");
        var status = new InfoBar
        {
            IsClosable = false,
            IsOpen = true,
            Message = "Focus the recorder and press a shortcut. Esc cancels.",
            Severity = InfoBarSeverity.Informational,
            MaxWidth = 360,
        };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var save = new Button { Content = "Save", IsEnabled = false };
        var cancel = new Button { Content = "Cancel" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(recorder);
        content.Children.Add(status);
        content.Children.Add(buttons);
        var flyout = new Flyout { Content = content };
        HotKeyDefinition? candidate = null;

        void SetStatus(string message, InfoBarSeverity severity)
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
            if (modifiers == 0 && args.Key is VirtualKey.Tab or VirtualKey.Enter or VirtualKey.Escape)
            {
                return;
            }

            args.Handled = true;
            candidate = null;
            save.IsEnabled = false;
            if (IsModifierKey(args.Key))
            {
                SetStatus("Now press a non-modifier key while holding the modifier.", InfoBarSeverity.Informational);
                return;
            }

            var proposed = new HotKeyDefinition(modifiers, (uint)args.Key);
            recorder.Text = proposed.DisplayString;
            var validation = hotKeys.ValidateBinding(action, proposed);
            if (!validation.IsValid)
            {
                SetStatus(HotKeyEditOperation.ValidationMessage(validation), InfoBarSeverity.Error);
                return;
            }

            candidate = proposed;
            save.IsEnabled = true;
            SetStatus($"{proposed.DisplayString} is ready. Choose Save to apply it.", InfoBarSeverity.Success);
        }

        void OnSave(object sender, RoutedEventArgs args)
        {
            if (candidate is not HotKeyDefinition proposed)
            {
                SetStatus("Press a valid shortcut before saving.", InfoBarSeverity.Error);
                return;
            }

            if (!edit.TryApply(action, proposed, out var error))
            {
                SetStatus(error, InfoBarSeverity.Error);
                recorder.Focus(FocusState.Programmatic);
                return;
            }

            report($"{HotKeyEditOperation.ActionName(action)} shortcut changed to {proposed.DisplayString}.", InfoBarSeverity.Success);
            flyout.Hide();
        }

        void OnCancel(object sender, RoutedEventArgs args) => flyout.Hide();
        void OnOpened(object? sender, object args) => recorder.Focus(FocusState.Programmatic);
        void OnClosed(object? sender, object args)
        {
            recorder.KeyDown -= OnKey;
            save.Click -= OnSave;
            cancel.Click -= OnCancel;
            flyout.Opened -= OnOpened;
            flyout.Closed -= OnClosed;
            anchor.Unloaded -= OnUnloaded;
            if (anchor is Control control)
            {
                control.Focus(FocusState.Programmatic);
            }
        }
        void OnUnloaded(object sender, RoutedEventArgs args) => flyout.Hide();

        recorder.KeyDown += OnKey;
        save.Click += OnSave;
        cancel.Click += OnCancel;
        flyout.Opened += OnOpened;
        flyout.Closed += OnClosed;
        anchor.Unloaded += OnUnloaded;
        flyout.ShowAt(anchor);
    }

    private static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static HotKeyModifiers CurrentModifiers()
    {
        HotKeyModifiers modifiers = 0;
        if (IsKeyDown(VirtualKey.Control)) modifiers |= HotKeyModifiers.Control;
        if (IsKeyDown(VirtualKey.Shift)) modifiers |= HotKeyModifiers.Shift;
        if (IsKeyDown(VirtualKey.Menu)) modifiers |= HotKeyModifiers.Alt;
        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows)) modifiers |= HotKeyModifiers.Win;
        return modifiers;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
