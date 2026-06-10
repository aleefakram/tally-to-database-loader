# Repository Guidelines

## Project Structure & Module Organization

This repository contains both the legacy Node/TypeScript loader and the .NET port. The .NET solution lives in `src/TallyDbLoader.sln`, with core sync logic in `src/TallyDbLoader.Core` and the WPF shell in `src/TallyDbLoader.Wpf`. Tests are in `tests/TallyDbLoader.Tests`. Runtime and export configuration examples are at the repo root, including `tally-export-config.yaml` and `tally-export-config-incremental.yaml`. Design notes, ADRs, phase specs, and implementation plans live under `docs/`, especially `docs/adr/` and `docs/superpowers/`.

## Build, Test, and Development Commands

- `dotnet build src/TallyDbLoader.sln` builds the .NET solution.
- `dotnet test tests/TallyDbLoader.Tests/TallyDbLoader.Tests.csproj --no-restore` runs the default fast test suite.
- `dotnet run --project src/TallyDbLoader.Wpf/TallyDbLoader.Wpf.csproj` starts the WPF app when a Windows desktop session is available.
- `npm run build` compiles the legacy TypeScript path into `dist/`.
- `npm run dev` runs the legacy TypeScript server through `nodemon`.

## Coding Style & Naming Conventions

Use C# with nullable reference types enabled. Keep `TallyDbLoader.Core` UI-free and independently testable; WPF should remain a thin presentation layer. Match existing repository patterns before adding abstractions. Use PascalCase for C# types and members, camelCase for locals, and snake_case only where matching persisted SQL/JSON schema fields. Keep changes surgical: do not refactor unrelated code or reformat files outside the task.

## Testing Guidelines

The .NET tests use xUnit and `Xunit.SkippableFact`. Default `dotnet test` must remain fast, local, and deterministic, using SQLite, mocks, fakes, and fixtures. Real MSSQL, PostgreSQL, MySQL, or live Tally tests must be opt-in and skipped when not configured. Name tests by behavior, for example `SaveCompanyProfile_Create_WritesAuditRow`.

## Commit & Pull Request Guidelines

Recent history uses conventional prefixes such as `feat:`, `test:`, and `docs:`; keep commits focused and descriptive. PRs should include the intent, changed files or modules, verification command output, and any skipped opt-in tests. Link the relevant spec or plan in `docs/superpowers/` when implementing a planned slice.

## Agent-Specific Instructions

Follow Karpathy-style discipline: think before coding, choose the simplest sufficient solution, touch only necessary files, and verify before claiming completion. For reviews, list findings first with file and line references, then summarize residual risks and test gaps.
