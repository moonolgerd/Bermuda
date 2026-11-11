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
//   * handles the `ViewIncident` intent and returns the same ack as its result
//
// Config (environment): FDC3_GRPC_PIPE (default: Bermuda's per-user pipe), NATIVE_CHANNEL.
// It reconnects whenever the host goes away, so start order doesn't matter.

const string AppId = "native-incidents";
const string AckType = "bermuda.native.ack";

var pipe = Environment.GetEnvironmentVariable("FDC3_GRPC_PIPE");
var channel = Environment.GetEnvironmentVariable("NATIVE_CHANNEL") is { Length: > 0 } c ? c : "fdc3.channel.1";

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

while (!shutdown.IsCancellationRequested)
{
    try
    {
        await using var client = await Fdc3PipeClient.ConnectAsync(pipe, AppId, shutdown.Token);
        Console.WriteLine($"[{AppId}] connected as {client.InstanceId}");
        await RunAsync(client, shutdown.Token);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
    }
    catch (Exception ex) when (ex is RpcException or IOException or TimeoutException or InvalidOperationException)
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
        new AddIntentListenerRequest { ListenerId = "view", Intent = "ViewIncident" },
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

static string Ack(string contextJson) =>
    JsonSerializer.Serialize(new { type = AckType, of = JsonSerializer.Deserialize<JsonElement>(contextJson) });
