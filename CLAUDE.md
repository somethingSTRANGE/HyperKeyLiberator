# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

HyperKeyLiberator is a C# Windows service that prevents File Explorer from monopolizing Hyper-key (Ctrl+Win+Alt+Shift) shortcuts such as Hyper+W (Word), Hyper+T (Teams), Hyper+O (Outlook), etc. It does this by pre-registering dummy hotkeys before Explorer loads, then releasing them after Explorer has finished its own registration — leaving the key combinations free for user-defined bindings.

Reference implementations (C++):
- Standalone (kill/restart explorer): https://github.com/acook/OfficeKeyFix
- Service (logon-aware monitor): https://github.com/acook/OfficeKeyFix/tree/logon

## Build & Run

```bash
# Build the full solution
dotnet build HyperKeyLiberator.sln

# Run the helper standalone (Session 1 — useful for testing hotkey logic without the service)
dotnet run --project HyperKeyLiberator/HyperKeyLiberator.csproj

# Publish self-contained release builds for deployment
dotnet publish HyperKeyLiberatorService/HyperKeyLiberatorService.csproj -c Release -r win-x64 --self-contained
dotnet publish HyperKeyLiberator/HyperKeyLiberator.csproj -c Release -r win-x64 --self-contained

# Install as a Windows service via NSSM (run as Administrator)
# Both exes must be in the same deployment folder
nssm install HyperKeyLiberator "C:\path\to\HyperKeyLiberatorService.exe"
# Leave Log On as Local System (default) — WTSQueryUserToken requires SE_TCB_PRIVILEGE
sc start HyperKeyLiberator
```

## Architecture

The project is split into two executables due to Windows Session 0 Isolation (see Key Technical Constraints below).

### 1. HyperKeyLiberatorService — Session 0 Windows Service

Registered with the Windows Service Control Manager via NSSM. Runs as Local System in Session 0. Its sole responsibility is session lifecycle management:

- Polls `WTSEnumerateSessions` every second for active interactive user sessions
- On new session: calls `WTSQueryUserToken` to get the user's token, then `CreateProcessAsUser` to spawn the helper into that session
- On session end: kills the helper process
- Handles multiple concurrent sessions and automatic re-spawn if the helper crashes

The service does not perform any hotkey operations itself.

### 2. HyperKeyLiberator — Session 1 Helper (plain console app)

Spawned by the service into the user's interactive session. Contains all hotkey blocking logic:

- Registers dummy stubs for all Hyper+key combinations via `RegisterHotKey`
- Waits for `explorer.exe` to appear (polling every second)
- Holds stubs for 4 seconds — the window in which Explorer attempts and fails its own registrations
- Calls `UnregisterHotKey` to release the stubs
- Monitors `explorer.exe` for exit, then loops back to re-register
- Can also be run standalone for testing without the service

### State machine (inside the helper's RunLoop)

```
REGISTER stubs
    → wait for explorer.exe to appear
    → delay 4 seconds (Explorer attempts registration and fails)
    → UNREGISTER stubs
    → wait for explorer.exe to exit
    → loop back to REGISTER
```

### Hotkey list (MOD_WIN | MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT)

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

The bare `Hyper` keypress opens the Office UWP app via the `ms-officeapp` protocol handler — not a registered hotkey — so it cannot be blocked by `RegisterHotKey`. Suppress it separately:
```
REG ADD HKCU\Software\Classes\ms-officeapp\Shell\Open\Command /t REG_SZ /d rundll32
```

## Key Technical Constraints

### Session 0 Isolation

Windows Vista+ enforces strict isolation between Session 0 (where all SCM-managed Windows Services run, regardless of logon account) and Session 1+ (the interactive user desktop). `RegisterHotKey` is session-scoped: a call from Session 0 only blocks hotkeys in Session 0 and has no effect on Explorer running in Session 1.

This is why the project uses two processes:
- The **service** runs in Session 0 and has the privileges (`SE_TCB_PRIVILEGE` via Local System) needed to obtain user tokens and spawn processes into other sessions
- The **helper** runs in Session 1 alongside Explorer and can therefore compete with it for hotkey registrations

### Startup Timing

The service receives a `WTS_SESSION_LOGON` notification when a user's session is created, before `Userinit.exe` has launched `explorer.exe`. The service spawns the helper immediately on this notification. By the time Explorer finishes initializing and attempts to register its hotkeys, the helper has already claimed them.

### Message Loop and Thread Ownership

`RegisterHotKey` with `hWnd = IntPtr.Zero` binds the registration to the calling thread's message queue. `UnregisterHotKey` must be called from the same thread. The helper uses a dedicated thread (`RunLoop`) for all `RegisterHotKey`/`UnregisterHotKey` calls to ensure thread ownership is preserved. `async/await` is avoided for these calls because continuations may resume on different thread-pool threads.