using System.Runtime.InteropServices;
using System.Text.Json;

namespace Bermuda;

[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public class HostBridge
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ping",
    };

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
