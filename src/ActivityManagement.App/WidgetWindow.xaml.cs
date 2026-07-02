using ActivityManagement.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace ActivityManagement.App;

public sealed partial class WidgetWindow : UserControl, IDisposable
{
    private readonly TaskStore _store;
    private readonly Action _showFlyout;
    private readonly Action _showQuickCreate;
    private readonly TaskbarWidgetHost _taskbarHost;
    private readonly DispatcherTimer _timer = new();

    public WidgetWindow(TaskStore store, Action showFlyout, Action showQuickCreate)
    {
        _store = store;
        _showFlyout = showFlyout;
        _showQuickCreate = showQuickCreate;

        InitializeComponent();
        _taskbarHost = new TaskbarWidgetHost(this, 270);
        _taskbarHost.Attach();
        QueuePositionUpdate(TimeSpan.FromSeconds(1));
        QueuePositionUpdate(TimeSpan.FromSeconds(3));
        QueuePositionUpdate(TimeSpan.FromSeconds(6));
        QueuePositionUpdate(TimeSpan.FromSeconds(10));

        _timer.Interval = TimeSpan.FromSeconds(15);
        _timer.Tick += (_, _) =>
        {
            Refresh();
            _ = Task.Run(_taskbarHost.UpdatePosition);
        };
        _timer.Start();
        Refresh();
    }

    public string? HostError => _taskbarHost.Error;

    public RectInt32? WidgetBounds => _taskbarHost.ScreenBounds;

    public void Dispose()
    {
        _timer.Stop();
        _taskbarHost.Dispose();
    }

    public void Refresh()
    {
        var tasks = _store.ListUnfinished();
        var next = tasks.FirstOrDefault();

        CountText.Text = tasks.Count.ToString();
        NextTitleText.Text = next?.Title ?? "No unfinished tasks";
        NextDueText.Text = next is null ? "Ctrl+Shift+K to add" : FormatDue(next.DueAt);
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (point.Properties.IsRightButtonPressed)
        {
            _showQuickCreate();
            return;
        }

        _showFlyout();
    }

    private async void QueuePositionUpdate(TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        _taskbarHost.UpdatePosition();
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
