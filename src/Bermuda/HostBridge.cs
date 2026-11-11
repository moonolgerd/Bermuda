using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bermuda.IpcContract;

namespace Bermuda;

[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public class HostBridge
{
    // Allow-list is derived from the contract so the host and the generated
    // TypeScript can never drift apart.
    private static readonly HashSet<string> AllowedCommands = new(
        Bermuda.IpcContract.IpcContract.Commands.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static async Task<ShowNotificationResult> ShowNotificationAsync(object? payload)
    {
        var request = payload is JsonElement json
            ? json.Deserialize<ShowNotificationRequest>(PayloadJson)
            : null;
        return request is null
            ? new(false, "Expected { title, body }.")
            : await NotificationService.ShowAsync(request);
    }

    // Commands that only make sense in dev/test runs (see MainWindow: enabled when CDP_PORT is set).
    private static readonly HashSet<string> TestCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "simulateNotificationActivation",
    };

    private readonly bool _testHooks;

    public HostBridge(bool testHooks = false) => _testHooks = testHooks;

    private static SimulateNotificationActivationResult SimulateNotificationActivation(object? payload)
    {
        var request = payload is JsonElement json
            ? json.Deserialize<SimulateNotificationActivationRequest>(PayloadJson)
            : null;
        return request is null
            ? new(false, "Expected { notificationId }.")
            : NotificationService.Simulate(request);
    }

    public async Task<object?> HandleAsync(string command, object? payload)
    {
        if (!AllowedCommands.Contains(command))
            return new { error = $"Unknown command: {command}" };

        if (TestCommands.Contains(command) && !_testHooks)
            return new { error = $"{command} is only available in dev/test runs." };

        try
        {
            return command.ToLowerInvariant() switch
            {
                "ping" => new { pong = true },
                "checkforupdate" => await UpdateService.CheckAsync(),
                "applyupdate" => await UpdateService.ApplyAsync(),
                "shownotification" => await ShowNotificationAsync(payload),
                "simulatenotificationactivation" => SimulateNotificationActivation(payload),
                _ => new { error = "Unhandled command" }
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }
}
