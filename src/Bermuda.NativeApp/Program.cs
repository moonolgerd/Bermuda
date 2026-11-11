using System.Text.Json;
using Bermuda.Fdc3.Grpc;
using Bermuda.Fdc3.Grpc.Proto;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

// Native FDC3 side app for Bermuda.
//
//   * joins user channel NATIVE_CHANNEL (default fdc3.channel.1)
//   * listens for `bermuda.incident` contexts there and answers each by
//     broadcasting `bermuda.native.ack` carrying the incident it saw
//   * handles the `AcknowledgeIncident` intent and returns the same ack as its result
//     (not `ViewIncident`: the dashboard handles that, and an untargeted raise must keep resolving to it)
//
// Config (environment): FDC3_GRPC_PIPE (default: Bermuda's per-user pipe), NATIVE_CHANNEL, and FDC3_INSTANCE_ID,
// which Bermuda sets when it launches the app for fdc3.open or an intent.
// It reconnects whenever the host goes away, so start order doesn't matter.

const string AppId = "native-incidents";
const string AckType = "bermuda.native.ack";

var pipe = Environment.GetEnvironmentVariable("FDC3_GRPC_PIPE") is { Length: > 0 } p ? p : null;
var launchInstance = Environment.GetEnvironmentVariable("FDC3_INSTANCE_ID") is { Length: > 0 } i ? i : null;
var channel = Environment.GetEnvironmentVariable("NATIVE_CHANNEL") is { Length: > 0 } c ? c : "fdc3.channel.1";

// One copy per user: Windows starts it at sign-in and Bermuda starts it too; a second copy just exits.
using var singleInstance = new Mutex(true, @"Local\Bermuda.NativeApp." + AppId, out var first);
if (!first) return;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

while (!shutdown.IsCancellationRequested)
{
    try
    {
        // The launch instance id is for the first connection only; a reconnect is a new instance.
        await using var client = await Fdc3PipeClient.ConnectAsync(pipe, AppId, shutdown.Token, launchInstance);
        launchInstance = null;
        Console.WriteLine($"[{AppId}] connected as {client.InstanceId}");
        await RunAsync(client, shutdown.Token);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
    }
    // Any failure ends this connection, not the process: Bermuda only starts this exe again on demand.
    catch (Exception ex)
    {
        Console.WriteLine($"[{AppId}] {ex.GetType().Name}: {ex.Message}");
    }

    try { await Task.Delay(TimeSpan.FromSeconds(1), shutdown.Token); }
    catch (OperationCanceledException) { }
}

async Task RunAsync(Fdc3PipeClient client, CancellationToken cancellationToken)
{
    await client.Rpc.JoinUserChannelAsync(new ChannelIdRequest { ChannelId = channel }, client.Headers, cancellationToken: cancellationToken);
    await client.Rpc.AddContextListenerAsync(
        new AddContextListenerRequest { ListenerId = "incidents", ContextType = "bermuda.incident" },
        client.Headers, cancellationToken: cancellationToken);
    await client.Rpc.AddIntentListenerAsync(
        new AddIntentListenerRequest { ListenerId = "ack", Intent = "AcknowledgeIncident" },
        client.Headers, cancellationToken: cancellationToken);

    while (!cancellationToken.IsCancellationRequested)
    {
        var e = await client.NextEventAsync(Timeout.InfiniteTimeSpan, cancellationToken);
        switch (e.KindCase)
        {
            case AgentEvent.KindOneofCase.Context:
                Console.WriteLine($"[{AppId}] context from {e.Context.Source?.AppId}: {e.Context.ContextJson}");
                await client.Rpc.BroadcastAsync(new BroadcastRequest { ContextJson = Ack(e.Context.ContextJson) },
                    client.Headers, cancellationToken: cancellationToken);
                break;

            case AgentEvent.KindOneofCase.Intent:
                Console.WriteLine($"[{AppId}] {e.Intent.Intent} from {e.Intent.Source?.AppId}: {e.Intent.ContextJson}");
                await client.Rpc.SendIntentResultAsync(
                    new SendIntentResultRequest { ResultId = e.Intent.ResultId, ContextJson = Ack(e.Intent.ContextJson) },
                    client.Headers, cancellationToken: cancellationToken);
                break;
        }
    }
}

// A payload that is not JSON is acknowledged with of: null instead of tearing the app down.
static string Ack(string contextJson)
{
    JsonElement? of = null;
    try { of = JsonSerializer.Deserialize<JsonElement>(contextJson); }
    catch (JsonException) { }
    return JsonSerializer.Serialize(new { type = AckType, of });
}
