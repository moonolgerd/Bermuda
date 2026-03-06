using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Configuration;
using Microsoft.Web.WebView2.Core;

namespace Bermuda;

public partial class MainWindow : Window
{
    private readonly HostBridge _bridge;
    private readonly string _webUrl;
    private readonly int _cdpPort;

    public MainWindow(IConfiguration configuration)
    {
        InitializeComponent();
        _bridge = new HostBridge();
        _webUrl = configuration["WEB_URL"] ?? "http://localhost:5555";
        _cdpPort = configuration.GetValue("CDP_PORT", 0);
        Loaded += OnLoaded;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int attrValue, int attrSize);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Dark Win32 title bar so caption matches the dark WebView2 content
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));

        if (_cdpPort > 0)
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = $"--remote-debugging-port={_cdpPort}"
            };
            var env = await CoreWebView2Environment.CreateAsync(options: options);
            await _webView.EnsureCoreWebView2Async(env);
        }
        else
        {
            await _webView.EnsureCoreWebView2Async();
        }

#if !DEBUG
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif

        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _webView.CoreWebView2.Navigate(_webUrl);
    }

    private async void OnWebMessageReceived(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        IpcRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<IpcRequest>(e.WebMessageAsJson);
        }
        catch
        {
            return;
        }

        if (req is null) return;

        var result = await _bridge.HandleAsync(req.Command, req.Payload);
        var response = JsonSerializer.Serialize(new { req.Id, result });

        Dispatcher.Invoke(() => _webView.CoreWebView2.PostWebMessageAsJson(response));
    }

    public void PushEvent(object eventPayload)
    {
        var json = JsonSerializer.Serialize(eventPayload);
        Dispatcher.Invoke(() => _webView.CoreWebView2.PostWebMessageAsJson(json));
    }
}
