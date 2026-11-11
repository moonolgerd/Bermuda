using System.Windows;
using Bermuda.Fdc3;
using Microsoft.Extensions.Configuration;

namespace Bermuda;

/// <summary>
/// Launches FDC3 App Directory apps (for <c>fdc3.open</c> and for intents
/// raised at an app that isn't running) as new host windows. Relative AppD
/// URLs resolve against WEB_URL, so the directory works unchanged against the
/// Vite dev server and a deployed frontend.
/// </summary>
public sealed class WindowAppLauncher(IConfiguration configuration) : IFdc3AppLauncher
{
    public DesktopAgent? Agent { get; set; }

    public void Launch(AppDefinition app, string instanceId)
    {
        if (Agent is null) throw new InvalidOperationException("Launcher not attached to a desktop agent.");
        if (app.Type != "web" || app.Details?.Url is not { Length: > 0 } url)
            throw new InvalidOperationException($"App '{app.AppId}' has no web URL to launch.");

        var baseUrl = new Uri(configuration["WEB_URL"] ?? "http://localhost:5555");
        var target = new Uri(baseUrl, url);
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"App '{app.AppId}' URL must be http(s).");

        Application.Current.Dispatcher.Invoke(() =>
        {
            var window = new MainWindow(configuration, Agent, app.AppId, instanceId, target.ToString())
            {
                Title = app.Title ?? app.Name ?? app.AppId,
            };
            window.Show();
        });
    }
}
