using ActivityManagement.Core;

namespace ActivityManagement.Tests;

public sealed class TaskStoreTests
{
    [Fact]
    public void InitializeCreatesDatabaseAndVersionedSchema()
    {
        using var fixture = TestDatabase.Create();
        fixture.Store.Initialize();

        Assert.True(File.Exists(fixture.DatabasePath));
    }

    [Fact]
    public void ListUnfinishedOrdersByDueDateThenCreatedDate()
    {
        using var fixture = TestDatabase.Create();
        fixture.Store.Initialize();
        var now = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);

        var noDue = fixture.Store.Create(new NewActivityTask("No due date"), now.AddMinutes(1));
        var secondDue = fixture.Store.Create(new NewActivityTask("Second due", now.AddDays(2)), now);
        var firstDueLaterCreated = fixture.Store.Create(new NewActivityTask("First due later created", now.AddDays(1)), now.AddMinutes(2));
        var firstDueEarlierCreated = fixture.Store.Create(new NewActivityTask("First due earlier created", now.AddDays(1)), now.AddMinutes(1));

        var tasks = fixture.Store.ListUnfinished();

        Assert.Equal(
            [firstDueEarlierCreated.Id, firstDueLaterCreated.Id, secondDue.Id, noDue.Id],
            tasks.Select(task => task.Id));
    }

    [Fact]
    public void DoneAndCanceledTasksAreNotUnfinished()
    {
        using var fixture = TestDatabase.Create();
        fixture.Store.Initialize();

        var active = fixture.Store.Create(new NewActivityTask("Active"));
        var done = fixture.Store.Create(new NewActivityTask("Done"));
        var canceled = fixture.Store.Create(new NewActivityTask("Canceled"));

        Assert.True(fixture.Store.UpdateStatus(done.Id, ActivityTaskStatus.Done));
        Assert.True(fixture.Store.UpdateStatus(canceled.Id, ActivityTaskStatus.Canceled));

        var tasks = fixture.Store.ListUnfinished();

        Assert.Equal(active.Id, Assert.Single(tasks).Id);
    }

    [Fact]
    public void CreateStoresOptionalNote()
    {
        using var fixture = TestDatabase.Create();
        fixture.Store.Initialize();

        var task = fixture.Store.Create(new NewActivityTask("With note", Note: "Remember the context."));

        Assert.Equal("Remember the context.", fixture.Store.Get(task.Id)?.Note);
    }

    [Fact]
    public void CreateRecurringTaskCreatesInitialScheduledTask()
    {
        using var fixture = TestDatabase.Create();
        fixture.Store.Initialize();
        var now = LocalDateTime(2026, 7, 2, 10, 0);

        var schedule = fixture.Store.CreateRecurringTask(new NewRecurringTask(
            "Timesheet",
            DayOfWeek.Friday,
            new TimeSpan(16, 0, 0),
            ActivityTaskPriority.High,
            "https://example.com/timesheet",
            "Submit weekly timesheet"), now);

        Assert.NotNull(schedule.LastTaskId);
        var task = fixture.Store.Get(schedule.LastTaskId.Value);

        Assert.NotNull(task);
        Assert.Equal(schedule.Id, task!.RecurringTaskId);
        Assert.Equal("Timesheet", task.Title);
        Assert.Equal(ActivityTaskPriority.High, task.Priority);
        Assert.Equal("recurring", task.Source);
        Assert.Equal("https://example.com/timesheet", task.ExternalReference);
        Assert.Equal("Submit weekly timesheet", task.Note);
        Assert.Equal(DayOfWeek.Friday, task.DueAt!.Value.ToLocalTime().DayOfWeek);
        Assert.Equal(new TimeSpan(16, 0, 0), task.DueAt.Value.ToLocalTime().TimeOfDay);
        Assert.Equal(schedule.Id, Assert.Single(fixture.Store.ListRecurringTasks()).Id);
    }

    [Fact]
    public void CompletingRecurringTaskCreatesNextFutureTaskOnce()
    {
        using var fixture = TestDatabase.Create();
        fixture.Store.Initialize();
        var now = LocalDateTime(2026, 7, 2, 10, 0);
        var schedule = fixture.Store.CreateRecurringTask(new NewRecurringTask(
            "Timesheet",
            DayOfWeek.Friday,
            new TimeSpan(16, 0, 0)), now);
        var firstTask = fixture.Store.Get(schedule.LastTaskId!.Value)!;
        var completedAt = firstTask.DueAt!.Value.AddDays(3);

        Assert.True(fixture.Store.UpdateStatus(firstTask.Id, ActivityTaskStatus.Done, completedAt));

        var nextTask = Assert.Single(fixture.Store.ListUnfinished(), task => task.RecurringTaskId == schedule.Id);
        Assert.NotEqual(firstTask.Id, nextTask.Id);
        Assert.Equal(DayOfWeek.Friday, nextTask.DueAt!.Value.ToLocalTime().DayOfWeek);
        Assert.Equal(new TimeSpan(16, 0, 0), nextTask.DueAt.Value.ToLocalTime().TimeOfDay);
        Assert.True(nextTask.DueAt.Value > completedAt);

        Assert.True(fixture.Store.UpdateStatus(firstTask.Id, ActivityTaskStatus.Done, completedAt.AddMinutes(1)));
        Assert.Single(fixture.Store.ListUnfinished(), task => task.RecurringTaskId == schedule.Id);
    }

    [Fact]
    public void UpdateStoresEditableTaskProperties()
    {
        using var fixture = TestDatabase.Create();
        fixture.Store.Initialize();
        var createdAt = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddHours(1);
        var dueAt = createdAt.AddDays(1);
        var task = fixture.Store.Create(new NewActivityTask("Original"), createdAt);

        var updated = fixture.Store.Update(new ActivityTaskUpdate(
            task.Id,
            "Updated",
            dueAt,
            ActivityTaskPriority.Urgent,
            ActivityTaskStatus.InProgress,
            "manual",
            "https://example.com/task",
            "More context"), updatedAt);

        Assert.NotNull(updated);
        Assert.Equal("Updated", updated.Title);
        Assert.Equal(dueAt, updated.DueAt);
        Assert.Equal(ActivityTaskPriority.Urgent, updated.Priority);
        Assert.Equal(ActivityTaskStatus.InProgress, updated.Status);
        Assert.Equal("manual", updated.Source);
        Assert.Equal("https://example.com/task", updated.ExternalReference);
        Assert.Equal("More context", updated.Note);
        Assert.Equal(createdAt, updated.CreatedAt);
        Assert.Equal(updatedAt, updated.UpdatedAt);
    }

    [Fact]
    public void UpdatingRecurringTaskToDoneCreatesNextTask()
    {
        using var fixture = TestDatabase.Create();
        fixture.Store.Initialize();
        var now = LocalDateTime(2026, 7, 2, 10, 0);
        var schedule = fixture.Store.CreateRecurringTask(new NewRecurringTask(
            "Timesheet",
            DayOfWeek.Friday,
            new TimeSpan(16, 0, 0)), now);
        var firstTask = fixture.Store.Get(schedule.LastTaskId!.Value)!;

        var updated = fixture.Store.Update(new ActivityTaskUpdate(
            firstTask.Id,
            firstTask.Title,
            firstTask.DueAt,
            firstTask.Priority,
            ActivityTaskStatus.Done,
            firstTask.Source,
            firstTask.ExternalReference,
            firstTask.Note), firstTask.DueAt!.Value.AddMinutes(30));

        Assert.NotNull(updated);
        Assert.Single(fixture.Store.ListUnfinished(), task => task.RecurringTaskId == schedule.Id);
    }

    [Fact]
    public void ReminderCandidatesIncludeDueAndImportantUnfinishedTasks()
    {
        using var fixture = TestDatabase.Create();
        fixture.Store.Initialize();
        var now = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);

        var due = fixture.Store.Create(new NewActivityTask("Due", now.AddMinutes(-1)), now.AddHours(-2));
        var high = fixture.Store.Create(new NewActivityTask("High priority", Priority: ActivityTaskPriority.High), now.AddHours(-1));
        _ = fixture.Store.Create(new NewActivityTask("Later", now.AddDays(1)), now);
        var snoozed = fixture.Store.Create(new NewActivityTask("Snoozed", now.AddMinutes(-1)), now);
        fixture.Store.SnoozeReminder(snoozed.Id, TimeSpan.FromMinutes(30), now);

        var tasks = fixture.Store.ListReminderCandidates(now, TimeSpan.FromMinutes(30));

        Assert.Equal([due.Id, high.Id], tasks.Select(task => task.Id));
    }

    private static DateTimeOffset LocalDateTime(int year, int month, int day, int hour, int minute)
    {
        var localDateTime = new DateTime(year, month, day, hour, minute, 0);
        return new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string _directory;

        private TestDatabase(string directory)
        {
            _directory = directory;
            DatabasePath = Path.Combine(directory, "activity.db");
            Store = new TaskStore(DatabasePath);
        }

        public string DatabasePath { get; }

        public TaskStore Store { get; }

        public static TestDatabase Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "activity-management-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return new TestDatabase(directory);
        }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
