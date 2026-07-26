using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Daqifi.Desktop.Models;

namespace Daqifi.Avalonia.Views.Mobile;

/// <summary>
/// View model behind the mobile Notifications overlay (#11) — the phone analog of the desktop top
/// bar's notifications bell + flyout. It exposes the same <see cref="Notifications"/> item type the
/// desktop <c>NotificationsFlyout</c> binds to (message + optional link), a live count for the bell
/// badge, and an empty-state flag.
/// <para>
/// The desktop's notification <em>producers</em> — the firmware-update coordinator and the
/// <c>DaqifiViewModel</c> version/device notices — are main-window/coordinator bound and do not run
/// in the mobile shell, so this surface starts empty. It is a standalone, WPF-free view model (no
/// <c>DaqifiViewModel</c> host needed, mirroring the mobile <c>SettingsViewModel</c>) with a small
/// producer API (<see cref="Add"/> / <see cref="Remove"/> / <see cref="Clear"/>) so a future
/// mobile notification source (device/firmware alerts once the mobile connection path surfaces them)
/// can push to it without any UI change.
/// </para>
/// </summary>
public partial class MobileNotificationsViewModel : ObservableObject
{
    /// <summary>The notifications shown in the overlay, newest-relevant first. Bound to the list;
    /// the count/empty-state derive from it and refresh whenever it changes.</summary>
    public ObservableCollection<Notifications> NotificationList { get; } = new();

    public MobileNotificationsViewModel()
    {
        // Keep the derived bindings (badge count, empty state) in sync with the collection.
        NotificationList.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(NotificationCount));
            OnPropertyChanged(nameof(HasNotifications));
            OnPropertyChanged(nameof(HasNoNotifications));
        };
    }

    /// <summary>Number of notifications — drives the bell badge.</summary>
    public int NotificationCount => NotificationList.Count;

    /// <summary>True when there is at least one notification (badge visible).</summary>
    public bool HasNotifications => NotificationList.Count > 0;

    /// <summary>True when there are no notifications (the overlay shows its empty state).</summary>
    public bool HasNoNotifications => NotificationList.Count == 0;

    /// <summary>Producer API: add a notification (ignores null). For a future mobile notification
    /// source; the overlay + badge update automatically via the collection-changed handler.</summary>
    public void Add(Notifications notification)
    {
        if (notification is not null) { OnUi(() => NotificationList.Add(notification)); }
    }

    /// <summary>Producer API: remove a notification if present.</summary>
    public void Remove(Notifications notification) => OnUi(() => NotificationList.Remove(notification));

    /// <summary>Producer API: clear all notifications.</summary>
    public void Clear() => OnUi(() => NotificationList.Clear());

    // A future notification source (device/firmware alerts) may push from a background thread, and
    // NotificationList is UI-bound — so marshal every mutation onto the UI thread (run inline if
    // already there) to keep the producer API safe to call from anywhere.
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) { action(); }
        else { Dispatcher.UIThread.Post(action); }
    }
}
