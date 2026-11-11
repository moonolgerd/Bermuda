using System.Windows;
using Microsoft.Extensions.Configuration;

namespace Bermuda;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        NotificationService.Initialize();
        var window = new MainWindow(config);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        NotificationService.Clear();
        base.OnExit(e);
    }
}


