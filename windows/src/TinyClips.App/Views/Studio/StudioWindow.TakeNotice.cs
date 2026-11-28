using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TinyClips.Core.Studio;

namespace TinyClips.App.Views.Studio;

// A take recorded with Record another segment is copied into the project and also stays in the
// Library as a draft of its own. The notice says so once, and leads to that draft.
public sealed partial class StudioWindow
{
    private static readonly TimeSpan TakeNoticeDuration = TimeSpan.FromSeconds(8);

    private DispatcherQueueTimer? _takeNoticeTimer;
    private StudioTakeReference? _takeNotice;
    private bool _isPointerOnTakeNotice;
    private bool _isTakeNoticeConnected;

    private void ShowTakeNotice(StudioProject take)
    {
        if (_isClosed)
        {
            return;
        }

        ConnectTakeNotice();
        var message = StudioTakeLinkage.SavedAsDraftNotice(take.Name);
        _takeNotice = new StudioTakeReference(take.Id, StudioTakeLinkage.LibraryName(take.Name));
        TakeNoticeBar.Message = message;
        TakeNoticeBar.Visibility = Visibility.Visible;
        TakeNoticeBar.IsOpen = true;
        Announce(message, "StudioTakeSavedAsDraft", AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.All);

        _takeNoticeTimer ??= CreateTakeNoticeTimer();
        _takeNoticeTimer.Stop();
        _takeNoticeTimer.Start();
    }

    private void ConnectTakeNotice()
    {
        if (_isTakeNoticeConnected)
        {
            return;
        }

        _isTakeNoticeConnected = true;
        TakeNoticeBar.PointerEntered += OnTakeNoticePointerEntered;
        TakeNoticeBar.PointerExited += OnTakeNoticePointerExited;
        TakeNoticeBar.PointerCanceled += OnTakeNoticePointerExited;
    }

    private DispatcherQueueTimer CreateTakeNoticeTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TakeNoticeDuration;
        timer.IsRepeating = false;
        timer.Tick += OnTakeNoticeTimerTick;
        return timer;
    }

    private void OnTakeNoticeTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_isClosed || !TakeNoticeBar.IsOpen)
        {
            return;
        }

        // Kept while it is being read or used; it goes once both have moved on.
        if (_isPointerOnTakeNotice || IsFocusInTakeNotice())
        {
            sender.Start();
            return;
        }

        HideTakeNotice();
    }

    private bool IsFocusInTakeNotice()
    {
        if (RootGrid.XamlRoot is not { } root)
        {
            return false;
        }

        for (var element = FocusManager.GetFocusedElement(root) as DependencyObject;
             element is not null;
             element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, TakeNoticeBar))
            {
                return true;
            }
        }

        return false;
    }

    private void OnTakeNoticePointerEntered(object sender, PointerRoutedEventArgs e) => _isPointerOnTakeNotice = true;

    private void OnTakeNoticePointerExited(object sender, PointerRoutedEventArgs e) => _isPointerOnTakeNotice = false;

    private void OnTakeNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        _takeNoticeTimer?.Stop();
        _isPointerOnTakeNotice = false;
        sender.Visibility = Visibility.Collapsed;
    }

    private void HideTakeNotice()
    {
        _takeNoticeTimer?.Stop();
        TakeNoticeBar.IsOpen = false;
        TakeNoticeBar.Visibility = Visibility.Collapsed;
    }

    private void OnShowTakeInLibrary(object sender, RoutedEventArgs e)
    {
        if (_takeNotice is { } take)
        {
            HideTakeNotice();
            ShowInLibrary(take);
        }
    }

    private void OnShowInLibraryRequested(object? sender, StudioTakeReference project) => ShowInLibrary(project);

    private void ShowInLibrary(StudioTakeReference project)
    {
        if (!_isClosed)
        {
            (Application.Current as App)?.ShowStudioProjectInLibrary(project.ProjectId, project.Name);
        }
    }

    private void DisconnectTakeNotice()
    {
        if (_takeNoticeTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnTakeNoticeTimerTick;
            _takeNoticeTimer = null;
        }

        if (_isTakeNoticeConnected)
        {
            _isTakeNoticeConnected = false;
            TakeNoticeBar.PointerEntered -= OnTakeNoticePointerEntered;
            TakeNoticeBar.PointerExited -= OnTakeNoticePointerExited;
            TakeNoticeBar.PointerCanceled -= OnTakeNoticePointerExited;
        }
    }
}
