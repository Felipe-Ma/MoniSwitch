# ScreenShift

A lightweight Windows monitor profile manager. Save display layouts and switch between them in one
click, without opening Settings.

Built to the spec in [AGENTS.md](AGENTS.md). **Phase 1 (detection) is complete**; the phases that
change display settings are not built yet.

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
| 2 | Change resolution, refresh rate, primary | Not started |
| 3 | Enable/disable monitors, topology switching | Not started |
| 4 | Save and load profiles | Not started |
| 5 | One-click apply with rollback | Not started |
| 6 | Tray icon, global hotkeys, polish | Not started |
