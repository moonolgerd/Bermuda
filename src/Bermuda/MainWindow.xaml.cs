using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Bermuda.Fdc3;
using Bermuda.IpcContract;
using Microsoft.Extensions.Configuration;
using Microsoft.Web.WebView2.Core;

namespace Bermuda;

public partial class MainWindow : Window, IWindowController, IFdc3AppSink
{
    // System.Text.Json's default is to preserve C# property names as-is
    // (PascalCase); the web side expects camelCase (matches the generated
    // TS shapes in src/ipc/__generated__/ipc.d.ts), so every host->web message
    // goes through this — and web->host requests are read with it too.
    private static readonly JsonSerializerOptions HostToWebJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Every window shares one WebView2 environment (and so one browser
    // process) — a second CreateAsync on the same user data folder would have
    // to repeat the exact same options anyway, e.g. the CDP port.
    private static Task<CoreWebView2Environment>? _environment;

    private readonly HostBridge _bridge;
    private readonly DesktopAgent _fdc3;
    private readonly string _fdc3AppId;
    private readonly string _webUrl;
    private readonly int _cdpPort;

    // This window's FDC3 app instance. Starts as the launcher-assigned id (if
    // FDC3 opened this window) and is connected on the first navigation; every
    // later navigation (reload, link) reconnects as a fresh instance, since
    // the old page's listeners died with it.
    private string? _fdc3InstanceId;
    private bool _fdc3Connected;

    /// <param name="fdc3AppId">The App Directory appId this window's page runs as.</param>
    /// <param name="fdc3InstanceId">Set when FDC3 launched this window; the agent is waiting on this id.</param>
    /// <param name="url">Overrides WEB_URL (FDC3-launched windows load their App Directory URL).</param>
    public MainWindow(IConfiguration configuration, DesktopAgent fdc3, string fdc3AppId, string? fdc3InstanceId = null, string? url = null)
    {
        InitializeComponent();
        _fdc3 = fdc3;
        _fdc3AppId = fdc3AppId;
        _fdc3InstanceId = fdc3InstanceId;
        _bridge = new HostBridge(fdc3, () => _fdc3Connected ? _fdc3InstanceId! : throw new InvalidOperationException("FDC3 not connected yet."));
        _webUrl = url ?? configuration["WEB_URL"] ?? "http://localhost:5555";
        _cdpPort = configuration.GetValue("CDP_PORT", 0);
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            DisconnectFdc3();
            // With several windows open the app (and browser process) outlives
            // this one, so release its page explicitly — otherwise it lingers
            // as an orphaned, never-rendering CDP target.
            _webView.Dispose();
        };
    }

    private Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_environment is not null) return _environment;

        var options = _cdpPort > 0
            ? new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = $"--remote-debugging-port={_cdpPort}" }
            : null;
        return _environment = CoreWebView2Environment.CreateAsync(options: options);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int attrValue, int attrSize);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Dark Win32 title bar so caption matches the dark WebView2 content
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));

        await _webView.EnsureCoreWebView2Async(await GetEnvironmentAsync());

#if !DEBUG
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif

        _webView.CoreWebView2.AddHostObjectToScript("windowController", new WindowScriptHost(this));
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
        _webView.CoreWebView2.Navigate(_webUrl);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_fdc3Connected)
        {
            DisconnectFdc3();
            _fdc3InstanceId = null;
        }

        _fdc3InstanceId = _fdc3.Connect(_fdc3AppId, this, _fdc3InstanceId).InstanceId;
        _fdc3Connected = true;
    }

    private void DisconnectFdc3()
    {
        if (!_fdc3Connected) return;

        _fdc3.Disconnect(_fdc3InstanceId!);
        _fdc3Connected = false;
    }

    // IFdc3AppSink: the agent's context/intent/event deliveries for this window's page.
    void IFdc3AppSink.Post(object message) => PushEvent(message);

    private async void OnWebMessageReceived(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        IpcRequest? req;
        try
        {
            // Same camelCase policy on the way in — without it nothing binds
            // ({ id, command } vs IpcRequest's Id/Command) and every command
            // arrives as "" and is rejected as unknown.
            req = JsonSerializer.Deserialize<IpcRequest>(e.WebMessageAsJson, HostToWebJson);
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

    // Deferred: this is usually called from script (windowController.Close()),
    // i.e. from inside a WebView2 callback, and closing disposes that WebView2.
    public void CloseWindow() => Dispatcher.BeginInvoke(Close);

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
