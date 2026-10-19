using System.Runtime.InteropServices;
using System.Text.Json;
using Bermuda.Fdc3;

namespace Bermuda;

[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public class HostBridge
{
    // Allow-list is derived from the contract so the host and the generated
    // TypeScript can never drift apart.
    private static readonly HashSet<string> AllowedCommands = new(
        Bermuda.IpcContract.IpcContract.Commands.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

    private readonly DesktopAgent _fdc3;
    private readonly Func<string> _fdc3InstanceId;

    /// <param name="fdc3InstanceId">
    /// This window's current FDC3 app instance — read per call because a page
    /// reload reconnects as a new instance (see MainWindow.OnNavigationStarting).
    /// </param>
    public HostBridge(DesktopAgent fdc3, Func<string> fdc3InstanceId)
    {
        _fdc3 = fdc3;
        _fdc3InstanceId = fdc3InstanceId;
    }

    public async Task<object?> HandleAsync(string command, object? payload)
    {
        if (!AllowedCommands.Contains(command))
            return new { error = $"Unknown command: {command}" };

        try
        {
            // fdc3.* commands are the window.fdc3 Desktop Agent API (see Bermuda.Fdc3).
            if (command.StartsWith("fdc3.", StringComparison.Ordinal))
                return await _fdc3.HandleAsync(_fdc3InstanceId(), command, payload as JsonElement?);

            return command.ToLowerInvariant() switch
            {
                "ping" => new { pong = true },
                _ => new { error = "Unhandled command" }
            };
        }
        catch (Exception ex)
        {
            // Fdc3Exception messages are the standard FDC3 error strings, which
            // the frontend's window.fdc3 rejects with as-is.
            return new { error = ex.Message };
        }
    }
}
