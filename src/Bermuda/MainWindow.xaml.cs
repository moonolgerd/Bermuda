using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Bermuda.IpcContract;
using Microsoft.Extensions.Configuration;
using Microsoft.Web.WebView2.Core;

namespace Bermuda;

public partial class MainWindow : Window, IWindowController
{
    // System.Text.Json's default is to preserve C# property names as-is
    // (PascalCase); the web side expects camelCase (matches the generated
    // TS shapes in src/__generated__/ipc.d.ts), so every host->web message
    // goes through this.
    private static readonly JsonSerializerOptions HostToWebJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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

        _webView.CoreWebView2.AddHostObjectToScript("windowController", new WindowScriptHost(this));
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
        var response = JsonSerializer.Serialize(new HostResponse(req.Id, result), HostToWebJson);

        Dispatcher.Invoke(() => _webView.CoreWebView2.PostWebMessageAsJson(response));
    }

    public void PushEvent(object eventPayload)
    {
        var json = JsonSerializer.Serialize(eventPayload, HostToWebJson);
        Dispatcher.Invoke(() => _webView.CoreWebView2.PostWebMessageAsJson(json));
    }

    // IWindowController -------------------------------------------------------

    public bool IsMaximized => base.WindowState == System.Windows.WindowState.Maximized;
    public bool IsMinimized => base.WindowState == System.Windows.WindowState.Minimized;

    // Explicit implementations: WPF's Window/FrameworkElement/UIElement already
    // declare Left/Top/Width/Height/IsFocused, so an implicit override here would
    // silently shadow them instead of satisfying the interface.
    bool IWindowController.IsFocused => IsKeyboardFocused || NativeWindowHandle != IntPtr.Zero && IsWindowForeground();

    int IWindowController.Left => (int)Math.Round((double)base.Left);
    int IWindowController.Top => (int)Math.Round((double)base.Top);
    int IWindowController.Width => (int)Math.Round((double)base.Width);
    int IWindowController.Height => (int)Math.Round((double)base.Height);

    private void ApplyWindowAction(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.Invoke(action);
        }
    }

    public void Minimize() => ApplyWindowAction(() => base.WindowState = System.Windows.WindowState.Minimized);

    public void Maximize() => ApplyWindowAction(() => base.WindowState = System.Windows.WindowState.Maximized);

    public void Restore() => ApplyWindowAction(() => base.WindowState = System.Windows.WindowState.Normal);

    public void CloseWindow() => ApplyWindowAction(Close);

    public void Resize(int width, int height)
    {
        ApplyWindowAction(() =>
        {
            if (base.WindowState != System.Windows.WindowState.Minimized)
            {
                base.WindowState = System.Windows.WindowState.Normal;
            }

            base.Width = width;
            base.Height = height;
        });
    }

    public void Move(int x, int y)
    {
        ApplyWindowAction(() =>
        {
            if (base.WindowState != System.Windows.WindowState.Minimized)
            {
                base.WindowState = System.Windows.WindowState.Normal;
            }

            base.Left = x;
            base.Top = y;
        });
    }

    public void SetSize(int width, int height) => Resize(width, height);

    public void SetPosition(int x, int y) => Move(x, y);

    public void ShowWindow() => ApplyWindowAction(() =>
    {
        if (base.WindowState == System.Windows.WindowState.Minimized)
        {
            base.WindowState = System.Windows.WindowState.Normal;
        }

        Show();
    });

    public void HideWindow() => ApplyWindowAction(() =>
    {
        base.WindowState = System.Windows.WindowState.Normal;
        Hide();
    });

    public void SetOverlayIcon(string iconPng, string description)
    {
        var bytes = Convert.FromBase64String(StripDataUrlPrefix(iconPng));

        ApplyWindowAction(() =>
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();

            TaskbarItemInfo ??= new TaskbarItemInfo();
            TaskbarItemInfo.Overlay = frame;
            TaskbarItemInfo.Description = description;
        });
    }

    public void ClearOverlayIcon()
    {
        ApplyWindowAction(() =>
        {
            if (TaskbarItemInfo is null) return;

            TaskbarItemInfo.Overlay = null;
            TaskbarItemInfo.Description = null;
        });
    }

    private static string StripDataUrlPrefix(string value)
    {
        var commaIndex = value.IndexOf(',');
        return value.StartsWith("data:", StringComparison.Ordinal) && commaIndex >= 0
            ? value[(commaIndex + 1)..]
            : value;
    }

    public void SetProgressState(string state)
    {
        // Validated against our own contract enum (source of truth for the generated
        // TS union), then converted by name to WPF's TaskbarItemProgressState.
        if (!Enum.TryParse<Bermuda.IpcContract.TaskbarProgressState>(state, ignoreCase: true, out var parsed))
            throw new ArgumentException($"Unknown progress state: {state}");

        var wpfState = Enum.Parse<TaskbarItemProgressState>(parsed.ToString());

        ApplyWindowAction(() =>
        {
            TaskbarItemInfo ??= new TaskbarItemInfo();
            TaskbarItemInfo.ProgressState = wpfState;
        });
    }

    public void SetProgressValue(double value)
    {
        var clamped = Math.Clamp(value, 0.0, 1.0);

        ApplyWindowAction(() =>
        {
            TaskbarItemInfo ??= new TaskbarItemInfo();
            TaskbarItemInfo.ProgressValue = clamped;
        });
    }

    [DllImport("user32.dll")]
    private static extern bool NativeIsWindowForeground(IntPtr hwnd);

    private IntPtr _nativeHandle;

    private IntPtr NativeWindowHandle
    {
        get
        {
            if (_nativeHandle == IntPtr.Zero)
            {
                var handle = new WindowInteropHelper(this).Handle;
                _nativeHandle = handle;
            }

            return _nativeHandle;
        }
    }

    private bool IsWindowForeground()
    {
        return NativeWindowHandle != IntPtr.Zero && NativeIsWindowForeground(NativeWindowHandle);
    }
}
