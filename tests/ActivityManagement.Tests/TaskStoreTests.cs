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
