using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    // TS shapes in src/ipc/__generated__/ipc.d.ts), so every host->web message
    // goes through this. Enums go out as their camelCase names, which is the
    // string-literal union the generator emits for [IpcType] enums (UpdateStatus, ...).
    private static readonly JsonSerializerOptions HostToWebJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // The web side sends camelCase requests ({ id, command, payload }); the C# records
    // use PascalCase members. Web defaults are camelCase + case-insensitive binding, so
    // the request id actually binds and the response can be matched to its caller.
    private static readonly JsonSerializerOptions WebToHostJson = new(JsonSerializerDefaults.Web);

    private readonly HostBridge _bridge;
    private readonly string _webUrl;
    private readonly int _cdpPort;
    private readonly bool _clientTitleBar;

    // Resize border width and caption strip height used in client title bar mode.
    private const double ClientResizeBorder = 3;
    private const double ClientCaptionHeight = 40;

    // Left/right/bottom: keep a strip of the WPF window uncovered so the invisible resize
    // border stays reachable (the WebView2 HWND would otherwise swallow the mouse). Top is 0:
    // the caption strip itself is that border.
    private static readonly Thickness ClientNormalMargin = new(ClientResizeBorder, 0, ClientResizeBorder, ClientResizeBorder);

    public MainWindow(IConfiguration configuration)
    {
        InitializeComponent();
        _webUrl = configuration["WEB_URL"] ?? "http://localhost:5555";
        _cdpPort = configuration.GetValue("CDP_PORT", 0);
        // A remote debugging port already means "dev/test run"; test-only commands ride on that.
        _bridge = new HostBridge(testHooks: _cdpPort > 0);

        // TITLE_BAR=client (default) lets the web app draw the title bar; TITLE_BAR=native
        // keeps the standard Windows caption. Read once at startup — the web app asks the
        // host which mode is active (IWindowController.UsesClientTitleBar) and only renders
        // its own title bar in client mode.
        _clientTitleBar = !string.Equals(configuration["TITLE_BAR"], "native", StringComparison.OrdinalIgnoreCase);
        if (_clientTitleBar)
            ApplyClientTitleBar();

        Loaded += OnLoaded;
        StateChanged += OnStateChanged;

        // A toast (or one of its buttons) was clicked: surface the window and tell the web app.
        NotificationService.Activated += activation => Dispatcher.Invoke(() =>
        {
            ShowWindow();
            Activate();
            if (_webView.CoreWebView2 is not null)
                PushEvent(activation);
        });
    }

    // Client title bar mode: no native title text/icon, but Windows still draws the real
    // minimize/maximize/close buttons (UseAeroCaptionButtons) — which is what gives the
    // Windows 11 Snap Layouts flyout on the maximize button. The caption strip is host-owned
    // and the WebView2 starts below it: WebView2's child window answers hit-tests itself, so
    // the buttons must not sit underneath it. Dragging, double-click-to-maximize and the
    // system menu all come from the strip being the window's caption.
    private void ApplyClientTitleBar()
    {
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = ClientCaptionHeight,
            ResizeBorderThickness = new Thickness(ClientResizeBorder),
            GlassFrameThickness = new Thickness(0, ClientCaptionHeight, 0, 0),
            UseAeroCaptionButtons = true,
            CornerRadius = new CornerRadius(0),
        });
        // Transparent so the glass strip (and the caption buttons Windows draws in it) show
        // through; DWMWA_CAPTION_COLOR (set in OnLoaded) gives the strip its colour.
        Background = System.Windows.Media.Brushes.Transparent;
        _fill.Margin = new Thickness(0, ClientCaptionHeight, 0, 0);
        _fill.Visibility = Visibility.Visible;
        _root.Margin = ClientNormalMargin;
        _webView.Margin = new Thickness(0, ClientCaptionHeight, 0, 0);
    }

    // A maximized WindowChrome window overhangs the screen by its resize border; inset the
    // content by the same amount so the caption strip and buttons aren't clipped.
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (!_clientTitleBar) return;

        _root.Margin = WindowState == System.Windows.WindowState.Maximized
            ? new Thickness(
                SystemParameters.WindowResizeBorderThickness.Left + SystemParameters.FixedFrameVerticalBorderWidth,
                SystemParameters.WindowResizeBorderThickness.Top + SystemParameters.FixedFrameHorizontalBorderHeight,
                SystemParameters.WindowResizeBorderThickness.Right + SystemParameters.FixedFrameVerticalBorderWidth,
                SystemParameters.WindowResizeBorderThickness.Bottom + SystemParameters.FixedFrameHorizontalBorderHeight)
            : ClientNormalMargin;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int attrValue, int attrSize);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Dark Win32 title bar so caption matches the dark WebView2 content
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));

        if (_clientTitleBar)
        {
            // WindowChrome with no caption drops the system's default rounding; ask DWM
            // for rounded corners explicitly (Windows 11+, ignored on Windows 10).
            int captionColor = 0x002d2d2d; // COLORREF (0x00BBGGRR), matches the page background
            DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref captionColor, sizeof(int));
            int noBackdrop = 1; // DWMSBT_NONE
            DwmSetWindowAttribute(hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref noBackdrop, sizeof(int));

            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        }

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
            req = JsonSerializer.Deserialize<IpcRequest>(e.WebMessageAsJson, WebToHostJson);
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

    public bool UsesClientTitleBar => _clientTitleBar;

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

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr NativeGetForegroundWindow();

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
        return NativeWindowHandle != IntPtr.Zero && NativeGetForegroundWindow() == NativeWindowHandle;
    }
}
