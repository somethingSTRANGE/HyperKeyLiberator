# HyperKey Liberator

Prevents Windows Explorer from hijacking Hyper-key shortcuts, freeing them for your own bindings.

## What is the Hyper key?

The **Hyper key** is the combination of all four standard modifier keys pressed simultaneously:

**Ctrl + Win + Alt + Shift**

Some keyboards — particularly Microsoft hardware — have a dedicated physical key labeled with the Office logo that produces this exact combination. Whether you use a dedicated key or press all four modifiers manually, Windows Explorer claims a set of Hyper+key shortcuts for launching Microsoft 365 apps. HyperKeyLiberator gets rid of them.

## Blocked shortcuts

These are the shortcuts Windows Explorer registers and this service disables:

| Shortcut       | Default behavior                                            |
|----------------|-------------------------------------------------------------|
| `Hyper+D`      | Open OneDrive                                               |
| `Hyper+L`      | Open LinkedIn in the default browser                        |
| `Hyper+N`      | Open OneNote UWP app                                        |
| `Hyper+O`      | Open Outlook                                                |
| `Hyper+P`      | Open PowerPoint                                             |
| `Hyper+T`      | Open Teams in the default browser                           |
| `Hyper+W`      | Open Word                                                   |
| `Hyper+X`      | Open Excel                                                  |
| `Hyper+Y`      | Open Yammer in the default browser                          |
| `Hyper+Space`  | Open the emoji picker (also accessible via `Win+;`)         |


## The _bare_ Hyper key

The _bare_ `Hyper` keypress (no additional key) launches the Office UWP app via the `ms-officeapp` protocol handler rather than a registered hotkey, so it cannot be blocked the same way. Suppress it separately with this one-time registry command:

```
REG ADD HKCU\Software\Classes\ms-officeapp\Shell\Open\Command /t REG_SZ /d rundll32
```

This is a per-user setting and only needs to be run once. To reverse it:

```
REG DELETE HKCU\Software\Classes\ms-officeapp\Shell\Open\Command /f
```

## How it works

Windows only allows one process to hold a global hotkey registration at a time. HyperKeyLiberator exploits this by registering dummy stubs for all Hyper+key combinations before Explorer loads. When Explorer starts and tries to register its shortcuts, they are already taken and its registrations silently fail. After a short delay (enough for Explorer to finish its startup sequence), the stubs are released — leaving the keys free for you to assign in AutoHotkey, PowerToys, or any other tool.

If Explorer ever restarts, the process repeats automatically.

### Why two executables?

Windows enforces **Session 0 Isolation**: all Windows Services run in Session 0, a background session isolated from the interactive desktop (Session 1, where Explorer runs). `RegisterHotKey` is session-scoped, so a call from Session 0 has no effect on Explorer in Session 1.

The solution is two processes:

- **`HyperKeyLiberatorService.exe`** — the Windows Service (Session 0). Runs as Local System. Monitors user logon events and spawns the helper into your interactive session using `WTSQueryUserToken` + `CreateProcessAsUser`.
- **`HyperKeyLiberator.exe`** — the helper (Session 1). A plain background process that runs alongside Explorer and handles all hotkey registration. Visible in Task Manager under your user processes.

### Logon sequence

```
Boot
 │
 └─ HyperKeyLiberatorService starts (Session 0)

User logs in
 │
 ├─ WTS_SESSION_LOGON fires immediately
 │   └─ Service spawns HyperKeyLiberator.exe into Session 1
 │       └─ Helper registers hotkey stubs  ✔️ completes in milliseconds
 │
 └─ Userinit.exe runs (profile load, group policy, logon scripts...)
     └─ Explorer.exe starts and initializes shell
         └─ Explorer attempts hotkey registration  ❌ already taken
             └─ After ~4 seconds: helper releases stubs
                 └─ Keys are now free for your own bindings
```

### Verifying it works

After logging in, run one of these scripts manually in AutoHotkey to confirm a Hyper-key combination is free. Press `Hyper+W` — if a message box appears instead of Word launching, everything is working.

**AutoHotkey v2:**
```ahk
^#!+w:: MsgBox("Hyper+W is free!")
```

**AutoHotkey v1:**
```ahk
^#!+w::
    MsgBox, Hyper+W is free!
return
```

In AutoHotkey, `^` is Ctrl, `#` is Win, `!` is Alt, and `+` is Shift — so `^#!+w` is the full Hyper+W combination.

> **Note for AutoHotkey, PowerToys, and similar tools:** The stubs are held for approximately 4 seconds after Explorer starts. If your tool launches at logon and registers Hyper-key bindings immediately, it may try to claim them while the stubs are still active — and silently fail. Add a 5-second startup delay (e.g., `Sleep 5000` in AutoHotkey) before registering any Hyper-key hotkeys.

## Installation

### Prerequisites

- Windows 10 or later (64-bit)
- Administrator access to register the service

### Steps

1. Download the latest release (or [build it yourself](#build))
2. Place **both** `HyperKeyLiberatorService.exe` and `HyperKeyLiberator.exe` in the same permanent folder (e.g. `C:\Services\HyperKeyLiberator\`)
3. Open an elevated terminal and register the service:
   ```
   sc create HyperKeyLiberator binPath= "C:\Services\HyperKeyLiberator\HyperKeyLiberatorService.exe" start= auto DisplayName= "HyperKey Liberator Service"
   ```
   Note the space after each `=` — this is required by `sc`.
4. Set the service description:
   ```
   sc description HyperKeyLiberator "Prevents Explorer from hijacking Hyper-key shortcuts"
   ```
5. The service runs as **Local System** by default — this is required. `SE_TCB_PRIVILEGE` (held by Local System) is needed to obtain user session tokens and spawn the helper into your interactive session.
6. Start the service:
   ```
   sc start HyperKeyLiberator
   ```
7. Log out and back in to verify the shortcuts no longer trigger their default behaviors

## Uninstallation

1. Open an elevated terminal and stop the service:
   ```
   sc stop HyperKeyLiberator
   ```
2. Remove the service registration:
   ```
   sc delete HyperKeyLiberator
   ```
3. Delete the folder containing `HyperKeyLiberatorService.exe` and `HyperKeyLiberator.exe`

If you applied the bare Hyper key fix, see [The _bare_ Hyper key](#the-bare-hyper-key) section for how to reverse it.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
# Debug build
dotnet build

# Self-contained release build for deployment
dotnet publish -c Release -r win-x64 --self-contained
```

The published executable will be in `bin\Release\net10.0\win-x64\publish\`.

## Development resources

- [Virtual-Key Codes](https://learn.microsoft.com/en-us/windows/win32/inputdev/virtual-key-codes)
- [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
- [UnregisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unregisterhotkey)

## Based on

This project is a C# rewrite of the C++ [OfficeKeyFix](https://github.com/acook/OfficeKeyFix) family of tools:

- The [standalone version](https://github.com/acook/OfficeKeyFix) (by Anthony Heddings, maintained by [@acook](https://github.com/acook)) kills and restarts Explorer to clear its hotkey registrations
- The [logon/service version](https://github.com/acook/OfficeKeyFix/tree/logon) (by [@acook](https://github.com/acook)) introduced the background monitoring approach this project is based on

## License

MIT — see [LICENSE](LICENSE).

Upstream C++ projects are licensed under BSD 3-Clause.