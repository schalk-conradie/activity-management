using System.Collections.ObjectModel;
using ActivityManagement.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace ActivityManagement.App;

public sealed partial class MainWindow : Window
{
    private const int FlyoutWidth = 460;
    private const int FlyoutHeight = 580;
    private readonly TaskStore _store;
    private readonly Action _showQuickCreate;
    private readonly Action _tasksChanged;
    private readonly Func<string?> _getIntegrationStatus;
    private bool _closeForShutdown;
    private bool _isDialogOpen;

    public MainWindow(TaskStore store, Action showQuickCreate, Action tasksChanged, Func<string?> getIntegrationStatus)
    {
        _store = store;
        _showQuickCreate = showQuickCreate;
        _tasksChanged = tasksChanged;
        _getIntegrationStatus = getIntegrationStatus;

        InitializeComponent();
        WindowPlacement.ConfigureFlyout(this, FlyoutWidth, FlyoutHeight);
        WindowPlacement.HideInsteadOfClose(this, () => !_closeForShutdown);
        AddEscapeToHide();
        RefreshTasks();
    }

    public ObservableCollection<TaskRow> Tasks { get; } = [];

    public void CloseForShutdown()
    {
        _closeForShutdown = true;
        Close();
    }

    private void AddEscapeToHide()
    {
        var accelerator = new KeyboardAccelerator { Key = VirtualKey.Escape };
        accelerator.Invoked += (_, args) =>
        {
            if (_isDialogOpen)
            {
                return;
            }

            NativeWindowStyles.Hide(this);
            args.Handled = true;
        };
        Content.KeyboardAccelerators.Add(accelerator);
    }

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
        var priorityBox = CreatePriorityBox(ActivityTaskPriority.Normal);

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

        ContentDialogResult result;
        _isDialogOpen = true;
        try
        {
            result = await dialog.ShowAsync();
        }
        finally
        {
            _isDialogOpen = false;
        }

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

    private async void TasksList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TaskRow taskRow)
        {
            await ShowTaskDetailDialog(taskRow.Id);
        }
    }

    private async Task ShowTaskDetailDialog(long id)
    {
        var task = _store.Get(id);
        if (task is null)
        {
            RefreshTasks();
            return;
        }

        var titleBox = new TextBox
        {
            Header = "Title",
            Text = task.Title
        };
        var dueDatePicker = new CalendarDatePicker
        {
            Header = "Due date",
            Date = task.DueAt?.ToLocalTime()
        };
        var dueTimePicker = new TimePicker
        {
            Header = "Due time",
            ClockIdentifier = "24HourClock",
            SelectedTime = task.DueAt?.ToLocalTime().TimeOfDay
        };
        var priorityBox = CreatePriorityBox(task.Priority);
        priorityBox.Header = "Priority";
        var statusBox = CreateStatusBox(task.Status);
        statusBox.Header = "Status";
        var noteBox = new TextBox
        {
            Header = "Note",
            Text = task.Note ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 96
        };
        var sourceBox = new TextBox
        {
            Header = "Source",
            Text = task.Source ?? string.Empty
        };
        var referenceBox = new TextBox
        {
            Header = "Reference",
            Text = task.ExternalReference ?? string.Empty
        };
        var datesText = new TextBlock
        {
            Text = $"Created {FormatDateTime(task.CreatedAt)} - Updated {FormatDateTime(task.UpdatedAt)}",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(titleBox);
        panel.Children.Add(dueDatePicker);
        panel.Children.Add(dueTimePicker);
        panel.Children.Add(priorityBox);
        panel.Children.Add(statusBox);
        panel.Children.Add(noteBox);
        panel.Children.Add(sourceBox);
        panel.Children.Add(referenceBox);
        panel.Children.Add(datesText);

        var dialog = new ContentDialog
        {
            Title = $"Task {task.Id}",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 440,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            XamlRoot = Content.XamlRoot
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            var title = titleBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                args.Cancel = true;
                titleBox.Focus(FocusState.Programmatic);
                return;
            }

            var updated = _store.Update(new ActivityTaskUpdate(
                task.Id,
                title,
                CombineDateAndTime(dueDatePicker.Date, dueTimePicker.SelectedTime),
                SelectedPriority(priorityBox),
                SelectedStatus(statusBox),
                NullIfWhiteSpace(sourceBox.Text),
                NullIfWhiteSpace(referenceBox.Text),
                NullIfWhiteSpace(noteBox.Text)));

            if (updated is null)
            {
                RefreshTasks();
                return;
            }

            _tasksChanged();
        };

        _isDialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _isDialogOpen = false;
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

    private static string SelectedStatus(ComboBox statusBox)
    {
        return ((ComboBoxItem?)statusBox.SelectedItem)?.Tag?.ToString() ?? ActivityTaskStatus.Pending;
    }

    private static ComboBox CreatePriorityBox(string selectedPriority)
    {
        var priorityBox = new ComboBox();
        AddComboBoxItem(priorityBox, "Low", ActivityTaskPriority.Low, selectedPriority);
        AddComboBoxItem(priorityBox, "Normal", ActivityTaskPriority.Normal, selectedPriority);
        AddComboBoxItem(priorityBox, "High", ActivityTaskPriority.High, selectedPriority);
        AddComboBoxItem(priorityBox, "Urgent", ActivityTaskPriority.Urgent, selectedPriority);
        return priorityBox;
    }

    private static ComboBox CreateStatusBox(string selectedStatus)
    {
        var statusBox = new ComboBox();
        AddComboBoxItem(statusBox, "Pending", ActivityTaskStatus.Pending, selectedStatus);
        AddComboBoxItem(statusBox, "In progress", ActivityTaskStatus.InProgress, selectedStatus);
        AddComboBoxItem(statusBox, "Done", ActivityTaskStatus.Done, selectedStatus);
        AddComboBoxItem(statusBox, "Canceled", ActivityTaskStatus.Canceled, selectedStatus);
        return statusBox;
    }

    private static void AddComboBoxItem(ComboBox comboBox, string content, string tag, string selectedTag)
    {
        var item = new ComboBoxItem { Content = content, Tag = tag };
        comboBox.Items.Add(item);
        if (tag == selectedTag)
        {
            comboBox.SelectedItem = item;
        }
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

    private static string FormatDateTime(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("g");
    }

    private static string? NullIfWhiteSpace(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
