# ScreenShift

A lightweight Windows monitor profile manager. Save display layouts and switch between them in one
click, without opening Settings.

Built to the spec in [AGENTS.md](AGENTS.md). **All six phases are complete**: monitors are detected
and shown, everything about them can be changed (on/off, resolution, refresh rate, orientation,
position, primary), named profiles capture whole layouts and bring them back with one click —
protected by a 15-second automatic revert — and profiles are reachable from a system tray menu or
a global hotkey without the window ever being open.

## Requirements

- Windows 10 1809 or later
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build; the .NET 8 Desktop
  Runtime to run

## Layout

```
ScreenShift.sln
├── src/ScreenShift/            The WPF application
│   ├── Models/                 MonitorInfo and the value types it is built from
│   ├── Native/                 P/Invoke: structs, declarations, and the safe wrapper
│   ├── Services/               DisplayService, logging, paths
│   ├── ViewModels/             MVVM layer
│   ├── Views/                  MainWindow and converters
│   └── Themes/                 Dark theme resource dictionary
└── tools/ScreenShift.Probe/    Console harness for checking detection against real hardware
```

The rule the native layer follows: `Native/` speaks Win32 and nothing above it does. `DisplayService`
is the boundary — it takes native paths and modes in, and hands clean models out.

## Build and run

```bash
dotnet build ScreenShift.sln
```

```bash
dotnet run --project src/ScreenShift/ScreenShift.csproj
```

## How changes are applied and saved

Applying a display mode and *saving* it are separate operations, and on Windows 11 they belong to
different APIs. Treating them as one thing is what made early versions of this lose changes.

**Applying** goes through GDI (`ChangeDisplaySettingsEx`), because that is the API that can apply a
driver-enumerated mode by value. It tries three strategies in order, so a change still takes effect
when a stricter one is refused:

1. Stage every display, then commit them together — one clean switch.
2. Apply each display on its own, persisting through the legacy registry path.
3. Apply each display dynamically, without the registry write.

The display designated primary is always written first, because Windows requires a primary anchored
at the desktop origin and moving anything else before that anchor exists leaves the configuration
momentarily without one.

**Switching monitors on and off** is a different operation again, and goes through the CCD path
table. A monitor being on is literally a flag on the path joining a desktop surface to a connector,
so this is setting or clearing that flag and letting Windows work out the rest. Two things about it
are easy to get wrong:

- A switched-off monitor keeps `targetAvailable` set but has `DISPLAYCONFIG_TARGET_IS_CONNECTED`
  cleared. Filtering the monitor list on "connected" makes disabled monitors disappear entirely —
  and a monitor you cannot see is one you cannot switch back on. `targetAvailable` is what
  distinguishes a real monitor from an empty connector.
- Each target has a path for every source on the adapter. Switching one on via a source that is
  already driving another display does not extend the desktop, it *clones* the two monitors onto one
  surface. Picking a free source is the difference between "turn this on" and "mirror that onto it".

**Saving** goes through the CCD API (`SetDisplayConfig` with `SDC_SAVE_TO_DATABASE`). Windows 11
keeps display configuration in the CCD database, not the registry keys GDI writes to — on the
machine this was developed against, `EnumDisplaySettingsEx(ENUM_REGISTRY_SETTINGS)` fails for every
display, meaning there is no legacy configuration stored at all. So after every successful apply
*and* every successful revert, the resulting configuration is re-applied through CCD with
`SDC_SAVE_TO_DATABASE` to write it where Windows will actually read it.

A failed save never fails the apply. A change that took effect but will not survive a reboot is
still better than no change, and it is logged as such.

One thing worth knowing: when the CCD database is out of sync with what is on screen, the GDI path
starts refusing writes — including reverts. That is what the layered fallback protects against, and
keeping the database current after every change is what stops the situation arising.

## Checking detection

The probe prints everything ScreenShift knows about each monitor. It is the fastest way to see
whether a hardware change is being picked up correctly — run it, replug or rotate something, run it
again.

```bash
dotnet run --project tools/ScreenShift.Probe/ScreenShift.Probe.csproj
```

It can also render the main window to a PNG, which is useful for checking the UI without a person
having to look at it:

```bash
dotnet run --project tools/ScreenShift.Probe/ScreenShift.Probe.csproj -- --capture window.png
```

## Profiles

Profiles live in `%APPDATA%\ScreenShift\profiles.json` — human-readable, one entry per monitor,
saved atomically (write-then-move) so a crash can never leave the file half-written. A corrupt file
is set aside under a `.corrupt-<timestamp>` name rather than silently discarded.

A profile stores desired state, not commands: applying one diffs it against what is on screen and
requests only the differences, so applying a profile twice is a no-op, and "nothing to change"
doubles as "this profile is active".

Saved monitors are matched back to hardware in three passes, strictest first:

1. **Device path** — monitor plus port. Survives reboots and keeps identical monitors apart.
2. **Adapter path + connector id** — covers a device path that changed shape.
3. **EDID model** — the monitor moved ports. Only taken when exactly one candidate and one saved
   entry want each other; with two identical monitors both moved, any pairing would be a guess, and
   guessing would hand the portrait settings to the landscape monitor. Ambiguity is skipped and
   reported instead.

A monitor that is connected but not in the profile is left untouched. A saved monitor that is not
connected is skipped with a warning. Applying is protected by the same 15-second revert as manual
changes — and the revert prompt defaults to *revert*, so a user who cannot see anything gets their
old configuration back by doing nothing.

## Tray and hotkeys

The tray icon (P/Invoked `Shell_NotifyIcon` — no packages, no WinForms) gives every profile a
one-click apply from the taskbar: right-click for the menu, left-click to open the window. It
re-adds itself when Explorer restarts, and its icon is drawn in code by the same routine that
generates the executable's .ico, so the two cannot drift apart.

Global hotkeys go through `RegisterHotKey`, the sanctioned API, not a keyboard hook. Each profile
can hold one gesture (set from its card; press the combination rather than typing it), stored in
profiles.json as plain text like `Ctrl+Alt+1`. A gesture must include Ctrl, Alt or Win — Shift-only
would swallow ordinary typing — and a gesture already owned by another program costs that one
hotkey, never the app. Assigning a gesture that another profile holds moves it.

Closing the window hides to the tray by default, because hotkeys only work while the process is
alive; Exit in the tray menu really exits. Both behaviours are settings, stored in
`%APPDATA%\ScreenShift\settings.json` and toggleable in the status bar.

**Start with Windows** (also in the status bar) registers a per-user Run entry pointing at the
current executable with `--minimized`, so sign-in brings up the tray icon and hotkeys without
opening the window. The registry entry is the single source of truth — Task Manager's Startup page
manages the same one — so there is no copy in settings.json to drift out of step. `--minimized` is
ignored when the tray icon is disabled, since an invisible app with no icon would be unreachable.

## Testing display changes

These write to the live display configuration. `--test-apply` always targets a non-primary display
and always restores afterwards, then verifies that what came back matches what was there before.

```bash
dotnet run --project tools/ScreenShift.Probe/ScreenShift.Probe.csproj -- --test-apply refresh
```

`--set` applies a change and leaves it applied, which is also the way to repair a configuration by
hand:

```bash
dotnet run --project tools/ScreenShift.Probe/ScreenShift.Probe.csproj -- --set 1 --refresh 180 --primary
```

`--persist` writes whatever is on screen to the display database, which is the repair for a
configuration that applied but did not save:

```bash
dotnet run --project tools/ScreenShift.Probe/ScreenShift.Probe.csproj -- --persist
```

Profiles have their own commands, sharing the app's profiles.json. `--profile-apply` verifies
itself: after applying it re-plans the profile and reports whether the live state matches.

```bash
dotnet run --project tools/ScreenShift.Probe/ScreenShift.Probe.csproj -- --profile-save Baseline
```

```bash
dotnet run --project tools/ScreenShift.Probe/ScreenShift.Probe.csproj -- --profile-apply Baseline
```

`--test-dialog` shows the real keep/revert prompt and passes only if it times out into "revert" —
the path that rescues a user who cannot see anything:

```bash
dotnet run --project tools/ScreenShift.Probe/ScreenShift.Probe.csproj -- --test-dialog 4
```

`--test-hotkey` registers Ctrl+Alt+F9 through the real hotkey service, injects the combination,
and requires the WM_HOTKEY round trip to fire. `--test-tray` adds and removes a real tray icon.
`--profile-hotkey <name> <gesture|clear>` assigns hotkeys from the command line, and the probe's
make-icon command regenerates `Assets/app.ico` from the in-code icon drawing.

## Logs

Everything, including every display API failure with the adapter and target ids that caused it,
goes to:

```
%APPDATA%\ScreenShift\logs\screenshift-<date>.log
```

Logs older than seven days are deleted at startup.

## Status

| Phase | Scope | State |
| --- | --- | --- |
| 1 | Detect monitors, show them in the UI | Done |
| 2 | Change resolution, refresh rate, primary | Done |
| 3 | Enable/disable monitors, topology switching | Done |
| 4 | Save and load profiles | Done |
| 5 | One-click apply with rollback | Done |
| 6 | Tray icon, global hotkeys, polish | Done |
