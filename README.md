# Claude Usage Monitor for Windows

> [!NOTE]
> Forked from https://github.com/sr-kai/claudeusagewin but with WinForms and WPF stripped out so we can use NativeAOT. There is no longer a GUI, only system tray icons.

Monitor Claude Code from your Windows system tray.

A native Windows tray app that shows your Claude usage at a glance.

Lightweight single EXE, no installation, no Electron, no Python.

Works with **Claude Code native for Windows** or **Claude Code in WSL**.

![Claude Usage Screenshot](image.png)

## Features

- **Native & lightweight** — single EXE, no installation. Download and run
- **Zero configuration** — authenticates through your existing Claude Code login. No API key, no manual token entry
- **Up to 4 tray icons** — Session (5h), Weekly (7d), model-specific weekly (e.g. Fable), and Overage usage, each showing a live percentage with color-coded status
- **Color-coded status** — green (under pace), yellow (normal), red (high usage or >95%), gray (error/no data)
- **Smart credential discovery** — automatically finds credentials from Claude Code native for Windows or WSL distros (Debian, Ubuntu, etc.), picking the most recently used installation when both exist
- **WSL availability guard** — WSL paths are skipped with a timeout if WSL isn't running, so native-only users experience zero startup delay
- **14 languages** — English, German, French, Spanish, Portuguese, Italian, Japanese, Korean, Hindi, Indonesian, Chinese Simplified, Chinese Traditional, Polish, Russian — auto-detected from your Windows display language, with manual override from the context menu
- **Adaptive polling** — speeds up during active usage (5 min), normal interval (7 min), slows down when idle (20 min), aligns to imminent quota resets, and backs off on errors with exponential backoff
- **Read-only credentials** — never refreshes or rewrites your Claude Code login; Claude Code keeps the token fresh
- **Launch at Login** — optional Windows startup via the right-click context menu
- **Show Details toggle** — right-click to show/hide the model-specific and Overage icons

## Requirements

- Windows 10 or Windows 11 (64-bit)
- Claude Code installed and logged in (native Windows CLI, WSL, VS Code extension, or JetBrains plugin — any variant works). The app reads the OAuth token that Claude Code stores locally (`~/.claude/.credentials.json`).

## Quick Start

No build tools required. Download the latest `ClaudeUsage.exe` from the [Releases](https://github.com/kaydensigh/claudeusagewin/releases) page, place it wherever you like, and run it.

## How to Use

| Action | What happens |
|--------|-------------|
| **Hover** over a tray icon | Tooltip shows usage percentage with reset time |
| **Right-click** a tray icon | Context menu: Refresh Now, Show Details, Launch at Login, Language selector, Exit |

### Tray icon not visible?

Windows may hide new tray icons by default. To keep the icon always visible:

1. Right-click the taskbar, then **Taskbar settings**
2. Expand **Other system tray icons** (Win 11) or **Select which icons appear on the taskbar** (Win 10)
3. Toggle **ClaudeUsage** to **On**

## How It Works

The app automatically discovers your Claude Code OAuth credentials by searching (in order):

1. **Windows native**: `%USERPROFILE%\.claude\.credentials.json`
2. **WSL distros**: `\\wsl$\{distro}\home\{user}\.claude\.credentials.json` (Debian, Ubuntu, Kali, etc.)

If credentials are found in both locations, the most recently modified file is used, so it automatically follows whichever Claude Code installation you're actively using.

The app queries the Anthropic usage API with proper authentication headers and displays your current limits as color-coded tray icons with live-updating percentages and tooltips.

> **Note:** This uses an undocumented API that could change at any time.

## Building from Source

1. Clone this repository
2. Open `ClaudeUsage.sln` in Visual Studio 2022
3. Restore NuGet packages
4. Build in Release mode
5. Publish:
```
dotnet publish -c Release -r win-x64
```

## Tech Stack

- **C# / .NET 10** with **Native AOT** compilation
- **Raw Win32 message pump** — minimal dependencies, no WPF or WinForms runtime
- **[H.NotifyIcon](https://github.com/HarinezumiSama/H.NotifyIcon)** — system tray icon management
- **System.Drawing** — programmatic icon rendering with percentage text
- **Source-generated JSON** — AOT-compatible serialization via `System.Text.Json`

## License

MIT
