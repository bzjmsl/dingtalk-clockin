# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A single-file Windows desktop app (WPF, .NET Framework 4.8) that drives an Android phone over ADB to clock in on DingTalk. It wakes and unlocks the phone, opens DingTalk, and either lets 极速打卡 fire or taps the punch button itself. `dingtalk-clockin.ps1` is the older scheduled-task version kept as a fallback.

## Build / test / run

```powershell
powershell -ExecutionPolicy Bypass -File app\build.ps1        # -> app\dist\DingTalkClockIn.exe
powershell -ExecutionPolicy Bypass -File app\test.ps1         # logic tests, prints PASS=n FAIL=m, exits 1 on failure
app\dist\DingTalkClockIn.exe --selftest                       # UI element contract -> app\dist\data\selftest.txt
app\dist\DingTalkClockIn.exe --render out.png settings        # render a page: overview|settings|holiday|logs
```

There is no MSBuild, NuGet, or Visual Studio involvement — both scripts invoke `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`. There is no linter.

`app\test.ps1` compiles `src\Core.cs`, `src\Adb.cs`, `src\Services.cs` plus `tests\Test.cs` into a console exe. Add assertions there; there is no test framework.

## Hard constraints (these bite constantly)

- **C# 5 only.** csc 4.8 rejects string interpolation, `?.`, expression-bodied members, `out var`, tuples.
- **XAML is loaded at runtime** from an embedded resource via `XamlReader.Load`. No `x:Class`, no compile-time event handlers — every handler is attached in `MainWindow.Wire()` using `FindName`.
- **Element registry:** `MainWindow.SelfTest()` contains the array of every element name referenced from code. Add new `x:Name`s there or `--selftest` count drops.
- **UTF-8 BOM** is required for any file containing Chinese text; csc and PowerShell 5.1 mangle it otherwise. After editing a `.cs`/`.xaml`/`.ps1` with Chinese content, re-write it with `(New-Object Text.UTF8Encoding($true))`.
- **Single instance:** a named `Mutex` makes a second launch exit silently while the first keeps the lock. Never start the exe from a script to "check it works" — the window may be invisible and will block the user's own launch.

## Architecture

`Program.cs` (`AppHost : Application`) is the composition root: it builds `AppPaths`, `Config`, `Logger`, `HolidayService`, `Adb`, `PunchEngine`, `AppState`, `Scheduler`, `PowerMonitor`, then hands the host to `MainWindow`. It also owns the tray icon, theme swap, and the `--selftest` / `--render` entry points.

- `Core.cs` — `AppPaths` (all data directories), `Config` (dot-path JSON with `S()/I()/B()`, PIN stored via DPAPI), `Logger` (append-only daily files plus an `ObservableCollection` bound to the UI), `AppState` (per-day run marks, last result).
- `Services.cs` — `HolidayService` (manual entries → downloaded cache → weekday fallback), `Scheduler` (a deterministic per-day random instant inside each window, derived from an FNV hash so it survives restarts), `PowerMonitor` (samples battery every N seconds, detects disconnects and reboots via `/proc/uptime`, dumps `last_kmsg` after a reboot).
- `Adb.cs` — thin Process wrapper. `ResolveDefault` searches app-local `platform-tools`, `%LOCALAPPDATA%\Android\Sdk`, `%ProgramFiles%\Android`, `C:\platform-tools`, then PATH. `DeviceState` distinguishes `device` / `unauthorized` / `offline` / `none` — `unauthorized` needs a tap on the phone, not a replug.
- `PunchEngine.cs` — the run itself: `EnsureDevice` → `EnsureAwake` (KEYCODE_POWER 26; 224 does nothing on this device) → `EnsureUnlocked` (keypad taps) → `Preflight` (no network aborts; location off only warns) → wake mode (open DingTalk, hold the screen) or full mode (find entry, screenshot, classify the button colour: blue = 内勤, green = 外勤, tap coordinates) → `FinishSession` (restore `stay_on_while_plugged_in`, lock the screen).
- `MainWindow.cs` — all UI wiring plus the 1-second `DispatcherTimer` that drives both the scheduler and the throttled power poll.

## Device-specific assumptions

- Punch-entry, button and keypad coordinates live under `advanced.*` in `config.json` and were measured on a vivo X6D at 1080×1920. Any other phone or resolution needs remeasuring.
- The punch page is an Ali mini-program webview: `uiautomator dump` cannot read its text, which is why detection is pixel colour plus fixed coordinates.
- 极速打卡 only fires while DingTalk is in the foreground, which is why wake mode opens DingTalk and holds the screen for `wakeHoldSec`.

## Data and secrets

Everything under `data/` is gitignored and must stay that way: it holds the DPAPI-encrypted lock-screen PIN, an ADB private-key backup, screenshots with personal information, and the `power/` monitoring output.

## Release

Pushing a `v*` tag triggers `.github/workflows/release.yml`: it builds on `windows-latest`, calls `build.ps1 -Version <tag without the v>`, and publishes `DingTalkClockIn-<tag>.exe` plus `SHA256SUMS.txt` to a GitHub Release. When `-Version` is omitted, `build.ps1` derives it from the latest git tag and injects it through a generated `dist\AssemblyInfo.g.cs` (csc 4.8 has no `/version` switch); the footer of the UI shows that assembly version.
