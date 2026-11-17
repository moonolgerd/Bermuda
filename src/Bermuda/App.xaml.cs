using System.IO;
using System.Windows;
using Bermuda.Fdc3;
using Bermuda.Fdc3.Grpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Windows.ApplicationModel.Activation;

namespace Bermuda;

public partial class App : Application
{
    /// <summary>The App Directory appId the main window's page runs as (see fdc3-appd.json).</summary>
    public const string MainAppId = "bermuda-incidents";

    private AppDirectory _directory = AppDirectory.Empty;
    private WindowAppLauncher? _launcher;

    // False for a second launch that handed over to the running Bermuda: it must not tear down that one's toasts.
    private bool _isPrimary;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Closing the last window no longer ends the app by itself: WindowRegistry decides (and the tray
        // icon will keep the app alive with no window).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // One Bermuda per user: a second launch (jump list task, startup relaunch, toast click) hands its
        // activation to the running one and exits before it opens anything.
        if (!BecomeSingleInstance(config)) return;
        _isPrimary = true;

        NotificationService.Initialize();
        var directory = _directory = AppDirectory.Load(Path.Combine(AppContext.BaseDirectory, "fdc3-appd.json"));
        var launcher = _launcher = new WindowAppLauncher(config);
        var fdc3 = new DesktopAgent(directory, launcher);
        launcher.Agent = fdc3;

        StartGrpcHost(config, fdc3);
        if (_grpcHost is not null)
        {
            _nativeApps = new NativeAppHost(_grpcHost.PipeName);
            launcher.NativeApps = _nativeApps;
            StartNativeApps(config, directory);
        }

        // Tray icon (TRAY_ENABLED, default true): the app then lives on with no window, so it also keeps
        // running when the last window is hidden.
        WindowRegistry.TrayEnabled = !(bool.TryParse(config["TRAY_ENABLED"], out var trayEnabled) && !trayEnabled);
        if (WindowRegistry.TrayEnabled)
        {
            try
            {
                _tray = new TrayService(() => { if (directory.Find(MainAppId) is { } main) launcher.Open(main); });
                // Logging off or shutting down must not be stopped by the main window hiding itself.
                SessionEnding += (_, _) => WindowRegistry.MarkQuitting();
            }
            catch (Exception ex)
            {
                // No tray means no way to bring a hidden window back, so fall back to closing normally.
                System.Diagnostics.Debug.WriteLine($"Tray icon failed to start: {ex}");
                WindowRegistry.TrayEnabled = false;
            }
        }

        var window = new MainWindow(config, fdc3, MainAppId);
        window.ShowActivated = !StartsInBackground();
        window.Show();
        // The jump list is a set of shortcuts on this window's taskbar button, so the window
        // has to exist first. Tasks relaunch Bermuda.exe; a running instance receives them below.
        JumpListService.Register(directory);
        // Started at sign-in (or with --background): sit in the tray, no window until the user asks for it.
        if (!window.ShowActivated) window.HideWindow();

        // The launch itself may ask for an app on top of the main window (e.g. a jump list task).
        HandleLaunchArguments(CurrentLaunchArguments(), activateMainOtherwise: false);
    }

    private TrayService? _tray;

    /// <summary>
    /// True when this launch should start hidden in the tray: the MSIX startup task (sign-in) or --background.
    /// Never without the tray, since there would be no way to bring the window back.
    /// </summary>
    private static bool StartsInBackground()
    {
        if (!WindowRegistry.TrayEnabled) return false;
        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase)))
            return true;
        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Registers this process as the user's Bermuda, or — when one is already running — forwards this launch's
    /// activation to it, shuts this process down and returns false. SINGLE_INSTANCE=false turns it off;
    /// SINGLE_INSTANCE_KEY isolates a dev/test run (the Aspire host sets one) from an installed Bermuda.
    /// </summary>
    private bool BecomeSingleInstance(IConfiguration config)
    {
        if (bool.TryParse(config["SINGLE_INSTANCE"], out var enabled) && !enabled) return true;

        var key = config["SINGLE_INSTANCE_KEY"] is { Length: > 0 } custom ? custom : "Bermuda";
        var instance = AppInstance.FindOrRegisterForKey(key);
        if (instance.IsCurrent)
        {
            instance.Activated += OnActivatedByAnotherLaunch;
            return true;
        }

        try
        {
            // Off the UI thread: redirecting waits on the other process, and an STA that blocks can deadlock.
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not hand this launch to the running Bermuda: {ex}");
        }

        Shutdown();
        return false;
    }

    // Another Bermuda process was launched and handed its activation here (on a background thread).
    private void OnActivatedByAnotherLaunch(object? sender, AppActivationArguments args) => Dispatcher.BeginInvoke(() =>
    {
        switch (args.Data)
        {
            case AppNotificationActivatedEventArgs toast:
                NotificationService.HandleForwarded(toast);
                break;
            case ILaunchActivatedEventArgs launch:
                // Packaged: arguments are only "--open <appId>" / "--new-window".
                // Unpackaged: the same string with the exe path in front. Parse accepts both.
                HandleLaunchArguments(LaunchArguments.Parse(launch.Arguments), activateMainOtherwise: true);
                break;
            default:
                WindowRegistry.ActivateMain();
                break;
        }
    });

    /// <summary>
    /// Command-line arguments, or — when those carry no task — the shell activation arguments.
    /// An unpackaged jump list puts <c>--open</c> on the command line. A packaged one does not:
    /// Windows launches the manifest's <c>Bermuda.exe</c> by package identity and delivers the task
    /// string only through <see cref="ILaunchActivatedEventArgs.Arguments"/>.
    /// </summary>
    private static string[] CurrentLaunchArguments()
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (LaunchArguments.HasTask(commandLine)) return commandLine;

        try
        {
            if (AppInstance.GetCurrent().GetActivatedEventArgs().Data is ILaunchActivatedEventArgs launch)
            {
                var activation = LaunchArguments.Parse(launch.Arguments);
                if (LaunchArguments.HasTask(activation)) return activation;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not read launch activation: {ex}");
        }

        return commandLine;
    }

    /// <summary>
    /// <c>--open &lt;appId&gt;</c> opens that App Directory app's window. <c>--new-window</c> opens another
    /// main window; the first launch's own window already counts, so it only does that when a second
    /// launch was forwarded here. Anything else just brings the main window forward.
    /// </summary>
    private void HandleLaunchArguments(string[] args, bool activateMainOtherwise)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, LaunchArguments.Open, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            if (index + 1 < args.Length && _directory.Find(args[index + 1]) is { } app && _launcher is not null)
            {
                try
                {
                    _launcher.Open(app);
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Could not open {app.AppId}: {ex.Message}");
                }
            }

            if (activateMainOtherwise) WindowRegistry.ActivateMain();
            return;
        }

        if (args.Any(a => string.Equals(a, LaunchArguments.NewWindow, StringComparison.OrdinalIgnoreCase)))
        {
            if (activateMainOtherwise) OpenNewWindow();
            return;
        }

        if (activateMainOtherwise) WindowRegistry.ActivateMain();
    }

    /// <summary>The jump list's "New window" task: another main window, same as the tray menu.</summary>
    private void OpenNewWindow()
    {
        if (_directory.Find(MainAppId) is { } app && _launcher is not null)
        {
            try
            {
                _launcher.Open(app);
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not open a new window: {ex.Message}");
            }
        }

        WindowRegistry.ActivateMain();
    }

    /// <summary>
    /// Lets native apps join the agent over gRPC on a named pipe. Config:
    /// FDC3_GRPC_ENABLED (default true) and FDC3_GRPC_PIPE (default per-user,
    /// see <see cref="Fdc3Pipe.DefaultName"/>). A failure to listen
    /// (e.g. the pipe name is taken) must not stop the app.
    /// </summary>
    private void StartGrpcHost(IConfiguration config, DesktopAgent agent)
    {
        if (bool.TryParse(config["FDC3_GRPC_ENABLED"], out var enabled) && !enabled) return;
        try
        {
            _grpcHost = Task.Run(() => Fdc3GrpcHost.StartAsync(agent, config["FDC3_GRPC_PIPE"])).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FDC3 gRPC pipe host failed to start: {ex}");
        }
    }

    private Fdc3GrpcHost? _grpcHost;
    private NativeAppHost? _nativeApps;

    /// <summary>
    /// Starts every native side app in the App Directory so they run whenever Bermuda does, however Bermuda was
    /// started. They ship in the same package. NATIVE_APP_AUTOSTART=false turns this off. An app that is down
    /// later is started again on demand, when fdc3.open or an intent needs it (see <see cref="WindowAppLauncher"/>).
    /// </summary>
    private void StartNativeApps(IConfiguration config, AppDirectory directory)
    {
        if (bool.TryParse(config["NATIVE_APP_AUTOSTART"], out var enabled) && !enabled) return;
        foreach (var app in directory.Apps.Where(a => NativeAppHost.ResolvePath(a) is not null))
        {
            try
            {
                _nativeApps!.Start(app);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Native FDC3 app {app.AppId} failed to start: {ex}");
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _nativeApps?.Dispose();

        if (_grpcHost is { } grpcHost)
        {
            Task.Run(() => grpcHost.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(6));
        }
        if (_isPrimary) NotificationService.Clear();
        base.OnExit(e);
    }
}
