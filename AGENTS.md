# ScreenShift — Windows Monitor Profile Manager

I want you to build a Windows desktop application called **ScreenShift**.

ScreenShift is a lightweight monitor configuration/profile manager similar in concept to DisplayFusion's monitor profiles, but much simpler and focused specifically on quickly switching between predefined display layouts.

## Technology

Use:

* C#
* .NET 8
* WPF
* MVVM architecture where appropriate
* Windows native display APIs through P/Invoke
* JSON for storing user profiles/settings

Do not unnecessarily over-engineer the project.

The app should be lightweight, responsive, and easy to maintain.

## Core Concept

A user may have several monitors connected.

For example:

* Monitor 1 — 4K 144 Hz
* Monitor 2 — 1440p 165 Hz
* Monitor 3 — 1080p 60 Hz

The user should be able to create profiles such as:

### Gaming

* Monitor 1: Enabled
* Monitor 2: Disabled
* Monitor 3: Disabled
* Monitor 1 refresh rate: 144 Hz
* Monitor 1 set as primary

### Work

* Monitor 1: Enabled
* Monitor 2: Enabled
* Monitor 3: Enabled
* Monitor 1: 120 Hz
* Monitor 2: 165 Hz
* Monitor 3: 60 Hz

### TV Mode

* Monitor 1: Disabled
* Monitor 2: Disabled
* TV/Monitor 3: Enabled
* Monitor 3 set as primary
* 3840x2160 @ 120 Hz

The user can switch between these profiles with one click.

# Main Features

## 1. Monitor Detection

Detect all displays connected to Windows.

For every monitor show:

* Friendly monitor name
* Windows display name
* Monitor number
* Device ID / stable identifier if available
* Resolution
* Refresh rate
* Orientation
* Position
* Primary monitor status
* Enabled/disabled status

Try to use a stable hardware identifier so profiles continue working even if Windows changes something about monitor ordering.

## 2. Monitor Layout

Display the connected monitors visually in the application.

For example:

```text
+-------------------+    +-------------------+
|                   |    |                   |
|     Monitor 1     |    |     Monitor 2     |
|    3840x2160      |    |    2560x1440      |
|      144 Hz       |    |      165 Hz       |
|                   |    |                   |
+-------------------+    +-------------------+
```

The visual positioning should approximately correspond to the Windows monitor layout.

Eventually I may want drag-and-drop monitor positioning, so structure the UI in a way that won't make adding that feature difficult.

## 3. Monitor Configuration

For each monitor allow the user to configure:

* Enabled / Disabled
* Resolution
* Refresh rate
* Primary monitor
* Orientation

  * Landscape
  * Portrait
  * Landscape flipped
  * Portrait flipped

Only show resolutions and refresh rates actually supported by that display.

Prevent obviously invalid combinations.

## 4. Profiles

Users should be able to create named display profiles.

Each profile stores:

* Which monitors are enabled
* Which monitor is primary
* Monitor positions
* Resolution per monitor
* Refresh rate per monitor
* Orientation per monitor

Users should be able to:

* Create profile
* Save current configuration as profile
* Rename profile
* Update profile
* Duplicate profile
* Delete profile
* Apply profile

Profiles should persist between launches.

Store them somewhere appropriate such as:

```text
%APPDATA%\ScreenShift\
```

Use human-readable JSON.

## 5. Applying Profiles

This is the most important part of the application.

When the user clicks a profile:

1. Determine which physical monitors correspond to the saved monitors.
2. Enable/disable the required displays.
3. Apply monitor topology/layout.
4. Apply positions.
5. Apply resolutions.
6. Apply refresh rates.
7. Set the primary display.
8. Apply orientation.

Use the appropriate Windows APIs rather than simulating UI interaction.

Investigate and use APIs such as:

* QueryDisplayConfig
* SetDisplayConfig
* DisplayConfigGetDeviceInfo
* DisplayConfigSetDeviceInfo
* EnumDisplaySettings / EnumDisplaySettingsEx
* ChangeDisplaySettingsEx

Create a clean abstraction around these APIs rather than spreading P/Invoke calls throughout the application.

For example:

```text
Services/
    DisplayService.cs
    DisplayProfileService.cs
    WindowsDisplayApi.cs
```

`WindowsDisplayApi` should deal with low-level Windows structures/PInvoke.

`DisplayService` should expose clean C# models and operations to the rest of the application.

## 6. Safety / Rollback

Changing monitor settings can leave someone with a black screen.

Implement a safety mechanism.

Before applying a profile:

* Save the current working configuration.

After applying:

Show:

```text
Keep these display settings?

Reverting in 15 seconds...

[Keep Changes] [Revert]
```

If the user doesn't confirm, restore the previous configuration automatically.

Also handle exceptions and partial failures gracefully.

Never leave the user's display configuration broken because one monitor failed to change refresh rate.

## 7. System Tray

Add an optional system tray icon.

Right-clicking it should show something similar to:

```text
ScreenShift
----------------
Gaming
Work
TV Mode
Presentation
----------------
Open ScreenShift
Exit
```

Clicking a profile should immediately apply it.

## 8. Hotkeys

Allow optional global keyboard shortcuts for profiles.

Example:

```text
Ctrl + Alt + 1 -> Gaming
Ctrl + Alt + 2 -> Work
Ctrl + Alt + 3 -> TV Mode
```

Hotkeys should be editable and optional.

Use the Windows global hotkey APIs rather than keyboard hooks if possible.

## UI

I want a modern but minimal dark UI.

Main window concept:

```text
ScreenShift

Displays
--------------------------------------------------

[ Monitor 1 ]       [ Monitor 2 ]       [ Monitor 3 ]
  3840x2160           2560x1440           1920x1080
  144 Hz               165 Hz               60 Hz
  Primary              Enabled              Enabled


Profiles
--------------------------------------------------

[ Gaming      ]   Apply
[ Work        ]   Apply
[ TV Mode     ]   Apply

                                      [+ New Profile]
```

Selecting a monitor should reveal configuration options on the side.

Selecting a profile should show its monitor configuration.

Keep the UI clean rather than trying to reproduce all of DisplayFusion.

## Architecture

Use approximately this structure:

```text
ScreenShift/
│
├── Models/
│   ├── MonitorInfo.cs
│   ├── MonitorConfiguration.cs
│   └── DisplayProfile.cs
│
├── Services/
│   ├── DisplayService.cs
│   ├── DisplayProfileService.cs
│   ├── HotkeyService.cs
│   └── SettingsService.cs
│
├── Native/
│   ├── WindowsDisplayApi.cs
│   ├── DisplayConfigStructures.cs
│   └── NativeMethods.cs
│
├── ViewModels/
│   ├── MainViewModel.cs
│   ├── MonitorViewModel.cs
│   └── ProfileViewModel.cs
│
├── Views/
│   ├── MainWindow.xaml
│   └── ProfileEditor.xaml
│
└── App.xaml
```

Adjust this structure if there is a good architectural reason.

# Important Engineering Requirements

Do not fake monitor switching.

Actually interact with the Windows display configuration APIs.

Be especially careful with:

* Multiple identical monitors
* Disconnected monitors
* Monitors connected through DisplayPort/HDMI/USB-C
* GPU driver changes
* Windows changing monitor numbering
* Refresh rates such as 59.94 vs 60 Hz
* DPI/scaling differences
* Negative display coordinates
* Primary monitor positioning
* Display topology changes
* Applying settings atomically where possible

Use stable display identifiers whenever Windows exposes them.

Log display API failures with enough information to diagnose them.

# MVP Scope

Do NOT attempt every feature immediately.

Build this in phases.

## Phase 1

Implement:

1. Enumerate connected/active monitors.
2. Display their names.
3. Display current resolution.
4. Display current refresh rate.
5. Display primary status.
6. Detect their positions.
7. Show this information in the WPF UI.

Get monitor detection completely reliable before continuing.

## Phase 2

Implement changing:

* Refresh rate
* Resolution
* Primary display

Test each independently.

## Phase 3

Implement:

* Enable monitor
* Disable monitor
* Complete topology switching

## Phase 4

Implement profile saving/loading.

## Phase 5

Implement one-click profile application with rollback protection.

## Phase 6

Implement:

* System tray
* Global hotkeys
* UI polishing

# Development Instructions

Work incrementally.

Do not generate the entire application as a giant untested code dump.

Start by:

1. Creating the solution/project structure.
2. Creating the display models.
3. Implementing monitor enumeration using the Windows Display Configuration APIs.
4. Showing detected monitors in a basic WPF window.

Explain any Windows API structures being used.

When writing P/Invoke declarations, verify:

* Struct layout
* Field sizes
* Unicode usage
* Pointer handling
* Windows API return codes

Do not silently ignore Windows API failures.

Create helper methods that convert native structures into clean C# objects.

After completing each development phase, show:

* Files created/modified
* Important architectural decisions
* How to run/test it
* Known limitations
* What should be implemented next

Start with **Phase 1 only**.

Do not move to Phase 2 until Phase 1 is implemented cleanly.
