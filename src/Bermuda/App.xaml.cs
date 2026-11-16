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

        var window = new MainWindow(config, fdc3, MainAppId);
        window.Show();

        // The launch itself may ask for an app on top of the main window (e.g. a jump list task).
        HandleLaunchArguments(Environment.GetCommandLineArgs().Skip(1).ToArray(), activateMainOtherwise: false);
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
                HandleLaunchArguments(launch.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), activateMainOtherwise: true);
                break;
            default:
                WindowRegistry.ActivateMain();
                break;
        }
    });

    /// <summary>
    /// <c>--open &lt;appId&gt;</c> opens that App Directory app's window; anything else just brings the main window
    /// forward. Used for the first launch and for activations forwarded from a second launch.
    /// </summary>
    private void HandleLaunchArguments(string[] args, bool activateMainOtherwise)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, "--open", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || _directory.Find(args[index + 1]) is not { } app || _launcher is null)
        {
            if (activateMainOtherwise) WindowRegistry.ActivateMain();
            return;
        }

        try
        {
            _launcher.Open(app);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not open {app.AppId}: {ex.Message}");
            if (activateMainOtherwise) WindowRegistry.ActivateMain();
        }
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
        _nativeApps?.Dispose();

        if (_grpcHost is { } grpcHost)
        {
            Task.Run(() => grpcHost.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(6));
        }
        if (_isPrimary) NotificationService.Clear();
        base.OnExit(e);
    }
}
