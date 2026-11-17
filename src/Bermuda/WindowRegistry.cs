using System.Windows;

namespace Bermuda;

/// <summary>
/// The open <see cref="MainWindow"/>s, in the order they were created. The app no longer exits just because
/// the last window closed (<c>ShutdownMode=OnExplicitShutdown</c>); this registry decides when it does, and
/// is what the tray icon, jump list and child windows build on. Touch it from the UI thread only.
/// </summary>
internal static class WindowRegistry
{
    private static readonly List<MainWindow> Windows = [];

    /// <summary>
    /// Whether the app quits once the last window has closed. True today; the tray icon turns it off so the
    /// app can live on with no window.
    /// </summary>
    public static bool QuitWhenLastWindowCloses { get; set; } = true;

    /// <summary>
    /// Whether the notification-area icon is on (TRAY_ENABLED, default true). When it is, closing the main window
    /// hides it instead, and Quit in the tray menu is the way out.
    /// </summary>
    public static bool TrayEnabled { get; set; }

    /// <summary>Set once the app is ending (Quit, sign-out), so closing a window really closes it.</summary>
    public static bool Quitting { get; private set; }

    /// <summary>True when closing <paramref name="window"/> should only hide it: it is the main window and the tray can bring it back.</summary>
    public static bool HidesToTray(MainWindow window) => TrayEnabled && !Quitting && Main == window;

    public static IReadOnlyList<MainWindow> All => Windows;

    /// <summary>The app's main window: the oldest one still open, or null when none is.</summary>
    public static MainWindow? Main => Windows.Count > 0 ? Windows[0] : null;

    public static void Add(MainWindow window) => Windows.Add(window);

    public static void Remove(MainWindow window)
    {
        if (!Windows.Remove(window)) return;
        if (Windows.Count == 0 && QuitWhenLastWindowCloses) Quit();
    }

    /// <summary>Brings the main window to the front, restoring it if minimized or hidden.</summary>
    public static void ActivateMain()
    {
        if (Main is not { } window) return;
        window.ShowWindow();
        window.Activate();
    }

    /// <summary>Ends the app (runs <c>App.OnExit</c>), whatever windows are still open.</summary>
    public static void Quit()
    {
        Quitting = true;
        Application.Current?.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
    }

    /// <summary>Tray left-click: hides the main window when it is showing (the click itself takes focus, so focus is no signal), otherwise brings it forward.</summary>
    public static void ToggleMain()
    {
        if (Main is not { } window) return;
        if (window.IsVisible && !window.IsMinimized) window.HideWindow();
        else ActivateMain();
    }

    /// <summary>Marks the app as ending without shutting it down (the OS session is ending).</summary>
    public static void MarkQuitting() => Quitting = true;
}
