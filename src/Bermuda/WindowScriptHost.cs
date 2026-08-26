using System.Runtime.InteropServices;
using Bermuda.IpcContract;

namespace Bermuda;

/// <summary>
/// Projected into the page via <c>CoreWebView2.AddHostObjectToScript("windowController", ...)</c>.
/// WebView2 maps its public methods and properties 1:1 onto promises on
/// <c>chrome.webview.hostObjects.windowController</c> in JS, so no command
/// dispatch is needed to drive the window from React. The [IpcHostObject]
/// attribute tells Bermuda.IpcContract.SourceGenerator to reflect this class
/// and emit the matching TypeScript into src/__generated__/ipc.d.ts.
/// </summary>
[IpcHostObject("windowController")]
[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public sealed class WindowScriptHost
{
    private readonly IWindowController _window;

    public WindowScriptHost(IWindowController window)
    {
        _window = window;
    }

    public bool IsMaximized => _window.IsMaximized;
    public bool IsMinimized => _window.IsMinimized;
    public bool IsFocused => _window.IsFocused;

    public int Left => _window.Left;
    public int Top => _window.Top;
    public int Width => _window.Width;
    public int Height => _window.Height;

    public void Minimize() => _window.Minimize();
    public void Maximize() => _window.Maximize();
    public void Restore() => _window.Restore();

    public void ToggleMaximize()
    {
        if (_window.IsMaximized)
            _window.Restore();
        else
            _window.Maximize();
    }

    public void Close() => _window.CloseWindow();
    public void Resize(int width, int height) => _window.Resize(width, height);
    public void Move(int x, int y) => _window.Move(x, y);
    public void Show() => _window.ShowWindow();
    public void Hide() => _window.HideWindow();

    public void SetOverlayIcon(string iconPng, string description) => _window.SetOverlayIcon(iconPng, description);
    public void ClearOverlayIcon() => _window.ClearOverlayIcon();

    public void SetProgressState(string state) => _window.SetProgressState(state);
    public void SetProgressValue(double value) => _window.SetProgressValue(value);
}
