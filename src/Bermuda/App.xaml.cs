using System.IO;
using System.Windows;
using Bermuda.Fdc3;
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

        var directory = AppDirectory.Load(Path.Combine(AppContext.BaseDirectory, "fdc3-appd.json"));
        var launcher = new WindowAppLauncher(config);
        var fdc3 = new DesktopAgent(directory, launcher);
        launcher.Agent = fdc3;

        var window = new MainWindow(config, fdc3, MainAppId);
        window.Show();
    }
}
