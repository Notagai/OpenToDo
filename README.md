# OpenToDo

A cross-platform, local-first task manager built with C# and .NET 8 using Avalonia UI.

## Structure

- **OpenToDo.Core** — platform-independent task models and interfaces.
- **OpenToDo.Data** — local JSON-backed storage, with one file per task.
- **OpenToDo.Plugins** — extension contract for integrations.
- **OpenToDo.App** — Windows/Linux desktop UI built with Avalonia.
- **OpenToDo.Tests** — unit tests.
- **docs/** — static documentation site ready for Cloudflare Pages.

## Development

Open OpenToDo.sln in Visual Studio with the .NET 8 SDK installed. Avalonia supports desktop Windows, Linux, and macOS targets while keeping the application on standard .NET APIs.
