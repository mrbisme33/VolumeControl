# VolumeControl requirements

**Purpose:** Apply a chosen volume to every app that makes sound, so new apps follow a default you set instead of playing at full volume.

## Startup and tray
| ID | Requirement |
|---|---|
| S1 | Option to start with Windows, **on by default**, at per-user login with no admin prompt. |
| S2 | Runs in the system tray with no main window, and only one copy can run at a time. |
| S3 | Tray menu has Open settings, Output, Pause/Resume, and Exit. |

## Install and uninstall
| ID | Requirement |
|---|---|
| I1 | A single installer places the app per-user, with no admin rights needed. |
| I2 | It appears in Windows "Installed apps" with a working Uninstall entry. |
| I3 | Uninstall removes the program, the startup entry, and its shortcuts. |
| I4 | Uninstall asks whether to keep or delete saved settings. |
| I5 | Uninstall restores every app the tool changed to 100% and unmuted (see B4). |

## Default for new apps
| ID | Requirement |
|---|---|
| D1 | A global setting for apps with no saved rule: **Mute**, **Full**, or **Custom**. |
| D2 | Custom has a 0-100% slider. |
| D3 | The default out of the box is Mute. |
| D4 | Changing the default affects only apps without a saved rule. |
| D5 | Full means 100% of the app's own slider, still scaled by the master volume. |
| D6 | Optional toast when an app is auto-muted, on by default. |

## Per-app settings
| ID | Requirement |
|---|---|
| P1 | Each app can have its own rule: Mute, Full, Custom %, or Use default. |
| P2 | Rules are saved and reapply whenever the app makes sound, including after reboots. |
| P3 | The settings window lists apps playing now and apps with saved rules, with name and icon. |
| P4 | A rule can be edited or removed, and removing it returns the app to the default. |
| P5 | Apps are identified by executable name, so rules survive app updates that change the install path. |
| P6 | A rule can optionally match on command-line text, so a separate Chrome instance (`--user-data-dir`) can have its own rule. |
| P7 | If you change an app's volume in the Windows mixer, the tool saves that as its rule rather than fighting you. |

## Output device
| ID | Requirement |
|---|---|
| O1 | The tray menu and settings window list active Windows outputs and show the current one. |
| O2 | Choosing one makes it the Windows default output. Headphones, aux, Bluetooth, and WiiM endpoints are all covered when Windows exposes them as outputs. |
| O3 | The list follows devices being added, removed, or changed elsewhere in Windows. |
| O4 | Each app's rule can send it to a specific output, or follow the Windows default. |
| O5 | If an app's chosen output is unavailable, the app follows the Windows default until it returns. |
| O6 | Pause, Exit, and Uninstall return open apps to the Windows default output. |
| O7 | Output is per program, because Windows stores it that way. Command-line rules (P6) cannot have their own output. |

## Behavior
| ID | Requirement |
|---|---|
| B1 | Rules apply to apps already playing at launch, and to new audio sessions as they appear, with no polling delay. |
| B2 | Covers all active output devices and picks up devices added later, such as headphones. |
| B3 | Windows system sounds are exempt by default, with their own optional rule. |
| B4 | Pause and Exit restore apps to their normal levels. |
| B5 | An app it can't fully inspect, such as one running as admin, falls back to name matching and must not crash the tool. |
| B6 | Idle CPU use is effectively zero. |

## Storage and platform
| ID | Requirement |
|---|---|
| T1 | Settings live in one readable JSON file in `%APPDATA%`, and a corrupt file resets safely with a backup. |
| T2 | Supports Windows 10 and 11, x64. |
| T3 | Built in C# on .NET Framework 4.8, which ships with Windows. |

## Out of scope for v1
- Per-window or per-tab control, beyond the separate Chrome instance in P6
- Hotkeys
- Profiles, such as Work and Gaming
- Casting directly to network speakers that Windows does not list as an output
