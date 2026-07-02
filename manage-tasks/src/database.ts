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
  reminder_snoozed_until: string | null;
  last_notified_at: string | null;
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
  reminder_snoozed_until,
  last_notified_at,
  created_at,
  updated_at
`;

export const TASKS_QUERY = `
  SELECT ${TASK_COLUMNS}
  FROM tasks
  ORDER BY
    status IN ('done', 'canceled'),
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
  const now = new Date().toISOString();
  const source = task.source?.trim();
  const externalReference = task.externalReference?.trim();
  const note = task.note?.trim();

  return withWritableDatabase((database) => {
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
        $updated_at: now,
      }) as ActivityTask | undefined;

    return requireTask(row, "The task was not found.");
  });
}

export async function updateTaskStatus(id: number, status: TaskStatus): Promise<ActivityTask> {
  const now = new Date().toISOString();

  return withWritableDatabase((database) => {
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
        $updated_at: now,
      }) as ActivityTask | undefined;

    return requireTask(row, "The task was not found.");
  });
}

async function withWritableDatabase<T>(action: (database: DatabaseSync) => T): Promise<T> {
  const { DatabaseSync } = await import("node:sqlite");
  const database = new DatabaseSync(DATABASE_PATH);

  try {
    database.exec("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
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
