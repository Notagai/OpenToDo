# OpenToDo

A free, local-first to-do app with optional AI providers, built with C# / .NET 8 and Avalonia.

- **Local-first:** every task is a plain JSON file under `%LOCALAPPDATA%/OpenToDo/tasks` (or the platform equivalent).
- **Home dashboard:** greeting, completion streak, day/week/month/year stats and a 14-day activity chart.
- **Fast capture:** quick-add bar, `Ctrl+N` for the full editor, due-date shortcuts, search.
- **AI (optional):** OpenAI, Groq and OpenRouter. API keys live in the OS credential store, never in `settings.json`.

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
