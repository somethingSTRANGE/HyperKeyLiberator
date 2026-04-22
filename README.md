# HyperKeyLiberator

Prevents Windows Explorer from hijacking Hyper-key shortcuts, freeing them for your own bindings.

## What is the Hyper key?

The **Hyper key** is the combination of all four standard modifier keys pressed simultaneously:

**Ctrl + Win + Alt + Shift**

Some keyboards — particularly Microsoft hardware — have a dedicated physical key labeled with the Office logo that produces this exact combination. Whether you use a dedicated key or press all four modifiers manually, Windows Explorer claims a set of Hyper+key shortcuts for launching Microsoft 365 apps. HyperKeyLiberator gets rid of them.

## Blocked shortcuts

These are the shortcuts Windows Explorer registers and this service disables:

| Shortcut       | Default behavior                                              |
|----------------|---------------------------------------------------------------|
| `Hyper+D`      | Open OneDrive                                                 |
| `Hyper+L`      | Open LinkedIn in the default browser                          |
| `Hyper+N`      | Open OneNote UWP app                                          |
| `Hyper+O`      | Open Outlook                                                  |
| `Hyper+P`      | Open PowerPoint                                               |
| `Hyper+T`      | Open Teams in the default browser                             |
| `Hyper+W`      | Open Word                                                     |
| `Hyper+X`      | Open Excel                                                    |
| `Hyper+Y`      | Open Yammer in the default browser                            |
| `Hyper+Space`  | Open the emoji picker                                         |


## The _bare_ Hyper key

The _bare_ `Hyper` keypress (no additional key) launches the Office UWP app via the `ms-officeapp` protocol handler rather than a registered hotkey, so it cannot be blocked the same way. Suppress it separately with this one-time registry command:

```
REG ADD HKCU\Software\Classes\ms-officeapp\Shell\Open\Command /t REG_SZ /d rundll32
```

This is a per-user setting and only needs to be run once.

## How it works

Windows only allows one process to hold a global hotkey registration at a time. HyperKeyLiberator exploits this by registering dummy stubs for all Hyper+key combinations before Explorer loads. When Explorer starts and tries to register its shortcuts, they are already taken and its registrations silently fail. After a short delay (enough for Explorer to finish its startup sequence), the service releases the stubs — leaving the keys free for you to assign in AutoHotkey, PowerToys, or any other tool.

If Explorer ever restarts, the service detects it and repeats the process automatically.

## Installation

### Prerequisites

- [NSSM](https://nssm.cc) (Non-Sucking Service Manager) — used to run the executable as a user-session service that starts at logon

### Steps

1. Download the latest release (or [build it yourself](#build))
2. Place the executable somewhere permanent (e.g. `C:\Tools\HyperKeyLiberator\HyperKeyLiberator.exe`)
3. Open an elevated terminal and install the service via NSSM:
   ```
   nssm install HyperKeyLiberator "C:\Tools\HyperKeyLiberator\HyperKeyLiberator.exe"
   ```
4. In the NSSM GUI that opens, go to the **Log on** tab and set it to log on as your user account (not Local System) — this is required for hotkey registration to work in your interactive session
5. Set the startup type to **Automatic** and start the service:
   ```
   sc start HyperKeyLiberator
   ```
6. Log out and back in to verify the shortcuts no longer trigger their default behaviors

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