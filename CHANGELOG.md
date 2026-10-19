# Changelog

All notable changes to PortJanitor are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-10-19

### Added

- Ports tab listing listening TCP ports (IPv4 and IPv6) with PID, process name and full command line, plus optional UDP sockets and established connections.
- Processes tab with parent process, listening ports, memory, start time and command line.
- Kill and Kill tree (parent first, then all descendants), with a confirmation that lists every process to be stopped.
- Protection against killing critical Windows processes, PortJanitor itself, or a PID that Windows has reused.
- Filter across port, name, PID and command line. Hide Windows processes, listening-only and UDP toggles.
- Auto-refresh every 3 seconds that keeps sort order, selection and scroll position.
- Context menu: go to process, go to parent process, copy PID, copy command line, open file location.
- Keyboard shortcuts: F5, Ctrl+F, Esc, Del, Shift+Del.
- Restart as admin for elevated and service processes.
- `PortJanitor-Setup.exe` installs per user without admin rights, adds desktop and Start menu shortcuts, and registers an uninstaller in Settings → Apps. Supports `/S` for silent install and uninstall. The portable `PortJanitor.exe` is still published.

[Unreleased]: https://github.com/md-saqib/port-janitor/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/md-saqib/port-janitor/releases/tag/v1.0.0
