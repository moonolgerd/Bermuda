using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace Bermuda;

/// <summary>
/// The notification-area icon. Left-click shows or hides the main window; the menu offers Show, New window and
/// Quit. Together with <see cref="WindowRegistry.TrayEnabled"/> it is what lets Bermuda keep running with no
/// window: closing the main window only hides it.
/// </summary>
internal sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TrayService(Action newWindow)
    {
        var show = new MenuItem { Header = "Show Bermuda" };
        show.Click += (_, _) => WindowRegistry.ActivateMain();
        var window = new MenuItem { Header = "New window" };
        window.Click += (_, _) => newWindow();
        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => WindowRegistry.Quit();

        _icon = new TaskbarIcon
        {
            ToolTipText = "Bermuda",
            // The exe's own multi-size icon, copied next to it (read from disk: a pack URI does not resolve this early).
            Icon = new System.Drawing.Icon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")),
            ContextMenu = new ContextMenu { Items = { show, window, new Separator(), quit } },
            MenuActivation = PopupActivationMode.RightClick,
        };
        _icon.TrayLeftMouseUp += (_, _) => WindowRegistry.ToggleMain();
        _icon.ForceCreate();
    }

    public void Dispose() => _icon.Dispose();
}
