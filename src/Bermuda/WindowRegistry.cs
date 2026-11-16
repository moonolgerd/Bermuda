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
    public static void Quit() => Application.Current?.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
}
