using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AutoDefaultToHeadset;

internal static class Program
{
    private static readonly string[] DefaultRenderMatches =
    {
        "headset",
        "headphones",
        "xbox"
    };

    private static readonly string[] DefaultCaptureMatches =
    {
        "headset",
        "xbox",
        "microphone"
    };

    private static readonly object LogLock = new();
    private const string InstanceMutexName = @"Local\AutoDefaultToHeadset";
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int ErrorAccessDenied = 5;
    private const uint WmQuit = 0x0012;
    private const uint WmApplyDefaults = 0x8000;
    private const uint PmNoRemove = 0x0000;
    private static readonly TimeSpan ReplaceExistingTimeout = TimeSpan.FromSeconds(10);
    private static bool ConsoleAvailable;

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception ex)
            {
                ShowFatalError("Unhandled domain exception.", ex);
            }
            else
            {
                ShowFatalError("Unhandled domain exception.", new Exception("Unknown exception object."));
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            ShowFatalError("Unhandled task exception.", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        Options options;

        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            EnsureConsole();
            WriteError(ex.Message);
            PrintHelp();
            return 1;
        }

        if (options.ShowHelp)
        {
            EnsureConsole();
            PrintHelp();
            return 0;
        }

        InitializeConsole(options);

        try
        {
            using var controller = new AudioController(options);

            if (options.InstallStartup)
            {
                EnsureConsole();
                controller.RunStartupInstaller();
                return 0;
            }

            if (options.ListDevices)
            {
                controller.PrintDevices();
                return 0;
            }

            using var instanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);
            var ownsMutex = createdNew;

            if (!ownsMutex)
            {
                if (!options.ReplaceExisting)
                {
                    WriteInfo("Another instance is already running.");
                    return 0;
                }

                if (!TryReplaceExistingInstance(instanceMutex))
                {
                    return 1;
                }

                ownsMutex = true;
            }

            try
            {
                WriteInfo("AutoDefaultToHeadset starting.");
                WriteInfo("PID: " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ". Logging: console only");
                WriteInfo("Render match: " + options.RenderDescription);
                WriteInfo("Capture match: " + options.CaptureDescription);

                controller.ApplyDefaults("startup");
                return RunEventLoop(controller);
            }
            finally
            {
                if (ownsMutex)
                {
                    try
                    {
                        instanceMutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ShowFatalError("Unhandled fatal error.", ex);
            return 1;
        }
    }

    private static int RunEventLoop(AudioController controller)
    {
        var threadId = NativeMethods.GetCurrentThreadId();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;

            if (!NativeMethods.PostThreadMessage(threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero))
            {
                WriteError("Failed to stop event loop. Win32 error: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
            }
        };

        Console.CancelKeyPress += cancelHandler;
        NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
        controller.RegisterNotifications();
        WriteInfo("Endpoint hook active. Waiting for headset connect. Press Ctrl+C to stop.");

        try
        {
            while (true)
            {
                var result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result == 0)
                {
                    WriteInfo("Event loop stopped.");
                    return 0;
                }

                if (result == -1)
                {
                    WriteError("Event loop failed. Win32 error: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                    return 1;
                }

                if (message.Message == WmApplyDefaults)
                {
                    controller.ApplyDefaults("endpoint event");
                    continue;
                }

                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        finally
        {
            controller.UnregisterNotifications();
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private sealed class Options
    {
        public string? RenderId { get; private set; }
        public string? CaptureId { get; private set; }
        public List<string> RenderMatches { get; } = new(DefaultRenderMatches);
        public List<string> CaptureMatches { get; } = new(DefaultCaptureMatches);
        public bool Background { get; private set; }
        public bool ReplaceExisting { get; private set; } = true;
        public bool ListDevices { get; private set; }
        public bool InstallStartup { get; private set; }
        public bool ShowHelp { get; private set; }

        public string RenderDescription => RenderId ?? string.Join(", ", RenderMatches.Select(s => "'" + s + "'"));
        public string CaptureDescription => CaptureId ?? string.Join(", ", CaptureMatches.Select(s => "'" + s + "'"));

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                var argument = args[i];

                switch (argument.ToLowerInvariant())
                {
                    case "--render-id":
                        options.RenderId = RequireValue(args, ref i, argument);
                        break;
                    case "--capture-id":
                        options.CaptureId = RequireValue(args, ref i, argument);
                        break;
                    case "--id":
                        options.RenderId = RequireValue(args, ref i, argument);
                        options.CaptureId = options.RenderId;
                        break;
                    case "--render-match":
                        options.RenderMatches.Clear();
                        options.RenderMatches.Add(RequireValue(args, ref i, argument));
                        break;
                    case "--capture-match":
                        options.CaptureMatches.Clear();
                        options.CaptureMatches.Add(RequireValue(args, ref i, argument));
                        break;
                    case "--match":
                        var match = RequireValue(args, ref i, argument);
                        options.RenderMatches.Clear();
                        options.CaptureMatches.Clear();
                        options.RenderMatches.Add(match);
                        options.CaptureMatches.Add(match);
                        break;
                    case "--background":
                        options.Background = true;
                        break;
                    case "--list-devices":
                        options.ListDevices = true;
                        break;
                    case "--install":
                        options.InstallStartup = true;
                        break;
                    case "--replace-existing":
                        options.ReplaceExisting = true;
                        break;
                    case "--exit-if-running":
                        options.ReplaceExisting = false;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        options.ShowHelp = true;
                        break;
                    default:
                        throw new ArgumentException("Unknown argument: " + argument);
                }
            }

            return options;
        }

        private static string RequireValue(string[] args, ref int index, string argument)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException(argument + " requires value.");
            }

            var value = args[++index];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(argument + " value cannot be empty.");
            }

            return value;
        }
    }

    private sealed class AudioController : IDisposable
    {
        private readonly Options _options;
        private readonly IMMDeviceEnumerator _enumerator;
        private readonly IPolicyConfig _policyConfig;
        private readonly NotificationClient _notificationClient;
        private uint _eventThreadId;
        private bool _registered;

        public AudioController(Options options)
        {
            _options = options;
            _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.MMDeviceEnumerator, throwOnError: true)!)!;
            _policyConfig = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.PolicyConfigClient, throwOnError: true)!)!;
            _notificationClient = new NotificationClient(RequestEndpointApply);
        }

        public void RegisterNotifications()
        {
            if (_registered)
            {
                return;
            }

            _eventThreadId = NativeMethods.GetCurrentThreadId();
            Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(_notificationClient));
            _registered = true;
        }

        private void RequestEndpointApply()
        {
            var threadId = _eventThreadId;
            if (threadId == 0 || !NativeMethods.PostThreadMessage(threadId, WmApplyDefaults, UIntPtr.Zero, IntPtr.Zero))
            {
                WriteError("Failed to queue endpoint apply. Win32 error: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
            }
        }

        public void UnregisterNotifications()
        {
            if (!_registered)
            {
                return;
            }

            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
            }
            catch
            {
            }

            _registered = false;
        }

        public void ApplyDefaults(string source)
        {
            try
            {
                var render = FindBestDevice(EDataFlow.eRender, _options.RenderId, _options.RenderMatches);
                var capture = FindBestDevice(EDataFlow.eCapture, _options.CaptureId, _options.CaptureMatches);

                if (render != null)
                {
                    SetDefaultForAllRoles(render, source);
                }
                else
                {
                    WriteInfo("No active render device matched " + _options.RenderDescription + ".");
                }

                if (capture != null)
                {
                    SetDefaultForAllRoles(capture, source);
                }
                else
                {
                    WriteInfo("No active capture device matched " + _options.CaptureDescription + ".");
                }
            }
            catch (Exception ex)
            {
                WriteError("Failed to apply defaults from " + source + ".", ex);
            }
        }

        public void PrintDevices()
        {
            PrintDevices(EDataFlow.eRender, "Output");
            PrintDevices(EDataFlow.eCapture, "Input");
        }

        public void RunStartupInstaller()
        {
            var renderDevices = EnumerateDevices(EDataFlow.eRender, DeviceState.All);
            var captureDevices = EnumerateDevices(EDataFlow.eCapture, DeviceState.All);

            var render = PromptForDevice("output", renderDevices);
            var capture = PromptForDevice("input", captureDevices);

            Console.WriteLine();
            Console.WriteLine("Choose match mode:");
            Console.WriteLine("  1. Exact endpoint IDs");
            Console.WriteLine("  2. Name contains");
            Console.Write("Mode [1]: ");
            var mode = Console.ReadLine();

            string arguments;
            if (string.Equals(mode, "2", StringComparison.OrdinalIgnoreCase))
            {
                var renderMatch = PromptForText("Output name contains", SuggestMatch(render.Name));
                var captureMatch = PromptForText("Input name contains", SuggestMatch(capture.Name));
                arguments = "--background --render-match " + Quote(renderMatch) + " --capture-match " + Quote(captureMatch);
            }
            else
            {
                arguments = "--background --render-id " + Quote(render.Id) + " --capture-id " + Quote(capture.Id);
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                throw new InvalidOperationException("Cannot locate running executable path.");
            }

            CreateStartupShortcut(exePath, arguments);
            Console.WriteLine();
            Console.WriteLine("Created Startup shortcut.");
            Console.WriteLine("Arguments: " + arguments);

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = true
            });
            Console.WriteLine("Launched AutoDefaultToHeadset.");
        }

        private static AudioDevice PromptForDevice(string label, IReadOnlyList<AudioDevice> devices)
        {
            var sorted = devices
                .OrderBy(device => device.State == DeviceState.Active ? 0 : device.State == DeviceState.Unplugged ? 1 : 2)
                .ThenByDescending(device => device.Name.Contains("headset", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(device => device.Name.Contains("headphones", StringComparison.OrdinalIgnoreCase))
                .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine();
            Console.WriteLine("Select " + label + " device:");
            for (var i = 0; i < sorted.Count; i++)
            {
                var device = sorted[i];
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0}. [{1}] {2}", i + 1, device.State, device.Name));
                Console.WriteLine("     " + device.Id);
            }

            while (true)
            {
                Console.Write(label + " number: ");
                var input = Console.ReadLine();
                if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                    index >= 1 &&
                    index <= sorted.Count)
                {
                    return sorted[index - 1];
                }

                Console.WriteLine("Invalid selection.");
            }
        }

        private static string PromptForText(string prompt, string suggestion)
        {
            Console.Write(prompt + " [" + suggestion + "]: ");
            var value = Console.ReadLine();
            return string.IsNullOrWhiteSpace(value) ? suggestion : value.Trim();
        }

        private static string SuggestMatch(string name)
        {
            var open = name.IndexOf('(');
            var close = name.LastIndexOf(')');
            if (open >= 0 && close > open + 1)
            {
                return name.Substring(open + 1, close - open - 1);
            }

            return name;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        private static void CreateStartupShortcut(string exePath, string arguments)
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var link = Path.Combine(startup, "AutoDefaultToHeadset.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = exePath;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
            shortcut.IconLocation = exePath;
            shortcut.Save();
        }

        private void PrintDevices(EDataFlow flow, string label)
        {
            EnsureConsole();
            Console.WriteLine(label + " devices:");

            var devices = EnumerateDevices(flow, DeviceState.All);
            foreach (var device in devices)
            {
                Console.WriteLine("  State: " + device.State);
                Console.WriteLine("  Name:  " + device.Name);
                Console.WriteLine("  Id:    " + device.Id);
                Console.WriteLine();
            }
        }

        private AudioDevice? FindBestDevice(EDataFlow flow, string? exactId, IReadOnlyList<string> matches)
        {
            var devices = EnumerateDevices(flow, DeviceState.Active);

            if (!string.IsNullOrWhiteSpace(exactId))
            {
                return devices.FirstOrDefault(device => string.Equals(device.Id, exactId, StringComparison.OrdinalIgnoreCase));
            }

            return devices
                .Where(device => matches.Any(match => device.Name.Contains(match, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(device => Score(device, matches))
                .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static int Score(AudioDevice device, IReadOnlyList<string> matches)
        {
            var score = 0;

            foreach (var match in matches)
            {
                if (device.Name.Equals(match, StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                }
                else if (device.Name.StartsWith(match, StringComparison.OrdinalIgnoreCase))
                {
                    score += 50;
                }
                else if (device.Name.Contains(match, StringComparison.OrdinalIgnoreCase))
                {
                    score += 10;
                }
            }

            if (device.Name.Contains("headset", StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }

            return score;
        }

        private List<AudioDevice> EnumerateDevices(EDataFlow flow, DeviceState stateMask)
        {
            Marshal.ThrowExceptionForHR(_enumerator.EnumAudioEndpoints(flow, stateMask, out var collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));

            var devices = new List<AudioDevice>();
            for (var i = 0u; i < count; i++)
            {
                Marshal.ThrowExceptionForHR(collection.Item(i, out var device));
                Marshal.ThrowExceptionForHR(device.GetId(out var id));
                Marshal.ThrowExceptionForHR(device.GetState(out var state));

                devices.Add(new AudioDevice(id, GetFriendlyName(device), state, flow));
            }

            return devices;
        }

        private static unsafe string GetFriendlyName(IMMDevice device)
        {
            try
            {
                Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StorageAccessMode.Read, out var propertyStore));
                using var property = new PropVariantScope();
                Marshal.ThrowExceptionForHR(propertyStore.GetValue(PropertyKeys.DeviceFriendlyName, property.Value));
                var name = Marshal.PtrToStringUni(property.Value->PointerValue);
                return string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
            }
            catch
            {
                return "(unnamed)";
            }
        }

        private void SetDefaultForAllRoles(AudioDevice device, string source)
        {
            SetDefault(device, ERole.eConsole, source);
            SetDefault(device, ERole.eMultimedia, source);
            SetDefault(device, ERole.eCommunications, source);
        }

        private void SetDefault(AudioDevice device, ERole role, string source)
        {
            var hr = _policyConfig.SetDefaultEndpoint(device.Id, role);
            if (hr == 0)
            {
                WriteInfo("Set " + device.Flow + " " + role + " from " + source + ": " + device.Name);
                return;
            }

            WriteError("Failed to set " + device.Flow + " " + role + ". HRESULT: 0x" + hr.ToString("X8", CultureInfo.InvariantCulture) + ". Device: " + device.Name);
        }

        public void Dispose()
        {
            UnregisterNotifications();
        }
    }

    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly Action _onChanged;
        private readonly object _gate = new();
        private DateTimeOffset _lastRun = DateTimeOffset.MinValue;

        public NotificationClient(Action onChanged)
        {
            _onChanged = onChanged;
        }

        public int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DeviceState newState)
        {
            if (newState == DeviceState.Active)
            {
                RunDebounced();
            }

            return 0;
        }

        public int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId)
        {
            RunDebounced();
            return 0;
        }

        public int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId)
        {
            return 0;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId)
        {
            return 0;
        }

        public int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key)
        {
            RunDebounced();
            return 0;
        }

        private void RunDebounced()
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _lastRun < TimeSpan.FromSeconds(1))
                {
                    return;
                }

                _lastRun = now;
            }

            _onChanged();
        }
    }

    private static void InitializeConsole(Options options)
    {
        if (options.Background)
        {
            return;
        }

        EnsureConsole();
    }

    private static void EnsureConsole()
    {
        if (ConsoleAvailable)
        {
            return;
        }

        if (NativeMethods.AttachConsole(AttachParentProcess))
        {
            ConsoleAvailable = true;
            RefreshConsoleStreams();
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorAccessDenied)
        {
            ConsoleAvailable = true;
            RefreshConsoleStreams();
            return;
        }

        if (NativeMethods.AllocConsole())
        {
            ConsoleAvailable = true;
            RefreshConsoleStreams();
        }
    }

    private static void RefreshConsoleStreams()
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    private static bool TryReplaceExistingInstance(Mutex instanceMutex)
    {
        using var currentProcess = Process.GetCurrentProcess();
        var currentProcessPath = GetProcessPath(currentProcess);
        var replaceableProcesses = FindReplaceableProcesses(currentProcess.Id, currentProcessPath);

        try
        {
            if (replaceableProcesses.Count == 0)
            {
                WriteInfo("Another instance is already running. Waiting for it to exit so this copy can take over.");
            }

            foreach (var process in replaceableProcesses)
            {
                if (!TryTerminateProcess(process))
                {
                    return false;
                }
            }

            var deadline = DateTimeOffset.UtcNow + ReplaceExistingTimeout;
            foreach (var process in replaceableProcesses)
            {
                if (!WaitForProcessExit(process, deadline))
                {
                    return false;
                }
            }

            if (WaitForMutexOwnership(instanceMutex, deadline))
            {
                WriteInfo("Existing switcher instance replaced.");
                return true;
            }

            WriteError("Timed out waiting for previous switcher instance to release single-instance lock.");
            return false;
        }
        finally
        {
            foreach (var process in replaceableProcesses)
            {
                process.Dispose();
            }
        }
    }

    private static List<Process> FindReplaceableProcesses(int currentProcessId, string currentProcessPath)
    {
        var matches = new List<Process>();
        var seenProcessIds = new HashSet<int>();

        if (string.IsNullOrWhiteSpace(currentProcessPath))
        {
            return matches;
        }

        var currentProcessName = Path.GetFileNameWithoutExtension(currentProcessPath);
        if (string.IsNullOrWhiteSpace(currentProcessName))
        {
            return matches;
        }

        foreach (var process in Process.GetProcessesByName(currentProcessName))
        {
            if (process.Id == currentProcessId || !seenProcessIds.Add(process.Id))
            {
                process.Dispose();
                continue;
            }

            var processPath = GetProcessPath(process);
            if (!string.Equals(processPath, currentProcessPath, StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                continue;
            }

            matches.Add(process);
        }

        return matches;
    }

    private static bool TryTerminateProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            WriteInfo("Stopping running switcher instance PID " + process.Id.ToString(CultureInfo.InvariantCulture) + ".");
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex)
        {
            WriteError("Failed to stop running switcher instance PID " + process.Id.ToString(CultureInfo.InvariantCulture) + ".", ex);
            return false;
        }
    }

    private static bool WaitForProcessExit(Process process, DateTimeOffset deadline)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            if (process.WaitForExit((int)Math.Ceiling(remaining.TotalMilliseconds)))
            {
                return true;
            }

            WriteError("Timed out waiting for switcher instance PID " + process.Id.ToString(CultureInfo.InvariantCulture) + " to exit.");
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception ex)
        {
            WriteError("Failed while waiting for switcher instance PID " + process.Id.ToString(CultureInfo.InvariantCulture) + ".", ex);
            return false;
        }
    }

    private static bool WaitForMutexOwnership(Mutex instanceMutex, DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            return instanceMutex.WaitOne(remaining);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static string GetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void PrintHelp()
    {
        EnsureConsole();
        Console.WriteLine("Usage: AutoDefaultToHeadset.exe [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --install               Select devices, create Startup shortcut, and launch.");
        Console.WriteLine("  --list-devices          List output/input endpoint names and ids, then exit.");
        Console.WriteLine("  --match <text>          Match same text for output and input.");
        Console.WriteLine("  --render-match <text>   Match active output device by friendly-name substring.");
        Console.WriteLine("  --capture-match <text>  Match active input device by friendly-name substring.");
        Console.WriteLine("  --id <id>               Use same exact endpoint id for output and input.");
        Console.WriteLine("  --render-id <id>        Use exact output endpoint id.");
        Console.WriteLine("  --capture-id <id>       Use exact input endpoint id.");
        Console.WriteLine("  --background            Run without opening a console window.");
        Console.WriteLine("  --replace-existing      Replace a running switcher instance (default).");
        Console.WriteLine("  --exit-if-running       Exit instead of replacing an existing switcher instance.");
        Console.WriteLine("  --help                  Show this help.");
    }

    private static void WriteInfo(string message)
    {
        WriteLog("INFO", message);
    }

    private static void WriteError(string message)
    {
        WriteLog("ERROR", message);
    }

    private static void WriteError(string message, Exception ex)
    {
        WriteLog("ERROR", message + " " + ex.GetType().Name + ": " + ex.Message);
    }

    private static void ShowFatalError(string message, Exception ex)
    {
        var text = string.Format(
            CultureInfo.InvariantCulture,
            "{0}\r\n\r\n{1}: {2}\r\nHResult: 0x{3:X8}\r\n\r\n{4}",
            message,
            ex.GetType().FullName,
            ex.Message,
            ex.HResult,
            ex.StackTrace);

        WriteLog("ERROR", text);

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var dialog = new FatalErrorForm(text);
            dialog.ShowDialog();
            return;
        }
        catch
        {
        }

        NativeMethods.MessageBoxW(IntPtr.Zero, text, "AutoDefaultToHeadset fatal error", 0x00000010u);
    }

    private static void WriteLog(string level, string message)
    {
        var line = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}", DateTime.Now, level, message);

        lock (LogLock)
        {
            try
            {
            }
            catch
            {
            }
        }

        try
        {
            if (ConsoleAvailable)
            {
                Console.WriteLine(line);
            }
        }
        catch
        {
        }
    }
}

internal sealed unsafe class PropVariantScope : IDisposable
{
    public PropVariant* Value { get; }

    public PropVariantScope()
    {
        Value = (PropVariant*)NativeMemory.AllocZeroed((nuint)sizeof(PropVariant));
    }

    public void Dispose()
    {
        if (Value != null)
        {
            NativeMethods.PropVariantClear((IntPtr)Value);
            NativeMemory.Free(Value);
        }
    }
}

internal sealed record AudioDevice(string Id, string Name, DeviceState State, EDataFlow Flow);

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public IntPtr Hwnd;
    public uint Message;
    public UIntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public Point Pt;
    public uint LPrivate;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public PropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

internal static class PropertyKeys
{
    public static readonly PropertyKey DeviceFriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort ValueType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public IntPtr PointerValue;
}

[Flags]
internal enum DeviceState : uint
{
    Active = 0x00000001,
    Disabled = 0x00000002,
    NotPresent = 0x00000004,
    Unplugged = 0x00000008,
    All = 0x0000000F
}

internal enum EDataFlow
{
    eRender,
    eCapture,
    eAll
}

internal enum ERole
{
    eConsole,
    eMultimedia,
    eCommunications
}

internal enum StorageAccessMode
{
    Read,
    Write,
    ReadWrite
}

internal static class ComIds
{
    public static readonly Guid MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid PolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr interfacePointer);

    [PreserveSig]
    int OpenPropertyStore(StorageAccessMode accessMode, out IPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out DeviceState state);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint propertyCount);

    [PreserveSig]
    int GetAt(uint propertyIndex, out PropertyKey key);

    [PreserveSig]
    int GetValue(PropertyKey key, PropVariant* value);

    [PreserveSig]
    int SetValue(PropertyKey key, PropVariant* value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DeviceState newState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key);
}

[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat();

    [PreserveSig]
    int GetDeviceFormat();

    [PreserveSig]
    int ResetDeviceFormat();

    [PreserveSig]
    int SetDeviceFormat();

    [PreserveSig]
    int GetProcessingPeriod();

    [PreserveSig]
    int SetProcessingPeriod();

    [PreserveSig]
    int GetShareMode();

    [PreserveSig]
    int SetShareMode();

    [PreserveSig]
    int GetPropertyValue();

    [PreserveSig]
    int SetPropertyValue();

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

    [PreserveSig]
    int SetEndpointVisibility();
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllocConsole();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out Msg message, IntPtr hWnd, uint messageFilterMin, uint messageFilterMax, uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetMessage(out Msg message, IntPtr hWnd, uint messageFilterMin, uint messageFilterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(IntPtr propVariant);
}

internal sealed class FatalErrorForm : Form
{
    public FatalErrorForm(string text)
    {
        Text = "AutoDefaultToHeadset fatal error";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 900;
        Height = 600;
        TopMost = true;

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Text = text
        };

        var copy = new Button
        {
            Text = "Copy",
            Dock = DockStyle.Right,
            Width = 120
        };
        copy.Click += (_, _) => Clipboard.SetText(text);

        var close = new Button
        {
            Text = "Close",
            Dock = DockStyle.Right,
            Width = 120
        };
        close.Click += (_, _) => Close();

        var buttons = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(12)
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(copy);

        Controls.Add(box);
        Controls.Add(buttons);
    }
}
