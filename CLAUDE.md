# CLAUDE.md — Claude Usage Monitor for Windows

## What This Is

A native Windows system tray app that monitors Claude Code API rate limits in real time. Displays up to 4 tray icons (session, weekly, sonnet, overage) with live percentage text and color-coded status. Single-file executable built with .NET 10 + Native AOT + H.NotifyIcon.

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

# Publish single-file exe (Native AOT)
dotnet publish -c Release -r win-x64
# Output: bin/Release/net10.0-windows/win-x64/publish/ClaudeUsage.exe
```

**Requirements:** .NET 10.0 Runtime, Windows 10/11 x64

**No test suite exists.** Testing is manual.

## Project Structure

```
visualstudio-project/ClaudeUsage/ClaudeUsage/
├── Program.cs               # Win32 message pump entry point (GetMessage loop)
├── App.cs                   # Tray icons, polling timer, context menu, icon rendering
├── Models/
│   └── UsageData.cs         # API response model with source-generated JSON serialization
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

- **Raw Win32 message pump** — no WPF or WinForms dependency; `Program.cs` runs `GetMessage`/`TranslateMessage`/`DispatchMessage` directly
- **Async/await everywhere** — never block the UI thread for I/O; timer fires on thread pool, marshaled back via `SynchronizationContext.Post()`
- **Adaptive polling** — 7min normal, 5min fast (usage increasing), 20min idle; aligns to quota resets
- **Exponential backoff** — up to 5 retries (1s, 2s, 4s, 8s, 16s) on HTTP 429/5xx errors
- **Credential caching** — 30min TTL; WSL path scan uses 5s timeout to avoid hangs
- **Token auto-refresh** — refreshes within 5min of expiry via `https://console.anthropic.com/v1/oauth/token`
- **Native AOT** — source-generated JSON serialization via `[JsonSerializable]` context for AOT compatibility
- **Icon rendering** — `System.Drawing.Graphics` draws percentage text onto bitmap icons; proper `DestroyIcon()` cleanup to avoid HICON leaks
- **API:** `GET https://api.anthropic.com/api/oauth/usage` with `anthropic-beta: oauth-2025-04-20` header (undocumented, may change)

## Code Conventions

- **C# 12**, nullable enabled, implicit usings enabled
- PascalCase public members, camelCase locals
- File-scoped namespaces (`namespace ClaudeUsage.Services;`)
- `[JsonPropertyName("snake_case")]` for API mapping
- Localization via `LocalizationService.T("key")` or `LocalizationService.T("key", args...)`
- Icon rendering uses `System.Drawing.Graphics` (not SkiaSharp/WPF) for minimal dependencies

## When Modifying

- **New service/helper:** Follow existing folder structure (Services/, Helpers/, Models/)
- **New locale key:** Must be added to all 14 JSON files in `Locale/`
- **Credential paths:** Native = `%USERPROFILE%\.claude\.credentials.json`; WSL = `\\wsl$\{distro}\home\{user}\.claude\.credentials.json`
- **Registry:** Startup at `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`; settings at `HKCU\SOFTWARE\ClaudeUsage`
- **JSON models:** Use source-generated serialization (`AppJsonContext`) for Native AOT compatibility

## NuGet Dependencies

- `H.NotifyIcon 2.4.1` — system tray icon management (no WinForms dependency)
