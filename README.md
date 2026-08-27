# AlienRgb

A free, lightweight replacement for the **lighting controls** in Alienware Command
Center — a Windows tray app + CLI that talks directly to the AlienFX USB HID controller
in an Alienware x14 to set keyboard/logo/power-button colors, save profiles, and keep
the lights working correctly across sleep and reboots.

It exists because Alienware Command Center is heavy, cloud-account-gated, and sometimes
just doesn't reliably restore lighting after sleep. This talks to the hardware directly:
no AWCC, no Dell account, no telemetry.

## What it does

- **Set colors per zone** (keyboard, alien-head logo, power button) from a GUI with
  sliders/hex input, or from the command line.
- **Save and switch named profiles**, and restore the last live state automatically.
- **Runs minimized in the system tray**, out of your way, and can start automatically
  at login for whichever Windows account installs it.
- **Survives sleep**: on wake, a Scheduled Task independent of the app process restores
  whatever colors were showing before the machine slept — see [Sleep/wake
  reliability](#sleepwake-reliability) for why this needed more than the obvious approach.

## Is my hardware supported?

Confirmed working on an **Alienware x14** with its onboard "AW-ELC" lighting controller
(USB HID, vendor ID `0x187C`, using the AlienFX **APIv4** protocol — 34-byte HID output
reports). Device detection matches on vendor ID + HID report length, not a specific
product ID, so:

- **Any x14 with the same AW-ELC controller should work out of the box** — the discovered
  light IDs (alien head, keyboard, power button) are assigned by firmware, so they should
  be identical across units of the same laptop, not something that varies per physical
  machine.
- **Other Alienware/Dell G-series hardware using the same APIv4 protocol generation might
  also work**, but the zone IDs almost certainly differ from the x14's, and it hasn't been
  tested on anything else. See [Remapping zones](#remapping-zones-if-yours-differ) below —
  it's a five-minute fix if the defaults don't match your unit.
- Newer/older Alienware lighting controllers (APIv5/v6/v7/v8 — most external RGB
  keyboards, mice, and monitors) use a different report format and are **not** supported
  by this code as written. `AlienRgb.Cli.exe list` will tell you plainly if your device
  doesn't match (wrong HID report length).

If you try this on different x14-family hardware, an issue or PR with what worked (or
didn't) is welcome.

## Quick start

1. Make sure **Alienware Command Center is closed** (see [Important caveats](#important-caveats)).
2. Grab the latest zip from [Releases](../../releases) and extract it — no .NET
   install needed, it's self-contained. (No release yet, or want to build from source
   instead? See [Build](#build).)
3. Run `AlienRgb.App.exe`. First launch it opens visibly so you can set colors; after
   that, closing to the tray (minimize) keeps it running quietly.
4. If your lights don't match the default zone names, jump to
   [Remapping zones](#remapping-zones-if-yours-differ).

## Usage

GUI — sliders per zone, live apply, named profiles, minimize-to-tray, a "start with
Windows" checkbox:

```bash
AlienRgb.App.exe
AlienRgb.App.exe --minimized   # start hidden in the tray (used by autostart)
```

CLI:

```bash
AlienRgb.Cli.exe list                         # show the device and known zones
AlienRgb.Cli.exe set all FF0000                # every zone to red
AlienRgb.Cli.exe multi 2=FF0000 1=00FF00 5=0000FF   # different color per zone, one batch
AlienRgb.Cli.exe flash 2                       # blink light ID 2 so you can identify it
AlienRgb.Cli.exe apply MyProfile               # apply a saved, named profile
AlienRgb.Cli.exe apply                         # restore whatever was last showing (this is what runs on wake)
```

All state — saved profiles, the zone map, and a diagnostic log — lives in a `data\`
folder next to the executables, not in `%APPDATA%`. See
[Why data lives next to the exe](#why-data-lives-next-to-the-exe-not-appdata) if you're
curious why.

## Remapping zones (if yours differ)

The app ships with the mapping discovered on one x14 (alien head, keyboard, power
button). If your unit's lights don't match those names:

1. Run `AlienRgb.Cli.exe flash 0`, then `flash 1`, `flash 2`, ... up through `flash 8`,
   watching the machine each time to see which physical light blinks (or stays dark, if
   that ID isn't used on your unit).
2. Create `data\zones.json` next to the exe with your findings:
   ```json
   [
     { "Id": 1, "Name": "Alien Head" },
     { "Id": 2, "Name": "Keyboard" },
     { "Id": 5, "Name": "Power Button" }
   ]
   ```
3. Restart the app (GUI and CLI both read this file automatically if it's present, and
   fall back to the built-in defaults if it's missing).

## Startup and multi-account installs

- **Self-installing, per Windows account**: the first time a given copy of the app runs,
  it registers itself for the *current* account only — an HKCU "start minimized" entry,
  plus a per-account Scheduled Task (named `AlienRgbWakeRestore-<username>`) that restores
  lighting on wake. No admin rights or UAC prompt needed for any of this. The "Start with
  Windows (minimized)" checkbox in the app toggles both later. Launching the app again
  after that just re-confirms the registration (so moving the folder self-heals the
  paths) and is otherwise a no-op.
- **Multiple people using the same laptop, different Windows accounts**: give each
  account its own copy of the app in its own profile folder (e.g. copy the built output
  into `C:\Users\<their-account>\AlienRgb\`, then have them double-click the exe once
  while logged in as themselves). This is deliberate, not a limitation to work around —
  see below for why a single shared, all-users install doesn't work here.
  - *Why per-account and not all-users:* an all-users (`HKLM`) autostart entry pointing
    at one account's copy fails for every other account, because NTFS normally doesn't
    let one standard user read into another user's profile folder. And a Scheduled Task
    only executes for the account it was created under — a task registered while
    account A is logged in does not fire for account B's session. There's no single
    all-users mechanism that solves both problems at once as simply as "each account
    gets its own copy, which registers its own pair of entries."
  - The lighting hardware itself is shared physical state, not per-account: whichever
    account's copy last applied a color is what everyone sees on screen, regardless of
    who's currently logged in.

## Important caveats

- **Don't run this alongside Alienware Command Center.** Both talk to the same
  controller; AWCC will fight this app for control and can overwrite what you set (or
  vice versa). Close AWCC (and ideally disable its lighting service/autostart) before
  using this.
- This is an **unofficial, reverse-engineered** tool. It is not affiliated with, endorsed
  by, or supported by Dell or Alienware. It talks to the hardware using an undocumented
  USB HID protocol figured out by community reverse-engineering (see
  [Credits](#credits)) — use it at your own risk. It only sends color/lighting commands;
  it doesn't touch fan curves, overclocking, or anything safety-critical, but as with any
  tool that bypasses vendor software, there are no guarantees.
- Tested only on Windows 10/11 x64, on one Alienware x14. Your mileage on other
  configurations may vary — see [Is my hardware supported?](#is-my-hardware-supported).

## Build

Only needed if you're not using a [Releases](../../releases) zip — e.g. you're
contributing, or want to build from source for another architecture. Requires the .NET 8
SDK.

Publish **self-contained** (embeds the runtime, so it never depends on `PATH` or
`DOTNET_ROOT` being configured correctly wherever it's launched from — Scheduled Tasks
and autostart entries in particular don't reliably inherit a user's shell environment,
and a framework-dependent build silently fails to launch in that case):

```bash
dotnet publish AlienRgb.App -c Release -r win-x64 --self-contained true -o publish
dotnet publish AlienRgb.Cli -c Release -r win-x64 --self-contained true -o publish
```

Don't add `-p:PublishSingleFile=true` — the single-file self-extraction step was found to
behave inconsistently under a Scheduled Task's restricted run level during development. A
plain self-contained output folder is what's actually been verified working end to end.

The output folder (`publish\`) is what you copy/zip to distribute or install for another
account — everything needed is in there, plus a `data\` folder that's created
automatically on first run.

**Cutting a release:** push a tag matching `v*.*.*` (e.g. `git tag v1.0.0 && git push
origin v1.0.0`) and [`.github/workflows/release.yml`](.github/workflows/release.yml) runs
the exact commands above on a clean Windows runner and attaches the resulting zip to a new
GitHub Release automatically. Use the workflow's manual "Run workflow" button to sanity-
check a build without cutting a release — it uploads the same zip as a plain build
artifact instead of publishing it.

## Sleep/wake reliability

This turned out to be the hardest part of the project, and the reasoning is worth
recording for anyone extending this app (or debugging why lights stop restoring after an
update).

**The obvious approach doesn't work on modern laptops.** Many current Alienware/Dell
laptops (including the x14) only support **Modern Standby** (S0 low-power idle) — there's
no classic S3 sleep state at all. Windows frequently **freezes ordinary application
processes solid** during Modern Standby, so the standard in-process resume signals
(`Microsoft.Win32.SystemEvents.PowerModeChanged`, or handling `WM_POWERBROADCAST`
directly) often never fire — the app's own process simply isn't executing at the moment
the system suspends or resumes, so it can't react to anything. This app still registers
both handlers, as a harmless fallback for machines with classic S3 sleep, but don't rely
on them alone if you're modifying this for a similar laptop.

**What's actually reliable** is that the OS kernel itself logs every wake transition to
the Windows Event Log, regardless of whether any given app process was frozen —
specifically `Microsoft-Windows-Kernel-Power`, Event ID 507 ("The system is exiting
Modern Standby"). This app creates a Windows Scheduled Task with an event-log trigger
bound directly to that event ID, which runs `AlienRgb.Cli.exe apply` (no profile name =
restore whatever was last showing) every time the machine wakes. Because it's a fresh
process launched by the Scheduled Task service — not a resumed, previously-frozen one —
it always gets to run, and it retries opening the HID device with backoff since the
embedded controller can take a few seconds to re-enumerate after resume.

### Why data lives next to the exe, not `%APPDATA%`

While building the above, saved profile data wasn't visible to the independently-launched
Scheduled Task process, even though it was clearly present and readable from an ordinary
interactive process, with unremarkable file permissions and no hidden/system attribute
set. Whatever the exact mechanism, the practical fix was to stop using `%APPDATA%`
entirely: all of this app's state (`profiles.json`, `zones.json`, `resume.log`) lives in a
plain `data\` folder next to the executables instead, which has been confirmed to behave
identically from both an interactive launch and a Scheduled Task. If you're adapting this
app and need state visible to a background/scheduled process, keeping it next to the exe
is the safer default — don't assume `%APPDATA%` is a Scheduled-Task-safe location without
testing it on your own machine first.

`data\resume.log` records every device open and apply attempt (tagged with what
triggered it), which is the first place to look if lighting-on-wake ever stops working.

## Credits

The AlienFX USB HID protocol (`AlienRgb.Core/AlienFxDevice.cs`) was reverse-engineered
and documented by the [alienfx-tools](https://github.com/T-Troll/alienfx-tools) project
(AlienFX-SDK, MIT licensed) — this app's device layer is a direct C# port of that work.
Enormous thanks to T-Troll and contributors for doing the hard reverse-engineering.

## License

MIT — see [LICENSE](LICENSE).
