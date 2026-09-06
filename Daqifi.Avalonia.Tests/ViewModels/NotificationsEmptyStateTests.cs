using System.ComponentModel;
using Daqifi.Desktop.DialogService;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Issue #250: the notifications flyout rendered a blank panel under its title when there was
/// nothing to show, so "no notifications" and "failed to load" looked identical.
///
/// <para>The flyout now swaps between the list and a named empty state on
/// <see cref="DaqifiViewModel.HasNotifications"/>. The swap itself is XAML and is verified by the
/// parity-audit capture, not here — views in this repo carry no <c>x:DataType</c>, so a binding
/// resolves by reflection at run time and no test in this project can see it. What IS testable, and
/// what these tests pin, is the contract the binding depends on: that the property exists under the
/// exact name the XAML binds, that it reports the collection's state, and above all that it
/// change-notifies on every mutation.</para>
///
/// <para>That last point is the one with teeth. <c>HasNotifications</c> is computed, so if it fails
/// to notify the flyout keeps whichever half it drew first — an empty state stranded over a list
/// that has items in it, which is a worse bug than the blank panel this issue is about. It is
/// deliberately NOT derived from <c>NotificationCount</c>: <c>UpdateUi</c> assigns that property the
/// *version*-notification count straight off <c>VersionNotification</c>, so it can differ from the
/// list's own Count, and two sites add to the list without touching it at all
/// (<see cref="AddingWithoutTouchingNotificationCount_StillNotifies"/> is that case).</para>
/// </summary>
[Collection(AppHostCollection.Name)]
public class NotificationsEmptyStateTests
{
    private static DaqifiViewModel NewViewModel()
    {
        // Same host setup the other DaqifiViewModel suite uses; idempotent, and it points at the
        // throwaway data directory the assembly's module initializer already set DAQIFI_DATA_DIR to.
        Daqifi.Desktop.App.InitializeMobile();
        return new DaqifiViewModel(new NullDialogService());
    }

    private static Notifications ANotification(string message = "Firmware 1.2.3 is available")
        => new() { Message = message };

    /// <summary>Records every <c>HasNotifications</c> change notification the view model raises.</summary>
    private static List<string> WatchHasNotifications(DaqifiViewModel viewModel)
    {
        var raised = new List<string>();
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DaqifiViewModel.HasNotifications)) { raised.Add(e.PropertyName); }
        };
        return raised;
    }

    /// <summary>
    /// The state the flyout is in every time a user opens it with nothing outstanding — the state
    /// that rendered blank, and the one the capture in this PR shows named.
    /// </summary>
    [Fact]
    public void AFreshViewModel_HasNoNotifications()
    {
        var viewModel = NewViewModel();

        Assert.Empty(viewModel.NotificationList);
        Assert.False(viewModel.HasNotifications);
    }

    [Fact]
    public void AddingTheFirstNotification_FlipsTheFlagAndNotifies()
    {
        var viewModel = NewViewModel();
        var raised = WatchHasNotifications(viewModel);

        viewModel.NotificationList.Add(ANotification());

        Assert.True(viewModel.HasNotifications);
        Assert.NotEmpty(raised);
    }

    [Fact]
    public void RemovingTheLastNotification_FlipsBackAndNotifies()
    {
        var viewModel = NewViewModel();
        var notification = ANotification();
        viewModel.NotificationList.Add(notification);

        var raised = WatchHasNotifications(viewModel);
        viewModel.NotificationList.Remove(notification);

        Assert.False(viewModel.HasNotifications);
        Assert.NotEmpty(raised);
    }

    /// <summary>
    /// <c>Clear()</c> raises a Reset whose args carry no OldItems. Handlers that read the args
    /// rather than the collection miss it; this one reads the collection, so it must not.
    /// </summary>
    [Fact]
    public void ClearingTheList_ReturnsToTheEmptyStateAndNotifies()
    {
        var viewModel = NewViewModel();
        viewModel.NotificationList.Add(ANotification("one"));
        viewModel.NotificationList.Add(ANotification("two"));
        Assert.True(viewModel.HasNotifications);

        var raised = WatchHasNotifications(viewModel);
        viewModel.NotificationList.Clear();

        Assert.False(viewModel.HasNotifications);
        Assert.NotEmpty(raised);
    }

    /// <summary>
    /// The reason this is wired to CollectionChanged and not to <c>NotificationCount</c>. The
    /// WiFi-firmware guard in <c>UpdateWifiFirmwareOnly</c> adds a notification and returns without
    /// ever assigning <c>NotificationCount</c>; had the flag hung off that property, the flyout
    /// would have gone on showing "NO NOTIFICATIONS" over a list with a message in it.
    /// </summary>
    [Fact]
    public void AddingWithoutTouchingNotificationCount_StillNotifies()
    {
        var viewModel = NewViewModel();
        var countBefore = viewModel.NotificationCount;
        var raised = WatchHasNotifications(viewModel);

        viewModel.NotificationList.Add(ANotification("Please connect device Nq1-GHOST before updating WiFi firmware."));

        Assert.Equal(countBefore, viewModel.NotificationCount);   // the stale half
        Assert.True(viewModel.HasNotifications);                  // the half the flyout binds
        Assert.NotEmpty(raised);
    }
}
