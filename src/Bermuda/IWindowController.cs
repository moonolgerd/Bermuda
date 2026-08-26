using System.Windows;

namespace Bermuda;

/// <summary>
/// Abstraction over the host window so that <see cref="HostBridge"/> can drive the
/// window without depending on WPF types directly. <see cref="MainWindow"/> implements it.
/// </summary>
public interface IWindowController
{
    bool IsMaximized { get; }
    bool IsMinimized { get; }
    bool IsFocused { get; }

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
}
