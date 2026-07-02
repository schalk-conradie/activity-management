using System.Collections.ObjectModel;
using ActivityManagement.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace ActivityManagement.App;

public sealed partial class MainWindow : Window
{
    private const int FlyoutWidth = 460;
    private const int FlyoutHeight = 580;
    private readonly TaskStore _store;
    private readonly Action _showQuickCreate;
    private readonly Action _tasksChanged;
    private readonly Func<string?> _getIntegrationStatus;

    public MainWindow(TaskStore store, Action showQuickCreate, Action tasksChanged, Func<string?> getIntegrationStatus)
    {
        _store = store;
        _showQuickCreate = showQuickCreate;
        _tasksChanged = tasksChanged;
        _getIntegrationStatus = getIntegrationStatus;

        InitializeComponent();
        WindowPlacement.ConfigureFlyout(this, FlyoutWidth, FlyoutHeight);
        RefreshTasks();
    }

    public ObservableCollection<TaskRow> Tasks { get; } = [];

    public void ShowNearWidget(RectInt32? widgetBounds)
    {
        RefreshTasks();
        if (widgetBounds is { } bounds)
        {
            WindowPlacement.MoveAboveAnchor(this, bounds, FlyoutWidth, FlyoutHeight, margin: 8);
        }
        else
        {
            WindowPlacement.MoveNearTray(this, FlyoutWidth, FlyoutHeight, yOffset: 12);
        }

        Activate();
    }

    public async void ShowQuickCreateDialog(RectInt32? widgetBounds)
    {
        ShowNearWidget(widgetBounds);

        var titleBox = new TextBox { PlaceholderText = "Task title" };
        var dueDatePicker = new CalendarDatePicker { PlaceholderText = "Due date" };
        var dueTimePicker = new TimePicker { ClockIdentifier = "24HourClock" };
        var priorityBox = new ComboBox { SelectedIndex = 1 };
        priorityBox.Items.Add(new ComboBoxItem { Content = "Low", Tag = ActivityTaskPriority.Low });
        priorityBox.Items.Add(new ComboBoxItem { Content = "Normal", Tag = ActivityTaskPriority.Normal });
        priorityBox.Items.Add(new ComboBoxItem { Content = "High", Tag = ActivityTaskPriority.High });
        priorityBox.Items.Add(new ComboBoxItem { Content = "Urgent", Tag = ActivityTaskPriority.Urgent });

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(titleBox);
        panel.Children.Add(dueDatePicker);
        panel.Children.Add(dueTimePicker);
        panel.Children.Add(priorityBox);

        var dialog = new ContentDialog
        {
            Title = "New task",
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = panel,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        CreateTask(titleBox.Text, dueDatePicker.Date, dueTimePicker.SelectedTime, SelectedPriority(priorityBox));
    }

    public void RefreshTasks()
    {
        var tasks = _store.ListUnfinished();
        Tasks.Clear();
        foreach (var task in tasks)
        {
            Tasks.Add(TaskRow.FromTask(task));
        }

        var nextDue = tasks.FirstOrDefault();
        SummaryText.Text = $"{tasks.Count} unfinished";
        NextDueText.Text = nextDue is null
            ? "No unfinished tasks."
            : $"Next: {nextDue.Title} - {FormatDue(nextDue.DueAt)}";

        var integrationStatus = _getIntegrationStatus();
        StatusText.Text = string.IsNullOrWhiteSpace(integrationStatus)
            ? $"Database: {_store.DatabasePath}"
            : $"Database: {_store.DatabasePath}{Environment.NewLine}{integrationStatus}";
    }

    private void AddTask_Click(object sender, RoutedEventArgs e)
    {
        CreateTask(TitleBox.Text, DueDatePicker.Date, DueTimePicker.SelectedTime, SelectedPriority(PriorityBox));
    }

    private void MarkDone_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetTaskId(sender, out var id) && _store.UpdateStatus(id, ActivityTaskStatus.Done))
        {
            _tasksChanged();
        }
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetTaskId(sender, out var id) && _store.UpdateStatus(id, ActivityTaskStatus.Canceled))
        {
            _tasksChanged();
        }
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTaskId(sender, out var id))
        {
            return;
        }

        var task = _store.Get(id);
        if (string.IsNullOrWhiteSpace(task?.ExternalReference))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = task.ExternalReference,
            UseShellExecute = true
        });
    }

    private void CreateTask(string title, DateTimeOffset? selectedDate, TimeSpan? selectedTime, string priority)
    {
        _store.Create(new NewActivityTask(
            title,
            CombineDateAndTime(selectedDate, selectedTime),
            priority,
            Source: "manual"));

        TitleBox.Text = string.Empty;
        DueDatePicker.Date = null;
        DueTimePicker.SelectedTime = null;
        PriorityBox.SelectedIndex = 1;
        _tasksChanged();
    }

    private static DateTimeOffset? CombineDateAndTime(DateTimeOffset? selectedDate, TimeSpan? selectedTime)
    {
        if (selectedDate is null)
        {
            return null;
        }

        var date = selectedDate.Value.Date;
        var time = selectedTime ?? TimeSpan.Zero;
        var localDateTime = date.Add(time);
        return new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
    }

    private static string SelectedPriority(ComboBox priorityBox)
    {
        return ((ComboBoxItem?)priorityBox.SelectedItem)?.Tag?.ToString() ?? ActivityTaskPriority.Normal;
    }

    private static bool TryGetTaskId(object sender, out long id)
    {
        id = 0;
        return sender is FrameworkElement { Tag: long taskId } && (id = taskId) > 0;
    }

    private static string FormatDue(DateTimeOffset? dueAt)
    {
        if (dueAt is null)
        {
            return "No due date";
        }

        var local = dueAt.Value.ToLocalTime();
        return local < DateTimeOffset.Now
            ? $"Overdue {local:g}"
            : $"Due {local:g}";
    }
}

public sealed class TaskRow
{
    public long Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Metadata { get; set; } = string.Empty;

    public bool HasExternalReference { get; set; }

    public static TaskRow FromTask(ActivityTask task)
    {
        return new TaskRow
        {
            Id = task.Id,
            Title = task.Title,
            Metadata = string.Join("  |  ", new[]
            {
                FormatDue(task.DueAt),
                task.Priority,
                task.Source
            }.Where(value => !string.IsNullOrWhiteSpace(value))),
            HasExternalReference = !string.IsNullOrWhiteSpace(task.ExternalReference)
        };
    }

    private static string FormatDue(DateTimeOffset? dueAt)
    {
        if (dueAt is null)
        {
            return "No due date";
        }

        var local = dueAt.Value.ToLocalTime();
        return local < DateTimeOffset.Now
            ? $"Overdue {local:g}"
            : $"Due {local:g}";
    }
}
