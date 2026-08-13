# ScreenShift

A lightweight Windows monitor profile manager. Save display layouts and switch between them in one
click, without opening Settings.

Built to the spec in [AGENTS.md](AGENTS.md). **Phases 1 and 2 are complete**: monitors are detected
and shown, and resolution, refresh rate and primary display can be changed, with changes saved so
they survive a reboot.

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
| 3 | Enable/disable monitors, topology switching | Not started |
| 4 | Save and load profiles | Not started |
| 5 | One-click apply with rollback | Not started |
| 6 | Tray icon, global hotkeys, polish | Not started |
