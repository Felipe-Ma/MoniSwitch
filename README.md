# ScreenShift

A lightweight Windows monitor profile manager. Save display layouts and switch between them in one
click, without opening Settings.

Built to the spec in [AGENTS.md](AGENTS.md). **Phase 1 (detection) is complete. Phase 2 (changing
resolution, refresh rate and primary display) works, with one caveat — see Persistence below.**

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

## Persistence: a known limitation

Applying a mode and *saving* it are separate operations, and on Windows 11 they use different APIs.

ScreenShift currently changes modes through the GDI path (`ChangeDisplaySettingsEx`), which persists
by passing `CDS_UPDATEREGISTRY`. On the machine this was developed against that flag is refused —
and `EnumDisplaySettingsEx(ENUM_REGISTRY_SETTINGS)` fails for every display, which says Windows has
no legacy GDI configuration saved at all. Modern Windows persists display configuration through the
**CCD display database** (`SetDisplayConfig` with `SDC_SAVE_TO_DATABASE`) instead.

So changes apply correctly and are visible immediately, but may not survive a reboot. The apply path
falls back through three strategies — staged batch, then per display, then dynamic without the
registry write — so a change always takes effect even when it cannot be saved, and the rollback path
uses the same chain.

Moving the apply path onto `SetDisplayConfig` is the next piece of work, and it is a prerequisite for
Phase 3 (enabling and disabling monitors) regardless, since topology changes need the CCD API anyway.

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
| 2 | Change resolution, refresh rate, primary | Works; does not reliably persist |
| 3 | Enable/disable monitors, topology switching | Not started |
| 4 | Save and load profiles | Not started |
| 5 | One-click apply with rollback | Not started |
| 6 | Tray icon, global hotkeys, polish | Not started |
