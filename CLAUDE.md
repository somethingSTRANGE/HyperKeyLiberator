# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

HyperKeyLiberator is a C# Windows service that prevents File Explorer from monopolizing Hyper-key (Ctrl+Win+Alt+Shift) shortcuts such as Hyper+W (Word), Hyper+T (Teams), Hyper+O (Outlook), etc. It does this by pre-registering dummy hotkeys before Explorer loads, then releasing them after Explorer has finished its own registration — leaving the key combinations free for user-defined bindings.

Reference implementations (C++):
- Standalone (kill/restart explorer): https://github.com/acook/OfficeKeyFix
- Service (logon-aware monitor): https://github.com/acook/OfficeKeyFix/tree/logon

## Build & Run

```bash
# Build
dotnet build

# Run locally (for testing outside the service host)
dotnet run --project HyperKeyLiberator

# Publish self-contained for deployment
dotnet publish -c Release -r win-x64 --self-contained

# Install as a Windows service (run as Administrator)
sc create HyperKeyLiberator binPath="C:\path\to\HyperKeyLiberator.exe" start=auto
sc start HyperKeyLiberator

# Or use NSSM for user-session deployment (preferred — see Session Isolation below)
nssm install HyperKeyLiberator "C:\path\to\HyperKeyLiberator.exe"
```

## Architecture

The project is a .NET Worker Service (`BackgroundService`) structured around three concerns:

### 1. Hotkey Registration (`HotkeyBlocker`)
P/Invoke wrapper around `RegisterHotKey` / `UnregisterHotKey` (user32.dll). Registers all Hyper-key combinations as stubs (no handler needed — ownership is the goal). Because `RegisterHotKey` with `hWnd = IntPtr.Zero` ties registrations to the calling thread's message queue, the blocker must run on a dedicated STA thread that pumps messages via `GetMessage` / `TranslateMessage` / `DispatchMessage`. Stubs are held only long enough to block Explorer's registration attempt, then released.

Hotkey list (MOD_WIN | MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT):

| VK      | Shortcut        | Original behavior                        |
|---------|-----------------|------------------------------------------|
| (none)  | `Hyper`         | Open Office UWP app                      |
| `0x44`  | `Hyper+D`       | Open OneDrive                            |
| `0x4C`  | `Hyper+L`       | Open LinkedIn (browser redirect)         |
| `0x4E`  | `Hyper+N`       | Open OneNote UWP                         |
| `0x4F`  | `Hyper+O`       | Open Outlook                             |
| `0x50`  | `Hyper+P`       | Open PowerPoint                          |
| `0x54`  | `Hyper+T`       | Open Teams (browser redirect)            |
| `0x57`  | `Hyper+W`       | Open Word                                |
| `0x58`  | `Hyper+X`       | Open Excel                               |
| `0x59`  | `Hyper+Y`       | Open Yammer (browser redirect)           |
| `0x20`  | `Hyper+Space`   | Open emoji picker                        |

The bare `Hyper` keypress (no additional key) opens the Office UWP app via a separate mechanism (ms-officeapp protocol handler). Block it with a registry tweak alongside the service:
```
REG ADD HKCU\Software\Classes\ms-officeapp\Shell\Open\Command /t REG_SZ /d rundll32
```

### 2. Explorer Monitor (`ExplorerMonitor`)
Watches the `explorer.exe` process lifecycle using `CreateToolhelp32Snapshot` / `Process32Next` (or `System.Diagnostics.Process.GetProcessesByName`) and `WaitForSingleObject` / `Process.WaitForExit`. Drives the state machine transitions.

### 3. State Machine (inside `BackgroundService.ExecuteAsync`)
```
REGISTER stubs
    → wait for explorer.exe to appear
    → delay ~4 seconds (gives Explorer time to attempt hotkey registration and fail)
    → UNREGISTER stubs
    → wait for explorer.exe to exit
    → loop back to REGISTER
```
The 4-second delay is the critical window: Explorer must be running and have attempted registration before stubs are released.

## Key Technical Constraints

### Session 0 Isolation
Standard Windows Services run in Session 0, which is isolated from the interactive desktop. `RegisterHotKey` calls from Session 0 apply only to Session 0 — they will **not** block Explorer's hotkeys in the user's interactive session (Session 1+). To register hotkeys in the user's session, the service must either:
- Run in the user's session context (preferred: install via NSSM as a logon-triggered user-session service or Task Scheduler `At log on` task), **or**
- Spawn a helper process in the interactive session using `WTSQueryUserToken` + `CreateProcessAsUser`.

The NSSM/Task Scheduler approach is simpler and matches how the reference C++ service is deployed.

### Message Loop Requirement
`RegisterHotKey` with `hWnd = IntPtr.Zero` requires the registering thread to have a Win32 message loop. In a Worker Service, spin up a dedicated `Thread` (set `ApartmentState.STA`), call `RegisterHotKey` on it, then loop `GetMessage` until cancellation. Do not use `Task.Run` for this — thread identity matters for hotkey ownership.

### Startup Timing
The service (or scheduled task) must start and register stubs **before** Explorer's shell registration runs at logon. Setting start type to `Automatic` with no dependencies is generally sufficient; a brief retry loop on registration failure handles races.
