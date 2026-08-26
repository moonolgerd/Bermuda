using System.Runtime.InteropServices;

namespace Bermuda;

[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public class HostBridge
{
    // Allow-list is derived from the contract so the host and the generated
    // TypeScript can never drift apart.
    private static readonly HashSet<string> AllowedCommands = new(
        Bermuda.IpcContract.IpcContract.Commands.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

    public async Task<object?> HandleAsync(string command, object? payload)
    {
        if (!AllowedCommands.Contains(command))
            return new { error = $"Unknown command: {command}" };

        try
        {
            return command.ToLowerInvariant() switch
            {
                "ping" => new { pong = true },
                _ => new { error = "Unhandled command" }
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }
}
