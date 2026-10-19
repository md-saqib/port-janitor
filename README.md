# PortJanitor

[![CI](https://github.com/md-saqib/port-janitor/actions/workflows/ci.yml/badge.svg)](https://github.com/md-saqib/port-janitor/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/md-saqib/port-janitor)](https://github.com/md-saqib/port-janitor/releases/latest)

**Find the dev servers you forgot to stop, and kill them.**

PortJanitor is a tiny Windows app that shows every listening port, the process behind it, and its full command line. One click stops it. No more `netstat -ano | findstr :3000` followed by `taskkill /PID 12345 /F`.

- Install it with a one-click setup that adds desktop and Start menu shortcuts, or run the single 90 KB portable `.exe`.
- No runtime to download: it uses the .NET Framework 4.8 built into Windows 10 and 11.
- No network access, no telemetry, no admin rights needed to install or start.

## Why

You close a terminal, but `next dev` keeps holding port 3000. A crashed watcher leaves an orphaned `node.exe` on 5173. An old `uvicorn` is still on 8000. The next `npm run dev` fails with `EADDRINUSE`, and Task Manager shows six identical `node.exe` processes with no hint of which project each one belongs to.

PortJanitor shows the port, PID, process and command line side by side, so you can tell `C:\work\shop\node_modules\next\dist\bin\next dev` apart from `C:\work\blog\node_modules\vite\bin\vite.js`.

## Features

- **Ports tab**: every listening TCP port (IPv4 and IPv6) with its PID, process name and command line. Optionally includes UDP sockets and established connections.
- **Processes tab**: every process with its parent, the ports it listens on, memory, start time and command line.
- **Kill** the selected processes, or **Kill tree** to also kill every child process they started (for example `npm` → `next` → the server worker).
- **Filter** by port, name, PID or any part of the command line. Every space-separated word must match, so `node 3000` works.
- **Hide Windows processes** (on by default) so your own servers stand out from `svchost.exe` and friends.
- **Auto-refresh** every 3 seconds while keeping your sort order, selection and scroll position.
- **Safe by default**:
  - Every kill asks for confirmation and lists exactly what will be stopped.
  - Critical Windows processes and PortJanitor itself are refused.
  - It checks that a PID hasn't been reused by Windows for another process before killing it.
- **Restart as admin** when you need to inspect or kill elevated or service processes.

## Download

Both files are on the [latest release](https://github.com/md-saqib/port-janitor/releases/latest) page.

### Install (recommended)

Download and run **`PortJanitor-Setup.exe`**. It:

- installs PortJanitor for your account in `%LOCALAPPDATA%\Programs\PortJanitor`, with no admin rights needed
- adds a **PortJanitor** shortcut to your desktop and Start menu
- adds PortJanitor to **Settings → Apps → Installed apps**, where you can uninstall it

To update, run the newer setup; it replaces the installed version. To pin PortJanitor to the taskbar or Start, right-click the Start menu entry and choose **Pin**. Windows doesn't let apps pin themselves.

For scripted installs, `PortJanitor-Setup.exe /S` installs silently. `"%LOCALAPPDATA%\Programs\PortJanitor\Uninstall.exe" /uninstall /S` uninstalls silently.

### Portable

Download **`PortJanitor.exe`**, put it anywhere (for example `C:\Tools`) and run it. Nothing is installed.

> **Windows SmartScreen** may say "Windows protected your PC" because the exe isn't code-signed. Click **More info → Run anyway**. Each release lists the SHA-256 of both files so you can verify them with `Get-FileHash .\PortJanitor-Setup.exe`. Or [build it yourself](#build-from-source); it takes a few seconds.

## Usage

| Action | How |
| --- | --- |
| Refresh now | **F5** or **Refresh** |
| Focus the filter | **Ctrl+F** (Esc clears it) |
| Kill selected processes | **Del**, the **Kill** button, or right-click → Kill |
| Kill selected processes and their children | **Shift+Del**, **Kill tree**, or right-click → Kill process tree |
| Jump from a port to its process | Double-click the row, or right-click → Go to process |
| Find who started a process | Processes tab → right-click → Go to parent process |
| Copy PID or command line, open file location | Right-click menu |

Select several rows with Ctrl or Shift to kill several processes at once.

### Kill vs. Kill tree

**Kill** force-terminates the selected processes, like `taskkill /F`. The process gets no chance to clean up.

**Kill tree** also terminates every descendant. Parents are killed before their children, so a supervisor like `nodemon` or `next dev` can't respawn the worker you just killed. To stop a whole dev server, jump to the listener's process, use **Go to parent process** until you reach the `npm`/`pnpm`/`next` process, then **Kill tree**.

### Not running as admin?

Without elevation, PortJanitor still sees every port and PID. It can't read the command line of elevated processes or processes owned by other users, and it can't kill them. Click **Restart as admin** for those.

## How it works

| What | Where it comes from |
| --- | --- |
| Ports and owning PIDs | `GetExtendedTcpTable` / `GetExtendedUdpTable` from `iphlpapi.dll`, the same source as `netstat -ano` |
| Command line, parent PID, start time | WMI `Win32_Process` |
| Executable path | `QueryFullProcessImageName` |
| Killing | `Process.Kill()` (`TerminateProcess`), after checking the process start time still matches |

A process counts as a "Windows process" if it runs from `%SystemRoot%` or its image path can't be read.

## Build from source

Requirements: Windows 10 or 11. No SDK or Visual Studio needed: the build uses the C# compiler that ships with .NET Framework 4.8.

```bat
git clone https://github.com/md-saqib/port-janitor.git
cd port-janitor
build.cmd            :: builds dist\PortJanitor.exe and dist\PortJanitor-Setup.exe
build.cmd -Test      :: builds and runs the smoke tests
```

From PowerShell you can also run `.\build.ps1 -Version 1.2.3 -Test`.

### Project layout

```
src/PortJanitor.cs       the whole app (port/process collection, kill logic, UI)
src/Setup.cs             the installer and uninstaller; the app exe is embedded in it
src/app.manifest         DPI awareness and "run as invoker"
tests/SmokeTest.cs       opens real sockets and child processes and checks they're found and killed
tests/install-test.ps1   installs silently, checks files, shortcuts and the Apps entry, then uninstalls
tools/make-icon.ps1      regenerates assets/PortJanitor.ico
build.ps1 / build.cmd    build script used locally and in CI
```

## Releasing

1. Add a section for the new version to [CHANGELOG.md](CHANGELOG.md).
2. Tag and push: `git tag v1.1.0` then `git push origin v1.1.0`.

GitHub Actions builds the tag, runs the smoke and install tests, then publishes a release. The release contains `PortJanitor-Setup.exe`, `PortJanitor.exe`, a `.sha256` file for each, and the changelog section as release notes. Tags with a hyphen (`v1.1.0-beta.1`) are published as pre-releases.

## Contributing

Bug reports, ideas and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
