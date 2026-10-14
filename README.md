# OpenToDo

A free, local-first to-do app with optional AI providers, built with C# / .NET 8 and Avalonia.

- **Local-first:** every task is a plain JSON file under `%LOCALAPPDATA%/OpenToDo/tasks` (or the platform equivalent).
- **First run:** a short welcome + tutorial asks for your username. It is saved to `settings.json` (created on first launch) and used to greet you; you can change it any time in Settings.
- **Home dashboard:** personalised greeting, completion streak with a flame that lights up, day/week/month/year stats, a 14-day activity chart and a one-click AI progress report.
- **Fast capture:** quick-add bar, `Ctrl+N` for the full editor, due-date shortcuts, search.
- **AI (optional):** OpenAI, Groq and OpenRouter. A collapsible sidebar assistant can add, complete, reschedule and delete tasks for you, and summarise how you're doing. API keys live in the OS credential store, never in `settings.json`.

## Build & test

```bash
dotnet build OpenToDo.sln
dotnet test
dotnet run --project src/OpenToDo.App
```

## Layout

| Project | Purpose |
|---|---|
| `OpenToDo.Core` | Task model, ordering/completion rules, analytics |
| `OpenToDo.Data` | Crash-safe JSON repository |
| `OpenToDo.Plugins` | AI client + settings/keyring store |
| `OpenToDo.App` | Avalonia desktop UI |
