using System.Text.Json;
using Bermuda.IpcContract;

namespace Bermuda.Fdc3;

/// <summary>
/// One app instance's handle on the <see cref="DesktopAgent"/>, returned by
/// <see cref="DesktopAgent.Connect"/>. It implements the <c>fdc3.*</c> command
/// interface for that instance, so the host can dispatch a page's commands to
/// it directly. Every call acts as this instance (source metadata, channel
/// membership, listeners). Dispose it when the page goes away. Calls made
/// after that throw <c>AccessDenied</c>.
/// </summary>
public sealed class Fdc3Connection : IFdc3Commands, IDisposable
{
    private readonly DesktopAgent _agent;

    internal Fdc3Connection(DesktopAgent agent, string appId, string instanceId)
    {
        _agent = agent;
        Identity = new Fdc3AppIdentifier(appId, instanceId);
    }

    public Fdc3AppIdentifier Identity { get; }

    public string InstanceId => Identity.InstanceId!;

    /// <summary>Looked up per call, so a disconnected instance fails with AccessDenied.</summary>
    private DesktopAgent.AppInstance Caller => _agent.GetInstance(InstanceId);

    public void Dispose() => _agent.Disconnect(InstanceId);

    public Fdc3ImplementationMetadata GetInfo() => _agent.GetInfo(Caller);
    public Fdc3AppMetadata GetAppMetadata(Fdc3AppIdentifier app) => _agent.GetAppMetadata(app);
    public Fdc3AppIdentifier[] FindInstances(Fdc3AppIdentifier app) => _agent.FindInstances(app);
    public Task<Fdc3AppIdentifier> OpenAsync(Fdc3OpenRequest request) => _agent.OpenAsync(request);

    public Fdc3ChannelInfo[] GetUserChannels() => _agent.GetUserChannels();
    public void JoinUserChannel(Fdc3ChannelIdRequest request) => _agent.JoinUserChannel(Caller, request.ChannelId);
    public void LeaveCurrentChannel() => _agent.LeaveCurrentChannel(Caller);
    public Fdc3ChannelInfo? GetCurrentChannel() => _agent.GetCurrentChannel(Caller);
    public Fdc3ChannelInfo GetOrCreateChannel(Fdc3ChannelIdRequest request) => _agent.GetOrCreateChannel(request.ChannelId);
    public Fdc3ChannelInfo CreatePrivateChannel() => _agent.CreatePrivateChannel(Caller);
    public void DisconnectPrivateChannel(Fdc3ChannelIdRequest request) => _agent.DisconnectPrivateChannel(Caller, request.ChannelId);

    public void Broadcast(Fdc3BroadcastRequest request) => _agent.Broadcast(Caller, request);
    public JsonElement? GetCurrentContext(Fdc3GetCurrentContextRequest request) => _agent.GetCurrentContext(Caller, request);
    public void AddContextListener(Fdc3AddContextListenerRequest request) => _agent.AddContextListener(Caller, request);
    public void AddIntentListener(Fdc3AddIntentListenerRequest request) => _agent.AddIntentListener(Caller, request);
    public void AddEventListener(Fdc3AddEventListenerRequest request) => _agent.AddEventListener(Caller, request);
    public void Unsubscribe(Fdc3ListenerIdRequest request) => _agent.Unsubscribe(Caller, request.ListenerId);

    public Fdc3AppIntent FindIntent(Fdc3FindIntentRequest request) => _agent.FindIntent(request);
    public Fdc3AppIntent[] FindIntentsByContext(Fdc3FindIntentsByContextRequest request) => _agent.FindIntentsByContext(request);
    public Task<Fdc3IntentResolutionInfo> RaiseIntentAsync(Fdc3RaiseIntentRequest request) => _agent.RaiseIntentAsync(Caller, request);
    public Task<Fdc3IntentResolutionInfo> RaiseIntentForContextAsync(Fdc3RaiseIntentForContextRequest request) => _agent.RaiseIntentForContextAsync(Caller, request);
    public Task<Fdc3IntentResultInfo> GetIntentResultAsync(Fdc3ResultIdRequest request) => _agent.GetIntentResultAsync(Caller, request.ResultId);
    public void SendIntentResult(Fdc3SendIntentResultRequest request) => _agent.SendIntentResult(Caller, request);
}
