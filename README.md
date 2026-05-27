# WinHDRSettingFix

Small per-user Windows helper for the Windows 11 HDR Calibration profile reload bug.

When HDR is toggled with `Win + Alt + B`, Windows can leave the active HDR calibration profile stale until Display Settings is opened. This helper waits in the current user session, watches display/session/power events, and runs Windows' built-in color calibration loader only when HDR is currently enabled on at least one display.

## Usage

Build:

```powershell
dotnet build -c Release
```

Install the helper to start at logon. It tries Task Scheduler first and falls back to the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` startup entry if Task Scheduler is locked down:

```powershell
.\bin\Release\net10.0-windows\WinHDRSettingFix.exe --install
```

Start it now:

```powershell
Start-Process .\bin\Release\net10.0-windows\WinHDRSettingFix.exe
```

Run a one-shot refresh test:

```powershell
.\bin\Release\net10.0-windows\WinHDRSettingFix.exe --run-once
```

Show detected HDR state:

```powershell
.\bin\Release\net10.0-windows\WinHDRSettingFix.exe --status
```

Run the watcher in the foreground for debugging:

```powershell
.\bin\Release\net10.0-windows\WinHDRSettingFix.exe --foreground
```

Uninstall the startup entry:

```powershell
.\bin\Release\net10.0-windows\WinHDRSettingFix.exe --uninstall
```

## Notes

- The helper is intentionally event-driven. It does not poll constantly.
- It calls the same built-in Color Calibration Loader COM handler used by `\Microsoft\Windows\WindowsColorSystem\Calibration Loader`. The scheduled task path is kept as a fallback for systems that allow it.
- If your system still requires the Display Settings workaround, run with `--open-settings-fallback`. The installed task can include that flag by installing with:

```powershell
.\bin\Release\net10.0-windows\WinHDRSettingFix.exe --install --open-settings-fallback
```
