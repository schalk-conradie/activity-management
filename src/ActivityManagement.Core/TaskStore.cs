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
    string? Note = null);

public sealed record ActivityTask(
    long Id,
    string Title,
    DateTimeOffset? DueAt,
    string Priority,
    string Status,
    string? Source,
    string? ExternalReference,
    string? Note,
    DateTimeOffset? ReminderSnoozedUntil,
    DateTimeOffset? LastNotifiedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed class TaskStore
{
    private const int CurrentSchemaVersion = 2;
    private readonly string _databasePath;

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

        if (version != CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Unsupported activity database schema version {version}.");
        }
    }

    public ActivityTask Create(NewActivityTask task, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(task.Title))
        {
            throw new ArgumentException("Task title is required.", nameof(task));
        }

        var timestamp = now ?? DateTimeOffset.UtcNow;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks (
                title,
                due_at,
                priority,
                status,
                source,
                external_reference,
                note,
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
                $created_at,
                $updated_at
            )
            RETURNING
                id,
                title,
                due_at,
                priority,
                status,
                source,
                external_reference,
                note,
                reminder_snoozed_until,
                last_notified_at,
                created_at,
                updated_at;
            """;
        command.Parameters.AddWithValue("$title", task.Title);
        AddNullableDate(command, "$due_at", task.DueAt);
        command.Parameters.AddWithValue("$priority", task.Priority);
        command.Parameters.AddWithValue("$status", task.Status);
        AddNullableText(command, "$source", task.Source);
        AddNullableText(command, "$external_reference", task.ExternalReference);
        AddNullableText(command, "$note", task.Note);
        command.Parameters.AddWithValue("$created_at", ToStorageText(timestamp));
        command.Parameters.AddWithValue("$updated_at", ToStorageText(timestamp));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("The task was not created.");
        }

        return ReadTask(reader);
    }

    public IReadOnlyList<ActivityTask> ListUnfinished(int limit = 100)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                title,
                due_at,
                priority,
                status,
                source,
                external_reference,
                note,
                reminder_snoozed_until,
                last_notified_at,
                created_at,
                updated_at
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
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                title,
                due_at,
                priority,
                status,
                source,
                external_reference,
                note,
                reminder_snoozed_until,
                last_notified_at,
                created_at,
                updated_at
            FROM tasks
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTask(reader) : null;
    }

    public ActivityTask? GetNextDue()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                title,
                due_at,
                priority,
                status,
                source,
                external_reference,
                note,
                reminder_snoozed_until,
                last_notified_at,
                created_at,
                updated_at
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
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks
            SET status = $status,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated_at", ToStorageText(now ?? DateTimeOffset.UtcNow));

        return command.ExecuteNonQuery() == 1;
    }

    public bool SnoozeReminder(long id, TimeSpan duration, DateTimeOffset? now = null)
    {
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
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                title,
                due_at,
                priority,
                status,
                source,
                external_reference,
                note,
                reminder_snoozed_until,
                last_notified_at,
                created_at,
                updated_at
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

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();

        return connection;
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
            ReadNullableDate(reader, 8),
            ReadNullableDate(reader, 9),
            ReadRequiredDate(reader, 10),
            ReadRequiredDate(reader, 11));
    }

    private static void AddNullableText(SqliteCommand command, string name, string? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value);
    }

    private static void AddNullableDate(SqliteCommand command, string name, DateTimeOffset? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : ToStorageText(value.Value));
    }

    private static string? ReadNullableText(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : ReadRequiredDate(reader, ordinal);
    }

    private static DateTimeOffset ReadRequiredDate(SqliteDataReader reader, int ordinal)
    {
        return DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    private static string ToStorageText(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}
