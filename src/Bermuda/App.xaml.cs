using System.IO;
using System.Windows;
using Bermuda.Fdc3;
using Bermuda.Fdc3.Grpc;
using Microsoft.Extensions.Configuration;

namespace Bermuda;

public partial class App : Application
{
    /// <summary>The App Directory appId the main window's page runs as (see fdc3-appd.json).</summary>
    public const string MainAppId = "bermuda-incidents";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        NotificationService.Initialize();
        var directory = AppDirectory.Load(Path.Combine(AppContext.BaseDirectory, "fdc3-appd.json"));
        var launcher = new WindowAppLauncher(config);
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
        NotificationService.Clear();
        base.OnExit(e);
    }
}
