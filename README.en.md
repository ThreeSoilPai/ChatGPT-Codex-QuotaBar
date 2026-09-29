# Quota Bar for Codex in the ChatGPT Desktop App

<a href="./README.md">中文</a> | <a href="./README.en.md">English</a>

A lightweight Windows desktop utility that displays your remaining account quota near the input box for Codex in the ChatGPT desktop app. It reads quota data from the local Codex App Server. It does not modify the ChatGPT desktop app installation or read or save your login token.

## Download and usage

Download **`CodexQuotaBar-Portable.exe`** from the [Releases page](../../releases). This self-contained single-file app is about 71 MB and includes the .NET runtime it needs. Save it in a permanent folder and double-click it. Then open Codex in the ChatGPT desktop app and start a conversation; the quota bar will appear near the input box. The app has no regular main window. You can check its status or exit from the system tray.

Requirements: 64-bit Windows 10 or 11; Codex in the ChatGPT desktop app must be installed and signed in, and its local `codex.exe app-server` must be available. This EXE does not include the ChatGPT desktop app.

By default, double-clicking registers the app to start when you sign in, using Windows Task Scheduler and a registry Run entry. To run it temporarily without registering it to start automatically, use a terminal:

```powershell
.\Codex剩余额度条.exe --overlay
```

## Features

- Displays the remaining quota percentage, with the color gradually changing as the quota decreases.
- follows the window and input box for Codex in the ChatGPT desktop app.
- Hides automatically when you switch to another app, the input box is not visible, or its position cannot be determined reliably.
- Refreshes quota data every 60 seconds. If the connection is interrupted, it shows a status instead of inventing a percentage.
- Shows the current status and full reset time in the system tray.

## Build from source

```powershell
dotnet publish .\src\CodexQuotaBar.Portable\CodexQuotaBar.Portable.csproj -c Release -o .\release
```

The generated `release/Codex剩余额度条.exe` is the portable app. When you push a `v*` version tag, GitHub Actions renames it to `CodexQuotaBar-Portable.exe` and publishes it on the Releases page. Both names refer to the same portable app.

## Frequently asked questions

**Nothing appears after I double-click the app.** The app waits in the background for Codex in the ChatGPT desktop app. Open Codex in the ChatGPT desktop app and start a conversation, then check the system tray. The quota bar hides when you switch to another app.

**The quota does not appear after I copy the app to another computer.** Make sure Codex in the ChatGPT desktop app is installed, signed in, and running normally on that computer. Quota data is provided by that computer's Codex App Server.

**How do I remove the app?** First, exit the quota bar from the system tray and stop the background watcher. If automatic startup was registered, also remove the `CodexQuotaBarWatcherTask` scheduled task and the `CodexQuotaBarWatcher` Run value for the current user, then delete the EXE. Deleting the EXE alone does not remove its startup entries.

This is an independent utility that works with Codex in the ChatGPT desktop app. It is not an official OpenAI product.
