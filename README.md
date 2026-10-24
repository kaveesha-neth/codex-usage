# Codex Usage Widget

An unofficial Windows desktop and taskbar widget for managed ChatGPT Codex quota. It reads quota data from a local `codex app-server` process; it does not send model requests or estimate message counts.

## Requirements

- Windows 10 or 11 on x64. The source also supports a Windows ARM64 build.
- The official [Codex CLI](https://developers.openai.com/codex/cli) installed and signed in with a managed ChatGPT account. API-key billing does not provide the subscription quota shown here.
- No separate .NET installation is needed for a self-contained release build. Building from source requires the .NET 8 SDK on Windows.

This project is not made or endorsed by OpenAI. Its executable is unsigned.

## Run the widget

Once a Windows x64 ZIP is published on this project's GitHub **Releases** page, extract the whole ZIP to a permanent folder and run `CodexUsage.exe`. The executable is distributed as a release asset, not committed to the source repository. See [RELEASING.md](RELEASING.md) for packaging details.

The widget uses the Codex CLI's existing managed sign-in. If sign-in is needed, use **Sign in with ChatGPT** from the tray icon. If the CLI is not found, open **Settings**, select the native `codex.exe`, save, quit, and relaunch. A custom npm installation may need that path selected manually; the widget does not run `codex.cmd` or shell scripts.

Drag the desktop widget to position it. Its tray icon provides Settings, refresh, lock, click-through recovery, startup registration, and Quit. Only one widget instance runs per Windows session, so quit before switching between live and demo modes.

## Desktop and taskbar views

The desktop view shows every quota bucket and window returned by the CLI. It displays quota remaining, local reset time, a live countdown, and connection freshness. Values that the service does not return remain unavailable. The bar represents `max(0, 100 - usedPercent)` and is visually clamped to 0–100; it does not invent daily, weekly, or monthly counts.

Enable **Taskbar widget** in Settings to show a ring and percentage on the primary horizontal taskbar **while the desktop view remains visible**. The ring uses the first reported 300-minute window. If none is reported, it shows an empty ring and an unavailable mark. Hover anywhere over the ring, number, or space between them for a detail popover with all returned windows. The popover includes quota remaining, reset times, and ticking countdowns. Right-click the taskbar view to open Settings.

Use **Taskbar offset** to move the overlay left of the notification area and **Taskbar zoom** to scale it from 50% to 200%. These settings save immediately. The overlay does not reserve taskbar space, so other icons can overlap it. Vertical and automatically hidden taskbars are not supported by this placement mode.

The desktop view normally attaches to Explorer's desktop layer so ordinary windows appear above it. This uses undocumented Windows behavior and can vary with Explorer versions, wallpaper tools, and virtual desktops. Settings offers a non-topmost bottom-window fallback. **Recover widget** in the tray menu restores selection and placement if the desktop view becomes inaccessible. The fallback can be hidden by Show Desktop.

## Build and verify from source

From PowerShell on Windows, install the .NET 8 SDK and run:

```powershell
cd Source
dotnet build .\CodexUsage.sln -c Release
dotnet run --project .\tests\CodexUsage.Tests -c Release --no-build
dotnet run --project .\src\CodexUsage.Widget -c Release
```

For an isolated synthetic display, quit the live widget and run:

```powershell
dotnet run --project .\src\CodexUsage.Widget -c Release -- --mock
```

The mock is permanently marked **DEMO**, does not start the app server, and never reads or writes the live usage cache. To publish a self-contained single-file Windows build after the checks, run `.\scripts\Build.ps1` from `Source`. Use `.\scripts\Build.ps1 -Runtime win-arm64` for ARM64. The output is in `Source/artifacts/<runtime>/` and is ignored by Git. The tests are a standalone assertion runner; `dotnet test` is not its verification command.

`Source/schemas/` is a checked-in protocol snapshot generated from Codex CLI 0.159.2. To compare your installed CLI's schema without signing in, run `.\scripts\Verify-CodexSchema.ps1` from `Source`; its default `schemas-local/` output is ignored. Review protocol changes before changing the quota adapter. See [VERIFICATION.md](VERIFICATION.md) for completed checks and the remaining manual Windows checklist.

## Privacy and data

The widget uses the CLI's managed account through its local app-server process. It reads account and rate-limit data; sign-in, cancel sign-in, and sign-out run only when you select those actions. Sign-out affects the shared CLI session. The transport blocks model turns, reset-credit consumption, purchases, API-key login, and email actions. The official usage dashboard opens only when selected; the widget does not scrape it.

Preferences and a sanitized quota cache live in `%LOCALAPPDATA%\CodexUsageWidget`, outside this repository. The cache contains quota fields, a fetch time, and a one-way account fingerprint, not credentials or raw account payloads. Delete that folder after quitting to reset local widget data.

The [license](LICENSE) applies to this project. Runtime license and third-party notices for packaged builds are in [Notices](Notices/).
