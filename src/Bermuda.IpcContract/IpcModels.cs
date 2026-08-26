namespace Bermuda.IpcContract;

/// <summary>Response to <c>ping</c>.</summary>
public sealed record PingResult(bool Pong);

[IpCommand("ping", "undefined", nameof(PingResult), "Health check echoed back by the host.")]
internal sealed class PingCommand;

/// <summary>
/// A postMessage request from the web side to <c>HostBridge</c>. <c>Payload</c>
/// is deliberately untyped (its shape depends on <c>Command</c>).
/// </summary>
[IpcType]
public sealed record IpcRequest(string Id, string Command, object? Payload);

/// <summary>
/// <c>HostBridge</c>'s postMessage response envelope. <c>Result</c> is
/// deliberately untyped, same reasoning as <see cref="IpcRequest.Payload"/>.
/// Serialized with <c>JsonNamingPolicy.CamelCase</c> — see
/// <c>MainWindow.OnWebMessageReceived</c> — to match this generated shape.
/// </summary>
[IpcType]
public sealed record HostResponse(string Id, object? Result);

/// <summary>Window bounds in screen coordinates. Part of <see cref="WindowStateSnapshot"/>.</summary>
[IpcType]
public sealed record WindowBounds(int Left, int Top, int Width, int Height);

/// <summary>
/// Shape returned by the frontend's <c>bermudaHost.window.getState()</c>, which
/// builds it by combining several <c>WindowScriptHost</c> (windowController)
/// properties — see the [IpcType] doc comment for why that combining can't
/// happen on the C# side.
/// </summary>
[IpcType]
public sealed record WindowStateSnapshot(WindowBounds Bounds, bool IsMaximized, bool IsMinimized, bool IsFocused);

/// <summary>
/// Valid values for <c>WindowScriptHost.SetProgressState</c>'s string parameter.
/// Mirrors WPF's <c>System.Windows.Shell.TaskbarItemProgressState</c> member
/// names exactly — kept as a separate enum here (rather than referencing WPF's)
/// so this project doesn't need a WPF reference just to name these values for
/// codegen; <c>MainWindow.SetProgressState</c> converts by name.
/// </summary>
[IpcType]
public enum TaskbarProgressState
{
    None,
    Indeterminate,
    Normal,
    Error,
    Paused,
}
