# FlowBoard

A fast, offline, keyboard-friendly kanban board for Windows 10/11.

**Stack:** .NET 8 · WPF · WPF-UI (Fluent) · CommunityToolkit.Mvvm · Microsoft.Data.Sqlite (WAL) · Markdig

- Data: `%APPDATA%\FlowBoard\flowboard.db` — autosaved on every change, WAL journalling, transactional writes.
- Window geometry: `%APPDATA%\FlowBoard\window.json`.
- See `docs/ARCHITECTURE.md` for the design rationale and the staged delivery plan.

## Build

```
dotnet restore
dotnet build -c Release
dotnet run --project src/FlowBoard/FlowBoard.csproj
```
