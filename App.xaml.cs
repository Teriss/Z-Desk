using System.Threading;
using System.Windows;
using ZDesk.Services;

namespace ZDesk;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = "Local\\ZDesk.SingleInstance.v1";
    private const string ActivationEventName = "Local\\ZDesk.Activate.v1";
    private const string ExitEventName = "Local\\ZDesk.Exit.v1";
    private const string CreateEmptyLayoutEventName = "Local\\ZDesk.CreateEmptyLayout.v1";
    private const string CreateFolderLayoutEventName = "Local\\ZDesk.CreateFolderLayout.v1";

    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitWait;
    private readonly List<EventWaitHandle> _createLayoutEvents = [];
    private readonly List<RegisteredWaitHandle> _createLayoutWaits = [];
    private readonly RecoveryService _recoveryService = new();
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (DesktopIconVisibilityService.TryRunWatchdog(e.Args))
        {
            Environment.Exit(0);
        }

        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        _ownsInstanceMutex = createdNew;
        if (!createdNew)
        {
            SignalExistingInstance(e.Args);
            Environment.Exit(0);
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogService.Error("UI thread unhandled exception", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogService.Error("AppDomain unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogService.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        _recoveryService.MarkSessionStarted();
        _mainWindow = new MainWindow(_recoveryService);
        MainWindow = _mainWindow;
        StartActivationListener();
        DesktopContextMenuRegistrationService.Register(Environment.ProcessPath);
        var createKind = GetCreateLayoutKind(e.Args);
        if (createKind is not null)
            _mainWindow.Loaded += (_, _) => _ = Dispatcher.BeginInvoke(() => _mainWindow.CreateLayoutFromDesktopMenu(createKind));
        _mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null);
        _activationEvent?.Dispose();
        _exitWait?.Unregister(null);
        _exitEvent?.Dispose();
        foreach (var wait in _createLayoutWaits) wait.Unregister(null);
        foreach (var handle in _createLayoutEvents) handle.Dispose();
        DesktopContextMenuRegistrationService.Unregister();
        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void StartActivationListener()
    {
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    _ = Dispatcher.BeginInvoke(() => _mainWindow?.ActivateFromSecondInstance());
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(
            _exitEvent,
            (_, timedOut) =>
            {
                if (!timedOut) _ = Dispatcher.BeginInvoke(() => _mainWindow?.RequestApplicationExit());
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        RegisterCreateLayoutListener(CreateEmptyLayoutEventName, "empty");
        RegisterCreateLayoutListener(CreateFolderLayoutEventName, "folder");
    }

    private void RegisterCreateLayoutListener(string eventName, string kind)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        _createLayoutEvents.Add(handle);
        _createLayoutWaits.Add(ThreadPool.RegisterWaitForSingleObject(
            handle,
            (_, timedOut) =>
            {
                if (timedOut) return;
                LogService.Info($"Desktop menu create event received | kind={kind}");
                _ = Dispatcher.BeginInvoke(() => _mainWindow?.CreateLayoutFromDesktopMenu(kind));
            },
            null, Timeout.Infinite, executeOnlyOnce: false));
    }

    private static void SignalExistingInstance(string[] args)
    {
        var exit = args.Contains("--exit", StringComparer.OrdinalIgnoreCase);
        var createKind = GetCreateLayoutKind(args);
        try
        {
            var createEvent = createKind switch
            {
                "folder" => CreateFolderLayoutEventName,
                "empty" => CreateEmptyLayoutEventName,
                _ => ActivationEventName
            };
            using var signal = EventWaitHandle.OpenExisting(exit ? ExitEventName : createEvent);
            LogService.Info($"Signaling existing instance | exit={exit} | createKind={createKind ?? "none"}");
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException ex)
        {
            LogService.Warning(exit ? "Could not stop the existing Z-Desk instance" : "Could not signal the existing Z-Desk instance", ex);
        }
    }

    private static string? GetCreateLayoutKind(IEnumerable<string> args)
    {
        var kind = args.FirstOrDefault(arg => arg.StartsWith("--create-layout=", StringComparison.OrdinalIgnoreCase))?
            .Split('=', 2)[1].ToLowerInvariant();
        return kind is null ? null : kind == "folder" ? "folder" : "empty";
    }
}
