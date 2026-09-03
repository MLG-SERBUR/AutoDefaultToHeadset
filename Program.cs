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
                WriteInfo("Args: " + string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));
                DumpSystemInfo();
                controller.DumpDiagnostics("startup");
                controller.ApplyDefaults("startup");
                controller.DumpDiagnostics("after startup apply");
                WriteInfo("Tip: This console is intentionally visible in --background so you can copy diagnostics. To hide, close this window and rely on Startup shortcut.");
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
        private readonly IPolicyConfig? _policyConfig;
        private readonly IPolicyConfigVista? _policyConfigVista;
        private readonly string _policySource;
        private readonly NotificationClient _notificationClient;
        private uint _eventThreadId;
        private bool _registered;

        public AudioController(Options options)
        {
            _options = options;
            _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.MMDeviceEnumerator, throwOnError: true)!)!;
            (_policyConfig, _policyConfigVista, _policySource) = CreatePolicyConfig();
            if (_policyConfig == null && _policyConfigVista == null)
            {
                WriteError("Failed to create PolicyConfig client. SetDefaultEndpoint will fail. HR lookup: try running as admin.");
            }
            else
            {
                WriteInfo("PolicyConfig client: " + _policySource);
            }
            _notificationClient = new NotificationClient(RequestEndpointApply);
        }

        private static (IPolicyConfig? primary, IPolicyConfigVista? vista, string source) CreatePolicyConfig()
        {
            // Try primary client (Win7-11) first, then Vista client as fallback.
            try
            {
                var pc = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.PolicyConfigClient, throwOnError: true)!)!;
                return (pc, null, "CPolicyConfigClient(870AF99C)+IPolicyConfig(F8679F50)");
            }
            catch (Exception ex)
            {
                WriteError("CPolicyConfigClient create failed: " + ex.Message);
            }

            try
            {
                var vista = (IPolicyConfigVista)Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.PolicyConfigVistaClient, throwOnError: true)!)!;
                return (null, vista, "CPolicyConfigVistaClient(294935CE)+IPolicyConfigVista(568B9108)");
            }
            catch (Exception ex)
            {
                WriteError("CPolicyConfigVistaClient create failed: " + ex.Message);
            }

            return (null, null, "none");
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
                // Log current defaults before attempt (helps diagnose comm vs default)
                LogCurrentDefaults("before " + source);

                // Retry loop: device may not be ready immediately after plug
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var render = FindBestDevice(EDataFlow.eRender, _options.RenderId, _options.RenderMatches);
                    var capture = FindBestDevice(EDataFlow.eCapture, _options.CaptureId, _options.CaptureMatches);

                    var didWork = false;

                    if (render != null)
                    {
                        WriteInfo("Matched render for " + source + " (attempt " + (attempt + 1) + "): " + render.Name + " [" + render.State + "] " + render.Id);
                        didWork |= SetDefaultForAllRoles(render, source);
                    }
                    else
                    {
                        WriteInfo("No active render device matched " + _options.RenderDescription + ". (attempt " + (attempt + 1) + ")");
                    }

                    if (capture != null)
                    {
                        WriteInfo("Matched capture for " + source + " (attempt " + (attempt + 1) + "): " + capture.Name + " [" + capture.State + "] " + capture.Id);
                        didWork |= SetDefaultForAllRoles(capture, source);
                    }
                    else
                    {
                        WriteInfo("No active capture device matched " + _options.CaptureDescription + ". (attempt " + (attempt + 1) + ")");
                    }

                    // verify after set
                    Thread.Sleep(250);
                    LogCurrentDefaults("after " + source + " attempt " + (attempt + 1));

                    // check if verification shows our device is now default
                    var renderOk = render == null || IsDefault(render.Id, EDataFlow.eRender, ERole.eConsole) || IsDefault(render.Id, EDataFlow.eRender, ERole.eMultimedia) || IsDefault(render.Id, EDataFlow.eRender, ERole.eCommunications);
                    var captureOk = capture == null || IsDefault(capture.Id, EDataFlow.eCapture, ERole.eConsole) || IsDefault(capture.Id, EDataFlow.eCapture, ERole.eCommunications);

                    if (didWork && renderOk && captureOk)
                    {
                        break;
                    }

                    if (attempt < 2)
                    {
                        WriteInfo("Retrying apply " + source + " in 800ms (renderOk=" + renderOk + " captureOk=" + captureOk + ")");
                        Thread.Sleep(800);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError("Failed to apply defaults from " + source + ".", ex);
            }
        }

        private void LogCurrentDefaults(string context)
        {
            foreach (var flow in new[] { EDataFlow.eRender, EDataFlow.eCapture })
            {
                foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
                {
                    var id = GetDefaultDeviceId(flow, role);
                    var name = id != null ? TryGetFriendlyNameById(id) : "(none)";
                    WriteInfo("Default " + context + ": " + flow + " " + role + " = " + (name ?? id ?? "(null)") + " [" + (id ?? "null") + "]");
                }
            }
        }

        private string? GetDefaultDeviceId(EDataFlow flow, ERole role)
        {
            try
            {
                var hr = _enumerator.GetDefaultAudioEndpoint(flow, role, out var device);
                if (hr != 0 || device == null)
                {
                    return null;
                }
                device.GetId(out var id);
                return id;
            }
            catch
            {
                return null;
            }
        }

        private bool IsDefault(string id, EDataFlow flow, ERole role)
        {
            var def = GetDefaultDeviceId(flow, role);
            return def != null && string.Equals(def, id, StringComparison.OrdinalIgnoreCase);
        }

        private string? TryGetFriendlyNameById(string id)
        {
            try
            {
                if (_enumerator.GetDevice(id, out var dev) != 0) return null;
                return GetFriendlyName(dev);
            }
            catch { return null; }
        }

        public void DumpDiagnostics(string context)
        {
            try
            {
                WriteInfo("=== Audio Diagnostics: " + context + " ===");
                foreach (var flow in new[] { EDataFlow.eRender, EDataFlow.eCapture })
                {
                    var label = flow == EDataFlow.eRender ? "Output" : "Input";
                    WriteInfo("-- " + label + " (" + flow + ") all states --");
                    var all = EnumerateDevices(flow, DeviceState.All);
                    if (all.Count == 0) WriteInfo("  (no devices)");
                    foreach (var d in all.OrderBy(x => x.State).ThenBy(x => x.Name))
                    {
                        var defaults = "";
                        foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
                        {
                            if (IsDefault(d.Id, flow, role)) defaults += " [DEFAULT " + role + "]";
                        }
                        if (defaults.Length == 0 && d.State != DeviceState.Active) defaults = "";
                        WriteInfo("  [" + d.State + "]" + defaults + " " + d.Name);
                        WriteInfo("       Id: " + d.Id);
                    }
                    // defaults per role summary
                    foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
                    {
                        var defId = GetDefaultDeviceId(flow, role);
                        var defName = defId != null ? TryGetFriendlyNameById(defId) ?? "(unnamed)" : "(none)";
                        WriteInfo("  Default " + flow + " " + role + ": " + defName + " [" + (defId ?? "null") + "]");
                    }
                }
                // show what would match
                var render = FindBestDevice(EDataFlow.eRender, _options.RenderId, _options.RenderMatches);
                var capture = FindBestDevice(EDataFlow.eCapture, _options.CaptureId, _options.CaptureMatches);
                WriteInfo("Match render: " + (render != null ? render.Name + " [" + render.State + "] " + render.Id : "NONE for " + _options.RenderDescription));
                WriteInfo("Match capture: " + (capture != null ? capture.Name + " [" + capture.State + "] " + capture.Id : "NONE for " + _options.CaptureDescription));
                // explicit check for exact IDs if supplied
                if (!string.IsNullOrWhiteSpace(_options.RenderId))
                {
                    var devId = _options.RenderId;
                    var exists = EnumerateDevices(EDataFlow.eRender, DeviceState.All).Any(x => string.Equals(x.Id, devId, StringComparison.OrdinalIgnoreCase));
                    var active = EnumerateDevices(EDataFlow.eRender, DeviceState.Active).Any(x => string.Equals(x.Id, devId, StringComparison.OrdinalIgnoreCase));
                    WriteInfo("RenderId " + devId + " exists(all)=" + exists + " active=" + active);
                }
                if (!string.IsNullOrWhiteSpace(_options.CaptureId))
                {
                    var devId = _options.CaptureId;
                    var exists = EnumerateDevices(EDataFlow.eCapture, DeviceState.All).Any(x => string.Equals(x.Id, devId, StringComparison.OrdinalIgnoreCase));
                    var active = EnumerateDevices(EDataFlow.eCapture, DeviceState.Active).Any(x => string.Equals(x.Id, devId, StringComparison.OrdinalIgnoreCase));
                    WriteInfo("CaptureId " + devId + " exists(all)=" + exists + " active=" + active);
                }
                WriteInfo("=== End Audio Diagnostics ===");
            }
            catch (Exception ex) { WriteError("DumpDiagnostics failed", ex); }
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
            Console.WriteLine("  1. Exact endpoint IDs (fragile across replug/BT repair)");
            Console.WriteLine("  2. Name contains (persistent)");
            Console.Write("Mode [2]: ");
            var mode = Console.ReadLine();

            string arguments;
            if (string.Equals(mode, "1", StringComparison.OrdinalIgnoreCase))
            {
                arguments = "--background --render-id " + Quote(render.Id) + " --capture-id " + Quote(capture.Id);
            }
            else
            {
                var renderMatch = PromptForText("Output name contains", SuggestMatch(render.Name));
                var captureMatch = PromptForText("Input name contains", SuggestMatch(capture.Name));
                arguments = "--background --render-match " + Quote(renderMatch) + " --capture-match " + Quote(captureMatch);
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
            // exact mode: return full friendly name, not substring inside ()
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
                var marker = "";
                foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
                {
                    if (IsDefault(device.Id, flow, role))
                    {
                        marker += " [DEFAULT:" + role + "]";
                    }
                }
                Console.WriteLine("  State: " + device.State + marker);
                Console.WriteLine("  Name:  " + device.Name);
                Console.WriteLine("  Id:    " + device.Id);
                Console.WriteLine();
            }

            // also dump current defaults per role
            Console.WriteLine("Current defaults (" + label + "):");
            foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
            {
                var defId = GetDefaultDeviceId(flow, role);
                Console.WriteLine("  " + role + ": " + (defId ?? "(none)") + " -> " + (defId != null ? TryGetFriendlyNameById(defId) : ""));
            }
            Console.WriteLine();
        }

        private AudioDevice? FindBestDevice(EDataFlow flow, string? exactId, IReadOnlyList<string> matches)
        {
            var devices = EnumerateDevices(flow, DeviceState.Active);

            if (!string.IsNullOrWhiteSpace(exactId))
            {
                var exact = devices.FirstOrDefault(device => string.Equals(device.Id, exactId, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;

                var staleExists = EnumerateDevices(flow, DeviceState.All).Any(d => string.Equals(d.Id, exactId, StringComparison.OrdinalIgnoreCase));
                if (!staleExists)
                {
                    WriteInfo("Exact " + flow + " Id not found even among All (stale) " + exactId + " -> falling back to exact name match " + string.Join(",", matches));
                }
                else
                {
                    WriteInfo("Exact " + flow + " Id not active " + exactId + " -> falling back to exact name match");
                }
            }

            // exact-only on friendly name (no Contains)
            return devices
                .Where(device => matches.Any(match => device.Name.Equals(match, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(device => Score(device, matches))
                .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static int Score(AudioDevice device, IReadOnlyList<string> matches)
        {
            // exact-only mode: only exact equality scores, headset bonus kept for tie-break
            var score = 0;

            foreach (var match in matches)
            {
                if (device.Name.Equals(match, StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
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

        private bool SetDefaultForAllRoles(AudioDevice device, string source)
        {
            var okConsole = SetDefault(device, ERole.eConsole, source);
            var okMultimedia = SetDefault(device, ERole.eMultimedia, source);
            var okComm = SetDefault(device, ERole.eCommunications, source);
            return okConsole || okMultimedia || okComm;
        }

        private bool SetDefault(AudioDevice device, ERole role, string source)
        {
            int hr;
            string which;
            if (_policyConfig != null)
            {
                hr = _policyConfig.SetDefaultEndpoint(device.Id, role);
                which = "IPolicyConfig";
            }
            else if (_policyConfigVista != null)
            {
                hr = _policyConfigVista.SetDefaultEndpoint(device.Id, role);
                which = "IPolicyConfigVista";
            }
            else
            {
                WriteError("No PolicyConfig available to set " + device.Flow + " " + role + ". Device: " + device.Name);
                return false;
            }

            // Fallback: if primary failed, try the other client once
            if (hr != 0 && _policyConfig != null && _policyConfigVista != null)
            {
                // already tried primary; try vista as second attempt (should not happen since we only have one)
            }
            else if (hr != 0 && _policyConfig == null && _policyConfigVista != null)
            {
                // no fallback needed
            }

            if (hr == 0)
            {
                // verify
                Thread.Sleep(80);
                var isDef = IsDefault(device.Id, device.Flow, role);
                WriteInfo("Set " + device.Flow + " " + role + " from " + source + " via " + which + ": " + device.Name + " HR=0x00000000 verify=" + (isDef ? "OK" : "MISMATCH"));
                if (!isDef)
                {
                    var cur = GetDefaultDeviceId(device.Flow, role);
                    WriteError("Verify failed after SetDefaultEndpoint " + role + ". Expected " + device.Id + " got " + (cur ?? "null") + ". Try running as admin. HR=0x" + hr.ToString("X8", CultureInfo.InvariantCulture));
                }
                return isDef;
            }

            var msg = GetHrMessage(hr);
            WriteError("Failed to set " + device.Flow + " " + role + " via " + which + ". HRESULT: 0x" + hr.ToString("X8", CultureInfo.InvariantCulture) + " (" + msg + "). Device: " + device.Name + " Id: " + device.Id);
            if ((uint)hr == 0x80070005)
            {
                WriteError("Access denied (0x80070005) - try Run as Administrator or Task Scheduler with /RL HIGHEST.");
            }
            if ((uint)hr == 0x80070490) // ERROR_NOT_FOUND
            {
                WriteError("Device not found - device may not be ready yet or Id changed.");
            }
            return false;
        }

        private static string GetHrMessage(int hr)
        {
            try
            {
                var ex = Marshal.GetExceptionForHR(hr);
                return ex?.Message ?? "unknown";
            }
            catch { return "unknown"; }
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
        private CancellationTokenSource? _pendingCts;
        // Coalesce burst events (headset often fires render + capture within ~300ms)
        // Leading edge fires immediately, trailing edge fires 700ms after last burst event
        private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

        public NotificationClient(Action onChanged)
        {
            _onChanged = onChanged;
        }

        public int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DeviceState newState)
        {
            WriteInfo("Notify OnDeviceStateChanged: " + newState + " id=" + deviceId);
            if (newState == DeviceState.Active)
            {
                RunDebounced();
            }
            return 0;
        }

        public int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId)
        {
            WriteInfo("Notify OnDeviceAdded: id=" + deviceId);
            RunDebounced();
            return 0;
        }

        public int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId)
        {
            WriteInfo("Notify OnDeviceRemoved: id=" + deviceId);
            return 0;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId)
        {
            // ignore - avoids feedback loop when we just set default
            return 0;
        }

        public int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key)
        {
            WriteInfo("Notify OnPropertyValueChanged: id=" + deviceId);
            RunDebounced();
            return 0;
        }

        private void RunDebounced()
        {
            bool shouldFireImmediately = false;
            CancellationTokenSource? ctsToCancel = null;
            CancellationTokenSource? newCts = null;

            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _lastRun >= MinInterval)
                {
                    // leading edge
                    _lastRun = now;
                    shouldFireImmediately = true;
                }

                // always schedule trailing edge to catch burst second device
                ctsToCancel = _pendingCts;
                newCts = new CancellationTokenSource();
                _pendingCts = newCts;
            }

            try { ctsToCancel?.Cancel(); } catch { }
            try { ctsToCancel?.Dispose(); } catch { }

            if (shouldFireImmediately)
            {
                try { _onChanged(); } catch (Exception ex) { WriteError("RunDebounced immediate failed", ex); }
            }

            // trailing edge after CoalesceWindow - ensures second device in burst gets applied
            var token = newCts.Token;
            Task.Delay(CoalesceWindow, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                lock (_gate)
                {
                    // if another burst already updated _lastRun recently, we still fire trailing to ensure both render+capture handled
                }
                try { _onChanged(); } catch (Exception ex) { WriteError("RunDebounced trailing failed", ex); }
            }, TaskScheduler.Default);
        }
    }

    private static void InitializeConsole(Options options)
    {
        // Background flag originally hid console, but window still appears on this system
        // Keep console visible so diagnostics are paste-able. Always ensure console.
        EnsureConsole();
        if (options.Background)
        {
            WriteInfo("Background flag set - console kept visible for diagnostics (window was supposed to be hidden)");
        }
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

    private static void DumpSystemInfo()
    {
        try
        {
            WriteInfo("=== System Info ===");
            WriteInfo("OSVersion: " + Environment.OSVersion.ToString());
            try { WriteInfo("Runtime OSDescription: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription); } catch { }
            try { WriteInfo("64-bit OS: " + Environment.Is64BitOperatingSystem + " 64-bit Proc: " + Environment.Is64BitProcess); } catch { }
            try
            {
                var winVer = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null);
                var build = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild", null);
                var ubr = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null);
                WriteInfo("DisplayVersion: " + (winVer ?? "n/a") + " Build: " + (build ?? "n/a") + " UBR: " + (ubr ?? "n/a"));
            }
            catch (Exception ex) { WriteInfo("Registry read failed: " + ex.Message); }
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var p = new System.Security.Principal.WindowsPrincipal(id);
                var isAdmin = p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                WriteInfo("Admin: " + isAdmin + " User: " + id.Name);
            }
            catch (Exception ex) { WriteInfo("Admin check failed: " + ex.Message); }
            WriteInfo("Exe: " + (Environment.ProcessPath ?? "n/a"));
            WriteInfo("Startup shortcut: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "AutoDefaultToHeadset.lnk"));
            try
            {
                var startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "AutoDefaultToHeadset.lnk");
                if (File.Exists(startup)) WriteInfo("Startup .lnk exists: " + startup);
                else WriteInfo("Startup .lnk NOT found");
            }
            catch { }
            WriteInfo("=== End System Info ===");
        }
        catch (Exception ex) { WriteError("DumpSystemInfo failed", ex); }
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
                // keep lock for console ordering; file logging could be added here
                System.Diagnostics.Debug.WriteLine(line);
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
            else
            {
                // background mode: still try to write to attached parent if any
                try { Console.WriteLine(line); } catch { }
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
    public static readonly Guid PolicyConfigVistaClient = new("294935CE-F637-4E7C-A41B-AB255460B862");
    public static readonly Guid IPolicyConfig = new("F8679F50-850A-41CF-9C72-430F290290C8");
    public static readonly Guid IPolicyConfigVista = new("568B9108-44BF-40B4-9006-86AFE5B5A620");
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
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);

    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr ppFormat);

    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);

    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr MixFormat);

    [PreserveSig]
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bVisible);
}

[ComImport]
[Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigVista
{
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);

    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr ppFormat);

    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr MixFormat);

    [PreserveSig]
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bVisible);
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
