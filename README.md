# AlienRgb

Lightweight RGB lighting control for **this Alienware x14** — a minimal replacement for
Alienware Command Center's lighting features. C# / .NET 8, WPF GUI + CLI.

## Hardware (verified on this machine, Aug 2026)

- Controller: **AW-ELC** — USB HID, VID `0x187C`, PID `0x0550`
- Protocol: **AlienFX APIv4** (34-byte HID output reports via `HidD_SetOutputReport`)
- Lights discovered by color sweep:

| Light ID | Element |
|---|---|
| 1 | Alien head (lid logo) |
| 2 | Keyboard (single zone) |
| 5 | Power button |

APIv4 semantics: every apply is an atomic *action set* — reset (`0x03 0x21` type 4 then 1),
stage colors (`0x03 0x27`), commit with "finish and play" (`0x03 0x21` type 3). A new set
replaces the old one entirely, so all zones are always staged together.

## Usage

GUI (sliders per zone, live apply, profiles, minimize-to-tray, startup toggle):

```bash
./publish/AlienRgb.App.exe
./publish/AlienRgb.App.exe --minimized   # start hidden in the tray (used by autostart)
```

CLI:

```bash
./publish/AlienRgb.Cli.exe list
./publish/AlienRgb.Cli.exe set all FF0000
./publish/AlienRgb.Cli.exe multi 2=FF0000 1=00FF00 5=0000FF
./publish/AlienRgb.Cli.exe flash 2
./publish/AlienRgb.Cli.exe apply MyProfile   # apply a named profile
./publish/AlienRgb.Cli.exe apply             # restore whatever was last showing (used on wake)
```

Profiles, the zone map, and `resume.log` live in `publish\data\` (next to the exe) —
**deliberately not `%APPDATA%`**, see Diagnostics below.

## Startup and sleep/wake

- **Self-installing, per account**: on its first run (tracked by `data\firstrun.done`),
  the app registers itself for the *current* Windows account — an HKCU Run entry
  (`AlienRgb.App.exe --minimized`) plus a per-account wake-restore Scheduled Task named
  `AlienRgbWakeRestore-<username>` — then minimizes straight to the tray. No admin/UAC
  needed for any of it. The "Start with Windows (minimized)" checkbox toggles both. On
  later launches, if installed, it re-registers silently so paths self-heal if the folder
  is moved. A per-session mutex prevents double instances if something launches it twice.
- **Why per-account, not all-users**: this was learned the hard way. An HKLM all-users Run
  entry pointed into one user's profile folder, which other accounts can't read (NTFS), and
  the wake-restore Scheduled Task only runs for the account that's actually logged in — so
  a second account (astri) got neither startup nor lights-after-sleep. Each account now
  keeps its own copy of the app in its own profile, and each copy registers its own pair.
- **Restoring lights after sleep/wake**: this laptop only supports Modern Standby (S0 low
  power idle) — there is no S3 sleep. Regular Win32 processes are frequently frozen solid
  during Modern Standby and never see `WM_POWERBROADCAST` / `SystemEvents.PowerModeChanged`
  at all, so in-process resume handlers are unreliable here (the app still has one, as a
  cheap fallback for classic S3 machines). The mechanism that's actually reliable is the
  Scheduled Task, triggered directly off the kernel's own wake log entry
  (`Microsoft-Windows-Kernel-Power`, Event ID 507, "exiting Modern Standby") — this fires
  even if every app process was frozen, because it's the OS itself logging the transition.
  It runs `AlienRgb.Cli.exe apply` (no profile name = restore last live state), which
  retries opening the HID device with backoff since the controller can take a few seconds
  to re-enumerate after resume.
- **Installing for another account**: extract `AlienRgb-ForAstrid.zip` into that account's
  own profile (e.g. `C:\Users\astri\AlienRgb\`) while logged in as them, and double-click
  `AlienRgb.App.exe` once. See `README-INSTALL.txt` inside the zip.

## Build

Requires .NET 8 SDK (installed per-user in `%LOCALAPPDATA%\Microsoft\dotnet`, with
`DOTNET_ROOT` set accordingly).

Publish **self-contained** — a framework-dependent apphost depends on `PATH`/`DOTNET_ROOT`
being set, which isn't guaranteed for every launch context (Task Scheduler, HKLM autostart
on a fresh logon before profile env vars propagate, etc.) and caused silent launch failures
during development:

```bash
dotnet publish AlienRgb.App -c Release -r win-x64 --self-contained true -o publish
dotnet publish AlienRgb.Cli -c Release -r win-x64 --self-contained true -o publish
```

(Don't add `-p:PublishSingleFile=true` — the single-file self-extraction step behaved
inconsistently under the scheduled task's restricted run level. A plain self-contained
folder is simpler and was the configuration actually verified working.)

## Diagnostics

`publish\data\resume.log` records device opens and every apply attempt (GUI, CLI, and the
wake-triggered task), which is how the sleep/wake bug above got tracked down. It's not
under `%APPDATA%` because, in this project's dev environment, files an interactively
launched process wrote there were invisible to an independently-launched process (like
Task Scheduler) even with normal-looking ACLs and no hidden/system attribute — some kind of
per-launch-context virtualization. Storing data next to the exe sidesteps it entirely and
has been confirmed to work identically from both an interactive launch and the Scheduled
Task. If you ever move this app to a different machine, keep data next to the exe rather
than reverting to `%APPDATA%` unless you've confirmed that machine doesn't have the same
quirk.

## Notes

- Do not run Alienware Command Center at the same time — it reprograms the controller.
- Protocol ported from [alienfx-tools](https://github.com/T-Troll/alienfx-tools)
  (AlienFX-SDK, MIT License) by T-Troll and contributors. Thank you!
