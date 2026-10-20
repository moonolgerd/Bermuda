using System.Text.Json;
using Bermuda.IpcContract;
using static Bermuda.Fdc3.Tests.DesktopAgentTests;

namespace Bermuda.Fdc3.Tests;

/// <summary>Behaviors pinned down by code review of the agent: lifetime, targeting and result handling.</summary>
public class ReviewRegressionTests
{
    private const string AppD = """
        {
          "applications": [
            { "appId": "incidents", "details": { "url": "/" },
              "interop": { "intents": { "listensFor": { "ViewIncident": { "contexts": ["bermuda.incident"] } } } } },
            { "appId": "map", "details": { "url": "/map" },
              "interop": { "intents": { "listensFor": {
                "ViewMap": { "contexts": ["bermuda.incident"], "resultType": "fdc3.location" },
                "Ping": { }
              } } } }
          ]
        }
        """;

    private static readonly JsonElement Incident = Json("""{ "type": "bermuda.incident", "id": { "incidentId": "7" } }""");
    private static readonly JsonElement Contact = Json("""{ "type": "fdc3.contact", "name": "Ann" }""");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static DesktopAgent NewAgent(IFdc3AppLauncher? launcher = null, TimeSpan? launchTimeout = null,
        TimeSpan? retention = null, TimeProvider? time = null) =>
        new(AppDirectory.Parse(AppD), launcher, launchTimeout ?? TimeSpan.FromSeconds(2), retention, time);

    private static async Task<Fdc3Exception> Throws(Func<Task> call) => await Assert.ThrowsAsync<Fdc3Exception>(call);

    [Fact]
    public async Task A_completed_result_survives_the_handler_disconnecting()
    {
        var agent = NewAgent();
        var raiser = TestApp.Connect(agent, "map");
        var handler = TestApp.Connect(agent, "incidents");
        handler.Fdc3.AddIntentListener(new("i1", "ViewIncident"));
        var resolution = await raiser.Fdc3.RaiseIntentAsync(new("ViewIncident", Incident, null));

        handler.Fdc3.SendIntentResult(new(resolution.ResultId, Contact, null, null));
        handler.Fdc3.Dispose(); // e.g. the handler window reloads right after answering

        var result = await raiser.Fdc3.GetIntentResultAsync(new(resolution.ResultId));
        Assert.Equal("fdc3.contact", result.Context!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_instance_of_a_different_app_is_not_a_valid_explicit_target()
    {
        var agent = NewAgent();
        var raiser = TestApp.Connect(agent, "incidents");
        var mapA = TestApp.Connect(agent, "map");
        var other = TestApp.Connect(agent, "incidents");
        mapA.Fdc3.AddIntentListener(new("m1", "ViewMap"));
        other.Fdc3.AddIntentListener(new("i1", "ViewMap")); // would happily take it if wrongly targeted

        var wrongApp = new Fdc3AppIdentifier("map", other.InstanceId);

        Assert.Equal(Fdc3Errors.TargetInstanceUnavailable,
            (await Throws(() => raiser.Fdc3.RaiseIntentForContextAsync(new(Incident, wrongApp)))).Message);
        Assert.Empty(other.Sink.Messages);
    }

    [Fact]
    public async Task An_invalid_handler_result_still_settles_the_raisers_result()
    {
        var agent = NewAgent();
        var raiser = TestApp.Connect(agent, "incidents");
        var handler = TestApp.Connect(agent, "map");
        handler.Fdc3.AddIntentListener(new("m1", "ViewMap"));
        var resolution = await raiser.Fdc3.RaiseIntentAsync(new("ViewMap", Incident, handler.Fdc3.Identity));

        // The handler returns a private channel it has already disconnected from.
        var channel = handler.Fdc3.CreatePrivateChannel();
        handler.Fdc3.DisconnectPrivateChannel(new(channel.Id));
        var pending = raiser.Fdc3.GetIntentResultAsync(new(resolution.ResultId));

        Assert.Throws<Fdc3Exception>(() => handler.Fdc3.SendIntentResult(new(resolution.ResultId, null, channel.Id, null)));

        Assert.Equal(Fdc3Errors.IntentHandlerRejected, (await Throws(() => pending)).Message);
    }

    [Fact]
    public void A_private_channel_is_forgotten_once_its_last_member_leaves()
    {
        var agent = NewAgent();
        var a = TestApp.Connect(agent, "map");
        var channel = a.Fdc3.CreatePrivateChannel();

        a.Fdc3.DisconnectPrivateChannel(new(channel.Id));

        Assert.Equal(Fdc3Errors.NoChannelFound,
            Assert.Throws<Fdc3Exception>(() => a.Fdc3.AddEventListener(new("x", null, channel.Id))).Message);
    }

    [Fact]
    public async Task A_private_channel_awaiting_collection_is_kept_alive()
    {
        var agent = NewAgent();
        var raiser = TestApp.Connect(agent, "incidents");
        var handler = TestApp.Connect(agent, "map");
        handler.Fdc3.AddIntentListener(new("m1", "ViewMap"));
        var resolution = await raiser.Fdc3.RaiseIntentAsync(new("ViewMap", Incident, handler.Fdc3.Identity));
        var channel = handler.Fdc3.CreatePrivateChannel();
        handler.Fdc3.SendIntentResult(new(resolution.ResultId, null, channel.Id, null));

        handler.Fdc3.Dispose(); // the handler is gone before the raiser collects

        var result = await raiser.Fdc3.GetIntentResultAsync(new(resolution.ResultId));
        Assert.Equal(channel.Id, result.Channel!.Id);
        raiser.Fdc3.AddContextListener(new("l", channel.Id, null)); // still usable
    }

    [Fact]
    public async Task Private_channel_event_listeners_hear_about_listeners_added_earlier()
    {
        var agent = NewAgent();
        var raiser = TestApp.Connect(agent, "incidents");
        var producer = TestApp.Connect(agent, "map");
        producer.Fdc3.AddIntentListener(new("m1", "ViewMap"));
        var resolution = await raiser.Fdc3.RaiseIntentAsync(new("ViewMap", Incident, producer.Fdc3.Identity));
        var channel = producer.Fdc3.CreatePrivateChannel();
        producer.Fdc3.SendIntentResult(new(resolution.ResultId, null, channel.Id, null));
        await raiser.Fdc3.GetIntentResultAsync(new(resolution.ResultId));

        // The consumer subscribes first; the producer only then asks to hear about it.
        raiser.Fdc3.AddContextListener(new("c1", channel.Id, "fdc3.location"));
        raiser.Fdc3.AddContextListener(new("c2", channel.Id, null));
        producer.Fdc3.AddEventListener(new("pe", "addContextListener", channel.Id));

        var events = producer.Sink.Messages.OfType<Fdc3ApiEvent>().ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("addContextListener", e.EventType));
    }

    [Fact]
    public void An_intent_that_declares_no_contexts_matches_every_context()
    {
        var agent = NewAgent();
        var a = TestApp.Connect(agent, "incidents");

        var byContext = a.Fdc3.FindIntentsByContext(new(Contact, null));

        Assert.Equal("Ping", Assert.Single(byContext).Intent.Name);
        Assert.Equal("Ping", a.Fdc3.FindIntent(new("Ping", Contact, null)).Intent.Name); // agrees with findIntent
    }

    [Fact]
    public async Task Uncollected_results_expire()
    {
        var time = new ManualTime();
        var agent = NewAgent(retention: TimeSpan.FromMinutes(5), time: time);
        var raiser = TestApp.Connect(agent, "map");
        var handler = TestApp.Connect(agent, "incidents");
        handler.Fdc3.AddIntentListener(new("i1", "ViewIncident"));
        var stale = await raiser.Fdc3.RaiseIntentAsync(new("ViewIncident", Incident, null));
        var fresh = await raiser.Fdc3.RaiseIntentAsync(new("ViewIncident", Incident, null)); // same time: kept

        time.Advance(TimeSpan.FromMinutes(6));
        var later = await raiser.Fdc3.RaiseIntentAsync(new("ViewIncident", Incident, null)); // sweeps the old ones

        Assert.Equal(1, agent.PendingResultCount); // only the recent one is left
        Assert.Equal(Fdc3Errors.InvalidArguments,
            (await Throws(() => raiser.Fdc3.GetIntentResultAsync(new(stale.ResultId)))).Message);
        Assert.Equal(Fdc3Errors.InvalidArguments,
            (await Throws(() => raiser.Fdc3.GetIntentResultAsync(new(fresh.ResultId)))).Message);
        handler.Fdc3.SendIntentResult(new(later.ResultId, null, null, null));
        await raiser.Fdc3.GetIntentResultAsync(new(later.ResultId)); // the recent one is still collectable
    }

    [Fact]
    public async Task A_failed_launch_leaves_no_result_behind()
    {
        var agent = NewAgent(new ThrowingLauncher());
        var raiser = TestApp.Connect(agent, "incidents");

        Assert.Equal(Fdc3Errors.ErrorOnLaunch,
            (await Throws(() => raiser.Fdc3.RaiseIntentAsync(new("ViewMap", Incident, null)))).Message);

        Assert.Equal(0, agent.PendingResultCount);
    }

    [Fact]
    public async Task Open_without_context_returns_only_once_the_app_has_connected()
    {
        var launcher = new SlowLauncher(TimeSpan.FromMilliseconds(150));
        var agent = NewAgent(launcher);
        launcher.Agent = agent;
        var caller = TestApp.Connect(agent, "incidents");

        var opened = await caller.Fdc3.OpenAsync(new(new("map"), null));

        // Usable straight away as a target, not "TargetInstanceUnavailable".
        Assert.Equal(opened.InstanceId, Assert.Single(caller.Fdc3.FindInstances(new("map"))).InstanceId);
        Assert.Equal(opened.InstanceId, caller.Fdc3.GetAppMetadata(opened).InstanceId);
    }

    [Fact]
    public async Task Open_times_out_if_the_app_never_connects()
    {
        var agent = NewAgent(new SlowLauncher(null), launchTimeout: TimeSpan.FromMilliseconds(200));
        var caller = TestApp.Connect(agent, "incidents");

        Assert.Equal(Fdc3Errors.AppTimeout,
            (await Throws(() => caller.Fdc3.OpenAsync(new(new("map"), null)))).Message);
        Assert.Empty(caller.Fdc3.FindInstances(new("map")));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ThrowingLauncher : IFdc3AppLauncher
    {
        public void Launch(AppDefinition app, string instanceId) => throw new InvalidOperationException("no window");
    }

    /// <summary>Connects the launched app after a delay (a window still initializing), or never.</summary>
    private sealed class SlowLauncher(TimeSpan? delay) : IFdc3AppLauncher
    {
        public DesktopAgent? Agent { get; set; }

        public void Launch(AppDefinition app, string instanceId)
        {
            if (delay is not { } d) return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(d);
                TestApp.Connect(Agent!, app.AppId, instanceId);
            });
        }
    }
}
