using Bermuda.IpcContract;

namespace Bermuda;

/// <summary>
/// Abstraction over the host window so that <see cref="HostBridge"/> can drive the
/// window without depending on WPF types directly. <see cref="MainWindow"/> implements it.
/// </summary>
public interface IWindowController
{
    /// <summary>
    /// True when the web app draws the title bar (the native caption is removed);
    /// false when the standard Windows title bar is used. Set once at startup from the
    /// <c>TITLE_BAR</c> setting (<c>client</c> | <c>native</c>).
    /// </summary>
    bool UsesClientTitleBar { get; }

    bool IsMaximized { get; }
    bool IsMinimized { get; }
    bool IsFocused { get; }

    /// <summary>False while the window is hidden (for example in the tray).</summary>
    bool IsVisible { get; }

    int Left { get; }
    int Top { get; }
    int Width { get; }
    int Height { get; }

    void Minimize();
    void Maximize();
    void Restore();
    void CloseWindow();

    void Resize(int width, int height);
    void Move(int x, int y);
    void SetSize(int width, int height);
    void SetPosition(int x, int y);

    void ShowWindow();
    void HideWindow();

    /// <summary>
    /// Sets the small badge icon shown over the app's taskbar button.
    /// <paramref name="iconPng"/> is a PNG image, either raw base64 or a
    /// <c>data:image/png;base64,...</c> URL (e.g. from a canvas).
    /// </summary>
    void SetOverlayIcon(string iconPng, string description);

    /// <summary>Removes the taskbar overlay icon set by <see cref="SetOverlayIcon"/>.</summary>
    void ClearOverlayIcon();

    /// <summary>
    /// Sets the taskbar progress indicator state. One of "none", "indeterminate",
    /// "normal", "error", "paused" (case-insensitive) — mirrors WPF's
    /// <c>System.Windows.Shell.TaskbarItemProgressState</c>.
    /// </summary>
    void SetProgressState(string state);

    /// <summary>Sets the taskbar progress value. Clamped to 0.0–1.0; ignored while the state is "none" or "indeterminate".</summary>
    void SetProgressValue(double value);

    /// <summary>
    /// Opens a child window owned by this one, sized from <paramref name="request"/>.
    /// The URL must be same-origin with this window; otherwise throws <see cref="IpcRejectedException"/>.
    /// The child connects to FDC3 as a new instance of this window's app and closes when this window closes.
    /// </summary>
    void OpenChild(OpenChildRequest request);

    /// <summary>
    /// Flashes the taskbar button until the window comes to the foreground or <see cref="StopFlash"/> is called.
    /// A window that is already in the foreground is left not flashing.
    /// </summary>
    void Flash();

    /// <summary>Stops taskbar flashing started by <see cref="Flash"/>.</summary>
    void StopFlash();

    /// <summary>True while the taskbar button is flashing.</summary>
    bool IsFlashing { get; }
}
