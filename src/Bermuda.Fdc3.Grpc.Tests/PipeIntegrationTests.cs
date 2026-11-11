using System.Collections.Concurrent;
using System.Text.Json;
using Bermuda.Fdc3.Grpc.Proto;
using Bermuda.IpcContract;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Bermuda.Fdc3.Grpc.Tests;

/// <summary>Real named pipe, real Kestrel, real gRPC: native clients next to an in-process (web-window-style) connection.</summary>
public sealed class PipeIntegrationTests : IAsyncLifetime
{
    private const string AppD = """
        {
          "applications": [
            { "appId": "native-app", "title": "Native", "type": "native" },
            {
              "appId": "incidents",
              "title": "Incidents",
              "type": "native",
              "interop": { "intents": { "listensFor": {
                "ViewIncident": { "contexts": ["bermuda.incident"], "resultType": "fdc3.contact" }
              } } }
            },
            {
              "appId": "web-app",
              "title": "Web",
              "interop": { "intents": { "listensFor": {
                "ViewMap": { "contexts": ["bermuda.incident"] }
              } } }
            }
          ]
        }
        """;

    private const string Incident = """{ "type": "bermuda.incident", "id": { "incidentId": "7" } }""";

    private readonly PipeLauncher _launcher = new();
    private readonly DesktopAgent _agent;
    private readonly string _pipe = "bermuda-fdc3-test-" + Guid.NewGuid().ToString("N");
    private readonly List<IAsyncDisposable> _clients = [];
    private Fdc3GrpcHost _host = null!;

    public PipeIntegrationTests()
    {
        _agent = new DesktopAgent(AppDirectory.Parse(AppD), _launcher, launchTimeout: TimeSpan.FromSeconds(2));
    }

    /// <summary>Stands in for Bermuda's NativeAppHost: "starts" the native app, which connects with the instance id it was given.</summary>
    private sealed class PipeLauncher : IFdc3AppLauncher
    {
        public Func<AppDefinition, string, Task>? Start { get; set; }

        public void Launch(AppDefinition app, string instanceId) => _ = Start?.Invoke(app, instanceId);
    }

    public async Task InitializeAsync() => _host = await Fdc3GrpcHost.StartAsync(_agent, _pipe);

    public async Task DisposeAsync()
    {
        foreach (var client in _clients) await client.DisposeAsync();
        await _host.DisposeAsync();
    }

    private async Task<Fdc3PipeClient> ConnectAsync(string appId, string? instanceId = null)
    {
        var client = await Fdc3PipeClient.ConnectAsync(_pipe, appId, instanceId: instanceId);
        _clients.Add(client);
        return client;
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public async Task Rejects_an_app_id_that_is_not_in_the_app_directory()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() => Fdc3PipeClient.ConnectAsync(_pipe, "nobody"));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
        Assert.Equal(Fdc3Errors.AppNotFound, ex.Status.Detail);
    }

    [Fact]
    public async Task Rejects_a_web_app_because_only_native_apps_use_the_pipe()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() => Fdc3PipeClient.ConnectAsync(_pipe, "web-app"));

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Equal(Fdc3Errors.AccessDenied, ex.Status.Detail);
    }

    [Fact]
    public async Task Open_launches_a_native_app_that_connects_with_the_instance_id_it_was_given()
    {
        _launcher.Start = async (app, instanceId) => await ConnectAsync(app.AppId, instanceId);
        using var web = _agent.Connect("web-app", new RecordingSink());

        var opened = await web.OpenAsync(new Fdc3OpenRequest(new Fdc3AppIdentifier("native-app", null), null));

        Assert.Equal("native-app", opened.AppId);
        Assert.NotNull(opened.InstanceId);
        Assert.Contains(web.FindInstances(new Fdc3AppIdentifier("native-app", null)), i => i.InstanceId == opened.InstanceId);
    }

    [Fact]
    public async Task Connecting_registers_an_instance_that_is_removed_when_the_client_goes_away()
    {
        var client = await ConnectAsync("native-app");
        using var observer = _agent.Connect("web-app", new RecordingSink());

        var instances = observer.FindInstances(new Fdc3AppIdentifier("native-app"));
        Assert.Equal(client.InstanceId, Assert.Single(instances).InstanceId);

        var info = await client.Rpc.GetInfoAsync(new Empty(), client.Headers);
        Assert.Equal("2.2", info.Fdc3Version);
        Assert.Equal("native-app", info.AppMetadata.AppId);
        Assert.Equal(client.InstanceId, info.AppMetadata.InstanceId);

        await client.DisposeAsync();
        await EventuallyAsync(() => observer.FindInstances(new Fdc3AppIdentifier("native-app")).Length == 0);
    }

    [Fact]
    public async Task Channel_broadcast_flows_between_a_pipe_client_and_an_in_process_connection()
    {
        var native = await ConnectAsync("native-app");
        var webSink = new RecordingSink();
        using var web = _agent.Connect("web-app", webSink);

        await native.Rpc.JoinUserChannelAsync(new ChannelIdRequest { ChannelId = "fdc3.channel.1" }, native.Headers);
        await native.Rpc.AddContextListenerAsync(
            new AddContextListenerRequest { ListenerId = "n1", ContextType = "bermuda.incident" }, native.Headers);
        web.JoinUserChannel(new Fdc3ChannelIdRequest("fdc3.channel.1"));
        web.AddContextListener(new Fdc3AddContextListenerRequest("w1", null, "bermuda.incident"));

        // web -> pipe
        web.Broadcast(new Fdc3BroadcastRequest(null, Json(Incident)));
        var received = (await native.NextEventAsync()).Context;
        Assert.Equal("n1", received.ListenerId);
        Assert.Equal("bermuda.incident", Json(received.ContextJson).GetProperty("type").GetString());
        Assert.Equal("web-app", received.Source.AppId);

        // pipe -> web
        await native.Rpc.BroadcastAsync(new BroadcastRequest { ContextJson = """{ "type": "bermuda.incident", "id": { "incidentId": "9" } }""" }, native.Headers);
        var delivered = await webSink.NextAsync<Fdc3ContextEvent>();
        Assert.Equal("w1", delivered.ListenerId);
        Assert.Equal("9", delivered.Context.GetProperty("id").GetProperty("incidentId").GetString());
        Assert.Equal("native-app", delivered.Source!.AppId);

        // a late joiner on the channel sees the current context
        var current = await native.Rpc.GetCurrentContextAsync(
            new GetCurrentContextRequest { ChannelId = "fdc3.channel.1" }, native.Headers);
        Assert.Contains("\"9\"", current.ResultJson);
    }

    [Fact]
    public async Task Two_pipe_clients_share_a_channel()
    {
        var a = await ConnectAsync("native-app");
        var b = await ConnectAsync("native-app");
        foreach (var c in new[] { a, b })
        {
            await c.Rpc.JoinUserChannelAsync(new ChannelIdRequest { ChannelId = "fdc3.channel.2" }, c.Headers);
            await c.Rpc.AddContextListenerAsync(new AddContextListenerRequest { ListenerId = "l" }, c.Headers);
        }

        await a.Rpc.BroadcastAsync(new BroadcastRequest { ContextJson = Incident }, a.Headers);

        Assert.Equal("l", (await b.NextEventAsync()).Context.ListenerId);
    }

    [Fact]
    public async Task Intent_round_trip_from_an_in_process_raiser_to_a_pipe_handler()
    {
        var native = await ConnectAsync("incidents");
        using var web = _agent.Connect("web-app", new RecordingSink());
        await native.Rpc.AddIntentListenerAsync(
            new AddIntentListenerRequest { ListenerId = "i1", Intent = "ViewIncident" }, native.Headers);

        var resolution = await web.RaiseIntentAsync(new Fdc3RaiseIntentRequest("ViewIncident", Json(Incident), null));
        Assert.Equal("incidents", resolution.Source.AppId);

        var e = (await native.NextEventAsync()).Intent;
        Assert.Equal("i1", e.ListenerId);
        Assert.Equal("ViewIncident", e.Intent);
        Assert.Equal("web-app", e.Source.AppId);
        Assert.Equal(resolution.ResultId, e.ResultId);

        await native.Rpc.SendIntentResultAsync(new SendIntentResultRequest
        {
            ResultId = e.ResultId,
            ContextJson = """{ "type": "fdc3.contact", "name": "Ann" }""",
        }, native.Headers);

        var result = await web.GetIntentResultAsync(new Fdc3ResultIdRequest(resolution.ResultId));
        Assert.Equal("Ann", result.Context!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Intent_round_trip_from_a_pipe_raiser_to_an_in_process_handler()
    {
        var native = await ConnectAsync("native-app");
        var webSink = new RecordingSink();
        using var web = _agent.Connect("web-app", webSink);
        web.AddIntentListener(new Fdc3AddIntentListenerRequest("h1", "ViewMap"));

        var resolution = await native.Rpc.RaiseIntentAsync(
            new RaiseIntentRequest { Intent = "ViewMap", ContextJson = Incident }, native.Headers);
        Assert.Equal("ViewMap", resolution.Intent);

        var delivered = await webSink.NextAsync<Fdc3IntentEvent>();
        Assert.Equal("native-app", delivered.Source!.AppId);
        web.SendIntentResult(new Fdc3SendIntentResultRequest(
            delivered.ResultId, Json("""{ "type": "fdc3.contact", "name": "Bo" }"""), null, null));

        var result = await native.Rpc.GetIntentResultAsync(new ResultIdRequest { ResultId = resolution.ResultId }, native.Headers);
        Assert.Equal("Bo", Json(result.ContextJson).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Errors_carry_the_fdc3_error_string_as_the_status_detail()
    {
        var native = await ConnectAsync("native-app");

        var unknownChannel = await Assert.ThrowsAsync<RpcException>(async () =>
            await native.Rpc.JoinUserChannelAsync(new ChannelIdRequest { ChannelId = "nope" }, native.Headers));
        Assert.Equal(Fdc3Errors.NoChannelFound, unknownChannel.Status.Detail);

        var noTarget = await Assert.ThrowsAsync<RpcException>(async () =>
            await native.Rpc.RaiseIntentAsync(new RaiseIntentRequest { Intent = "Nothing", ContextJson = Incident }, native.Headers));
        Assert.Equal(Fdc3Errors.NoAppsFound, noTarget.Status.Detail);

        var malformed = await Assert.ThrowsAsync<RpcException>(async () =>
            await native.Rpc.BroadcastAsync(new BroadcastRequest { ContextJson = "{ not json" }, native.Headers));
        Assert.Equal(Fdc3Errors.MalformedContext, malformed.Status.Detail);
        Assert.Equal(StatusCode.InvalidArgument, malformed.StatusCode);
    }

    [Fact]
    public async Task Calls_without_a_valid_instance_header_are_denied()
    {
        var native = await ConnectAsync("native-app");

        var missing = await Assert.ThrowsAsync<RpcException>(async () => await native.Rpc.GetInfoAsync(new Empty()));
        Assert.Equal(Fdc3Errors.AccessDenied, missing.Status.Detail);

        var wrong = await Assert.ThrowsAsync<RpcException>(async () =>
            await native.Rpc.GetInfoAsync(new Empty(), new Metadata { { Fdc3Headers.Instance, "someone-else" } }));
        Assert.Equal(StatusCode.PermissionDenied, wrong.StatusCode);
    }

    [Fact]
    public async Task An_instance_id_does_not_work_from_another_pipe_connection()
    {
        var native = await ConnectAsync("native-app");
        var other = await ConnectAsync("native-app");

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await other.Rpc.GetInfoAsync(new Empty(), native.Headers));

        Assert.Equal(Fdc3Errors.AccessDenied, ex.Status.Detail);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(25);
        }
    }

    private sealed class RecordingSink : IFdc3AppSink
    {
        private readonly BlockingCollection<object> _messages = [];

        public void Post(object message) => _messages.Add(message);

        public Task<T> NextAsync<T>() => Task.Run(() =>
        {
            if (!_messages.TryTake(out var message, TimeSpan.FromSeconds(5)))
                throw new TimeoutException("No message arrived.");
            return Assert.IsType<T>(message);
        });
    }
}
