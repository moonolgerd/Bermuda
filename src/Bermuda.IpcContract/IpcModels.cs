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

/// <summary>
/// Outcome of <c>checkForUpdate</c>. <c>Unsupported</c> means the app isn't running
/// from an MSIX installed through an .appinstaller (dev runs, plain MSIX installs).
/// Mirrors <c>Windows.ApplicationModel.PackageUpdateAvailability</c> otherwise.
/// </summary>
[IpcType]
public enum UpdateStatus
{
    Unsupported,
    None,
    Available,
    Required,
    Unknown,
    Error,
}

/// <summary>Response to <c>checkForUpdate</c>.</summary>
public sealed record UpdateCheckResult(UpdateStatus Status, string CurrentVersion, string Message);

[IpCommand("checkForUpdate", "undefined", nameof(UpdateCheckResult), "Asks Windows whether a newer version is published at the .appinstaller URL.")]
internal sealed class CheckForUpdateCommand;

/// <summary>Response to <c>applyUpdate</c>.</summary>
public sealed record UpdateApplyResult(bool Started, string Message);

[IpCommand("applyUpdate", "undefined", nameof(UpdateApplyResult), "Installs the newer version from the .appinstaller; the app restarts when it finishes.")]
internal sealed class ApplyUpdateCommand;

/// <summary>How hard a toast tries to get the user's attention.</summary>
[IpcType]
public enum NotificationPriority
{
    /// <summary>Normal toast; goes to Action Center when ignored.</summary>
    Normal,
    /// <summary>High-priority toast: ranked above others in Action Center.</summary>
    High,
    /// <summary>High priority and stays on screen until dismissed (reminder scenario).</summary>
    Urgent,
}

/// <summary>A button on a toast. Clicking it raises a <see cref="NotificationActivation"/> carrying <c>Id</c>.</summary>
[IpcType]
public sealed record NotificationButton(string Id, string Label, string? Icon = null);

/// <summary>
/// Input to <c>showNotification</c>. Image fields accept an https URL, an absolute file path, or a
/// <c>data:</c> URL. <c>Id</c> is echoed back in <see cref="NotificationActivation"/> and, when reused,
/// replaces the earlier toast with that id instead of stacking a new one.
/// </summary>
[IpcType]
public sealed record ShowNotificationRequest(
    string Title,
    string Body,
    string? Id = null,
    string? Attribution = null,
    NotificationPriority? Priority = null,
    string? Icon = null,
    bool? CircleIcon = null,
    string? Image = null,
    string? HeroImage = null,
    NotificationButton[]? Buttons = null,
    string? ReplyPlaceholder = null,
    bool? Silent = null,
    int? ExpiresInSeconds = null);

/// <summary>Response to <c>showNotification</c>. <c>Shown</c> is false when the toast could not be raised.</summary>
public sealed record ShowNotificationResult(bool Shown, string Message);

[IpCommand("showNotification", nameof(ShowNotificationRequest), nameof(ShowNotificationResult), "Raises a Windows toast with optional images, buttons, a reply box and a priority.")]
internal sealed class ShowNotificationCommand;

/// <summary>
/// Pushed to the web side (<c>type: "notificationActivated"</c>) when the user clicks a toast or one of
/// its buttons. <c>Action</c> is the button id, or empty for a click on the toast body.
/// </summary>
[IpcType]
public sealed record NotificationActivation(string Type, string NotificationId, string Action, string? Reply = null);

/// <summary>
/// Input to <c>simulateNotificationActivation</c>. An empty <c>Action</c> clicks the toast body; otherwise the
/// button with that id. <c>Reply</c> fills the reply box (the toast must have one).
/// </summary>
[IpcType]
public sealed record SimulateNotificationActivationRequest(string NotificationId, string? Action = null, string? Reply = null);

/// <summary>Response to <c>simulateNotificationActivation</c>. <c>Delivered</c> is false when the toast or button is unknown.</summary>
public sealed record SimulateNotificationActivationResult(bool Delivered, string Message);

[IpCommand("simulateNotificationActivation", nameof(SimulateNotificationActivationRequest), nameof(SimulateNotificationActivationResult), "Test hook (only when the host runs with CDP_PORT): acts as if a toast or one of its buttons was clicked.")]
internal sealed class SimulateNotificationActivationCommand;
