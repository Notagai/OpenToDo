# OpenToDo

OpenToDo is a cross-platform, local-first to-do application built with C#/.NET 8 and Avalonia.

## Current features
- Home analytics for today, this week, this month, and this year.
- Graph / exact-number analytics toggle.
- Create, edit, complete, restore, and permanently delete tasks.
- Optional descriptions and due dates.
- Local JSON task storage.
- Fluent desktop UI for Windows, macOS, and Linux.
- OpenAI, Groq, and OpenRouter settings with live model fetching.
- In-app AI connection test.
- API keys stored in the native OS credential/keyring system rather than settings.json.

## Development
Open OpenToDo.sln in Visual Studio with the Avalonia extension installed, then build and run the application.

Avalonia 12 uses compiled bindings by default. Typed DataTemplate bindings are used for task and chart objects.
