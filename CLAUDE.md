# CLAUDE.md — Claude Usage Monitor for Windows

## What This Is

A native Windows system tray app that monitors Claude Code API rate limits in real time. Single-file ~6MB executable built with .NET 8 + WPF + Fluent Design (WPF-UI).

## Build & Run

```bash
cd visualstudio-project/ClaudeUsage/ClaudeUsage

# Build and run (debug) — app runs synchronously, output piped to terminal,
# terminates when dotnet process is killed (Ctrl+C)
dotnet run

# Build only
dotnet build

# Release build
dotnet build -c Release

# Publish single-file exe
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
# Output: bin/Release/net8.0-windows/win-x64/publish/ClaudeUsage.exe
```

**Requirements:** .NET 8.0 Runtime, Windows 10/11 x64

**No test suite exists.** Testing is manual.

## Project Structure

```
visualstudio-project/ClaudeUsage/ClaudeUsage/
├── App.xaml.cs              # Entry point, tray icon, polling timer, theme sync
├── MainWindow.xaml(.cs)     # Detail popup with gauges, cards, animations
├── Controls/
│   └── UsageGauge.cs        # Custom speedometer gauge (native WPF DrawingContext)
├── Models/
│   └── UsageData.cs         # API response model with JSON deserialization
├── Services/
│   ├── CredentialService.cs # OAuth token discovery (native Windows + WSL) & refresh
│   ├── LocalizationService.cs # 14-language JSON-based i18n
│   └── UsageApiService.cs   # Anthropic usage API client with retry/backoff
├── Helpers/
│   ├── IdleHelper.cs        # Win32 idle/lock detection (P/Invoke)
│   └── StartupHelper.cs     # Registry-based startup & language persistence
└── Locale/                  # 14 language JSON files (en, de, fr, es, ja, ko, etc.)
```

## Architecture & Key Patterns

- **Async/await everywhere** — never block the UI thread for I/O
- **Adaptive polling** — 7min normal, 5min fast (usage increasing), 20min idle; aligns to quota resets
- **Exponential backoff** — up to 5 retries (1s, 2s, 4s, 8s, 16s) on HTTP errors
- **Credential caching** — 30min TTL; WSL path scan uses 3s timeout to avoid hangs
- **Token auto-refresh** — refreshes within 5min of expiry
- **API:** `GET https://api.anthropic.com/api/oauth/usage` with `anthropic-beta: oauth-2025-04-20` header (undocumented, may change)

## Code Conventions

- **C# 12**, nullable enabled, implicit usings enabled
- PascalCase public members, camelCase locals
- File-scoped namespaces (`namespace ClaudeUsage.Services;`)
- `[JsonPropertyName("snake_case")]` for API mapping
- Localization via `LocalizationService.T("key")` or `LocalizationService.T("key", args...)`
- UI rendering uses WPF `DrawingContext` (not SkiaSharp) for smaller exe size
- Theme changes via `ApplicationThemeManager.Changed` event

## When Modifying

- **New service/helper:** Follow existing folder structure (Services/, Helpers/, Models/)
- **New locale key:** Must be added to all 14 JSON files in `Locale/`
- **UI controls:** Prefer `DrawingContext` over SkiaSharp; use DependencyProperty for data binding
- **Credential paths:** Native = `%USERPROFILE%\.claude\.credentials.json`; WSL = `\\wsl$\{distro}\home\{user}\.claude\.credentials.json`
- **Registry:** Startup at `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`; settings at `HKCU\SOFTWARE\ClaudeUsage`

## NuGet Dependencies

- `WPF-UI 3.0.5` — Fluent Design (Mica, themes)
- `Svg.NET 3.4.7` — SVG tray icon rendering
- `System.Text.Json 8.0.5` — JSON parsing
