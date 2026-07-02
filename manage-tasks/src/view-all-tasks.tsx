import { useState } from "react";
import { Action, ActionPanel, Color, Form, Icon, List, Toast, showToast, useNavigation } from "@raycast/api";
import { useSQL } from "@raycast/utils";
import {
  DATABASE_PATH,
  TASKS_QUERY,
  updateTask,
  updateTaskStatus,
  type ActivityTask,
  type Priority,
  type TaskStatus,
} from "./database";

const PRIORITIES: Priority[] = ["low", "normal", "high", "urgent"];
const STATUSES: TaskStatus[] = ["pending", "in_progress", "done", "canceled"];

export default function Command() {
  const { data, isLoading, permissionView, revalidate } = useSQL<ActivityTask>(DATABASE_PATH, TASKS_QUERY, {
    permissionPriming: "This extension reads your local activity database from your home directory.",
    failureToastOptions: {
      title: "Could not load tasks",
    },
  });

  if (permissionView) {
    return permissionView;
  }

  const tasks = data ?? [];

  return (
    <List isLoading={isLoading} searchBarPlaceholder="Search tasks">
      {tasks.length === 0 ? <List.EmptyView icon={Icon.CheckCircle} title="No tasks" /> : null}
      {tasks.map((task) => (
        <List.Item
          key={task.id}
          icon={taskIcon(task)}
          title={task.title}
          subtitle={formatDue(task.due_at)}
          accessories={taskAccessories(task)}
          actions={<TaskActions task={task} revalidate={revalidate} />}
        />
      ))}
    </List>
  );
}

function TaskActions({ task, revalidate }: { task: ActivityTask; revalidate: () => void }) {
  const webReference = getWebReference(task.external_reference);

  return (
    <ActionPanel>
      <Action.Push
        title="Show Details"
        icon={Icon.Sidebar}
        target={<TaskDetail task={task} revalidate={revalidate} />}
      />
      <StatusActions
        currentStatus={task.status}
        onStatusChange={(status) => changeTaskStatus(task.id, task.title, status, revalidate)}
      />
      {webReference ? <Action.OpenInBrowser title="Open Reference" url={webReference} /> : null}
      {task.external_reference ? (
        <Action.CopyToClipboard title="Copy Reference" content={task.external_reference} />
      ) : null}
      <Action title="Refresh" icon={Icon.ArrowClockwise} onAction={revalidate} />
    </ActionPanel>
  );
}

function TaskDetail({ task, revalidate }: { task: ActivityTask; revalidate: () => void }) {
  const { pop } = useNavigation();
  const [title, setTitle] = useState(task.title);
  const [dueAt, setDueAt] = useState<Date | null>(parseDate(task.due_at));
  const [priority, setPriority] = useState<Priority>(task.priority);
  const [status, setStatus] = useState<TaskStatus>(task.status);
  const [source, setSource] = useState(task.source ?? "");
  const [externalReference, setExternalReference] = useState(task.external_reference ?? "");
  const [note, setNote] = useState(task.note ?? "");
  const webReference = getWebReference(externalReference);

  async function handleSave() {
    const cleanTitle = title.trim();

    if (!cleanTitle) {
      await showToast({ style: Toast.Style.Failure, title: "Title is required" });
      return;
    }

    const toast = await showToast({ style: Toast.Style.Animated, title: "Saving task" });

    try {
      await updateTask({
        id: task.id,
        title: cleanTitle,
        dueAt,
        priority,
        status,
        source,
        externalReference,
        note,
      });

      toast.style = Toast.Style.Success;
      toast.title = "Task saved";
      toast.message = cleanTitle;
      revalidate();
      pop();
    } catch (error) {
      toast.style = Toast.Style.Failure;
      toast.title = "Could not save task";
      toast.message = error instanceof Error ? error.message : String(error);
    }
  }

  async function handleStatusChange(nextStatus: TaskStatus) {
    const updated = await changeTaskStatus(task.id, title.trim() || task.title, nextStatus, revalidate);
    if (updated) {
      setStatus(updated.status);
    }
  }

  return (
    <Form
      navigationTitle={`Task ${task.id}`}
      actions={
        <ActionPanel>
          <Action.SubmitForm title="Save Task" icon={Icon.SaveDocument} onSubmit={handleSave} />
          <StatusActions currentStatus={status} onStatusChange={handleStatusChange} />
          {webReference ? <Action.OpenInBrowser title="Open Reference" url={webReference} /> : null}
          {externalReference ? <Action.CopyToClipboard title="Copy Reference" content={externalReference} /> : null}
        </ActionPanel>
      }
    >
      <Form.TextField id="title" title="Title" value={title} onChange={setTitle} autoFocus />
      <Form.DatePicker id="dueAt" title="Due Date" value={dueAt} onChange={setDueAt} />
      <Form.Dropdown
        id="priority"
        title="Priority"
        value={priority}
        onChange={(value) => setPriority(value as Priority)}
      >
        {PRIORITIES.map((priority) => (
          <Form.Dropdown.Item key={priority} value={priority} title={formatPriority(priority)} />
        ))}
      </Form.Dropdown>
      <Form.Dropdown id="status" title="Status" value={status} onChange={(value) => setStatus(value as TaskStatus)}>
        {STATUSES.map((status) => (
          <Form.Dropdown.Item key={status} value={status} title={formatStatus(status)} />
        ))}
      </Form.Dropdown>
      <Form.TextArea id="note" title="Note" value={note} onChange={setNote} placeholder="Comments or context" />
      <Form.TextField id="source" title="Source" value={source} onChange={setSource} />
      <Form.TextField
        id="externalReference"
        title="Reference"
        value={externalReference}
        onChange={setExternalReference}
      />
      <Form.Description
        text={`Created ${formatDateTime(task.created_at)} - Updated ${formatDateTime(task.updated_at)}`}
      />
    </Form>
  );
}

function StatusActions({
  currentStatus,
  onStatusChange,
}: {
  currentStatus: TaskStatus;
  onStatusChange: (status: TaskStatus) => void | Promise<void>;
}) {
  return (
    <>
      {currentStatus !== "done" ? (
        <Action title="Complete Task" icon={Icon.CheckCircle} onAction={() => onStatusChange("done")} />
      ) : null}
      <ActionPanel.Submenu title="Set Status" icon={statusIcon(currentStatus)}>
        {STATUSES.map((status) => (
          <Action
            key={status}
            title={formatStatus(status)}
            icon={statusIcon(status)}
            onAction={() => onStatusChange(status)}
          />
        ))}
      </ActionPanel.Submenu>
    </>
  );
}

function taskAccessories(task: ActivityTask): List.Item.Accessory[] {
  const accessories: List.Item.Accessory[] = [
    { tag: { value: formatStatus(task.status), color: statusColor(task.status) } },
    { tag: { value: formatPriority(task.priority), color: priorityColor(task.priority) } },
  ];

  const dueDate = parseDate(task.due_at);
  if (dueDate) {
    accessories.push({ date: dueDate });
  }

  return accessories;
}

function taskIcon(task: ActivityTask) {
  if (task.status !== "pending") {
    return statusIcon(task.status);
  }

  return task.priority === "urgent" || task.priority === "high" ? Icon.ExclamationMark : Icon.Circle;
}

function statusIcon(status: TaskStatus): Icon {
  switch (status) {
    case "done":
      return Icon.CheckCircle;
    case "canceled":
      return Icon.XMarkCircle;
    case "in_progress":
      return Icon.CircleEllipsis;
    case "pending":
      return Icon.Circle;
  }
}

async function changeTaskStatus(
  id: number,
  title: string,
  status: TaskStatus,
  revalidate: () => void,
): Promise<ActivityTask | null> {
  const toast = await showToast({ style: Toast.Style.Animated, title: "Updating status" });

  try {
    const task = await updateTaskStatus(id, status);
    toast.style = Toast.Style.Success;
    toast.title = "Status updated";
    toast.message = `${title}: ${formatStatus(status)}`;
    revalidate();
    return task;
  } catch (error) {
    toast.style = Toast.Style.Failure;
    toast.title = "Could not update status";
    toast.message = error instanceof Error ? error.message : String(error);
    return null;
  }
}

function formatDue(value: string | null): string | undefined {
  return value ? `Due ${formatDateTime(value)}` : undefined;
}

function formatDateTime(value: string | null): string {
  const date = parseDate(value);
  return date ? date.toLocaleString() : "None";
}

function parseDate(value: string | null): Date | null {
  if (!value) {
    return null;
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

function formatPriority(priority: Priority): string {
  return priority.replace("_", " ").replace(/^\w/, (letter) => letter.toUpperCase());
}

function formatStatus(status: TaskStatus): string {
  return status.replace("_", " ").replace(/^\w/, (letter) => letter.toUpperCase());
}

function priorityColor(priority: Priority): Color {
  switch (priority) {
    case "urgent":
      return Color.Red;
    case "high":
      return Color.Orange;
    case "low":
      return Color.SecondaryText;
    default:
      return Color.Blue;
  }
}

function statusColor(status: TaskStatus): Color {
  switch (status) {
    case "done":
      return Color.Green;
    case "canceled":
      return Color.SecondaryText;
    case "in_progress":
      return Color.Yellow;
    default:
      return Color.Blue;
  }
}

function getWebReference(value: string | null): string | null {
  if (!value) {
    return null;
  }

  try {
    const url = new URL(value);
    return url.protocol === "http:" || url.protocol === "https:" ? value : null;
  } catch {
    return null;
  }
}
