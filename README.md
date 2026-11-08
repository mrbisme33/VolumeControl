# VolumeControl

A Windows tray app that sets a default volume for every app that makes sound. Out of the box, new apps are muted until you allow them. You can also switch the Windows output device from the tray.

Requirements are in [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md).

## Build

Needs the .NET SDK. The result targets .NET Framework 4.8, which ships with Windows.

```bash
dotnet build -c Release
```

## Install

Run `bin\Release\net48\VolumeControl.exe` and accept the prompt. It installs to `%LOCALAPPDATA%\Programs\VolumeControl` without admin rights, then starts in the tray. Uninstall it from Windows **Installed apps**.

## Use

- Left-click the tray icon to open Settings. Right-click it for Output, Pause, and Exit.
- Pick the default for new apps: Mute, Full, or Custom.
- Select an app to give it its own rule. Changing an app in the Windows Volume Mixer also saves a rule.
- To treat a separate Chrome profile differently, select Chrome, enter text from its command line such as `ChromeSolo`, and choose **Add separate rule**.

Settings are stored in `%APPDATA%\VolumeControl\settings.json`.

## Known limits

- **Closed apps can stay muted after you stop the tool.** Windows remembers per-app levels, and Pause, Exit, and Uninstall can only reset apps that are still open. Fix a leftover in the Windows Volume Mixer.
- **Apps that change their own volume** after their first 3 seconds look like a mixer change, so that level is saved as their rule.
- **A sound can leak briefly** if it starts at the same moment as its audio session.
- **Output switching** uses the undocumented Windows interface behind the Sound panel, so a future Windows release could change it.
- **WiiM and other network speakers** appear only if Windows exposes them as an output device.
