# OpenToDo

OpenToDo is a cross-platform, local-first to-do application built with C#/.NET 8 and Avalonia.

## Current features

- Home dashboard opens by default.
- Completion analytics for today, this week, this month, and this year.
- 14-day and 6-month completion activity charts.
- Todo and Completed archive views.
- Restore and permanent task deletion.
- Local JSON task storage.
- Unified Fluent/Avalonia styling.
- AI provider settings for OpenAI, Groq, and OpenRouter.
- Live model fetching for each configured provider.
- Provider-specific API request handling:
  - OpenAI: Responses API, `max_output_tokens`, `store=false`.
  - Groq: OpenAI-compatible Chat Completions API, `temperature`, `max_completion_tokens`.
  - OpenRouter: OpenAI-compatible Chat Completions API, `temperature`, `max_tokens`.

## Structure

```text
OpenToDo/
├── src/
│   ├── OpenToDo.Core/        # task/domain models
│   ├── OpenToDo.Data/        # local JSON storage
│   ├── OpenToDo.Plugins/     # plugin and AI provider integrations
│   └── OpenToDo.App/         # Avalonia desktop UI
├── tests/
│   └── OpenToDo.Tests/
├── docs/
├── README.md
└── OpenToDo.sln
```

## Development

Open `OpenToDo.sln` in Visual Studio with the Avalonia extension installed, then build and run the application.

API keys configured through Settings are stored in the user's local OpenToDo application-data directory. Keep that file private and never commit it.
