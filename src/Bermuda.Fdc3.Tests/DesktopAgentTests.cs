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
    public void Exposes_the_eight_recommended_user_channels()
    {
        var channels = Connect("incidents").Fdc3.GetUserChannels();

        Assert.Equal(8, channels.Length);
        Assert.All(channels, c => Assert.Equal("user", c.Type));
        Assert.Equal("fdc3.channel.1", channels[0].Id);
    }

    [Fact]
    public void Broadcast_reaches_listeners_on_the_same_user_channel_only()
    {
        var a = Connect("incidents");
        var b = Connect("map");
        var c = Connect("map");

        a.Fdc3.JoinUserChannel(new("fdc3.channel.1"));
        b.Fdc3.JoinUserChannel(new("fdc3.channel.1"));
        c.Fdc3.JoinUserChannel(new("fdc3.channel.2"));
        a.Fdc3.AddContextListener(new("la", null, null));
        b.Fdc3.AddContextListener(new("lb", null, "bermuda.incident"));
        c.Fdc3.AddContextListener(new("lc", null, null));

        a.Fdc3.Broadcast(new(null, Incident));

        var received = Assert.IsType<Fdc3ContextEvent>(Assert.Single(b.Sink.Messages));
        Assert.Equal("lb", received.ListenerId);
        Assert.Equal(a.Fdc3.Identity, received.Source);
        Assert.Empty(a.Sink.Messages); // never echoed back to the sender
        Assert.Empty(c.Sink.Messages); // different channel
    }

    [Fact]
    public void Context_type_filter_is_respected()
    {
        var a = Connect("incidents");
        var b = Connect("map");
        a.Fdc3.JoinUserChannel(new("fdc3.channel.3"));
        b.Fdc3.JoinUserChannel(new("fdc3.channel.3"));
        b.Fdc3.AddContextListener(new("lb", null, "bermuda.incident"));

        a.Fdc3.Broadcast(new(null, Contact));

        Assert.Empty(b.Sink.Messages);
    }

    [Fact]
    public void Joining_a_channel_delivers_its_current_context()
    {
        var a = Connect("incidents");
        var b = Connect("map");
        a.Fdc3.JoinUserChannel(new("fdc3.channel.4"));
        a.Fdc3.Broadcast(new(null, Incident));
        b.Fdc3.AddContextListener(new("lb", null, null));

        b.Fdc3.JoinUserChannel(new("fdc3.channel.4"));

        var received = Assert.IsType<Fdc3ContextEvent>(Assert.Single(b.Sink.Messages));
        Assert.Equal("7", received.Context.GetProperty("id").GetProperty("incidentId").GetString());
    }

    [Fact]
    public void Broadcast_when_not_joined_is_a_no_op()
    {
        var a = Connect("incidents");
        a.Fdc3.Broadcast(new(null, Incident));
        Assert.Null(a.Fdc3.GetCurrentChannel());
    }

    [Fact]
    public void Malformed_context_is_rejected()
    {
        var a = Connect("incidents");
        a.Fdc3.JoinUserChannel(new("fdc3.channel.1"));

        AssertFdc3Error(Fdc3Errors.MalformedContext, () => a.Fdc3.Broadcast(new(null, Json("""{ "name": "no type" }"""))));
    }

    [Fact]
    public void Joining_an_unknown_channel_fails()
    {
        var a = Connect("incidents");
        AssertFdc3Error(Fdc3Errors.NoChannelFound, () => a.Fdc3.JoinUserChannel(new("nope")));
    }

    [Fact]
    public void User_channel_changes_fire_events()
    {
        var a = Connect("incidents");
        a.Fdc3.AddEventListener(new("e1", "userChannelChanged", null));

        a.Fdc3.JoinUserChannel(new("fdc3.channel.5"));
        a.Fdc3.LeaveCurrentChannel();

        Assert.Equal(2, a.Sink.Messages.OfType<Fdc3ApiEvent>().Count(e => e.EventType == "userChannelChanged"));
    }

    [Fact]
    public void App_channels_are_shared_by_name_and_keep_current_context()
    {
        var a = Connect("incidents");
        var b = Connect("map");

        Assert.Equal("app", a.Fdc3.GetOrCreateChannel(new("ops")).Type);
        b.Fdc3.GetOrCreateChannel(new("ops"));
        b.Fdc3.AddContextListener(new("lb", "ops", null));

        a.Fdc3.Broadcast(new("ops", Incident));

        Assert.Single(b.Sink.Messages);
        var current = b.Fdc3.GetCurrentContext(new("ops", "bermuda.incident"));
        Assert.Equal("bermuda.incident", current!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public void User_channels_cannot_be_fetched_as_app_channels()
    {
        var a = Connect("incidents");
        AssertFdc3Error(Fdc3Errors.AccessDenied, () => a.Fdc3.GetOrCreateChannel(new("fdc3.channel.1")));
    }

    // Intents ------------------------------------------------------------------------

    [Fact]
    public void FindIntent_lists_running_listeners_and_directory_apps()
    {
        var a = Connect("incidents");
        a.Fdc3.AddIntentListener(new("i1", "ViewIncident"));

        var appIntent = a.Fdc3.FindIntent(new("ViewIncident", Incident, null));

        Assert.Equal("View Incident", appIntent.Intent.DisplayName);
        Assert.Contains(appIntent.Apps, app => app.AppId == "incidents" && app.InstanceId == a.InstanceId);
        Assert.Contains(appIntent.Apps, app => app.AppId == "incidents" && app.InstanceId is null);
    }

    [Fact]
    public void FindIntent_with_no_handlers_fails()
    {
        var a = Connect("incidents");
        AssertFdc3Error(Fdc3Errors.NoAppsFound, () => a.Fdc3.FindIntent(new("StartChat", null, null)));
    }

    [Fact]
    public void FindIntentsByContext_uses_declared_contexts_and_result_types()
    {
        var a = Connect("incidents");

        var all = a.Fdc3.FindIntentsByContext(new(Incident, null));
        Assert.Equal(["ViewIncident", "ViewMap"], all.Select(i => i.Intent.Name));

        var located = a.Fdc3.FindIntentsByContext(new(Incident, "fdc3.location"));
        Assert.Equal("ViewMap", Assert.Single(located).Intent.Name);
    }

    [Fact]
    public async Task RaiseIntent_delivers_to_the_running_listener_and_returns_its_result()
    {
        var raiser = Connect("map");
        var handler = Connect("incidents");
        handler.Fdc3.AddIntentListener(new("i1", "ViewIncident"));

        var resolution = await raiser.Fdc3.RaiseIntentAsync(new("ViewIncident", Incident, null));
        Assert.Equal(handler.InstanceId, resolution.Source.InstanceId);

        var delivered = Assert.IsType<Fdc3IntentEvent>(Assert.Single(handler.Sink.Messages));
        Assert.Equal("i1", delivered.ListenerId);
        Assert.Equal(raiser.InstanceId, delivered.Source!.InstanceId);

        var pendingResult = raiser.Fdc3.GetIntentResultAsync(new(resolution.ResultId));
        handler.Fdc3.SendIntentResult(new(delivered.ResultId, Contact, null, null));

        var result = await pendingResult;
        Assert.Equal("fdc3.contact", result.Context!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public async Task RaiseIntent_handler_rejection_surfaces_as_IntentHandlerRejected()
    {
        var raiser = Connect("map");
        var handler = Connect("incidents");
        handler.Fdc3.AddIntentListener(new("i1", "ViewIncident"));
        var resolution = await raiser.Fdc3.RaiseIntentAsync(new("ViewIncident", Incident, null));

        var pendingResult = raiser.Fdc3.GetIntentResultAsync(new(resolution.ResultId));
        handler.Fdc3.SendIntentResult(new(resolution.ResultId, null, null, "boom"));

        await AssertFdc3ErrorAsync(Fdc3Errors.IntentHandlerRejected, () => pendingResult);
    }

    [Fact]
    public async Task RaiseIntent_launches_the_directory_app_when_nothing_is_running()
    {
        var raiser = Connect("incidents");
        _launcher.OnLaunched = launched => launched.Fdc3.AddIntentListener(new("m1", "ViewMap"));

        var resolution = await raiser.Fdc3.RaiseIntentAsync(new("ViewMap", Incident, null));

        var launched = Assert.Single(_launcher.Launched);
        Assert.Equal("map", resolution.Source.AppId);
        Assert.Equal(launched.InstanceId, resolution.Source.InstanceId);
        Assert.IsType<Fdc3IntentEvent>(Assert.Single(launched.Sink.Messages));
    }

    [Fact]
    public async Task RaiseIntent_fails_when_the_launched_app_never_listens()
    {
        var raiser = Connect("incidents");

        await AssertFdc3ErrorAsync(Fdc3Errors.IntentDeliveryFailed,
            () => raiser.Fdc3.RaiseIntentAsync(new("ViewMap", Incident, null)));
    }

    [Fact]
    public async Task RaiseIntent_with_several_running_listeners_needs_a_target()
    {
        var raiser = Connect("map");
        var h1 = Connect("incidents");
        var h2 = Connect("incidents");
        h1.Fdc3.AddIntentListener(new("i1", "ViewIncident"));
        h2.Fdc3.AddIntentListener(new("i2", "ViewIncident"));

        await AssertFdc3ErrorAsync(Fdc3Errors.ResolverUnavailable,
            () => raiser.Fdc3.RaiseIntentAsync(new("ViewIncident", Incident, null)));

        var resolution = await raiser.Fdc3.RaiseIntentAsync(new("ViewIncident", Incident, h2.Fdc3.Identity));
        Assert.Equal(h2.InstanceId, resolution.Source.InstanceId);
        Assert.Single(h2.Sink.Messages);
        Assert.Empty(h1.Sink.Messages);
    }

    [Fact]
    public async Task RaiseIntent_rejects_context_types_the_app_does_not_declare()
    {
        var raiser = Connect("map");
        var handler = Connect("incidents");
        handler.Fdc3.AddIntentListener(new("i1", "ViewIncident"));

        await AssertFdc3ErrorAsync(Fdc3Errors.NoAppsFound,
            () => raiser.Fdc3.RaiseIntentAsync(new("ViewIncident", Contact, null)));
    }

    [Fact]
    public async Task RaiseIntentForContext_picks_the_intent_from_the_directory()
    {
        var raiser = Connect("incidents");
        var handler = Connect("map");
        handler.Fdc3.AddIntentListener(new("m1", "ViewMap"));

        var resolution = await raiser.Fdc3.RaiseIntentForContextAsync(new(Incident, new("map")));

        Assert.Equal("ViewMap", resolution.Intent);
        Assert.Equal(handler.InstanceId, resolution.Source.InstanceId);
    }

    [Fact]
    public void Intent_listener_conflict_is_reported()
    {
        var a = Connect("incidents");
        a.Fdc3.AddIntentListener(new("i1", "ViewIncident"));

        AssertFdc3Error(Fdc3Errors.IntentListenerConflict, () => a.Fdc3.AddIntentListener(new("i2", "ViewIncident")));
    }

    // Private channels -------------------------------------------------------------

    [Fact]
    public async Task Private_channel_returned_as_an_intent_result_is_shared_with_the_raiser_only()
    {
        var raiser = Connect("incidents");
        var handler = Connect("map");
        var outsider = Connect("map");
        handler.Fdc3.AddIntentListener(new("m1", "ViewMap"));

        var resolution = await raiser.Fdc3.RaiseIntentAsync(new("ViewMap", Incident, handler.Fdc3.Identity));
        var channel = handler.Fdc3.CreatePrivateChannel();
        handler.Fdc3.AddEventListener(new("pe", null, channel.Id));

        var pendingResult = raiser.Fdc3.GetIntentResultAsync(new(resolution.ResultId));
        handler.Fdc3.SendIntentResult(new(resolution.ResultId, null, channel.Id, null));
        Assert.Equal(channel.Id, (await pendingResult).Channel!.Id);

        // Raiser can now listen; handler hears about it.
        raiser.Fdc3.AddContextListener(new("pl", channel.Id, "fdc3.location"));
        Assert.Contains(handler.Sink.Messages.OfType<Fdc3ApiEvent>(), e => e.EventType == "addContextListener");

        // Outsiders can't touch it, even by id.
        AssertFdc3Error(Fdc3Errors.AccessDenied, () => outsider.Fdc3.AddContextListener(new("x", channel.Id, null)));

        // Disconnecting tells the other side.
        raiser.Fdc3.DisconnectPrivateChannel(new(channel.Id));
        var events = handler.Sink.Messages.OfType<Fdc3ApiEvent>().Select(e => e.EventType);
        Assert.Equal(["addContextListener", "unsubscribe", "disconnect"], events);
    }

    // open & lifecycle -----------------------------------------------------------------

    [Fact]
    public async Task Open_launches_the_app_and_hands_it_the_context()
    {
        var a = Connect("incidents");
        _launcher.OnLaunched = launched => launched.Fdc3.AddContextListener(new("c1", null, "bermuda.incident"));

        var opened = await a.Fdc3.OpenAsync(new(new("map"), Incident));

        var launched = Assert.Single(_launcher.Launched);
        Assert.Equal(launched.InstanceId, opened.InstanceId);
        Assert.Equal("c1", Assert.IsType<Fdc3ContextEvent>(Assert.Single(launched.Sink.Messages)).ListenerId);
        Assert.Equal(opened.InstanceId, Assert.Single(a.Fdc3.FindInstances(new("map"))).InstanceId);
    }

    [Fact]
    public async Task Open_unknown_app_fails()
    {
        var a = Connect("incidents");
        await AssertFdc3ErrorAsync(Fdc3Errors.AppNotFound, () => a.Fdc3.OpenAsync(new(new("nope"), null)));
    }

    [Fact]
    public async Task Disposing_the_handler_fails_its_pending_result()
    {
        var raiser = Connect("map");
        var handler = Connect("incidents");
        handler.Fdc3.AddIntentListener(new("i1", "ViewIncident"));
        var resolution = await raiser.Fdc3.RaiseIntentAsync(new("ViewIncident", Incident, null));
        var pendingResult = raiser.Fdc3.GetIntentResultAsync(new(resolution.ResultId));

        handler.Fdc3.Dispose();

        await AssertFdc3ErrorAsync(Fdc3Errors.NoResultReturned, () => pendingResult);
        AssertFdc3Error(Fdc3Errors.AccessDenied, () => handler.Fdc3.GetInfo());
    }

    [Fact]
    public void GetInfo_describes_the_agent_and_caller()
    {
        var a = Connect("incidents");
        var info = a.Fdc3.GetInfo();

        Assert.Equal("2.2", info.Fdc3Version);
        Assert.Equal("Bermuda", info.Provider);
        Assert.Equal("Incidents", info.AppMetadata.Title);
        Assert.Equal(a.InstanceId, info.AppMetadata.InstanceId);
    }

    // Helpers ----------------------------------------------------------------------------

    private TestApp Connect(string appId) => TestApp.Connect(_agent, appId);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void AssertFdc3Error(string expected, Action call) =>
        Assert.Equal(expected, Assert.Throws<Fdc3Exception>(call).Message);

    private static async Task AssertFdc3ErrorAsync(string expected, Func<Task> call) =>
        Assert.Equal(expected, (await Assert.ThrowsAsync<Fdc3Exception>(call)).Message);

    internal sealed class RecordingSink : IFdc3AppSink
    {
        private readonly List<object> _messages = [];
        public IReadOnlyList<object> Messages { get { lock (_messages) return [.. _messages]; } }
        public void Post(object message) { lock (_messages) _messages.Add(message); }
    }

    internal sealed record TestApp(Fdc3Connection Fdc3, RecordingSink Sink)
    {
        public string InstanceId => Fdc3.InstanceId;

        public static TestApp Connect(DesktopAgent agent, string appId, string? instanceId = null)
        {
            var sink = new RecordingSink();
            return new TestApp(agent.Connect(appId, sink, instanceId), sink);
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
