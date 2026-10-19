using System.Runtime.InteropServices;
using System.Text.Json;
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
/// reconnects as a new instance (see MainWindow.OnNavigationStarting).
/// </param>
[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public class HostBridge(Func<IFdc3Commands> fdc3) : IHostCommands
{
    public PingResult Ping() => new(Pong: true);

    public async Task<object?> HandleAsync(string command, object? payload)
    {
        if (IpcContract.IpcContract.Find(command) is not { } ipcCommand)
            return new { error = $"Unknown command: {command}" };

        try
        {
            return await ipcCommand.InvokeAsync(TargetFor(ipcCommand.Interface), payload as JsonElement?, JsonSerializerOptions.Web);
        }
        catch (Exception ex)
        {
            // Fdc3Exception messages are the standard FDC3 error strings (and
            // IpcInvalidPayloadException's is "InvalidArguments"), which the
            // frontend's window.fdc3 rejects with as-is.
            return new { error = ex.Message };
        }
    }

    private object TargetFor(Type commands) =>
        commands == typeof(IFdc3Commands) ? fdc3()
        : commands == typeof(IHostCommands) ? this
        : throw new InvalidOperationException($"No handler for {commands.Name}.");
}
