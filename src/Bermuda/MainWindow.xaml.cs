using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Bermuda.Fdc3;
using Bermuda.IpcContract;
using Microsoft.Extensions.Configuration;
using Microsoft.Web.WebView2.Core;

namespace Bermuda;

public partial class MainWindow : Window, IWindowController, IWindowCommands, IFdc3AppSink
{
    // The web side speaks camelCase (the generated TS shapes in
    // src/ipc/__generated__/ipc.d.ts); System.Text.Json's default is to keep
    // C# PascalCase and to match names case-sensitively. Its Web preset is
    // camelCase out and case-insensitive in, so every message in both
    // directions goes through it — without it nothing in { id, command }
    // binds to IpcRequest and every command is rejected as unknown.
    private static readonly JsonSerializerOptions HostJson = HostBridge.PayloadJson;

    // Every window shares one WebView2 environment (and so one browser
    // process) — a second CreateAsync on the same user data folder would have
    // to repeat the exact same options anyway, e.g. the CDP port.
    private static Task<CoreWebView2Environment>? _environment;

    private readonly IConfiguration _configuration;
    private readonly HostBridge _bridge;
    private readonly DesktopAgent _fdc3;
    private readonly string _fdc3AppId;
    private readonly string? _fdc3LaunchInstanceId;
    private readonly string _webUrl;
    private readonly int _cdpPort;
    private readonly bool _clientTitleBar;

    // False when this window exists only to satisfy NewWindowRequested: WebView2 navigates
    // it (so window.opener is wired) and we must not navigate it ourselves.
    private readonly bool _navigateOnLoad;

    // One initialization, shared by OnLoaded and by a parent waiting on the NewWindowRequested deferral.
    private Task<CoreWebView2>? _coreWebView;

    // Set before Show when this window exists to satisfy NewWindowRequested. Invoked on the
    // init stack, before that task's awaiters resume, so NewWindow is set while the
    // CoreWebView2 is still unnavigated (WebView2 rejects one that has already navigated).
    private Action<CoreWebView2>? _assignNewWindow;

    private bool _coreEventsWired;

    // window.open without a size, and openChild when a dimension is missing or not positive.
    private const int DefaultChildWidth = 800;
    private const int DefaultChildHeight = 600;

    // Resize border width and caption strip height used in client title bar mode.
    private const double ClientResizeBorder = 3;
    private const double ClientCaptionHeight = 40;

    // Left/right/bottom: keep a strip of the WPF window uncovered so the invisible resize
    // border stays reachable (the WebView2 HWND would otherwise swallow the mouse). Top is 0:
    // the caption strip itself is that border.
    private static readonly Thickness ClientNormalMargin = new(ClientResizeBorder, 0, ClientResizeBorder, ClientResizeBorder);

    // This window's FDC3 app instance. Connected on the first navigation (as
    // the launcher-assigned instance, if FDC3 opened this window); every later
    // navigation (reload, link) reconnects as a fresh instance, since the old
    // page's listeners died with it.
    private Fdc3Connection? _fdc3Connection;

    // Set when the window closes. Requests still in flight (e.g. a page
    // awaiting fdc3.getIntentResult) finish after that, and by then the WebView2
    // is disposed, so every continuation checks this before touching it.
    private bool _closed;

    // True after Flash() until StopFlash(), or until the window comes to the foreground
    // (FLASHW_TIMERNOFG). Read from the WebView thread; written on the UI thread.
    private volatile bool _flashing;

    /// <param name="fdc3AppId">The App Directory appId this window's page runs as.</param>
    /// <param name="fdc3InstanceId">Set when FDC3 launched this window; the agent is waiting on this id.</param>
    /// <param name="url">Overrides WEB_URL (FDC3-launched windows load their App Directory URL).</param>
    /// <param name="navigateOnLoad">False for a <c>NewWindowRequested</c> child: WebView2 navigates it after the deferral.</param>
    public MainWindow(IConfiguration configuration, DesktopAgent fdc3, string fdc3AppId, string? fdc3InstanceId = null, string? url = null, bool navigateOnLoad = true)
    {
        InitializeComponent();
        _configuration = configuration;
        _navigateOnLoad = navigateOnLoad;
        _fdc3 = fdc3;
        _fdc3AppId = fdc3AppId;
        _fdc3LaunchInstanceId = fdc3InstanceId;
        _webUrl = url ?? configuration["WEB_URL"] ?? "http://localhost:5555";
        _cdpPort = configuration.GetValue("CDP_PORT", 0);
        // A remote debugging port already means "dev/test run"; test-only commands ride on that.
        _bridge = new HostBridge(
            () => _fdc3Connection ?? throw new InvalidOperationException("FDC3 not connected yet."),
            this,
            testHooks: _cdpPort > 0);

        // TITLE_BAR=client (default) lets the web app draw the title bar; TITLE_BAR=native
        // keeps the standard Windows caption. Read once at startup — the web app asks the
        // host which mode is active (IWindowController.UsesClientTitleBar) and only renders
        // its own title bar in client mode.
        _clientTitleBar = !string.Equals(configuration["TITLE_BAR"], "native", StringComparison.OrdinalIgnoreCase);
        if (_clientTitleBar)
            ApplyClientTitleBar();

        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        // FLASHW_TIMERNOFG stops on its own once this window is in front; keep our flag in step.
        Activated += OnWindowActivated;

        // A toast (or one of its buttons) was clicked: surface the window and tell the web app.
        NotificationService.ToastShown += OnNotificationShown;
        NotificationService.Activated += OnNotificationActivated;
        WindowRegistry.Add(this);
        // With the tray icon on, closing the main window only hides it; the tray's Quit is the way out.
        Closing += (_, e) =>
        {
            if (!WindowRegistry.HidesToTray(this)) return;
            e.Cancel = true;
            HideWindow();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            StopFlash();
            NotificationService.ToastShown -= OnNotificationShown;
            NotificationService.Activated -= OnNotificationActivated;
            _fdc3Connection?.Dispose();
            _fdc3Connection = null;
            // With several windows open the app (and browser process) outlives
            // this one, so release its page explicitly — otherwise it lingers
            // as an orphaned, never-rendering CDP target.
            _webView.Dispose();
            // Last: may end the app, which tears down everything above.
            WindowRegistry.Remove(this);
        };
    }

    // Only the app's main window reacts to toasts (every window is subscribed to the static events).
    private void OnNotificationShown() => Dispatcher.Invoke(() =>
    {
        if (_closed || WindowRegistry.Main != this) return;
        if (!((IWindowController)this).IsFocused)
            Flash();
    });

    private void OnNotificationActivated(NotificationActivation activation) => Dispatcher.Invoke(() =>
    {
        if (_closed || WindowRegistry.Main != this) return;

        StopFlash();
        ShowWindow();
        Activate();
        if (_webView.CoreWebView2 is not null)
            PushEvent(activation);
    });

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (_closed || !_flashing) return;
        StopFlash();
    }

    private Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_environment is not null) return _environment;

        var options = _cdpPort > 0
            ? new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = $"--remote-debugging-port={_cdpPort}" }
            : null;
        return _environment = CoreWebView2Environment.CreateAsync(options: options);
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

        try
        {
            await EnsureCoreWebViewAsync();
        }
        catch (Exception ex)
        {
            // A child window's init failure must not take the process down; the opener sees no window.
            System.Diagnostics.Debug.WriteLine($"WebView2 failed to start: {ex}");
        }
    }

    /// <summary>
    /// Creates this window's WebView2 once. A parent opening this window from
    /// <c>NewWindowRequested</c> awaits the same task <see cref="OnLoaded"/> starts,
    /// then hands the <see cref="CoreWebView2"/> to the deferral so <c>window.opener</c> works.
    /// </summary>
    internal Task<CoreWebView2> EnsureCoreWebViewAsync() => _coreWebView ??= InitializeCoreWebViewAsync();

    /// <summary>
    /// Called by the parent before this window is shown, when it is the target of
    /// <c>NewWindowRequested</c>. Invoked once the core exists and its document-created
    /// script is registered, while the opener's deferral is still open.
    /// </summary>
    internal void WhenCoreReady(Action<CoreWebView2> assign) => _assignNewWindow = assign;

    private async Task<CoreWebView2> InitializeCoreWebViewAsync()
    {
        // The script has to be registered before this core is handed to NewWindowRequested.
        // WebView2 snapshots the document-created scripts when it accepts NewWindow, so a
        // registration after that assignment misses the page window.open navigates to.
        var preload = await LoadFdc3PreloadAsync();

        await _webView.EnsureCoreWebView2Async(await GetEnvironmentAsync());
        if (_closed) throw new InvalidOperationException("Window closed before its WebView2 was ready.");

        var core = _webView.CoreWebView2;
#if !DEBUG
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
#endif

        if (preload is not null)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(preload);
        if (_closed) throw new InvalidOperationException("Window closed before its WebView2 was ready.");

        WireCoreEvents(core);
        if (_navigateOnLoad)
        {
            ExposeHostObject();
            core.Navigate(_webUrl);
        }
        else
        {
            // Assign while the opener's deferral is still open so window.opener is
            // this core. The host object is registered after the deferral completes,
            // once WebView2 has finished initializing the new web content.
            _assignNewWindow?.Invoke(core);
        }

        return core;
    }

    private void WireCoreEvents(CoreWebView2 core)
    {
        if (_coreEventsWired) return;
        _coreEventsWired = true;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.ContentLoading += OnContentLoading;
        core.NewWindowRequested += OnNewWindowRequested;
        // window.close() from a page opened with window.open. CloseWindow honors the tray rule.
        core.WindowCloseRequested += (_, _) => CloseWindow();
    }

    private static string? _fdc3Preload;

    private static async Task<string?> LoadFdc3PreloadAsync()
    {
        if (_fdc3Preload is not null) return _fdc3Preload;
        if (!File.Exists(Fdc3PreloadPath)) return null;
        var text = await File.ReadAllTextAsync(Fdc3PreloadPath);
        return _fdc3Preload ??= text;
    }

    private static string Fdc3PreloadPath { get; } = Path.Combine(AppContext.BaseDirectory, "Preload", "fdc3-preload.js");

    private string Origin(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : "";

    private bool IsTrustedOrigin(string? uri) =>
        string.Equals(Origin(uri), Origin(_webUrl), StringComparison.OrdinalIgnoreCase);

    // True once a trusted document has started loading. Until then the implicit about:blank
    // WebView2 creates must be left alone: cancelling it drops a NewWindowRequested child.
    private bool _acceptedNavigation;

    // Only this window's own origin may be shown: anything else would be
    // connected to the agent (and the host bridge) under this window's appId.
    // Child windows use the same rule (their _webUrl is already on this origin).
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsInitialBlank(e.Uri) && !_acceptedNavigation)
            return;

        if (!IsTrustedOrigin(e.Uri))
        {
            e.Cancel = true;
            return;
        }

        _acceptedNavigation = true;
    }

    private static bool IsInitialBlank(string? uri) =>
        string.IsNullOrEmpty(uri) || string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase);

    // window.open / target=_blank. The deferral keeps the caller's script waiting until
    // NewWindow is set, which is what makes the return value (and window.opener) the child.
    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Handled with no NewWindow blocks the default popup, including a cross-origin one.
        e.Handled = true;
        var accepted = TryResolveSameOrigin(e.Uri, out var target);
        if (_closed || !accepted) return;

        MainWindow? child = null;
        using (e.GetDeferral())
        {
            try
            {
                var features = e.WindowFeatures;
                var width = features is { HasSize: true, Width: > 0 } ? (int)features.Width : DefaultChildWidth;
                var height = features is { HasSize: true, Height: > 0 } ? (int)features.Height : DefaultChildHeight;
                double? left = features is { HasPosition: true } ? (double)features.Left : null;
                double? top = features is { HasPosition: true } ? (double)features.Top : null;
                child = ShowOwnedChild(
                    target.AbsoluteUri,
                    width,
                    height,
                    navigateOnLoad: false,
                    left,
                    top,
                    onCoreReady: core => e.NewWindow = core);
                await child.EnsureCoreWebViewAsync();
                if (e.NewWindow is null)
                    throw new InvalidOperationException("Child CoreWebView2 was not assigned to NewWindow.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Child window failed to open: {ex}");
                // CloseWindow defers the close: this handler is still inside the parent's WebView2 callback.
                try { child?.CloseWindow(); } catch { /* already closing */ }
                child = null;
            }
        }

        // Deferral is complete, so the child's web content is initialized.
        try { child?.ExposeHostObject(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Child window failed to open: {ex}");
            try { child?.CloseWindow(); } catch { /* already closing */ }
        }
    }

    /// <summary>
    /// Resolves <paramref name="url"/> against this page and accepts it only when the result is an
    /// http(s) URL on this window's origin — the same trust check as <see cref="OnNavigationStarting"/>.
    /// </summary>
    private bool TryResolveSameOrigin(string? url, out Uri resolved)
    {
        resolved = null!;
        if (string.IsNullOrWhiteSpace(url)) return false;

        var baseText = _webView.CoreWebView2?.Source;
        if (!IsTrustedOrigin(baseText) || !Uri.TryCreate(baseText, UriKind.Absolute, out var baseUri))
        {
            if (!Uri.TryCreate(_webUrl, UriKind.Absolute, out baseUri)) return false;
        }

        if (!Uri.TryCreate(baseUri, url.Trim(), out var absolute)) return false;
        if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps) return false;
        if (!IsTrustedOrigin(absolute.AbsoluteUri)) return false;

        resolved = absolute;
        return true;
    }

    /// <summary>
    /// A new window owned by this one, registered like every other window, running as a new FDC3
    /// instance of this window's appId. Pass <paramref name="navigateOnLoad"/> false when WebView2
    /// itself will navigate the child (NewWindowRequested).
    /// </summary>
    private MainWindow ShowOwnedChild(
        string url,
        int width,
        int height,
        bool navigateOnLoad,
        double? left,
        double? top,
        Action<CoreWebView2>? onCoreReady = null)
    {
        var child = new MainWindow(_configuration, _fdc3, _fdc3AppId, url: url, navigateOnLoad: navigateOnLoad)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = width,
            Height = height,
            Left = left ?? Left + 32,
            Top = top ?? Top + 32,
        };
        // Before Show: OnLoaded starts init, and the callback must be in place when init finishes.
        if (onCoreReady is not null) child.WhenCoreReady(onCoreReady);
        child.Show();
        return child;
    }

    /// <summary>
    /// Projects the window controller into the page. For a <c>window.open</c> child this
    /// has to happen after the opener's deferral completes; before that the web content
    /// is not initialized and the registration never receives calls.
    /// </summary>
    internal void ExposeHostObject()
    {
        if (_webView.CoreWebView2 is not { } core) return;
        _scriptHost ??= new WindowScriptHost(this);
        core.AddHostObjectToScript("windowController", _scriptHost);
    }

    private WindowScriptHost? _scriptHost;

    // Reconnect once a navigation commits (ContentLoading fires before the
    // page's scripts and not for cancelled/failed navigations or redirect hops),
    // so the live page keeps its listeners until it is really replaced.
    private void OnContentLoading(object? sender, CoreWebView2ContentLoadingEventArgs e)
    {
        if (!_navigateOnLoad)
        {
            try { ExposeHostObject(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Child host object failed: {ex}"); }
        }

        try
        {
            var isFirstNavigation = _fdc3Connection is null;
            _fdc3Connection?.Dispose();
            _fdc3Connection = null;
            _fdc3Connection = _fdc3.Connect(_fdc3AppId, this, isFirstNavigation ? _fdc3LaunchInstanceId : null);
        }
        catch (Exception)
        {
            // Leave the page unconnected (commands fail with an error) rather than crash the host.
        }
    }

    // IFdc3AppSink: the agent's context/intent/event deliveries for this window's page.
    void IFdc3AppSink.Post(object message) => PushEvent(message);

    private async void OnWebMessageReceived(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        IpcRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<IpcRequest>(e.WebMessageAsJson, HostJson);
        }
        catch
        {
            return;
        }

        if (req is null || !IsTrustedOrigin(e.Source)) return;

        var result = await _bridge.HandleAsync(req.Command, req.Payload);
        if (_closed) return;

        PostToPage(JsonSerializer.Serialize(new HostResponse(req.Id, result), HostJson));
    }

    public void PushEvent(object eventPayload) => PostToPage(JsonSerializer.Serialize(eventPayload, HostJson));

    /// <summary>Posts to the page unless the window has closed (which disposes the WebView2).</summary>
    private void PostToPage(string json)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closed) _webView.CoreWebView2.PostWebMessageAsJson(json);
        });
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

    public void OpenChild(OpenChildRequest request)
    {
        if (!TryResolveSameOrigin(request.Url, out var target))
            throw new IpcRejectedException("Child URL must be same-origin.");

        var width = request.Width > 0 ? request.Width : DefaultChildWidth;
        var height = request.Height > 0 ? request.Height : DefaultChildHeight;
        ApplyWindowAction(() => ShowOwnedChild(target.AbsoluteUri, width, height, navigateOnLoad: true, left: null, top: null));
    }

    public void Flash() => ApplyWindowAction(() =>
    {
        // TIMERNOFG does not keep flashing a window that is already in front.
        var foreground = IsWindowForeground();
        FlashWindow(foreground ? FLASHW_STOP : (FLASHW_TRAY | FLASHW_TIMERNOFG));
        _flashing = NativeWindowHandle != IntPtr.Zero && !foreground;
    });

    public void StopFlash() => ApplyWindowAction(() =>
    {
        FlashWindow(FLASHW_STOP);
        _flashing = false;
    });

    public bool IsFlashing => _flashing;

    // FLASHW_TRAY | FLASHW_TIMERNOFG flashes the taskbar button until this window is in
    // the foreground or FLASHW_STOP. dwTimeout 0 uses the caret blink rate.
    private const uint FLASHW_STOP = 0;
    private const uint FLASHW_TRAY = 0x00000002;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private void FlashWindow(uint flags)
    {
        var hwnd = NativeWindowHandle;
        if (hwnd == IntPtr.Zero) return;

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = flags,
            uCount = flags == FLASHW_STOP ? 0u : uint.MaxValue,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
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
