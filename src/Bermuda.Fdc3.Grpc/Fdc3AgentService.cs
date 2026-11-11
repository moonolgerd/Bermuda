using System.Collections.Concurrent;
using System.Threading.Channels;
using Bermuda.Fdc3.Grpc.Proto;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Bermuda.Fdc3.Grpc;

/// <summary>Request header carrying the instance id from the <c>Connected</c> event.</summary>
public static class Fdc3Headers
{
    public const string Instance = "x-fdc3-instance";
}

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
        if (Mapping.ToProtoEvent(message) is { } e) _events.Writer.TryWrite(e);
    }

    public void Complete() => _events.Writer.TryComplete();
}

/// <summary>
/// The gRPC face of <see cref="DesktopAgent"/>. A <c>Subscribe</c> stream is
/// one app instance; its other RPCs are served by that instance's
/// <see cref="Fdc3Connection"/>. <see cref="Fdc3Exception"/>s become gRPC
/// errors in <see cref="Fdc3ErrorInterceptor"/>.
/// </summary>
internal sealed class Fdc3AgentService(DesktopAgent agent, Fdc3ConnectionRegistry registry) : Fdc3Agent.Fdc3AgentBase
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

    public override Task<ImplementationMetadata> GetInfo(Empty request, ServerCallContext context) =>
        Task.FromResult(Mapping.ToProto(Caller(context).GetInfo()));

    public override Task<AppMetadata> GetAppMetadata(AppIdentifier request, ServerCallContext context) =>
        Task.FromResult(Mapping.ToProto(Caller(context).GetAppMetadata(Mapping.ToContract(request))));

    public override Task<AppIdentifiers> FindInstances(AppIdentifier request, ServerCallContext context)
    {
        var result = new AppIdentifiers();
        result.Apps.AddRange(Caller(context).FindInstances(Mapping.ToContract(request)).Select(Mapping.ToProto));
        return Task.FromResult(result);
    }

    public override async Task<AppIdentifier> Open(OpenRequest request, ServerCallContext context) =>
        Mapping.ToProto(await Caller(context).OpenAsync(Mapping.ToContract(request)));

    public override Task<ChannelInfos> GetUserChannels(Empty request, ServerCallContext context)
    {
        var result = new ChannelInfos();
        result.Channels.AddRange(Caller(context).GetUserChannels().Select(Mapping.ToProto));
        return Task.FromResult(result);
    }

    public override Task<Empty> JoinUserChannel(ChannelIdRequest request, ServerCallContext context)
    {
        Caller(context).JoinUserChannel(Mapping.ToContract(request));
        return Task.FromResult(Done);
    }

    public override Task<Empty> LeaveCurrentChannel(Empty request, ServerCallContext context)
    {
        Caller(context).LeaveCurrentChannel();
        return Task.FromResult(Done);
    }

    public override Task<CurrentChannel> GetCurrentChannel(Empty request, ServerCallContext context)
    {
        var result = new CurrentChannel();
        if (Caller(context).GetCurrentChannel() is { } channel) result.Channel = Mapping.ToProto(channel);
        return Task.FromResult(result);
    }

    public override Task<ChannelInfo> GetOrCreateChannel(ChannelIdRequest request, ServerCallContext context) =>
        Task.FromResult(Mapping.ToProto(Caller(context).GetOrCreateChannel(Mapping.ToContract(request))));

    public override Task<ChannelInfo> CreatePrivateChannel(Empty request, ServerCallContext context) =>
        Task.FromResult(Mapping.ToProto(Caller(context).CreatePrivateChannel()));

    public override Task<Empty> DisconnectPrivateChannel(ChannelIdRequest request, ServerCallContext context)
    {
        Caller(context).DisconnectPrivateChannel(Mapping.ToContract(request));
        return Task.FromResult(Done);
    }

    public override Task<Empty> Broadcast(BroadcastRequest request, ServerCallContext context)
    {
        Caller(context).Broadcast(Mapping.ToContract(request));
        return Task.FromResult(Done);
    }

    public override Task<CurrentContext> GetCurrentContext(GetCurrentContextRequest request, ServerCallContext context) =>
        Task.FromResult(Mapping.ToCurrentContext(Caller(context).GetCurrentContext(Mapping.ToContract(request))));

    public override Task<Empty> AddContextListener(AddContextListenerRequest request, ServerCallContext context)
    {
        Caller(context).AddContextListener(Mapping.ToContract(request));
        return Task.FromResult(Done);
    }

    public override Task<Empty> AddIntentListener(AddIntentListenerRequest request, ServerCallContext context)
    {
        Caller(context).AddIntentListener(Mapping.ToContract(request));
        return Task.FromResult(Done);
    }

    public override Task<Empty> AddEventListener(AddEventListenerRequest request, ServerCallContext context)
    {
        Caller(context).AddEventListener(Mapping.ToContract(request));
        return Task.FromResult(Done);
    }

    public override Task<Empty> Unsubscribe(ListenerIdRequest request, ServerCallContext context)
    {
        Caller(context).Unsubscribe(Mapping.ToContract(request));
        return Task.FromResult(Done);
    }

    public override Task<AppIntent> FindIntent(FindIntentRequest request, ServerCallContext context) =>
        Task.FromResult(Mapping.ToProto(Caller(context).FindIntent(Mapping.ToContract(request))));

    public override Task<AppIntents> FindIntentsByContext(FindIntentsByContextRequest request, ServerCallContext context)
    {
        var result = new AppIntents();
        result.AppIntents_.AddRange(Caller(context).FindIntentsByContext(Mapping.ToContract(request)).Select(Mapping.ToProto));
        return Task.FromResult(result);
    }

    public override async Task<IntentResolutionInfo> RaiseIntent(RaiseIntentRequest request, ServerCallContext context) =>
        Mapping.ToProto(await Caller(context).RaiseIntentAsync(Mapping.ToContract(request)));

    public override async Task<IntentResolutionInfo> RaiseIntentForContext(RaiseIntentForContextRequest request, ServerCallContext context) =>
        Mapping.ToProto(await Caller(context).RaiseIntentForContextAsync(Mapping.ToContract(request)));

    public override async Task<IntentResultInfo> GetIntentResult(ResultIdRequest request, ServerCallContext context) =>
        Mapping.ToProto(await Caller(context).GetIntentResultAsync(Mapping.ToContract(request)));

    public override Task<Empty> SendIntentResult(SendIntentResultRequest request, ServerCallContext context)
    {
        Caller(context).SendIntentResult(Mapping.ToContract(request));
        return Task.FromResult(Done);
    }
}
