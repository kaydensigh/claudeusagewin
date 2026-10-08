# CLAUDE.md — Claude Usage Monitor for Windows

## What This Is

A native Windows system tray app that monitors Claude Code API rate limits in real time. Displays up to 4 tray icons (session, weekly, model-specific weekly, overage) with live percentage text and color-coded status. Single-file executable built with .NET 10 + Native AOT + H.NotifyIcon.

## Build & Run

```bash
cd ClaudeUsage

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
ClaudeUsage/
├── Program.cs               # Win32 message pump entry point (GetMessage loop)
├── App.cs                   # Tray icons, polling timer, context menu, icon rendering
├── Models/
│   └── UsageData.cs         # API response model with source-generated JSON serialization
├── Services/
│   ├── CredentialService.cs # OAuth token discovery (native Windows + WSL), read-only
│   ├── LocalizationService.cs # 14-language JSON-based i18n
│   └── UsageApiService.cs   # Anthropic usage API client with retry/backoff
├── Helpers/
│   ├── IdleHelper.cs        # Win32 idle/lock detection (P/Invoke)
│   └── StartupHelper.cs     # Registry-based startup & language persistence
└── Locale/                  # 14 language JSON files (en, de, fr, es, ja, ko, etc.)
```

## Architecture & Key Patterns

- **Raw Win32 message pump** — no WPF or WinForms dependency; `Program.cs` runs `GetMessage`/`TranslateMessage`/`DispatchMessage` directly
- **Async/await everywhere** — never block the message pump for I/O; a one-shot `System.Threading.Timer` fires on the thread pool and updates tray icons directly (`Shell_NotifyIcon` works from any thread on Windows 10/11, so no marshalling)
- **Wake scheduling** — wakes every 5min and refreshes if the last success is 4min+ old; skips refreshes while the workstation is locked or idle 10min+; when session or weekly hits 100%, sleeps until the reset; after a failed refresh, retries with backoff from 1min up to 20min
- **Exponential backoff** — up to 5 retries (1s, 2s, 4s, 8s, 16s) on HTTP 429/5xx errors
- **Credential caching** — 30min TTL; WSL path scan uses 10s timeout to avoid hangs
- **No token refresh** — credentials are read-only; refresh tokens are single-use, so refreshing here would sign Claude Code out. An expired token just means waiting for Claude Code to refresh it
- **Native AOT** — source-generated JSON serialization via `[JsonSerializable]` context for AOT compatibility
- **Icon rendering** — `System.Drawing.Graphics` draws percentage text onto bitmap icons; proper `DestroyIcon()` cleanup to avoid HICON leaks
- **API:** `GET https://api.anthropic.com/api/oauth/usage` with `anthropic-beta: oauth-2025-04-20` header (undocumented, may change). The model-specific weekly quota comes from the `limits` array (`kind: "weekly_scoped"`, name in `scope.model.display_name`); classify on `kind`, never on the label

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
