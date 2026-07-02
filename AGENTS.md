# AGENTS.md

## Project Shape

This is a local-first activity management monorepo. The shared source of truth is a SQLite database at:

`%USERPROFILE%\.activity-management\activity.db`

There are two application parts:

- `src/` and `tests/`: a .NET 10 solution with shared storage logic, tests, and a WinUI 3 Windows companion app.
- `manage-tasks/`: a Raycast extension that reads and writes the same SQLite database.

Keep these two parts aligned. Schema, enum values, ordering, and date storage rules must stay compatible across C# and TypeScript.

## Current .NET App

The .NET solution is `ActivityManagement.slnx`.

- `src/ActivityManagement.Core`: shared SQLite store and domain records.
- `src/ActivityManagement.App`: unpackaged WinUI 3 Windows app.
- `tests/ActivityManagement.Tests`: xUnit tests for core storage behavior.

`ActivityManagement.Core.TaskStore` owns database initialization, migrations, and the canonical storage behavior. It currently uses schema version 2 and a single `tasks` table.

Current task fields:

- `id`
- `title`
- `due_at`
- `priority`: `low`, `normal`, `high`, `urgent`
- `status`: `pending`, `in_progress`, `done`, `canceled`
- `source`
- `external_reference`
- `note`
- `reminder_snoozed_until`
- `last_notified_at`
- `created_at`
- `updated_at`

The Windows app currently provides:

- a taskbar-hosted compact widget showing unfinished count and next task
- a flyout window with unfinished tasks and quick-create controls
- tray icon menu
- `Ctrl+Shift+K` quick-create hotkey
- due/high-priority reminder notifications with actions

## Current Raycast Extension

The Raycast extension lives in `manage-tasks/`.

- `src/database.ts`: TypeScript SQLite access for the shared database.
- `src/create-new-task.tsx`: create-task command.
- `src/view-all-tasks.tsx`: list, inspect, update, and complete tasks.

Use `npm` and `npx` in this repository unless explicitly told otherwise.

Raycast uses Node's `node:sqlite` and Raycast's `useSQL`. Do not introduce a separate database location or schema from the .NET app.

## Shared Storage Rules

- SQLite is the source of truth.
- Store timestamps as UTC ISO-8601 text.
- Default unfinished ordering is due date first, then created date, with no-due-date tasks last.
- `done` and `canceled` are not unfinished.
- Add schema changes through `TaskStore.Initialize()` migrations first, then update Raycast access and tests.
- Do not add a second migration system unless there is a concrete need.

## Development Commands

From the repository root:

```powershell
dotnet test ActivityManagement.slnx
```

For the Raycast extension:

```powershell
cd manage-tasks
npm run lint
npm run build
```

Run the smallest relevant check for the area changed. For storage changes, update or add focused tests in `tests/ActivityManagement.Tests`.

## Implementation Constraints

- Keep the first version local-first.
- Avoid network synchronization until there is a concrete need.
- Prefer small direct changes over broad service architecture.
- Prefer existing project patterns over new abstractions.
- Do not add dependencies for small parsing, formatting, validation, date handling, mapping, or request wrapping.
- Do not hide database, notification, save, delete, auth, or permission failures behind silent defaults.
- Validate at real trust boundaries or when needed for a clear local error; avoid decorative validation.
- Keep generated output out of Git: `.vs/`, `bin/`, `obj/`, `node_modules/`, and Raycast build output should remain ignored.

## Notes For Future Work

- If the schema changes, update both `TaskStore.cs` and `manage-tasks/src/database.ts` in the same change.
- If task ordering changes, update C# tests and Raycast queries together.
- If notification actions change, keep status and snooze behavior explicit in `WindowsIntegration.cs`.
- If a CLI or Codex skill is added later, make it use the existing database path and schema rather than inventing a new storage layer.
