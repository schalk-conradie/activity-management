# Activity Management

Activity Management is a local-first task companion for tracking work that needs attention. It uses a SQLite database in the user's home directory as the shared source of truth, with a small Windows companion app and a Raycast extension writing to the same data store.

The database lives at:

```text
%USERPROFILE%\.activity-management\activity.db
```

## What Is In This Repo

- `src/ActivityManagement.Core`: shared .NET storage library for database setup, migrations, task creation, listing, status updates, snoozing, and reminder selection.
- `src/ActivityManagement.App`: WinUI 3 Windows companion app with a taskbar widget, flyout, tray icon, global quick-create shortcut, and notifications.
- `tests/ActivityManagement.Tests`: xUnit tests for the storage layer.
- `manage-tasks`: Raycast extension for creating, viewing, editing, and completing tasks from Raycast.

## Requirements

- Windows 11 for the WinUI companion app.
- .NET SDK with .NET 10 support.
- Node.js with `node:sqlite` support for the Raycast extension.
- Raycast if you want to use the `manage-tasks` extension.

## Install And Run The Windows App

From the repository root:

```powershell
dotnet restore ActivityManagement.slnx
dotnet build ActivityManagement.slnx
dotnet run --project src\ActivityManagement.App\ActivityManagement.App.csproj -p:Platform=x64
```

On first launch, the app creates the local database directory and initializes the SQLite schema.

The Windows app provides:

- a compact taskbar widget showing the next task and unfinished count
- a flyout with unfinished tasks and quick-create controls
- a tray menu for opening tasks, quick add, and exit
- `Ctrl+Shift+K` to open quick create
- Windows notifications for due or high-priority tasks
- notification actions for done, snooze, dismiss, and open source where available
- weekly recurring tasks that create the next reminder task when completed

## Use The App

Create a task from the Windows flyout or with `Ctrl+Shift+K`.

Tasks support:

- title
- due date and time
- priority: `low`, `normal`, `high`, `urgent`
- status: `pending`, `in_progress`, `done`, `canceled`
- source
- external reference
- note
- recurring weekly schedule, for generated reminder tasks

Unfinished tasks are ordered by due date, then created date. Tasks without due dates appear after dated tasks. `done` and `canceled` tasks are excluded from the unfinished list.

## Add The Raycast Extension

From the repository root:

```powershell
cd manage-tasks
npm install
npm run dev
```

`npm run dev` starts Raycast extension development mode and adds the local extension to Raycast.

The extension exposes two commands:

- `Create New Task`: create a task in the shared local SQLite database.
- `View Unfinished Tasks`: browse, inspect, update, complete, and open references for unfinished tasks.

For a production build of the extension:

```powershell
cd manage-tasks
npm run build
```

## Checks

Run .NET tests from the repository root:

```powershell
dotnet test ActivityManagement.slnx
```

Run Raycast checks from the extension folder:

```powershell
cd manage-tasks
npm run lint
npm run build
```

## Storage Compatibility

Both the Windows app and Raycast extension use the same SQLite file and schema. If the schema changes, update these together:

- `src/ActivityManagement.Core/TaskStore.cs`
- `manage-tasks/src/database.ts`
- `tests/ActivityManagement.Tests/TaskStoreTests.cs`

Do not introduce a second database location or separate migration system unless there is a concrete need.
