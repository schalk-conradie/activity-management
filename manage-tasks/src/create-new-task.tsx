import { Action, ActionPanel, Form, Icon, Toast, popToRoot, showToast } from "@raycast/api";
import { createTask, type Priority } from "./database";

type Values = {
  title: string;
  dueAt?: Date;
  priority: Priority;
  externalReference?: string;
  note?: string;
};

export default function Command() {
  async function handleSubmit(values: Values) {
    const title = values.title.trim();

    if (!title) {
      await showToast({ style: Toast.Style.Failure, title: "Title is required" });
      return;
    }

    const toast = await showToast({ style: Toast.Style.Animated, title: "Creating task" });

    try {
      await createTask({
        title,
        dueAt: values.dueAt,
        priority: values.priority,
        externalReference: values.externalReference,
        note: values.note,
      });

      toast.style = Toast.Style.Success;
      toast.title = "Task created";
      toast.message = title;
      await popToRoot();
    } catch (error) {
      toast.style = Toast.Style.Failure;
      toast.title = "Could not create task";
      toast.message = error instanceof Error ? error.message : String(error);
    }
  }

  return (
    <Form
      enableDrafts
      actions={
        <ActionPanel>
          <Action.SubmitForm title="Create Task" icon={Icon.Plus} onSubmit={handleSubmit} />
        </ActionPanel>
      }
    >
      <Form.TextField id="title" title="Title" placeholder="What needs doing?" autoFocus />
      <Form.DatePicker id="dueAt" title="Due Date" />
      <Form.Dropdown id="priority" title="Priority" defaultValue="normal">
        <Form.Dropdown.Item value="low" title="Low" />
        <Form.Dropdown.Item value="normal" title="Normal" />
        <Form.Dropdown.Item value="high" title="High" />
        <Form.Dropdown.Item value="urgent" title="Urgent" />
      </Form.Dropdown>
      <Form.TextArea id="note" title="Note" placeholder="Optional comments or context" />
      <Form.TextField id="externalReference" title="Reference" placeholder="Optional URL or ticket reference" />
    </Form>
  );
}
