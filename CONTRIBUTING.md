# Contributing to PortJanitor

Thanks for helping. PortJanitor aims to stay a small, dependency-free Windows tool that any developer can download and trust.

## Build and test

```bat
git clone https://github.com/md-saqib/port-janitor.git
cd port-janitor
build.cmd -Test
```

This compiles `dist\PortJanitor.exe` and `dist\PortJanitor-Setup.exe`, then builds and runs `tests\SmokeTest.cs`. The smoke test opens real TCP/UDP sockets and starts child processes to check they are found, linked to their parents, and killed.

If you change `src\Setup.cs`, also run `powershell -File tests\install-test.ps1`. It really installs PortJanitor into your profile, checks the files, shortcuts and Apps entry, then uninstalls it. It refuses to run if PortJanitor is already installed.

CI runs both on every push and pull request.

## Ground rules

- **C# 5 only.** The build uses the compiler that ships inside Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), so nobody needs an SDK. That compiler stops at C# 5, so these won't compile:
  - string interpolation (`$"..."`)
  - `?.`
  - `nameof`
  - `=>` expression-bodied members
  - `out var`
  - tuples
  - auto-property initializers
- **No NuGet packages or extra DLLs.** Each release file is a single `.exe`. Only use assemblies that ship with .NET Framework 4.8.
- **Keep the source ASCII.** Write `·` instead of pasting `·`. The in-box compiler can misread non-ASCII characters in files without a BOM.
- **Short comments only**, and only where the code isn't self-explanatory.
- **Safety first.** Anything that kills processes must keep the confirmation dialog, the critical-process blocklist, and the PID start-time check.

## Testing by hand

1. Start something that listens, for example `python -m http.server 4000` or `npx http-server -p 4000`.
2. Find it in PortJanitor (type `4000` in the filter).
3. Kill it and check the port disappears.
4. Start a dev server through npm (`npm run dev`). Use **Go to process** and **Go to parent process** to reach the `npm` process, then **Kill tree** and confirm everything stops.
5. Run `dist\PortJanitor-Setup.exe`, start the app from the desktop shortcut, then uninstall it from **Settings → Apps** and check the shortcuts and `%LOCALAPPDATA%\Programs\PortJanitor` are gone.

## Pull requests

- One focused change per PR, with a short description of what and why.
- Run `build.cmd -Test` before pushing.
- Add a line under `## [Unreleased]` in `CHANGELOG.md` for anything user-visible.

## Ideas that would make good contributions

- Graceful stop (send Ctrl+C before force-killing)
- System tray mode with a quick "kill port" menu
- Dark mode
- Remembering window size and filter settings
- Flagging well-known dev ports (3000, 5173, 8000, 8080...) in the list
