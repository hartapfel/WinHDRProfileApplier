using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinHDRProfileApplier;

internal static class Program
{
    private const string MutexName = @"Local\WinHDRProfileApplier";

    [STAThread]
    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        if (options.ShowHelp)
        {
            NativeConsole.AttachToParent();
            PrintHelp();
            return 0;
        }

        if (options.Command is AppCommand.Install)
        {
            NativeConsole.AttachToParent();
            return StartupTask.Install(options);
        }

        if (options.Command is AppCommand.Uninstall)
        {
            NativeConsole.AttachToParent();
            return StartupTask.Uninstall();
        }

        if (options.Command is AppCommand.Status)
        {
            NativeConsole.AttachToParent();
            PrintStatus();
            return 0;
        }

        var refresher = new CalibrationRefresher(options);

        if (options.Command is AppCommand.RunOnce)
        {
            NativeConsole.AttachToParent();
            return refresher.RefreshIfHdrEnabled("manual", force: options.Force) ? 0 : 1;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            return 0;
        }

        try
        {
            if (!options.Foreground)
            {
                NativeConsole.DetachConsole();
            }

            ApplicationConfiguration.Initialize();
            using var window = new HdrProfileWatchWindow(refresher, options);
            Application.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Write($"Watcher failed: {ex}");
            if (options.Foreground)
            {
                Console.WriteLine(ex);
            }

            return 1;
        }
    }

    private static void PrintStatus()
    {
        var displays = NativeDisplay.GetAdvancedColorDisplays();
        if (displays.Count == 0)
        {
            Console.WriteLine("No active display paths reported advanced color state.");
            return;
        }

        foreach (var display in displays)
        {
            Console.WriteLine(
                $"{display.DevicePath}: supported={display.Supported}, enabled={display.Enabled}, " +
                $"forceDisabled={display.ForceDisabled}, bitsPerChannel={display.BitsPerColorChannel}, " +
                $"encoding={display.ColorEncoding}");
        }

        Console.WriteLine(displays.Any(static display => display.Enabled)
            ? "HDR/Advanced Color is enabled on at least one active display."
            : "HDR/Advanced Color is not enabled on active displays.");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("WinHDRProfileApplier");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  WinHDRProfileApplier.exe                         Run hidden event watcher");
        Console.WriteLine("  WinHDRProfileApplier.exe --install               Install per-user logon task");
        Console.WriteLine("  WinHDRProfileApplier.exe --uninstall             Remove logon task");
        Console.WriteLine("  WinHDRProfileApplier.exe --run-once              Refresh now if HDR is enabled");
        Console.WriteLine("  WinHDRProfileApplier.exe --run-once --force      Refresh now regardless of HDR state");
        Console.WriteLine("  WinHDRProfileApplier.exe --status                Print active display HDR state");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --open-settings-fallback   Also open Display Settings after running the loader");
        Console.WriteLine("  --foreground               Keep the console attached for debugging watcher startup");
        Console.WriteLine("  --watchdog-minutes N       Optional low-frequency refresh while HDR is enabled");
        Console.WriteLine("  --quiet                    Reduce console output");
    }
}

internal enum AppCommand
{
    Watch,
    Install,
    Uninstall,
    RunOnce,
    Status
}

internal sealed class AppOptions
{
    public AppCommand Command { get; private init; } = AppCommand.Watch;
    public bool Force { get; private init; }
    public bool OpenSettingsFallback { get; private init; }
    public bool Foreground { get; private init; }
    public bool Quiet { get; private init; }
    public bool ShowHelp { get; private init; }
    public int WatchdogMinutes { get; private init; }

    public static AppOptions Parse(string[] args)
    {
        var command = AppCommand.Watch;
        var force = false;
        var openSettingsFallback = false;
        var foreground = false;
        var quiet = false;
        var showHelp = false;
        var watchdogMinutes = 0;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg.ToLowerInvariant())
            {
                case "--install":
                    command = AppCommand.Install;
                    break;
                case "--uninstall":
                    command = AppCommand.Uninstall;
                    break;
                case "--run-once":
                    command = AppCommand.RunOnce;
                    break;
                case "--status":
                    command = AppCommand.Status;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--open-settings-fallback":
                    openSettingsFallback = true;
                    break;
                case "--foreground":
                    foreground = true;
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                case "--watchdog-minutes":
                    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out watchdogMinutes))
                    {
                        showHelp = true;
                    }

                    index++;
                    break;
            }
        }

        watchdogMinutes = Math.Clamp(watchdogMinutes, 0, 1440);
        return new AppOptions
        {
            Command = command,
            Force = force,
            OpenSettingsFallback = openSettingsFallback,
            Foreground = foreground,
            Quiet = quiet,
            ShowHelp = showHelp,
            WatchdogMinutes = watchdogMinutes
        };
    }

    public string ToTaskArguments()
    {
        var args = new List<string> { "--quiet" };
        if (OpenSettingsFallback)
        {
            args.Add("--open-settings-fallback");
        }

        if (WatchdogMinutes > 0)
        {
            args.Add("--watchdog-minutes");
            args.Add(WatchdogMinutes.ToString());
        }

        return string.Join(' ', args);
    }
}

internal sealed class HdrProfileWatchWindow : NativeWindow, IDisposable
{
    private const int DebounceMilliseconds = 1500;
    private readonly CalibrationRefresher refresher;
    private readonly AppOptions options;
    private readonly System.Windows.Forms.Timer debounceTimer;
    private readonly System.Windows.Forms.Timer? watchdogTimer;
    private bool disposed;
    private string pendingReason = "startup";

    public HdrProfileWatchWindow(CalibrationRefresher refresher, AppOptions options)
    {
        this.refresher = refresher;
        this.options = options;

        CreateHandle(new CreateParams { Caption = "WinHDRProfileApplier" });
        _ = NativeMethods.WTSRegisterSessionNotification(Handle, NativeMethods.NOTIFY_FOR_THIS_SESSION);

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        debounceTimer = new System.Windows.Forms.Timer { Interval = DebounceMilliseconds };
        debounceTimer.Tick += (_, _) =>
        {
            debounceTimer.Stop();
            refresher.RefreshIfHdrEnabled(pendingReason);
        };

        if (options.WatchdogMinutes > 0)
        {
            watchdogTimer = new System.Windows.Forms.Timer
            {
                Interval = checked(options.WatchdogMinutes * 60 * 1000)
            };
            watchdogTimer.Tick += (_, _) => refresher.RefreshIfHdrEnabled("watchdog");
            watchdogTimer.Start();
        }

        ScheduleRefresh("startup");
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeMethods.WM_DISPLAYCHANGE:
                ScheduleRefresh("WM_DISPLAYCHANGE");
                break;
            case NativeMethods.WM_DEVICECHANGE:
                ScheduleRefresh("WM_DEVICECHANGE");
                break;
            case NativeMethods.WM_SETTINGCHANGE:
                ScheduleRefresh("WM_SETTINGCHANGE");
                break;
            case NativeMethods.WM_POWERBROADCAST:
                if (m.WParam.ToInt32() is NativeMethods.PBT_APMRESUMEAUTOMATIC or NativeMethods.PBT_APMRESUMESUSPEND)
                {
                    ScheduleRefresh("power resume");
                }

                break;
            case NativeMethods.WM_WTSSESSION_CHANGE:
                if (m.WParam.ToInt32() is NativeMethods.WTS_SESSION_UNLOCK or NativeMethods.WTS_CONSOLE_CONNECT or NativeMethods.WTS_REMOTE_CONNECT)
                {
                    ScheduleRefresh("session change");
                }

                break;
        }

        base.WndProc(ref m);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => ScheduleRefresh("DisplaySettingsChanged");

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Resume)
        {
            ScheduleRefresh("PowerModeChanged");
        }
    }

    private void ScheduleRefresh(string reason)
    {
        pendingReason = reason;
        debounceTimer.Stop();
        debounceTimer.Start();
        Logger.Write($"Scheduled refresh: {reason}");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        debounceTimer.Dispose();
        watchdogTimer?.Dispose();
        _ = NativeMethods.WTSUnRegisterSessionNotification(Handle);
        DestroyHandle();
    }
}

internal sealed class CalibrationRefresher
{
    private const string CalibrationTaskName = @"\Microsoft\Windows\WindowsColorSystem\Calibration Loader";
    private static readonly TimeSpan MinimumRunInterval = TimeSpan.FromSeconds(20);
    private readonly AppOptions options;
    private DateTimeOffset lastRun = DateTimeOffset.MinValue;

    public CalibrationRefresher(AppOptions options)
    {
        this.options = options;
    }

    public bool RefreshIfHdrEnabled(string reason, bool force = false)
    {
        try
        {
            var hdrEnabled = NativeDisplay.IsAnyAdvancedColorEnabled();
            if (!force && !hdrEnabled)
            {
                WriteLine("HDR/Advanced Color is not enabled; calibration reload skipped.");
                Logger.Write($"Skipped refresh for {reason}: HDR is not enabled");
                return true;
            }

            if (!force && DateTimeOffset.UtcNow - lastRun < MinimumRunInterval)
            {
                Logger.Write($"Skipped refresh for {reason}: throttled");
                return true;
            }

            lastRun = DateTimeOffset.UtcNow;
            Logger.Write($"Running calibration loader for {reason}; hdrEnabled={hdrEnabled}, force={force}");

            var comResult = TryRunCalibrationLoaderCom();
            if (!comResult.Success)
            {
                Logger.Write($"Direct calibration loader COM call failed: {comResult.Message}");

                var taskResult = RunProcess("schtasks.exe", $"/Run /TN \"{CalibrationTaskName}\"", waitMilliseconds: 10_000);
                if (taskResult.ExitCode != 0)
                {
                    Logger.Write($"Calibration loader task failed: exit={taskResult.ExitCode}; {taskResult.Output}");
                    WriteLine("Calibration loader failed.");
                    WriteLine($"COM: {comResult.Message}");
                    WriteLine($"Scheduled task exit {taskResult.ExitCode}: {taskResult.Output.Trim()}");
                    return false;
                }
            }

            WriteLine("Calibration loader triggered.");
            Logger.Write("Calibration loader triggered successfully.");

            if (options.OpenSettingsFallback)
            {
                OpenDisplaySettings();
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Write($"Refresh failed: {ex}");
            WriteLine(ex.Message);
            return false;
        }
    }

    private static ProcessResult RunProcess(string fileName, string arguments, int waitMilliseconds)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        process.Start();
        if (!process.WaitForExit(waitMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort timeout cleanup.
            }

            return new ProcessResult(-1, "Timed out.");
        }

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        return new ProcessResult(process.ExitCode, output);
    }

    private static void OpenDisplaySettings()
    {
        Logger.Write("Opening Display Settings fallback.");
        Process.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:display",
            UseShellExecute = true
        });
    }

    private static CalibrationLoaderComResult TryRunCalibrationLoaderCom()
    {
        var instance = default(object);
        try
        {
            var type = Type.GetTypeFromCLSID(NativeMethods.ColorCalibrationLoaderClsid, throwOnError: true);
            instance = Activator.CreateInstance(type!);
            if (instance is not ITaskHandler handler)
            {
                return new CalibrationLoaderComResult(false, "COM object does not expose ITaskHandler.");
            }

            var status = new TaskHandlerStatus();
            handler.Start(status, string.Empty);
            return new CalibrationLoaderComResult(true, "OK");
        }
        catch (Exception ex)
        {
            return new CalibrationLoaderComResult(false, ex.Message);
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                _ = Marshal.ReleaseComObject(instance);
            }
        }
    }

    private void WriteLine(string message)
    {
        if (!options.Quiet)
        {
            Console.WriteLine(message);
        }
    }
}

internal readonly record struct ProcessResult(int ExitCode, string Output);

internal readonly record struct CalibrationLoaderComResult(bool Success, string Message);

internal static class StartupTask
{
    private const string TaskName = "WinHDRProfileApplier";
    private const string LegacyTaskName = "WinHDRSettingFix";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WinHDRProfileApplier";
    private const string LegacyRunValueName = "WinHDRSettingFix";

    public static int Install(AppOptions options)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            Console.WriteLine("Cannot determine executable path.");
            return 1;
        }

        var taskRun = $"\"{exePath}\" {options.ToTaskArguments()}";
        var result = RunSchtasks($"/Create /TN \"{TaskName}\" /SC ONLOGON /RL LIMITED /F /TR \"{taskRun}\"");
        if (result.ExitCode == 0)
        {
            Console.WriteLine(result.Output.Trim());
            Console.WriteLine($"Installed logon task '{TaskName}'.");
            RemoveRunKey(RunValueName);
            RemoveRunKey(LegacyRunValueName);
            return 0;
        }

        Console.WriteLine("Task Scheduler install failed; falling back to HKCU Run startup.");
        Console.WriteLine(result.Output.Trim());

        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            runKey.SetValue(RunValueName, taskRun, RegistryValueKind.String);
            Console.WriteLine($"Installed HKCU Run startup value '{RunValueName}'.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HKCU Run fallback failed: {ex.Message}");
            return result.ExitCode == 0 ? 1 : result.ExitCode;
        }
    }

    public static int Uninstall()
    {
        var result = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        var legacyTaskResult = RunSchtasks($"/Delete /TN \"{LegacyTaskName}\" /F");
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            Console.WriteLine(result.Output.Trim());
        }

        if (result.ExitCode == 0)
        {
            Console.WriteLine($"Removed logon task '{TaskName}'.");
        }

        if (legacyTaskResult.ExitCode == 0)
        {
            Console.WriteLine($"Removed legacy logon task '{LegacyTaskName}'.");
        }

        var removedRunKey = RemoveRunKey(RunValueName);
        var removedLegacyRunKey = RemoveRunKey(LegacyRunValueName);
        if (removedRunKey)
        {
            Console.WriteLine($"Removed HKCU Run startup value '{RunValueName}'.");
        }

        if (removedLegacyRunKey)
        {
            Console.WriteLine($"Removed legacy HKCU Run startup value '{LegacyRunValueName}'.");
        }

        return result.ExitCode == 0 || legacyTaskResult.ExitCode == 0 || removedRunKey || removedLegacyRunKey
            ? 0
            : result.ExitCode;
    }

    private static bool RemoveRunKey(string valueName)
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (runKey?.GetValue(valueName) is null)
            {
                return false;
            }

            runKey.DeleteValue(valueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessResult RunSchtasks(string arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        process.Start();
        process.WaitForExit();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        return new ProcessResult(process.ExitCode, output);
    }
}

internal sealed record AdvancedColorDisplay(
    string DevicePath,
    bool Supported,
    bool Enabled,
    bool WideColorEnforced,
    bool ForceDisabled,
    uint ColorEncoding,
    uint BitsPerColorChannel);

internal static class NativeDisplay
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_SUCCESS = 0;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;

    public static bool IsAnyAdvancedColorEnabled() =>
        GetAdvancedColorDisplays().Any(static display => display.Enabled);

    public static IReadOnlyList<AdvancedColorDisplay> GetAdvancedColorDisplays()
    {
        var result = NativeMethods.GetDisplayConfigBufferSizes(
            QDC_ONLY_ACTIVE_PATHS,
            out var pathCount,
            out var modeCount);

        if (result != ERROR_SUCCESS || pathCount == 0)
        {
            Logger.Write($"GetDisplayConfigBufferSizes failed: {result}");
            return Array.Empty<AdvancedColorDisplay>();
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        result = NativeMethods.QueryDisplayConfig(
            QDC_ONLY_ACTIVE_PATHS,
            ref pathCount,
            paths,
            ref modeCount,
            modes,
            IntPtr.Zero);

        if (result != ERROR_SUCCESS)
        {
            Logger.Write($"QueryDisplayConfig failed: {result}");
            return Array.Empty<AdvancedColorDisplay>();
        }

        var displays = new List<AdvancedColorDisplay>();
        for (var index = 0; index < pathCount; index++)
        {
            var path = paths[index];
            var info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                    size = Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id
                }
            };

            result = NativeMethods.DisplayConfigGetDeviceInfo(ref info);
            if (result != ERROR_SUCCESS)
            {
                Logger.Write($"DisplayConfigGetDeviceInfo advanced color failed for target {path.targetInfo.id}: {result}");
                continue;
            }

            displays.Add(new AdvancedColorDisplay(
                DevicePath: GetDisplayLabel(path),
                Supported: (info.value & 0x1) != 0,
                Enabled: (info.value & 0x2) != 0,
                WideColorEnforced: (info.value & 0x4) != 0,
                ForceDisabled: (info.value & 0x8) != 0,
                ColorEncoding: info.colorEncoding,
                BitsPerColorChannel: info.bitsPerColorChannel));
        }

        return displays;
    }

    private static string GetDisplayLabel(DISPLAYCONFIG_PATH_INFO path)
    {
        var luid = $"{path.targetInfo.adapterId.HighPart:x8}:{path.targetInfo.adapterId.LowPart:x8}";
        return $"adapter={luid},target={path.targetInfo.id}";
    }
}

internal static class Logger
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinHDRProfileApplier");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "WinHDRProfileApplier.log");
            lock (Gate)
            {
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never affect the display/profile refresh path.
        }
    }
}

internal static class NativeConsole
{
    private const int ATTACH_PARENT_PROCESS = -1;
    public static void AttachToParent()
    {
        _ = NativeMethods.AttachConsole(ATTACH_PARENT_PROCESS);
    }

    public static void DetachConsole()
    {
        _ = NativeMethods.FreeConsole();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint outputTechnology;
    public uint rotation;
    public uint scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate;
    public uint scanLineOrdering;
    [MarshalAs(UnmanagedType.Bool)]
    public bool targetAvailable;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_MODE_INFO
{
    public uint infoType;
    public uint id;
    public LUID adapterId;
    public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
}

[StructLayout(LayoutKind.Explicit)]
internal struct DISPLAYCONFIG_MODE_INFO_UNION
{
    [FieldOffset(0)]
    public DISPLAYCONFIG_TARGET_MODE targetMode;

    [FieldOffset(0)]
    public DISPLAYCONFIG_SOURCE_MODE sourceMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_TARGET_MODE
{
    public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_SOURCE_MODE
{
    public uint width;
    public uint height;
    public uint pixelFormat;
    public POINTL position;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTL
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
{
    public ulong pixelRate;
    public DISPLAYCONFIG_RATIONAL hSyncFreq;
    public DISPLAYCONFIG_RATIONAL vSyncFreq;
    public DISPLAYCONFIG_2DREGION activeSize;
    public DISPLAYCONFIG_2DREGION totalSize;
    public uint videoStandard;
    public uint scanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_2DREGION
{
    public uint cx;
    public uint cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public int type;
    public int size;
    public LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint value;
    public uint colorEncoding;
    public uint bitsPerColorChannel;
}

internal static partial class NativeMethods
{
    public static readonly Guid ColorCalibrationLoaderClsid = new("B210D694-C8DF-490D-9576-9E20CDBC20BD");

    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_DEVICECHANGE = 0x0219;
    public const int WM_POWERBROADCAST = 0x0218;
    public const int WM_WTSSESSION_CHANGE = 0x02B1;

    public const int PBT_APMRESUMESUSPEND = 0x0007;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    public const int WTS_CONSOLE_CONNECT = 0x1;
    public const int WTS_REMOTE_CONNECT = 0x3;
    public const int WTS_SESSION_UNLOCK = 0x8;
    public const int NOTIFY_FOR_THIS_SESSION = 0;

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeConsole();
}

[ComImport]
[Guid("839D7762-5121-4009-9234-4F0D19394F04")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskHandler
{
    void Start(
        [MarshalAs(UnmanagedType.IUnknown)] object? handlerServices,
        [MarshalAs(UnmanagedType.BStr)] string data);

    void Stop(out int returnCode);

    void Pause();

    void Resume();
}

[ComImport]
[Guid("EAEC7A8F-27A0-4DDC-8675-14726A01A38A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskHandlerStatus
{
    void UpdateStatus(
        short percentComplete,
        [MarshalAs(UnmanagedType.BStr)] string statusMessage);

    void TaskCompleted(int taskErrCode);
}

[ComVisible(true)]
internal sealed class TaskHandlerStatus : ITaskHandlerStatus
{
    public void UpdateStatus(short percentComplete, string statusMessage)
    {
        Logger.Write($"Calibration loader status {percentComplete}: {statusMessage}");
    }

    public void TaskCompleted(int taskErrCode)
    {
        Logger.Write($"Calibration loader completed: 0x{taskErrCode:x8}");
    }
}
