using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ActivityManagement.Core;

public static class ActivityPaths
{
    public static string DataDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                throw new InvalidOperationException("The user profile directory could not be resolved.");
            }

            return Path.Combine(home, ".activity-management");
        }
    }

    public static string DatabasePath => Path.Combine(DataDirectory, "activity.db");
}

public static class ActivityTaskStatus
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Done = "done";
    public const string Canceled = "canceled";
}

public static class ActivityTaskPriority
{
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";
    public const string Urgent = "urgent";
}

public sealed record NewActivityTask(
    string Title,
    DateTimeOffset? DueAt = null,
    string Priority = ActivityTaskPriority.Normal,
    string Status = ActivityTaskStatus.Pending,
    string? Source = "manual",
    string? ExternalReference = null,
    string? Note = null,
    long? RecurringTaskId = null);

public sealed record ActivityTaskUpdate(
    long Id,
    string Title,
    DateTimeOffset? DueAt,
    string Priority,
    string Status,
    string? Source,
    string? ExternalReference,
    string? Note);

public sealed record ActivityTask(
    long Id,
    string Title,
    DateTimeOffset? DueAt,
    string Priority,
    string Status,
    string? Source,
    string? ExternalReference,
    string? Note,
    long? RecurringTaskId,
    DateTimeOffset? ReminderSnoozedUntil,
    DateTimeOffset? LastNotifiedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record NewRecurringTask(
    string Title,
    DayOfWeek DayOfWeek,
    TimeSpan TimeOfDay,
    string Priority = ActivityTaskPriority.Normal,
    string? ExternalReference = null,
    string? Note = null);

public sealed record RecurringTaskSchedule(
    long Id,
    string Title,
    DayOfWeek DayOfWeek,
    TimeSpan TimeOfDay,
    string Priority,
    string? ExternalReference,
    string? Note,
    bool IsActive,
    long? LastTaskId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed class TaskStore
{
    private const int CurrentSchemaVersion = 3;
    private const string TaskColumns = """
        id,
        title,
        due_at,
        priority,
        status,
        source,
        external_reference,
        note,
        recurring_task_id,
        reminder_snoozed_until,
        last_notified_at,
        created_at,
        updated_at
        """;
    private const string RecurringTaskColumns = """
        id,
        title,
        day_of_week,
        time_of_day_minutes,
        priority,
        external_reference,
        note,
        is_active,
        last_task_id,
        created_at,
        updated_at
        """;
    private readonly string _databasePath;
    private bool _initialized;

    public TaskStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public static TaskStore ForDefaultLocation() => new(ActivityPaths.DatabasePath);

    public string DatabasePath => _databasePath;

    public void Initialize()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = OpenConnection();
        var version = GetSchemaVersion(connection);

        if (version == 0)
        {
            ApplySchemaVersion1(connection);
            version = 1;
        }

        if (version == 1)
        {
            ApplySchemaVersion2(connection);
            version = 2;
        }

        if (version == 2)
        {
            ApplySchemaVersion3(connection);
            version = 3;
        }

        if (version != CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Unsupported activity database schema version {version}.");
        }

        _initialized = true;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("TaskStore has not been initialized. Call Initialize() first.");
        }
    }

    public ActivityTask Create(NewActivityTask task, DateTimeOffset? now = null)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(task.Title))
        {
            throw new ArgumentException("Task title is required.", nameof(task));
        }

        var timestamp = now ?? DateTimeOffset.UtcNow;

        using var connection = OpenConnection();
        return InsertTask(connection, transaction: null, task, timestamp);
    }

    public IReadOnlyList<ActivityTask> ListUnfinished(int limit = 100)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {TaskColumns}
            FROM tasks
            WHERE status NOT IN ($done, $canceled)
            ORDER BY due_at IS NULL, due_at, created_at
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$done", ActivityTaskStatus.Done);
        command.Parameters.AddWithValue("$canceled", ActivityTaskStatus.Canceled);
        command.Parameters.AddWithValue("$limit", limit);

        return ReadTasks(command);
    }

    public ActivityTask? Get(long id)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        return ReadTaskById(connection, transaction: null, id);
    }

    public ActivityTask? GetNextDue()
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {TaskColumns}
            FROM tasks
            WHERE status NOT IN ($done, $canceled)
            ORDER BY due_at IS NULL, due_at, created_at
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$done", ActivityTaskStatus.Done);
        command.Parameters.AddWithValue("$canceled", ActivityTaskStatus.Canceled);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTask(reader) : null;
    }

    public bool UpdateStatus(long id, string status, DateTimeOffset? now = null)
    {
        EnsureInitialized();
        var timestamp = now ?? DateTimeOffset.UtcNow;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var previous = ReadTaskById(connection, transaction, id);
        if (previous is null)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE tasks
            SET status = $status,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated_at", ToStorageText(timestamp));

        var updated = command.ExecuteNonQuery() == 1;
        if (updated)
        {
            CreateNextRecurringTaskIfNeeded(connection, transaction, previous, status, timestamp);
        }

        transaction.Commit();
        return updated;
    }

    public ActivityTask? Update(ActivityTaskUpdate task, DateTimeOffset? now = null)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(task.Title))
        {
            throw new ArgumentException("Task title is required.", nameof(task));
        }

        var timestamp = now ?? DateTimeOffset.UtcNow;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var previous = ReadTaskById(connection, transaction, task.Id);
        if (previous is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE tasks
            SET title = $title,
                due_at = $due_at,
                priority = $priority,
                status = $status,
                source = $source,
                external_reference = $external_reference,
                note = $note,
                updated_at = $updated_at
            WHERE id = $id
            RETURNING
                {TaskColumns};
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$title", task.Title);
        AddNullableDate(command, "$due_at", task.DueAt);
        command.Parameters.AddWithValue("$priority", task.Priority);
        command.Parameters.AddWithValue("$status", task.Status);
        AddNullableText(command, "$source", task.Source);
        AddNullableText(command, "$external_reference", task.ExternalReference);
        AddNullableText(command, "$note", task.Note);
        command.Parameters.AddWithValue("$updated_at", ToStorageText(timestamp));

        ActivityTask? updated;
        using (var reader = command.ExecuteReader())
        {
            updated = reader.Read() ? ReadTask(reader) : null;
        }

        if (updated is not null)
        {
            CreateNextRecurringTaskIfNeeded(connection, transaction, previous, updated.Status, timestamp);
        }

        transaction.Commit();
        return updated;
    }

    public bool SnoozeReminder(long id, TimeSpan duration, DateTimeOffset? now = null)
    {
        EnsureInitialized();
        var timestamp = now ?? DateTimeOffset.UtcNow;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks
            SET reminder_snoozed_until = $reminder_snoozed_until,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$reminder_snoozed_until", ToStorageText(timestamp.Add(duration)));
        command.Parameters.AddWithValue("$updated_at", ToStorageText(timestamp));

        return command.ExecuteNonQuery() == 1;
    }

    public bool RecordNotified(long id, DateTimeOffset? now = null)
    {
        EnsureInitialized();
        var timestamp = now ?? DateTimeOffset.UtcNow;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks
            SET last_notified_at = $last_notified_at,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$last_notified_at", ToStorageText(timestamp));
        command.Parameters.AddWithValue("$updated_at", ToStorageText(timestamp));

        return command.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<ActivityTask> ListReminderCandidates(DateTimeOffset now, TimeSpan throttle)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {TaskColumns}
            FROM tasks
            WHERE status NOT IN ($done, $canceled)
              AND (
                    due_at <= $now
                    OR priority IN ($high, $urgent)
                  )
              AND (
                    reminder_snoozed_until IS NULL
                    OR reminder_snoozed_until <= $now
                  )
              AND (
                    last_notified_at IS NULL
                    OR last_notified_at <= $notify_before
                  )
            ORDER BY due_at IS NULL, due_at, created_at;
            """;
        command.Parameters.AddWithValue("$done", ActivityTaskStatus.Done);
        command.Parameters.AddWithValue("$canceled", ActivityTaskStatus.Canceled);
        command.Parameters.AddWithValue("$high", ActivityTaskPriority.High);
        command.Parameters.AddWithValue("$urgent", ActivityTaskPriority.Urgent);
        command.Parameters.AddWithValue("$now", ToStorageText(now));
        command.Parameters.AddWithValue("$notify_before", ToStorageText(now.Subtract(throttle)));

        return ReadTasks(command);
    }

    public RecurringTaskSchedule CreateRecurringTask(NewRecurringTask task, DateTimeOffset? now = null)
    {
        EnsureInitialized();
        ValidateRecurringTask(task);

        var timestamp = now ?? DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO recurring_tasks (
                title,
                day_of_week,
                time_of_day_minutes,
                priority,
                external_reference,
                note,
                is_active,
                created_at,
                updated_at
            )
            VALUES (
                $title,
                $day_of_week,
                $time_of_day_minutes,
                $priority,
                $external_reference,
                $note,
                1,
                $created_at,
                $updated_at
            )
            RETURNING
                {RecurringTaskColumns};
            """;
        command.Parameters.AddWithValue("$title", task.Title.Trim());
        command.Parameters.AddWithValue("$day_of_week", (int)task.DayOfWeek);
        command.Parameters.AddWithValue("$time_of_day_minutes", (int)task.TimeOfDay.TotalMinutes);
        command.Parameters.AddWithValue("$priority", task.Priority);
        AddNullableText(command, "$external_reference", NullIfWhiteSpace(task.ExternalReference));
        AddNullableText(command, "$note", NullIfWhiteSpace(task.Note));
        command.Parameters.AddWithValue("$created_at", ToStorageText(timestamp));
        command.Parameters.AddWithValue("$updated_at", ToStorageText(timestamp));

        RecurringTaskSchedule schedule;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new InvalidOperationException("The recurring task was not created.");
            }

            schedule = ReadRecurringTask(reader);
        }

        var firstTask = CreateNextRecurringTask(connection, transaction, schedule, previousDueAt: null, timestamp);
        UpdateRecurringTaskLastTaskId(connection, transaction, schedule.Id, firstTask.Id, timestamp);

        var created = ReadRecurringTaskById(connection, transaction, schedule.Id)
            ?? throw new InvalidOperationException("The recurring task was not found after creation.");
        transaction.Commit();
        return created;
    }

    public IReadOnlyList<RecurringTaskSchedule> ListRecurringTasks()
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {RecurringTaskColumns}
            FROM recurring_tasks
            ORDER BY is_active DESC, day_of_week, time_of_day_minutes, title;
            """;

        var schedules = new List<RecurringTaskSchedule>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            schedules.Add(ReadRecurringTask(reader));
        }

        return schedules;
    }

    public RecurringTaskSchedule? GetRecurringTask(long id)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        return ReadRecurringTaskById(connection, transaction: null, id);
    }

    public bool SetRecurringTaskActive(long id, bool isActive, DateTimeOffset? now = null)
    {
        EnsureInitialized();
        var timestamp = now ?? DateTimeOffset.UtcNow;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var schedule = ReadRecurringTaskById(connection, transaction, id);
        if (schedule is null)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE recurring_tasks
            SET is_active = $is_active,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$is_active", isActive ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at", ToStorageText(timestamp));
        var updated = command.ExecuteNonQuery() == 1;

        if (updated && isActive)
        {
            var lastTask = schedule.LastTaskId is null
                ? null
                : ReadTaskById(connection, transaction, schedule.LastTaskId.Value);
            if (lastTask is null || lastTask.Status is ActivityTaskStatus.Done or ActivityTaskStatus.Canceled)
            {
                var activeSchedule = schedule with { IsActive = true };
                var nextTask = CreateNextRecurringTask(connection, transaction, activeSchedule, previousDueAt: null, timestamp);
                UpdateRecurringTaskLastTaskId(connection, transaction, schedule.Id, nextTask.Id, timestamp);
            }
        }

        transaction.Commit();
        return updated;
    }

    private static int GetSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void ApplySchemaVersion1(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE tasks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL CHECK (length(title) > 0),
                due_at TEXT NULL,
                priority TEXT NOT NULL DEFAULT 'normal' CHECK (priority IN ('low', 'normal', 'high', 'urgent')),
                status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'in_progress', 'done', 'canceled')),
                source TEXT NULL,
                external_reference TEXT NULL,
                reminder_snoozed_until TEXT NULL,
                last_notified_at TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX idx_tasks_unfinished_order
                ON tasks(status, due_at, created_at);

            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void ApplySchemaVersion2(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE tasks ADD COLUMN note TEXT NULL;

            PRAGMA user_version = 2;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void ApplySchemaVersion3(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE recurring_tasks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL CHECK (length(title) > 0),
                day_of_week INTEGER NOT NULL CHECK (day_of_week BETWEEN 0 AND 6),
                time_of_day_minutes INTEGER NOT NULL CHECK (time_of_day_minutes BETWEEN 0 AND 1439),
                priority TEXT NOT NULL DEFAULT 'normal' CHECK (priority IN ('low', 'normal', 'high', 'urgent')),
                external_reference TEXT NULL,
                note TEXT NULL,
                is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
                last_task_id INTEGER NULL REFERENCES tasks(id),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX idx_recurring_tasks_active_schedule
                ON recurring_tasks(is_active, day_of_week, time_of_day_minutes);

            ALTER TABLE tasks ADD COLUMN recurring_task_id INTEGER NULL REFERENCES recurring_tasks(id);

            CREATE INDEX idx_tasks_recurring_task_id
                ON tasks(recurring_task_id);

            PRAGMA user_version = 3;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        return connection;
    }

    private static ActivityTask InsertTask(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        NewActivityTask task,
        DateTimeOffset timestamp)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.CommandText = $"""
            INSERT INTO tasks (
                title,
                due_at,
                priority,
                status,
                source,
                external_reference,
                note,
                recurring_task_id,
                created_at,
                updated_at
            )
            VALUES (
                $title,
                $due_at,
                $priority,
                $status,
                $source,
                $external_reference,
                $note,
                $recurring_task_id,
                $created_at,
                $updated_at
            )
            RETURNING
                {TaskColumns};
            """;
        command.Parameters.AddWithValue("$title", task.Title);
        AddNullableDate(command, "$due_at", task.DueAt);
        command.Parameters.AddWithValue("$priority", task.Priority);
        command.Parameters.AddWithValue("$status", task.Status);
        AddNullableText(command, "$source", task.Source);
        AddNullableText(command, "$external_reference", task.ExternalReference);
        AddNullableText(command, "$note", task.Note);
        AddNullableInt64(command, "$recurring_task_id", task.RecurringTaskId);
        command.Parameters.AddWithValue("$created_at", ToStorageText(timestamp));
        command.Parameters.AddWithValue("$updated_at", ToStorageText(timestamp));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("The task was not created.");
        }

        return ReadTask(reader);
    }

    private static ActivityTask CreateNextRecurringTask(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecurringTaskSchedule schedule,
        DateTimeOffset? previousDueAt,
        DateTimeOffset timestamp)
    {
        var reference = previousDueAt is { } dueAt && dueAt > timestamp
            ? dueAt
            : timestamp;
        var nextDueAt = NextOccurrence(schedule.DayOfWeek, schedule.TimeOfDay, reference);

        return InsertTask(
            connection,
            transaction,
            new NewActivityTask(
                schedule.Title,
                nextDueAt,
                schedule.Priority,
                Source: "recurring",
                ExternalReference: schedule.ExternalReference,
                Note: schedule.Note,
                RecurringTaskId: schedule.Id),
            timestamp);
    }

    private static void CreateNextRecurringTaskIfNeeded(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ActivityTask previousTask,
        string newStatus,
        DateTimeOffset timestamp)
    {
        if (newStatus != ActivityTaskStatus.Done
            || previousTask.Status == ActivityTaskStatus.Done
            || previousTask.RecurringTaskId is not { } recurringTaskId)
        {
            return;
        }

        var schedule = ReadRecurringTaskById(connection, transaction, recurringTaskId);
        if (schedule is null
            || !schedule.IsActive
            || schedule.LastTaskId != previousTask.Id)
        {
            return;
        }

        var nextTask = CreateNextRecurringTask(connection, transaction, schedule, previousTask.DueAt, timestamp);
        UpdateRecurringTaskLastTaskId(connection, transaction, schedule.Id, nextTask.Id, timestamp);
    }

    private static void UpdateRecurringTaskLastTaskId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long recurringTaskId,
        long taskId,
        DateTimeOffset timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE recurring_tasks
            SET last_task_id = $last_task_id,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", recurringTaskId);
        command.Parameters.AddWithValue("$last_task_id", taskId);
        command.Parameters.AddWithValue("$updated_at", ToStorageText(timestamp));
        command.ExecuteNonQuery();
    }

    private static ActivityTask? ReadTaskById(SqliteConnection connection, SqliteTransaction? transaction, long id)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.CommandText = $"""
            SELECT
                {TaskColumns}
            FROM tasks
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTask(reader) : null;
    }

    private static IReadOnlyList<ActivityTask> ReadTasks(SqliteCommand command)
    {
        var tasks = new List<ActivityTask>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tasks.Add(ReadTask(reader));
        }

        return tasks;
    }

    private static ActivityTask ReadTask(SqliteDataReader reader)
    {
        return new ActivityTask(
            reader.GetInt64(0),
            reader.GetString(1),
            ReadNullableDate(reader, 2),
            reader.GetString(3),
            reader.GetString(4),
            ReadNullableText(reader, 5),
            ReadNullableText(reader, 6),
            ReadNullableText(reader, 7),
            ReadNullableInt64(reader, 8),
            ReadNullableDate(reader, 9),
            ReadNullableDate(reader, 10),
            ReadRequiredDate(reader, 11),
            ReadRequiredDate(reader, 12));
    }

    private static RecurringTaskSchedule? ReadRecurringTaskById(SqliteConnection connection, SqliteTransaction? transaction, long id)
    {
        using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.CommandText = $"""
            SELECT
                {RecurringTaskColumns}
            FROM recurring_tasks
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecurringTask(reader) : null;
    }

    private static RecurringTaskSchedule ReadRecurringTask(SqliteDataReader reader)
    {
        return new RecurringTaskSchedule(
            reader.GetInt64(0),
            reader.GetString(1),
            (DayOfWeek)reader.GetInt32(2),
            TimeSpan.FromMinutes(reader.GetInt32(3)),
            reader.GetString(4),
            ReadNullableText(reader, 5),
            ReadNullableText(reader, 6),
            reader.GetInt64(7) == 1,
            ReadNullableInt64(reader, 8),
            ReadRequiredDate(reader, 9),
            ReadRequiredDate(reader, 10));
    }

    private static DateTimeOffset NextOccurrence(DayOfWeek dayOfWeek, TimeSpan timeOfDay, DateTimeOffset after)
    {
        var localAfter = after.ToLocalTime();
        var daysUntil = ((int)dayOfWeek - (int)localAfter.DayOfWeek + 7) % 7;
        var candidateLocal = localAfter.Date.AddDays(daysUntil).Add(timeOfDay);
        var candidate = new DateTimeOffset(candidateLocal, TimeZoneInfo.Local.GetUtcOffset(candidateLocal));
        if (candidate <= localAfter)
        {
            candidateLocal = candidateLocal.AddDays(7);
            candidate = new DateTimeOffset(candidateLocal, TimeZoneInfo.Local.GetUtcOffset(candidateLocal));
        }

        return candidate;
    }

    private static void ValidateRecurringTask(NewRecurringTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Title))
        {
            throw new ArgumentException("Recurring task title is required.", nameof(task));
        }

        if (!Enum.IsDefined(task.DayOfWeek))
        {
            throw new ArgumentOutOfRangeException(nameof(NewRecurringTask.DayOfWeek), "Day of week is invalid.");
        }

        if (task.TimeOfDay < TimeSpan.Zero || task.TimeOfDay >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(NewRecurringTask.TimeOfDay), "Time of day must be within a single day.");
        }
    }

    private static void AddNullableText(SqliteCommand command, string name, string? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value);
    }

    private static void AddNullableDate(SqliteCommand command, string name, DateTimeOffset? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : ToStorageText(value.Value));
    }

    private static void AddNullableInt64(SqliteCommand command, string name, long? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value.Value);
    }

    private static string? ReadNullableText(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : ReadRequiredDate(reader, ordinal);
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static DateTimeOffset ReadRequiredDate(SqliteDataReader reader, int ordinal)
    {
        return DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    private static string ToStorageText(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
