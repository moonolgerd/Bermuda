using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bermuda.Fdc3;
using Bermuda.IpcContract;

namespace Bermuda;

/// <summary>
/// Dispatches <c>invoke()</c> commands. A name is only accepted if
/// <see cref="IpcContract.IpcContract"/> found it on an <c>[IpcCommands]</c>
/// interface, and its payload is bound to that method's parameter type. The
/// allow-list, the dispatch and the generated TypeScript all come from the
/// same declarations, so they can't drift apart.
/// </summary>
/// <param name="fdc3">
/// This window's current FDC3 connection, read per call because a page reload
/// reconnects as a new instance (see MainWindow.OnContentLoading).
/// </param>
/// <param name="testHooks">Enables dev/test-only commands (MainWindow: when CDP_PORT is set).</param>
[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public class HostBridge(Func<IFdc3Commands> fdc3, bool testHooks = false) : IHostCommands
{
    /// <summary>Web preset (camelCase, case-insensitive) with enums as camelCase strings, matching the generated TS unions.</summary>
    public static JsonSerializerOptions PayloadJson { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // Commands that only make sense in dev/test runs.
    private static readonly HashSet<string> TestCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "simulateNotificationActivation",
    };

    private static readonly Dictionary<string, object> InvalidPayloadResults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["showNotification"] = new ShowNotificationResult(false, "Expected { title, body }."),
        ["simulateNotificationActivation"] = new SimulateNotificationActivationResult(false, "Expected { notificationId }."),
    };

    public PingResult Ping() => new(Pong: true);

    public Task<UpdateCheckResult> CheckForUpdateAsync() => UpdateService.CheckAsync();

    public Task<UpdateApplyResult> ApplyUpdateAsync() => UpdateService.ApplyAsync();

    public Task<ShowNotificationResult> ShowNotificationAsync(ShowNotificationRequest request) =>
        NotificationService.ShowAsync(request);

    public SimulateNotificationActivationResult SimulateNotificationActivation(SimulateNotificationActivationRequest request) =>
        NotificationService.Simulate(request);

    public async Task<object?> HandleAsync(string command, object? payload)
    {
        if (IpcContract.IpcContract.Find(command) is not { } ipcCommand)
            return new { error = $"Unknown command: {command}" };

        if (TestCommands.Contains(command) && !testHooks)
            return new { error = $"{command} is only available in dev/test runs." };

        try
        {
            return await ipcCommand.InvokeAsync(TargetFor(ipcCommand.Interface), payload as JsonElement?, PayloadJson);
        }
        catch (IpcInvalidPayloadException) when (payload is not JsonElement { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) }
                                              && InvalidPayloadResults.TryGetValue(command, out var result))
        {
            // A missing payload is reported in these commands' normal result shape.
            return result;
        }
        catch (Exception ex) when (ex is Fdc3Exception or IpcInvalidPayloadException)
        {
            // Fdc3Exception messages are the standard FDC3 error strings (and
            // IpcInvalidPayloadException's is "InvalidArguments"), which the
            // frontend's window.fdc3 rejects with as-is.
            return new { error = ex.Message };
        }
        catch (Exception)
        {
            // Internal failures must not leak their message to page script.
            return new { error = "Internal error" };
        }
    }

    private object TargetFor(Type commands) =>
        commands == typeof(IFdc3Commands) ? fdc3()
        : commands == typeof(IHostCommands) ? this
        : throw new InvalidOperationException($"No handler for {commands.Name}.");
}
