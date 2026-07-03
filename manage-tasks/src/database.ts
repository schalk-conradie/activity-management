import { homedir } from "node:os";
import { join } from "node:path";
import type { DatabaseSync } from "node:sqlite";

export const DATABASE_PATH = join(homedir(), ".activity-management", "activity.db");

export type Priority = "low" | "normal" | "high" | "urgent";
export type TaskStatus = "pending" | "in_progress" | "done" | "canceled";

export type ActivityTask = {
  id: number;
  title: string;
  due_at: string | null;
  priority: Priority;
  status: TaskStatus;
  source: string | null;
  external_reference: string | null;
  note: string | null;
  recurring_task_id: number | null;
  reminder_snoozed_until: string | null;
  last_notified_at: string | null;
  created_at: string;
  updated_at: string;
};

type RecurringTaskSchedule = {
  id: number;
  title: string;
  day_of_week: number;
  time_of_day_minutes: number;
  priority: Priority;
  external_reference: string | null;
  note: string | null;
  is_active: number;
  last_task_id: number | null;
  created_at: string;
  updated_at: string;
};

type NewTask = {
  title: string;
  dueAt?: Date | null;
  priority: Priority;
  externalReference?: string;
  note?: string;
};

type TaskUpdate = {
  id: number;
  title: string;
  dueAt?: Date | null;
  priority: Priority;
  status: TaskStatus;
  source?: string;
  externalReference?: string;
  note?: string;
};

const TASK_COLUMNS = `
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
`;

const RECURRING_TASK_COLUMNS = `
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
`;

export const TASKS_QUERY = `
  SELECT ${TASK_COLUMNS}
  FROM tasks
  WHERE status NOT IN ('done', 'canceled')
  ORDER BY
    due_at IS NULL,
    due_at,
    created_at;
`;

export async function createTask(task: NewTask): Promise<ActivityTask> {
  const now = new Date().toISOString();
  const externalReference = task.externalReference?.trim();

  return withWritableDatabase((database) => {
    const row = database
      .prepare(
        `
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
            'pending',
            'raycast',
            $external_reference,
            $note,
            $created_at,
            $updated_at
          )
          RETURNING ${TASK_COLUMNS};
        `,
      )
      .get({
        $title: task.title,
        $due_at: task.dueAt?.toISOString() ?? null,
        $priority: task.priority,
        $external_reference: externalReference || null,
        $note: task.note?.trim() || null,
        $created_at: now,
        $updated_at: now,
      }) as ActivityTask | undefined;

    return requireTask(row, "The task was not created.");
  });
}

export async function updateTask(task: TaskUpdate): Promise<ActivityTask> {
  const now = new Date();
  const nowText = now.toISOString();
  const source = task.source?.trim();
  const externalReference = task.externalReference?.trim();
  const note = task.note?.trim();

  return withWritableDatabase((database) => {
    return withTransaction(database, () => {
      const previous = getTask(database, task.id);
      if (!previous) {
        throw new Error("The task was not found.");
      }

      const row = database
        .prepare(
          `
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
            RETURNING ${TASK_COLUMNS};
          `,
        )
        .get({
          $id: task.id,
          $title: task.title,
          $due_at: task.dueAt?.toISOString() ?? null,
          $priority: task.priority,
          $status: task.status,
          $source: source || null,
          $external_reference: externalReference || null,
          $note: note || null,
          $updated_at: nowText,
        }) as ActivityTask | undefined;

      const updated = requireTask(row, "The task was not found.");
      createNextRecurringTaskIfNeeded(database, previous, updated.status, now);
      return updated;
    });
  });
}

export async function updateTaskStatus(id: number, status: TaskStatus): Promise<ActivityTask> {
  const now = new Date();
  const nowText = now.toISOString();

  return withWritableDatabase((database) => {
    return withTransaction(database, () => {
      const previous = getTask(database, id);
      if (!previous) {
        throw new Error("The task was not found.");
      }

      const row = database
        .prepare(
          `
            UPDATE tasks
            SET status = $status,
                updated_at = $updated_at
            WHERE id = $id
            RETURNING ${TASK_COLUMNS};
          `,
        )
        .get({
          $id: id,
          $status: status,
          $updated_at: nowText,
        }) as ActivityTask | undefined;

      const updated = requireTask(row, "The task was not found.");
      createNextRecurringTaskIfNeeded(database, previous, updated.status, now);
      return updated;
    });
  });
}

async function withWritableDatabase<T>(action: (database: DatabaseSync) => T): Promise<T> {
  const { DatabaseSync } = await import("node:sqlite");
  const database = new DatabaseSync(DATABASE_PATH);

  try {
    database.exec("PRAGMA busy_timeout = 5000;");
    return action(database);
  } finally {
    database.close();
  }
}

function requireTask(task: ActivityTask | undefined, message: string): ActivityTask {
  if (!task) {
    throw new Error(message);
  }

  return task;
}

function withTransaction<T>(database: DatabaseSync, action: () => T): T {
  database.exec("BEGIN IMMEDIATE;");

  try {
    const result = action();
    database.exec("COMMIT;");
    return result;
  } catch (error) {
    database.exec("ROLLBACK;");
    throw error;
  }
}

function getTask(database: DatabaseSync, id: number): ActivityTask | undefined {
  return database
    .prepare(
      `
        SELECT ${TASK_COLUMNS}
        FROM tasks
        WHERE id = $id;
      `,
    )
    .get({ $id: id }) as ActivityTask | undefined;
}

function createNextRecurringTaskIfNeeded(
  database: DatabaseSync,
  previousTask: ActivityTask,
  newStatus: TaskStatus,
  now: Date,
) {
  if (newStatus !== "done" || previousTask.status === "done" || previousTask.recurring_task_id === null) {
    return;
  }

  const schedule = getRecurringTask(database, previousTask.recurring_task_id);
  if (!schedule || schedule.is_active !== 1 || schedule.last_task_id !== previousTask.id) {
    return;
  }

  const nextTaskId = createNextRecurringTask(database, schedule, previousTask.due_at, now);
  database
    .prepare(
      `
        UPDATE recurring_tasks
        SET last_task_id = $last_task_id,
            updated_at = $updated_at
        WHERE id = $id;
      `,
    )
    .run({
      $id: schedule.id,
      $last_task_id: nextTaskId,
      $updated_at: now.toISOString(),
    });
}

function getRecurringTask(database: DatabaseSync, id: number): RecurringTaskSchedule | undefined {
  return database
    .prepare(
      `
        SELECT ${RECURRING_TASK_COLUMNS}
        FROM recurring_tasks
        WHERE id = $id;
      `,
    )
    .get({ $id: id }) as RecurringTaskSchedule | undefined;
}

function createNextRecurringTask(
  database: DatabaseSync,
  schedule: RecurringTaskSchedule,
  previousDueAtText: string | null,
  now: Date,
): number {
  const previousDueAt = parseDate(previousDueAtText);
  const reference = previousDueAt && previousDueAt > now ? previousDueAt : now;
  const nextDueAt = nextOccurrence(schedule.day_of_week, schedule.time_of_day_minutes, reference);
  const timestamp = now.toISOString();
  const row = database
    .prepare(
      `
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
          'pending',
          'recurring',
          $external_reference,
          $note,
          $recurring_task_id,
          $created_at,
          $updated_at
        )
        RETURNING id;
      `,
    )
    .get({
      $title: schedule.title,
      $due_at: nextDueAt.toISOString(),
      $priority: schedule.priority,
      $external_reference: schedule.external_reference,
      $note: schedule.note,
      $recurring_task_id: schedule.id,
      $created_at: timestamp,
      $updated_at: timestamp,
    }) as { id: number } | undefined;

  if (!row) {
    throw new Error("The next recurring task was not created.");
  }

  return row.id;
}

function nextOccurrence(dayOfWeek: number, timeOfDayMinutes: number, after: Date): Date {
  const candidate = new Date(after);
  const daysUntil = (dayOfWeek - after.getDay() + 7) % 7;
  candidate.setHours(0, 0, 0, 0);
  candidate.setDate(candidate.getDate() + daysUntil);
  candidate.setHours(Math.floor(timeOfDayMinutes / 60), timeOfDayMinutes % 60, 0, 0);

  if (candidate <= after) {
    candidate.setDate(candidate.getDate() + 7);
  }

  return candidate;
}

function parseDate(value: string | null): Date | null {
  if (!value) {
    return null;
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}
