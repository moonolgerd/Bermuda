using System.Text.Json;
using Bermuda.IpcContract;

namespace Bermuda.Fdc3.Tests;

public class DesktopAgentTests
{
    private const string AppD = """
        {
          "applications": [
            {
              "appId": "incidents",
              "title": "Incidents",
              "details": { "url": "/" },
              "interop": { "intents": { "listensFor": {
                "ViewIncident": { "displayName": "View Incident", "contexts": ["bermuda.incident"] }
              } } }
            },
            {
              "appId": "map",
              "title": "Map",
              "details": { "url": "/map" },
              "interop": { "intents": { "listensFor": {
                "ViewMap": { "contexts": ["bermuda.incident", "fdc3.location"], "resultType": "fdc3.location" }
              } } }
            }
          ]
        }
        """;

    private static readonly JsonElement Incident = Json("""{ "type": "bermuda.incident", "id": { "incidentId": "7" } }""");
    private static readonly JsonElement Contact = Json("""{ "type": "fdc3.contact", "name": "Ann" }""");

    private readonly FakeLauncher _launcher = new();
    private readonly DesktopAgent _agent;

    public DesktopAgentTests()
    {
        _agent = new DesktopAgent(AppDirectory.Parse(AppD), _launcher, TimeSpan.FromSeconds(2));
        _launcher.Agent = _agent;
    }

    // Channels & context ---------------------------------------------------------

    [Fact]
    public async Task Exposes_the_eight_recommended_user_channels()
    {
        var a = Connect("incidents");

        var channels = (Fdc3ChannelInfo[])(await Call(a, "fdc3.getUserChannels"))!;

        Assert.Equal(8, channels.Length);
        Assert.All(channels, c => Assert.Equal("user", c.Type));
        Assert.Equal("fdc3.channel.1", channels[0].Id);
    }

    [Fact]
    public async Task Broadcast_reaches_listeners_on_the_same_user_channel_only()
    {
        var a = Connect("incidents");
        var b = Connect("map");
        var c = Connect("map");

        await Call(a, "fdc3.joinUserChannel", new { channelId = "fdc3.channel.1" });
        await Call(b, "fdc3.joinUserChannel", new { channelId = "fdc3.channel.1" });
        await Call(c, "fdc3.joinUserChannel", new { channelId = "fdc3.channel.2" });
        await Call(a, "fdc3.addContextListener", new { listenerId = "la", channelId = (string?)null, contextType = (string?)null });
        await Call(b, "fdc3.addContextListener", new { listenerId = "lb", channelId = (string?)null, contextType = "bermuda.incident" });
        await Call(c, "fdc3.addContextListener", new { listenerId = "lc", channelId = (string?)null, contextType = (string?)null });

        await Call(a, "fdc3.broadcast", new { channelId = (string?)null, context = Incident });

        var received = Assert.IsType<Fdc3ContextEvent>(Assert.Single(b.Sink.Messages));
        Assert.Equal("lb", received.ListenerId);
        Assert.Equal(new Fdc3AppIdentifier("incidents", a.InstanceId), received.Source);
        Assert.Empty(a.Sink.Messages); // never echoed back to the sender
        Assert.Empty(c.Sink.Messages); // different channel
    }

    [Fact]
    public async Task Context_type_filter_is_respected()
    {
        var a = Connect("incidents");
        var b = Connect("map");
        await Call(a, "fdc3.joinUserChannel", new { channelId = "fdc3.channel.3" });
        await Call(b, "fdc3.joinUserChannel", new { channelId = "fdc3.channel.3" });
        await Call(b, "fdc3.addContextListener", new { listenerId = "lb", contextType = "bermuda.incident" });

        await Call(a, "fdc3.broadcast", new { context = Contact });

        Assert.Empty(b.Sink.Messages);
    }

    [Fact]
    public async Task Joining_a_channel_delivers_its_current_context()
    {
        var a = Connect("incidents");
        var b = Connect("map");
        await Call(a, "fdc3.joinUserChannel", new { channelId = "fdc3.channel.4" });
        await Call(a, "fdc3.broadcast", new { context = Incident });
        await Call(b, "fdc3.addContextListener", new { listenerId = "lb" });

        await Call(b, "fdc3.joinUserChannel", new { channelId = "fdc3.channel.4" });

        var received = Assert.IsType<Fdc3ContextEvent>(Assert.Single(b.Sink.Messages));
        Assert.Equal("7", received.Context.GetProperty("id").GetProperty("incidentId").GetString());
    }

    [Fact]
    public async Task Broadcast_when_not_joined_is_a_no_op()
    {
        var a = Connect("incidents");
        await Call(a, "fdc3.broadcast", new { context = Incident });
        Assert.Null(await Call(a, "fdc3.getCurrentChannel"));
    }

    [Fact]
    public async Task Malformed_context_is_rejected()
    {
        var a = Connect("incidents");
        await Call(a, "fdc3.joinUserChannel", new { channelId = "fdc3.channel.1" });

        var error = await Assert.ThrowsAsync<Fdc3Exception>(() =>
            Call(a, "fdc3.broadcast", new { context = Json("""{ "name": "no type" }""") }));
        Assert.Equal(Fdc3Errors.MalformedContext, error.Message);
    }

    [Fact]
    public async Task Joining_an_unknown_channel_fails()
    {
        var a = Connect("incidents");
        var error = await Assert.ThrowsAsync<Fdc3Exception>(() => Call(a, "fdc3.joinUserChannel", new { channelId = "nope" }));
        Assert.Equal(Fdc3Errors.NoChannelFound, error.Message);
    }

    [Fact]
    public async Task User_channel_changes_fire_events()
    {
        var a = Connect("incidents");
        await Call(a, "fdc3.addEventListener", new { listenerId = "e1", eventType = "userChannelChanged" });

        await Call(a, "fdc3.joinUserChannel", new { channelId = "fdc3.channel.5" });
        await Call(a, "fdc3.leaveCurrentChannel");

        Assert.Equal(2, a.Sink.Messages.OfType<Fdc3ApiEvent>().Count(e => e.EventType == "userChannelChanged"));
    }

    [Fact]
    public async Task App_channels_are_shared_by_name_and_keep_current_context()
    {
        var a = Connect("incidents");
        var b = Connect("map");

        var channel = (Fdc3ChannelInfo)(await Call(a, "fdc3.getOrCreateChannel", new { channelId = "ops" }))!;
        Assert.Equal("app", channel.Type);
        await Call(b, "fdc3.getOrCreateChannel", new { channelId = "ops" });
        await Call(b, "fdc3.addContextListener", new { listenerId = "lb", channelId = "ops" });

        await Call(a, "fdc3.broadcast", new { channelId = "ops", context = Incident });

        Assert.Single(b.Sink.Messages);
        var current = (JsonElement?)await Call(b, "fdc3.getCurrentContext", new { channelId = "ops", contextType = "bermuda.incident" });
        Assert.Equal("bermuda.incident", current!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public async Task User_channels_cannot_be_fetched_as_app_channels()
    {
        var a = Connect("incidents");
        var error = await Assert.ThrowsAsync<Fdc3Exception>(() => Call(a, "fdc3.getOrCreateChannel", new { channelId = "fdc3.channel.1" }));
        Assert.Equal(Fdc3Errors.AccessDenied, error.Message);
    }

    // Intents ------------------------------------------------------------------------

    [Fact]
    public async Task FindIntent_lists_running_listeners_and_directory_apps()
    {
        var a = Connect("incidents");
        await Call(a, "fdc3.addIntentListener", new { listenerId = "i1", intent = "ViewIncident" });

        var appIntent = (Fdc3AppIntent)(await Call(a, "fdc3.findIntent", new { intent = "ViewIncident", context = Incident }))!;

        Assert.Equal("View Incident", appIntent.Intent.DisplayName);
        Assert.Contains(appIntent.Apps, app => app.AppId == "incidents" && app.InstanceId == a.InstanceId);
        Assert.Contains(appIntent.Apps, app => app.AppId == "incidents" && app.InstanceId is null);
    }

    [Fact]
    public async Task FindIntent_with_no_handlers_fails()
    {
        var a = Connect("incidents");
        var error = await Assert.ThrowsAsync<Fdc3Exception>(() => Call(a, "fdc3.findIntent", new { intent = "StartChat" }));
        Assert.Equal(Fdc3Errors.NoAppsFound, error.Message);
    }

    [Fact]
    public async Task FindIntentsByContext_uses_declared_contexts_and_result_types()
    {
        var a = Connect("incidents");

        var all = (Fdc3AppIntent[])(await Call(a, "fdc3.findIntentsByContext", new { context = Incident }))!;
        Assert.Equal(["ViewIncident", "ViewMap"], all.Select(i => i.Intent.Name));

        var located = (Fdc3AppIntent[])(await Call(a, "fdc3.findIntentsByContext", new { context = Incident, resultType = "fdc3.location" }))!;
        Assert.Equal("ViewMap", Assert.Single(located).Intent.Name);
    }

    [Fact]
    public async Task RaiseIntent_delivers_to_the_running_listener_and_returns_its_result()
    {
        var raiser = Connect("map");
        var handler = Connect("incidents");
        await Call(handler, "fdc3.addIntentListener", new { listenerId = "i1", intent = "ViewIncident" });

        var resolution = (Fdc3IntentResolutionInfo)(await Call(raiser, "fdc3.raiseIntent", new { intent = "ViewIncident", context = Incident }))!;
        Assert.Equal(handler.InstanceId, resolution.Source.InstanceId);

        var delivered = Assert.IsType<Fdc3IntentEvent>(Assert.Single(handler.Sink.Messages));
        Assert.Equal("i1", delivered.ListenerId);
        Assert.Equal(raiser.InstanceId, delivered.Source!.InstanceId);

        var pendingResult = Call(raiser, "fdc3.getIntentResult", new { resultId = resolution.ResultId });
        await Call(handler, "fdc3.sendIntentResult", new { resultId = delivered.ResultId, context = Contact });

        var result = (Fdc3IntentResultInfo)(await pendingResult)!;
        Assert.Equal("fdc3.contact", result.Context!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public async Task RaiseIntent_handler_rejection_surfaces_as_IntentHandlerRejected()
    {
        var raiser = Connect("map");
        var handler = Connect("incidents");
        await Call(handler, "fdc3.addIntentListener", new { listenerId = "i1", intent = "ViewIncident" });
        var resolution = (Fdc3IntentResolutionInfo)(await Call(raiser, "fdc3.raiseIntent", new { intent = "ViewIncident", context = Incident }))!;

        var pendingResult = Call(raiser, "fdc3.getIntentResult", new { resultId = resolution.ResultId });
        await Call(handler, "fdc3.sendIntentResult", new { resultId = resolution.ResultId, error = "boom" });

        var error = await Assert.ThrowsAsync<Fdc3Exception>(() => pendingResult);
        Assert.Equal(Fdc3Errors.IntentHandlerRejected, error.Message);
    }

    [Fact]
    public async Task RaiseIntent_launches_the_directory_app_when_nothing_is_running()
    {
        var raiser = Connect("incidents");
        _launcher.OnLaunched = launched =>
            _ = Call(launched, "fdc3.addIntentListener", new { listenerId = "m1", intent = "ViewMap" });

        var resolution = (Fdc3IntentResolutionInfo)(await Call(raiser, "fdc3.raiseIntent", new { intent = "ViewMap", context = Incident }))!;

        var launched = Assert.Single(_launcher.Launched);
        Assert.Equal("map", resolution.Source.AppId);
        Assert.Equal(launched.InstanceId, resolution.Source.InstanceId);
        Assert.IsType<Fdc3IntentEvent>(Assert.Single(launched.Sink.Messages));
    }

    [Fact]
    public async Task RaiseIntent_fails_when_the_launched_app_never_listens()
    {
        var raiser = Connect("incidents");

        var error = await Assert.ThrowsAsync<Fdc3Exception>(() => Call(raiser, "fdc3.raiseIntent", new { intent = "ViewMap", context = Incident }));
        Assert.Equal(Fdc3Errors.IntentDeliveryFailed, error.Message);
    }

    [Fact]
    public async Task RaiseIntent_with_several_running_listeners_needs_a_target()
    {
        var raiser = Connect("map");
        var h1 = Connect("incidents");
        var h2 = Connect("incidents");
        await Call(h1, "fdc3.addIntentListener", new { listenerId = "i1", intent = "ViewIncident" });
        await Call(h2, "fdc3.addIntentListener", new { listenerId = "i2", intent = "ViewIncident" });

        var error = await Assert.ThrowsAsync<Fdc3Exception>(() => Call(raiser, "fdc3.raiseIntent", new { intent = "ViewIncident", context = Incident }));
        Assert.Equal(Fdc3Errors.ResolverUnavailable, error.Message);

        var resolution = (Fdc3IntentResolutionInfo)(await Call(raiser, "fdc3.raiseIntent", new
        {
            intent = "ViewIncident",
            context = Incident,
            app = new { appId = "incidents", instanceId = h2.InstanceId },
        }))!;
        Assert.Equal(h2.InstanceId, resolution.Source.InstanceId);
        Assert.Single(h2.Sink.Messages);
        Assert.Empty(h1.Sink.Messages);
    }

    [Fact]
    public async Task RaiseIntent_rejects_context_types_the_app_does_not_declare()
    {
        var raiser = Connect("map");
        var handler = Connect("incidents");
        await Call(handler, "fdc3.addIntentListener", new { listenerId = "i1", intent = "ViewIncident" });

        var error = await Assert.ThrowsAsync<Fdc3Exception>(() => Call(raiser, "fdc3.raiseIntent", new { intent = "ViewIncident", context = Contact }));
        Assert.Equal(Fdc3Errors.NoAppsFound, error.Message);
    }

    [Fact]
    public async Task RaiseIntentForContext_picks_the_intent_from_the_directory()
    {
        var raiser = Connect("incidents");
        var handler = Connect("map");
        await Call(handler, "fdc3.addIntentListener", new { listenerId = "m1", intent = "ViewMap" });

        var resolution = (Fdc3IntentResolutionInfo)(await Call(raiser, "fdc3.raiseIntentForContext", new
        {
            context = Incident,
            app = new { appId = "map" },
        }))!;

        Assert.Equal("ViewMap", resolution.Intent);
        Assert.Equal(handler.InstanceId, resolution.Source.InstanceId);
    }

    [Fact]
    public async Task Intent_listener_conflict_is_reported()
    {
        var a = Connect("incidents");
        await Call(a, "fdc3.addIntentListener", new { listenerId = "i1", intent = "ViewIncident" });

        var error = await Assert.ThrowsAsync<Fdc3Exception>(() => Call(a, "fdc3.addIntentListener", new { listenerId = "i2", intent = "ViewIncident" }));
        Assert.Equal(Fdc3Errors.IntentListenerConflict, error.Message);
    }

    // Private channels -------------------------------------------------------------

    [Fact]
    public async Task Private_channel_returned_as_an_intent_result_is_shared_with_the_raiser_only()
    {
        var raiser = Connect("incidents");
        var handler = Connect("map");
        var outsider = Connect("map");
        await Call(handler, "fdc3.addIntentListener", new { listenerId = "m1", intent = "ViewMap" });

        var resolution = (Fdc3IntentResolutionInfo)(await Call(raiser, "fdc3.raiseIntent", new
        {
            intent = "ViewMap",
            context = Incident,
            app = new { appId = "map", instanceId = handler.InstanceId },
        }))!;
        var channel = (Fdc3ChannelInfo)(await Call(handler, "fdc3.createPrivateChannel"))!;
        await Call(handler, "fdc3.addEventListener", new { listenerId = "pe", channelId = channel.Id });

        var pendingResult = Call(raiser, "fdc3.getIntentResult", new { resultId = resolution.ResultId });
        await Call(handler, "fdc3.sendIntentResult", new { resultId = resolution.ResultId, channelId = channel.Id });
        var result = (Fdc3IntentResultInfo)(await pendingResult)!;
        Assert.Equal(channel.Id, result.Channel!.Id);

        // Raiser can now listen; handler hears about it.
        await Call(raiser, "fdc3.addContextListener", new { listenerId = "pl", channelId = channel.Id, contextType = "fdc3.location" });
        Assert.Contains(handler.Sink.Messages.OfType<Fdc3ApiEvent>(), e => e.EventType == "addContextListener");

        // Outsiders can't touch it, even by id.
        var error = await Assert.ThrowsAsync<Fdc3Exception>(() =>
            Call(outsider, "fdc3.addContextListener", new { listenerId = "x", channelId = channel.Id }));
        Assert.Equal(Fdc3Errors.AccessDenied, error.Message);

        // Disconnecting tells the other side.
        await Call(raiser, "fdc3.disconnectPrivateChannel", new { channelId = channel.Id });
        var events = handler.Sink.Messages.OfType<Fdc3ApiEvent>().Select(e => e.EventType).ToList();
        Assert.Equal(["addContextListener", "unsubscribe", "disconnect"], events);
    }

    // open & lifecycle -----------------------------------------------------------------

    [Fact]
    public async Task Open_launches_the_app_and_hands_it_the_context()
    {
        var a = Connect("incidents");
        _launcher.OnLaunched = launched =>
            _ = Call(launched, "fdc3.addContextListener", new { listenerId = "c1", contextType = "bermuda.incident" });

        var opened = (Fdc3AppIdentifier)(await Call(a, "fdc3.open", new { app = new { appId = "map" }, context = Incident }))!;

        var launched = Assert.Single(_launcher.Launched);
        Assert.Equal(launched.InstanceId, opened.InstanceId);
        Assert.Equal("c1", Assert.IsType<Fdc3ContextEvent>(Assert.Single(launched.Sink.Messages)).ListenerId);

        var instances = (Fdc3AppIdentifier[])(await Call(a, "fdc3.findInstances", new { appId = "map" }))!;
        Assert.Equal(opened.InstanceId, Assert.Single(instances).InstanceId);
    }

    [Fact]
    public async Task Open_unknown_app_fails()
    {
        var a = Connect("incidents");
        var error = await Assert.ThrowsAsync<Fdc3Exception>(() => Call(a, "fdc3.open", new { app = new { appId = "nope" } }));
        Assert.Equal(Fdc3Errors.AppNotFound, error.Message);
    }

    [Fact]
    public async Task Disconnecting_the_handler_fails_its_pending_result()
    {
        var raiser = Connect("map");
        var handler = Connect("incidents");
        await Call(handler, "fdc3.addIntentListener", new { listenerId = "i1", intent = "ViewIncident" });
        var resolution = (Fdc3IntentResolutionInfo)(await Call(raiser, "fdc3.raiseIntent", new { intent = "ViewIncident", context = Incident }))!;
        var pendingResult = Call(raiser, "fdc3.getIntentResult", new { resultId = resolution.ResultId });

        _agent.Disconnect(handler.InstanceId);

        var error = await Assert.ThrowsAsync<Fdc3Exception>(() => pendingResult);
        Assert.Equal(Fdc3Errors.NoResultReturned, error.Message);
    }

    [Fact]
    public async Task GetInfo_describes_the_agent_and_caller()
    {
        var a = Connect("incidents");
        var info = (Fdc3ImplementationMetadata)(await Call(a, "fdc3.getInfo"))!;

        Assert.Equal("2.2", info.Fdc3Version);
        Assert.Equal("Bermuda", info.Provider);
        Assert.Equal("Incidents", info.AppMetadata.Title);
        Assert.Equal(a.InstanceId, info.AppMetadata.InstanceId);
    }

    [Fact]
    public async Task Unknown_commands_and_bad_payloads_are_rejected()
    {
        var a = Connect("incidents");
        Assert.Equal(Fdc3Errors.InvalidArguments,
            (await Assert.ThrowsAsync<Fdc3Exception>(() => Call(a, "fdc3.nope"))).Message);
        Assert.Equal(Fdc3Errors.InvalidArguments,
            (await Assert.ThrowsAsync<Fdc3Exception>(() => Call(a, "fdc3.joinUserChannel"))).Message);
    }

    // Helpers ----------------------------------------------------------------------------

    private TestApp Connect(string appId) => TestApp.Connect(_agent, appId);

    private Task<object?> Call(TestApp app, string command, object? payload = null) =>
        _agent.HandleAsync(app.InstanceId, command, payload is null ? null : JsonSerializer.SerializeToElement(payload));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class RecordingSink : IFdc3AppSink
    {
        private readonly List<object> _messages = [];
        public IReadOnlyList<object> Messages { get { lock (_messages) return _messages.ToList(); } }
        public void Post(object message) { lock (_messages) _messages.Add(message); }
    }

    private sealed record TestApp(string AppId, string InstanceId, RecordingSink Sink)
    {
        public static TestApp Connect(DesktopAgent agent, string appId, string? instanceId = null)
        {
            var sink = new RecordingSink();
            var id = agent.Connect(appId, sink, instanceId);
            return new TestApp(appId, id.InstanceId!, sink);
        }
    }

    /// <summary>Simulates a window opening: connects the new instance, then lets the test add its listeners.</summary>
    private sealed class FakeLauncher : IFdc3AppLauncher
    {
        public DesktopAgent? Agent { get; set; }
        public List<TestApp> Launched { get; } = [];
        public Action<TestApp>? OnLaunched { get; set; }

        public void Launch(AppDefinition app, string instanceId)
        {
            var launched = TestApp.Connect(Agent!, app.AppId, instanceId);
            Launched.Add(launched);
            OnLaunched?.Invoke(launched);
        }
    }
}
