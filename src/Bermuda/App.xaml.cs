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
        if (_grpcHost is not null) StartNativeApp(config);

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
    private System.Diagnostics.Process? _nativeApp;

    /// <summary>
    /// Starts the sample native FDC3 app that ships in the same package (Bermuda.NativeApp.exe next to this exe),
    /// so it is running whenever Bermuda is, however Bermuda was started and whether or not Windows has run the
    /// app's own startup task yet. It allows a single copy per user, so a second start is harmless. Not found
    /// (development: Aspire runs it) or NATIVE_APP_AUTOSTART=false: nothing happens.
    /// </summary>
    private void StartNativeApp(IConfiguration config)
    {
        if (bool.TryParse(config["NATIVE_APP_AUTOSTART"], out var enabled) && !enabled) return;
        var exe = Path.Combine(AppContext.BaseDirectory, "Bermuda.NativeApp.exe");
        if (!File.Exists(exe)) return;
        try
        {
            _nativeApp = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Native FDC3 app failed to start: {ex}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Close the side app we started. If it was already running (so our copy exited at once because it
        // allows one per user), or someone else started it, that handle has exited and nothing is killed.
        try
        {
            if (_nativeApp is { HasExited: false }) _nativeApp.Kill();
            _nativeApp?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Native FDC3 app did not stop: {ex.Message}");
        }

        if (_grpcHost is { } grpcHost)
        {
            Task.Run(() => grpcHost.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(6));
        }
        NotificationService.Clear();
        base.OnExit(e);
    }
}
