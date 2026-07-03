using ActivityManagement.Core;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace ActivityManagement.App;

public sealed partial class App : Application
{
    private readonly TaskStore _store = TaskStore.ForDefaultLocation();
    private MainWindow? _mainWindow;
    private WidgetWindow? _widgetWindow;
    private ShellIntegrationHost? _shellIntegration;
    private ReminderNotifications? _reminderNotifications;
    private ReleaseUpdater? _releaseUpdater;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _store.Initialize();

        _mainWindow = CreateMainWindow();
        _widgetWindow = new WidgetWindow(_store, ShowFlyout, ShowQuickCreate);

        _shellIntegration = new ShellIntegrationHost(ShowFlyout, ShowQuickCreate, ExitApplication);
        _reminderNotifications = new ReminderNotifications(_store, _widgetWindow.DispatcherQueue, HandleNotificationAction, RefreshOpenWindows);
        _releaseUpdater = new ReleaseUpdater();
        _releaseUpdater.CheckForUpdates();

        var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activatedArgs.Kind == ExtendedActivationKind.AppNotification)
        {
            HandleNotificationActivation((AppNotificationActivatedEventArgs)activatedArgs.Data);
        }

        RefreshOpenWindows();
    }

    private MainWindow CreateMainWindow()
    {
        var window = new MainWindow(_store, ShowQuickCreate, RefreshOpenWindows, GetIntegrationStatus);
        window.Closed += (_, _) => _mainWindow = null;
        return window;
    }

    private void ShowFlyout()
    {
        _mainWindow ??= CreateMainWindow();
        _mainWindow.RefreshTasks();
        _mainWindow.ShowNearWidget(_widgetWindow?.WidgetBounds);
    }

    private void ShowQuickCreate()
    {
        _mainWindow ??= CreateMainWindow();
        _mainWindow.ShowQuickCreateDialog(_widgetWindow?.WidgetBounds);
    }

    private void HandleNotificationAction(string action, long taskId)
    {
        switch (action)
        {
            case ReminderNotifications.ActionMarkDone:
                _store.UpdateStatus(taskId, ActivityTaskStatus.Done);
                break;
            case ReminderNotifications.ActionSnooze:
                _store.SnoozeReminder(taskId, TimeSpan.FromMinutes(15));
                break;
            case ReminderNotifications.ActionDismiss:
                _store.SnoozeReminder(taskId, TimeSpan.FromDays(1));
                break;
            case ReminderNotifications.ActionOpenSource:
                OpenExternalReference(taskId);
                break;
            default:
                ShowFlyout();
                break;
        }

        RefreshOpenWindows();
    }

    private void HandleNotificationActivation(AppNotificationActivatedEventArgs args)
    {
        var values = args.Arguments;
        if (!values.ContainsKey(ReminderNotifications.ArgumentTaskId))
        {
            ShowFlyout();
            return;
        }

        var action = values.ContainsKey(ReminderNotifications.ArgumentAction)
            ? values[ReminderNotifications.ArgumentAction]
            : ReminderNotifications.ActionOpen;

        if (long.TryParse(values[ReminderNotifications.ArgumentTaskId], out var taskId))
        {
            HandleNotificationAction(action, taskId);
        }
    }

    private void OpenExternalReference(long taskId)
    {
        var task = _store.Get(taskId);
        var url = ExternalReferences.GetOpenableUrl(task?.ExternalReference);
        if (url is null)
        {
            ShowFlyout();
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private void RefreshOpenWindows()
    {
        _widgetWindow?.Refresh();
        _mainWindow?.RefreshTasks();
    }

    private string? GetIntegrationStatus()
    {
        var messages = new List<string>();

        if (!string.IsNullOrWhiteSpace(_shellIntegration?.TrayError))
        {
            messages.Add($"Tray icon unavailable: {_shellIntegration.TrayError}");
        }

        if (!string.IsNullOrWhiteSpace(_shellIntegration?.HotkeyError))
        {
            messages.Add($"Ctrl+Shift+K unavailable: {_shellIntegration.HotkeyError}");
        }

        if (!string.IsNullOrWhiteSpace(_widgetWindow?.HostError))
        {
            messages.Add($"Taskbar widget unavailable: {_widgetWindow.HostError}");
        }

        if (!string.IsNullOrWhiteSpace(_reminderNotifications?.Error))
        {
            messages.Add($"Notifications unavailable: {_reminderNotifications.Error}");
        }

        if (!string.IsNullOrWhiteSpace(_releaseUpdater?.Error))
        {
            messages.Add($"Updater unavailable: {_releaseUpdater.Error}");
        }

        return messages.Count == 0 ? null : string.Join(Environment.NewLine, messages);
    }

    private void ExitApplication()
    {
        _reminderNotifications?.Dispose();
        _widgetWindow?.Dispose();
        _shellIntegration?.Dispose();
        _mainWindow?.CloseForShutdown();
        Exit();
    }
}
