using System.Collections.Concurrent;
using System.Threading.Channels;
using Bermuda.Fdc3.Grpc.Proto;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Bermuda.Fdc3.Grpc;

/// <summary>Instances currently subscribed through gRPC, and the pipe connection each belongs to.</summary>
internal sealed class Fdc3ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, (Fdc3Connection Connection, string TransportConnectionId)> _live = new();

    public void Add(Fdc3Connection connection, string transportConnectionId) =>
        _live[connection.InstanceId] = (connection, transportConnectionId);

    public void Remove(string instanceId) => _live.TryRemove(instanceId, out _);

    public int Count => _live.Count;

    /// <summary>The caller's connection; only valid on the pipe connection it subscribed on.</summary>
    public Fdc3Connection Resolve(ServerCallContext context)
    {
        var instance = context.RequestHeaders.GetValue(Fdc3Headers.Instance);
        var transportId = context.GetHttpContext().Connection.Id;
        if (instance is null || !_live.TryGetValue(instance, out var entry) || entry.TransportConnectionId != transportId)
            throw new Fdc3Exception(Fdc3Errors.AccessDenied);
        return entry.Connection;
    }
}

/// <summary>Queues the agent's deliveries for the Subscribe stream.</summary>
internal sealed class ChannelSink : IFdc3AppSink
{
    private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<AgentEvent> Reader => _events.Reader;

    public void Post(object message)
    {
        if (GeneratedMapping.ToProtoEvent(message) is { } e) _events.Writer.TryWrite(e);
    }

    public void Complete() => _events.Writer.TryComplete();
}

/// <summary>
/// The gRPC face of <see cref="DesktopAgent"/>. A <c>Subscribe</c> stream is
/// one app instance; its other RPCs (generated, see Generated/Fdc3Generated.g.cs) are served by that instance's
/// <see cref="Fdc3Connection"/>. <see cref="Fdc3Exception"/>s become gRPC
/// errors in <see cref="Fdc3ErrorInterceptor"/>.
/// </summary>
internal sealed partial class Fdc3AgentService(DesktopAgent agent, Fdc3ConnectionRegistry registry) : Fdc3Agent.Fdc3AgentBase
{
    private static readonly Empty Done = new();

    public override async Task Subscribe(SubscribeRequest request, IServerStreamWriter<AgentEvent> responseStream, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.AppId)) throw new Fdc3Exception(Fdc3Errors.InvalidArguments);
        if (agent.Directory.Find(request.AppId) is not { } app) throw new Fdc3Exception(Fdc3Errors.AppNotFound);

        var sink = new ChannelSink();
        // The directory's casing wins, so instances of one app always share an AppId.
        using var connection = agent.Connect(app.AppId, sink);
        registry.Add(connection, context.GetHttpContext().Connection.Id);
        try
        {
            await responseStream.WriteAsync(new AgentEvent
            {
                Connected = new Connected { InstanceId = connection.InstanceId, AppId = app.AppId },
            });
            await foreach (var e in sink.Reader.ReadAllAsync(context.CancellationToken))
                await responseStream.WriteAsync(e);
        }
        catch (OperationCanceledException)
        {
            // Client went away (or the host is stopping): fall through to disconnect.
        }
        finally
        {
            registry.Remove(connection.InstanceId);
            sink.Complete();
        }
    }

    private Fdc3Connection Caller(ServerCallContext context) => registry.Resolve(context);
}
