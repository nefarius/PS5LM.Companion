# PS5LM Companion

Standalone .NET 10 / ASP.NET Core companion with a MudBlazor dashboard for PS5LM
(`cobanov/PS5LM`) and PS5LM Plus (`abhi245y/PS5LM-Plus`).

- Layout: `src/PS5LM.Companion.{Core,Infrastructure,App}`, `tests/PS5LM.Companion.Tests`.
- `Core` has no third-party dependencies; FluentFTP and Refit types stay in `Infrastructure`.
- Package versions live in `Directory.Packages.props`; warnings are errors.
- Multiple consoles: `PS5:Consoles:{id}` options, one isolated `ConsoleRuntime` each (`IConsoleRegistry`); REST is `/api/consoles/{consoleId}/...`; one transfer queue per console.
- The PS5 app only listens on port 8081 once a model is loaded: discovery and control are FTP-first.
- Workflow rules: `.cursor/rules/feature-workflow.mdc`.
