using System.Text.Json;
using Bermuda.IpcContract;

namespace Bermuda.Fdc3;

/// <summary>
/// An in-process FDC3 2.2 Desktop Agent. Every WebView2 page the host shows
/// is one connected app instance: <see cref="Connect"/> returns its
/// <see cref="Fdc3Connection"/>, which the host dispatches that page's
/// <c>fdc3.*</c> commands to, and deliveries come back through the
/// instance's <see cref="IFdc3AppSink"/>.
///
/// Supported: user channels (the 8 FDC3 recommended channels), app channels,
/// private channels (incl. their events), context broadcast/listen,
/// intents (find/raise/listen/results), open, findInstances, getAppMetadata,
/// getInfo, and the 'userChannelChanged' event.
///
/// There's no intent resolver UI yet, so when a raise has several possible
/// targets the agent picks deterministically: the single running instance
/// with a matching listener, else the single App Directory app to launch;
/// anything more ambiguous rejects with <c>ResolverUnavailable</c> — callers
/// can disambiguate by passing a target <c>app</c>.
/// </summary>
public sealed class DesktopAgent
{
    public const string Fdc3Version = "2.2";
    public const string ProviderName = "Bermuda";

    private readonly Lock _gate = new();
    private readonly AppDirectory _directory;
    private readonly IFdc3AppLauncher? _launcher;
    private readonly TimeSpan _launchTimeout;
    private readonly TimeSpan _resultRetention;
    private readonly TimeProvider _time;

    private readonly Dictionary<string, AppInstance> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChannelState> _channels = new(StringComparer.Ordinal);
    private readonly List<ListenerRegistration> _listeners = [];
    private readonly Dictionary<string, PendingResult> _results = new(StringComparer.Ordinal);
    private readonly List<PendingDelivery> _pendingDeliveries = [];

    /// <param name="launchTimeout">How long a launched app gets to connect and add the listener a pending context/intent is waiting for.</param>
    /// <param name="resultRetention">
    /// How long an intent's result is kept for the raiser to collect. Apps that
    /// raise intents just for their side effect never call getResult(), so
    /// without this the long-lived agent would hold every result forever.
    /// </param>
    public DesktopAgent(
        AppDirectory directory,
        IFdc3AppLauncher? launcher = null,
        TimeSpan? launchTimeout = null,
        TimeSpan? resultRetention = null,
        TimeProvider? timeProvider = null)
    {
        _directory = directory;
        _launcher = launcher;
        _launchTimeout = launchTimeout ?? TimeSpan.FromSeconds(15);
        _resultRetention = resultRetention ?? TimeSpan.FromMinutes(10);
        _time = timeProvider ?? TimeProvider.System;

        foreach (var channel in RecommendedUserChannels)
        {
            _channels[channel.Id] = new ChannelState(channel.Id, "user", channel.DisplayMetadata);
        }
    }

    /// <summary>The FDC3 recommended user channel set (fdc3.channel.1 … fdc3.channel.8).</summary>
    public static IReadOnlyList<Fdc3ChannelInfo> RecommendedUserChannels { get; } =
    [
        new("fdc3.channel.1", "user", new("Channel 1", "red", "1")),
        new("fdc3.channel.2", "user", new("Channel 2", "orange", "2")),
        new("fdc3.channel.3", "user", new("Channel 3", "yellow", "3")),
        new("fdc3.channel.4", "user", new("Channel 4", "green", "4")),
        new("fdc3.channel.5", "user", new("Channel 5", "cyan", "5")),
        new("fdc3.channel.6", "user", new("Channel 6", "blue", "6")),
        new("fdc3.channel.7", "user", new("Channel 7", "magenta", "7")),
        new("fdc3.channel.8", "user", new("Channel 8", "purple", "8")),
    ];

    public AppDirectory Directory => _directory;

    /// <summary>Intent results the agent is still holding (for tests).</summary>
    internal int PendingResultCount { get { lock (_gate) return _results.Count; } }

    // Connection lifecycle ---------------------------------------------------

    /// <summary>
    /// Registers a new app instance. Pass the <c>instanceId</c> the launcher
    /// was given, if any. Dispose the connection when the page goes away.
    /// </summary>
    public Fdc3Connection Connect(string appId, IFdc3AppSink sink, string? instanceId = null)
    {
        instanceId ??= Guid.NewGuid().ToString();
        List<PendingDelivery> launched;
        lock (_gate)
        {
            if (!_instances.TryAdd(instanceId, new AppInstance(appId, instanceId, sink)))
                throw new InvalidOperationException($"FDC3 instance '{instanceId}' is already connected.");

            // fdc3.open() without a context waits for the opened app to connect.
            launched = TakePendingDeliveries(instanceId, PendingKind.Connect, _ => true);
        }

        foreach (var pending in launched) pending.Delivered.TrySetResult(true);
        return new Fdc3Connection(this, appId, instanceId);
    }

    /// <summary>Removes an instance (its window closed or navigated away) and everything it owned.</summary>
    internal void Disconnect(string instanceId)
    {
        var outbox = new Outbox();
        List<PendingResult> orphanedResults;
        List<PendingDelivery> orphanedDeliveries;

        lock (_gate)
        {
            if (!_instances.Remove(instanceId)) return;

            foreach (var channel in _channels.Values.Where(c => c.Type == "private" && c.Members.Contains(instanceId)).ToList())
            {
                LeavePrivateChannel(instanceId, channel, outbox);
            }

            _listeners.RemoveAll(l => l.InstanceId == instanceId);

            // Results this instance raised can't be collected any more: drop them.
            // Results it was handling stay for their (still connected) raiser:
            // completed ones keep their value, unfinished ones fail below.
            foreach (var raised in _results.Values.Where(r => r.RaiserInstanceId == instanceId).ToList())
            {
                _results.Remove(raised.ResultId);
                ReleasePrivateChannelIfAbandoned(raised);
            }

            orphanedResults = _results.Values.Where(r => r.HandlerInstanceId == instanceId).ToList();

            orphanedDeliveries = _pendingDeliveries.Where(d => d.InstanceId == instanceId).ToList();
            _pendingDeliveries.RemoveAll(d => d.InstanceId == instanceId);
        }

        outbox.Flush();
        foreach (var result in orphanedResults)
            result.Fail(Fdc3Errors.NoResultReturned); // no-op for results already sent
        foreach (var delivery in orphanedDeliveries)
            delivery.Delivered.TrySetResult(false);
    }

    // Metadata -------------------------------------------------------------------

    internal Fdc3ImplementationMetadata GetInfo(AppInstance caller) =>
        new(
            Fdc3Version,
            ProviderName,
            typeof(DesktopAgent).Assembly.GetName().Version?.ToString(),
            new Fdc3OptionalFeatures(
                OriginatingAppMetadata: true,
                UserChannelMembershipAPIs: true,
                DesktopAgentBridging: false),
            ToAppMetadata(caller.AppId, caller.InstanceId));

    private Fdc3AppMetadata ToAppMetadata(string appId, string? instanceId, string? resultType = null)
    {
        var app = _directory.Find(appId);
        return new Fdc3AppMetadata(
            app?.AppId ?? appId,
            instanceId,
            app?.Name ?? appId,
            app?.Version,
            app?.Title,
            app?.Tooltip,
            app?.Description,
            resultType);
    }

    internal Fdc3AppMetadata GetAppMetadata(Fdc3AppIdentifier app)
    {
        lock (_gate)
        {
            if (app.InstanceId is not null)
            {
                if (!_instances.TryGetValue(app.InstanceId, out var instance) ||
                    !string.Equals(instance.AppId, app.AppId, StringComparison.OrdinalIgnoreCase))
                    throw new Fdc3Exception(Fdc3Errors.TargetInstanceUnavailable);

                return ToAppMetadata(instance.AppId, instance.InstanceId);
            }

            if (_directory.Find(app.AppId) is null && !_instances.Values.Any(i => SameApp(i.AppId, app.AppId)))
                throw new Fdc3Exception(Fdc3Errors.TargetAppUnavailable);

            return ToAppMetadata(app.AppId, null);
        }
    }

    internal Fdc3AppIdentifier[] FindInstances(Fdc3AppIdentifier app)
    {
        lock (_gate)
        {
            var instances = _instances.Values
                .Where(i => SameApp(i.AppId, app.AppId))
                .Select(i => new Fdc3AppIdentifier(i.AppId, i.InstanceId))
                .ToArray();

            if (instances.Length == 0 && _directory.Find(app.AppId) is null)
                throw new Fdc3Exception(Fdc3Errors.NoAppsFound);

            return instances;
        }
    }

    // Channels -------------------------------------------------------------------

    internal Fdc3ChannelInfo[] GetUserChannels()
    {
        lock (_gate)
        {
            return _channels.Values.Where(c => c.Type == "user").Select(c => c.Info).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        }
    }

    internal Fdc3ChannelInfo? GetCurrentChannel(AppInstance caller)
    {
        lock (_gate)
        {
            return caller.CurrentUserChannelId is { } id ? _channels[id].Info : null;
        }
    }

    internal void JoinUserChannel(AppInstance caller, string channelId)
    {
        var outbox = new Outbox();
        lock (_gate)
        {
            if (!_channels.TryGetValue(channelId, out var channel) || channel.Type != "user")
                throw new Fdc3Exception(Fdc3Errors.NoChannelFound);

            if (caller.CurrentUserChannelId == channelId) return;
            caller.CurrentUserChannelId = channelId;

            // Listeners following the user channel get its current context on join.
            foreach (var listener in ContextListenersOf(caller).Where(l => l.ChannelId is null))
            {
                if (channel.CurrentContext(listener.ContextType) is { } context)
                    outbox.Add(caller.Sink, new Fdc3ContextEvent(listener.ListenerId, context, null));
            }

            QueueUserChannelChanged(caller, outbox);
        }

        outbox.Flush();
    }

    internal void LeaveCurrentChannel(AppInstance caller)
    {
        var outbox = new Outbox();
        lock (_gate)
        {
            if (caller.CurrentUserChannelId is null) return;
            caller.CurrentUserChannelId = null;
            QueueUserChannelChanged(caller, outbox);
        }

        outbox.Flush();
    }

    private void QueueUserChannelChanged(AppInstance caller, Outbox outbox)
    {
        var details = new Dictionary<string, object?> { ["currentChannelId"] = caller.CurrentUserChannelId };
        foreach (var listener in _listeners.Where(l =>
                     l.InstanceId == caller.InstanceId && l.Kind == ListenerKind.Event && l.ChannelId is null &&
                     (l.EventType is null || l.EventType == "userChannelChanged")))
        {
            outbox.Add(caller.Sink, new Fdc3ApiEvent(listener.ListenerId, "userChannelChanged", details));
        }
    }

    internal Fdc3ChannelInfo GetOrCreateChannel(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            throw new Fdc3Exception(Fdc3Errors.CreationFailed);

        lock (_gate)
        {
            if (_channels.TryGetValue(channelId, out var existing))
            {
                // User and private channels can't be reached by name.
                if (existing.Type != "app") throw new Fdc3Exception(Fdc3Errors.AccessDenied);
                return existing.Info;
            }

            var channel = new ChannelState(channelId, "app", null);
            _channels[channelId] = channel;
            return channel.Info;
        }
    }

    internal Fdc3ChannelInfo CreatePrivateChannel(AppInstance caller)
    {
        lock (_gate)
        {
            var channel = new ChannelState($"private.{Guid.NewGuid()}", "private", null);
            channel.Members.Add(caller.InstanceId);
            _channels[channel.Id] = channel;
            return channel.Info;
        }
    }

    internal void DisconnectPrivateChannel(AppInstance caller, string channelId)
    {
        var outbox = new Outbox();
        lock (_gate)
        {
            var channel = AccessibleChannel(caller, channelId);
            if (channel.Type != "private") throw new Fdc3Exception(Fdc3Errors.InvalidArguments);

            LeavePrivateChannel(caller.InstanceId, channel, outbox);
        }

        outbox.Flush();
    }

    /// <summary>
    /// Drops <paramref name="instanceId"/>'s listeners on a private channel and
    /// tells the other side: one 'unsubscribe' per context listener, then 'disconnect'.
    /// </summary>
    private void LeavePrivateChannel(string instanceId, ChannelState channel, Outbox outbox)
    {
        foreach (var listener in _listeners.Where(l => l.InstanceId == instanceId && l.Kind == ListenerKind.Context && l.ChannelId == channel.Id).ToList())
        {
            QueuePrivateChannelEvent(instanceId, channel, "unsubscribe", new Dictionary<string, object?> { ["contextType"] = listener.ContextType }, outbox);
        }

        QueuePrivateChannelEvent(instanceId, channel, "disconnect", null, outbox);

        _listeners.RemoveAll(l => l.InstanceId == instanceId && l.ChannelId == channel.Id);
        channel.Members.Remove(instanceId);
        channel.DisconnectedMembers.Add(instanceId);
        ReleasePrivateChannelIfAbandoned(channel);
    }

    /// <summary>
    /// Forgets a private channel once nobody can use it again: no members left
    /// and no uncollected intent result that would hand it to someone.
    /// </summary>
    private void ReleasePrivateChannelIfAbandoned(ChannelState channel)
    {
        if (channel.Type != "private" || channel.Members.Count > 0) return;
        if (_results.Values.Any(r => r.ResultChannelId == channel.Id)) return;

        _channels.Remove(channel.Id);
    }

    private void ReleasePrivateChannelIfAbandoned(PendingResult result)
    {
        if (result.ResultChannelId is { } id && _channels.TryGetValue(id, out var channel))
            ReleasePrivateChannelIfAbandoned(channel);
    }

    private void QueuePrivateChannelEvent(string fromInstanceId, ChannelState channel, string eventType, object? details, Outbox outbox)
    {
        foreach (var listener in _listeners.Where(l =>
                     l.Kind == ListenerKind.Event && l.ChannelId == channel.Id && l.InstanceId != fromInstanceId &&
                     (l.EventType is null || l.EventType == eventType)))
        {
            if (_instances.TryGetValue(listener.InstanceId, out var target))
                outbox.Add(target.Sink, new Fdc3ApiEvent(listener.ListenerId, eventType, details));
        }
    }

    /// <summary>Looks up a channel the caller is allowed to use (private channels are members-only).</summary>
    private ChannelState AccessibleChannel(AppInstance caller, string channelId)
    {
        if (!_channels.TryGetValue(channelId, out var channel))
            throw new Fdc3Exception(Fdc3Errors.NoChannelFound);

        if (channel.Type == "private" && !channel.Members.Contains(caller.InstanceId))
            throw new Fdc3Exception(Fdc3Errors.AccessDenied);

        return channel;
    }

    // Context ----------------------------------------------------------------------

    internal void Broadcast(AppInstance caller, Fdc3BroadcastRequest request)
    {
        var contextType = ContextTypeOf(request.Context);
        var context = request.Context.Clone();
        var source = new Fdc3AppIdentifier(caller.AppId, caller.InstanceId);
        var outbox = new Outbox();

        lock (_gate)
        {
            ChannelState channel;
            if (request.ChannelId is null)
            {
                // Broadcasting while not joined to a user channel is a no-op per the standard.
                if (caller.CurrentUserChannelId is null) return;
                channel = _channels[caller.CurrentUserChannelId];
            }
            else
            {
                channel = AccessibleChannel(caller, request.ChannelId);
            }

            channel.Store(contextType, context);

            foreach (var listener in _listeners.Where(l => l.Kind == ListenerKind.Context && l.InstanceId != caller.InstanceId))
            {
                if (!_instances.TryGetValue(listener.InstanceId, out var target)) continue;
                if ((listener.ChannelId ?? target.CurrentUserChannelId) != channel.Id) continue;
                if (listener.ContextType is not null && listener.ContextType != contextType) continue;

                outbox.Add(target.Sink, new Fdc3ContextEvent(listener.ListenerId, context, source));
            }
        }

        outbox.Flush();
    }

    internal JsonElement? GetCurrentContext(AppInstance caller, Fdc3GetCurrentContextRequest request)
    {
        lock (_gate)
        {
            return AccessibleChannel(caller, request.ChannelId).CurrentContext(request.ContextType);
        }
    }

    internal void AddContextListener(AppInstance caller, Fdc3AddContextListenerRequest request)
    {
        var outbox = new Outbox();
        var delivered = new List<PendingDelivery>();

        lock (_gate)
        {
            ChannelState? channel = request.ChannelId is null ? null : AccessibleChannel(caller, request.ChannelId);
            AddListener(new ListenerRegistration(caller.InstanceId, request.ListenerId, ListenerKind.Context)
            {
                ChannelId = request.ChannelId,
                ContextType = request.ContextType,
            });

            if (channel is { Type: "private" })
            {
                QueuePrivateChannelEvent(caller.InstanceId, channel, "addContextListener",
                    new Dictionary<string, object?> { ["contextType"] = request.ContextType }, outbox);
            }

            if (request.ChannelId is null)
            {
                // Already on a user channel: the new listener gets its current context.
                if (caller.CurrentUserChannelId is { } current &&
                    _channels[current].CurrentContext(request.ContextType) is { } context)
                {
                    outbox.Add(caller.Sink, new Fdc3ContextEvent(request.ListenerId, context, null));
                }

                // A context passed to fdc3.open() waits for the opened app's first matching listener.
                foreach (var pending in TakePendingDeliveries(caller.InstanceId, PendingKind.Context,
                             key => request.ContextType is null || request.ContextType == key))
                {
                    outbox.Add(caller.Sink, pending.CreateMessage(request.ListenerId));
                    delivered.Add(pending);
                }
            }
        }

        outbox.Flush();
        foreach (var pending in delivered) pending.Delivered.TrySetResult(true);
    }

    // Listeners ----------------------------------------------------------------------

    internal void AddIntentListener(AppInstance caller, Fdc3AddIntentListenerRequest request)
    {
        var outbox = new Outbox();
        var delivered = new List<PendingDelivery>();

        lock (_gate)
        {
            if (_listeners.Any(l => l.InstanceId == caller.InstanceId && l.Kind == ListenerKind.Intent && l.Intent == request.Intent))
                throw new Fdc3Exception(Fdc3Errors.IntentListenerConflict);

            AddListener(new ListenerRegistration(caller.InstanceId, request.ListenerId, ListenerKind.Intent)
            {
                Intent = request.Intent,
            });

            foreach (var pending in TakePendingDeliveries(caller.InstanceId, PendingKind.Intent, key => key == request.Intent))
            {
                outbox.Add(caller.Sink, pending.CreateMessage(request.ListenerId));
                delivered.Add(pending);
            }
        }

        outbox.Flush();
        foreach (var pending in delivered) pending.Delivered.TrySetResult(true);
    }

    internal void AddEventListener(AppInstance caller, Fdc3AddEventListenerRequest request)
    {
        var outbox = new Outbox();
        lock (_gate)
        {
            ChannelState? channel = null;
            if (request.ChannelId is not null)
            {
                channel = AccessibleChannel(caller, request.ChannelId);
                if (channel.Type != "private") throw new Fdc3Exception(Fdc3Errors.InvalidArguments);
            }

            AddListener(new ListenerRegistration(caller.InstanceId, request.ListenerId, ListenerKind.Event)
            {
                ChannelId = request.ChannelId,
                EventType = request.EventType,
            });

            // FDC3 2.2: a private channel's addContextListener handler also hears
            // about listeners the other side added *before* it subscribed, so a
            // producer that starts streaming on that event can't miss its consumer.
            if (channel is not null && request.EventType is null or "addContextListener")
            {
                foreach (var existing in _listeners.Where(l =>
                             l.Kind == ListenerKind.Context && l.ChannelId == channel.Id && l.InstanceId != caller.InstanceId))
                {
                    outbox.Add(caller.Sink, new Fdc3ApiEvent(request.ListenerId, "addContextListener",
                        new Dictionary<string, object?> { ["contextType"] = existing.ContextType }));
                }
            }
        }

        outbox.Flush();
    }

    private void AddListener(ListenerRegistration listener)
    {
        if (string.IsNullOrEmpty(listener.ListenerId) ||
            _listeners.Any(l => l.InstanceId == listener.InstanceId && l.ListenerId == listener.ListenerId))
            throw new Fdc3Exception(Fdc3Errors.InvalidArguments);

        _listeners.Add(listener);
    }

    internal void Unsubscribe(AppInstance caller, string listenerId)
    {
        var outbox = new Outbox();
        lock (_gate)
        {
            var listener = _listeners.FirstOrDefault(l => l.InstanceId == caller.InstanceId && l.ListenerId == listenerId);
            if (listener is null) return;

            _listeners.Remove(listener);

            if (listener is { Kind: ListenerKind.Context, ChannelId: { } channelId } &&
                _channels.TryGetValue(channelId, out var channel) && channel.Type == "private")
            {
                QueuePrivateChannelEvent(caller.InstanceId, channel, "unsubscribe",
                    new Dictionary<string, object?> { ["contextType"] = listener.ContextType }, outbox);
            }
        }

        outbox.Flush();
    }

    private IEnumerable<ListenerRegistration> ContextListenersOf(AppInstance instance) =>
        _listeners.Where(l => l.InstanceId == instance.InstanceId && l.Kind == ListenerKind.Context);

    // Intents ------------------------------------------------------------------------

    internal Fdc3AppIntent FindIntent(Fdc3FindIntentRequest request)
    {
        var contextType = request.Context is { } context ? ContextTypeOf(context) : null;

        lock (_gate)
        {
            var targets = IntentTargets(request.Intent, contextType, request.ResultType, requireDeclaredContext: false);
            if (targets.Count == 0) throw new Fdc3Exception(Fdc3Errors.NoAppsFound);

            return ToAppIntent(request.Intent, targets);
        }
    }

    internal Fdc3AppIntent[] FindIntentsByContext(Fdc3FindIntentsByContextRequest request)
    {
        var contextType = ContextTypeOf(request.Context);

        lock (_gate)
        {
            var intents = IntentsForContext(contextType);
            var appIntents = intents
                .Select(intent => (intent, targets: IntentTargets(intent, contextType, request.ResultType, requireDeclaredContext: true)))
                .Where(x => x.targets.Count > 0)
                .Select(x => ToAppIntent(x.intent, x.targets))
                .ToArray();

            if (appIntents.Length == 0) throw new Fdc3Exception(Fdc3Errors.NoAppsFound);
            return appIntents;
        }
    }

    /// <summary>
    /// Intent names some App Directory app declares for this context type, by
    /// the same rule as everywhere else (<see cref="IntentDefinition.Accepts"/>),
    /// so an intent with no declared contexts accepts any.
    /// </summary>
    private SortedSet<string> IntentsForContext(string contextType) =>
        new(_directory.Apps
                .SelectMany(a => a.ListensFor)
                .Where(kv => kv.Value.Accepts(contextType))
                .Select(kv => kv.Key),
            StringComparer.Ordinal);

    private Fdc3AppIntent ToAppIntent(string intent, List<IntentTarget> targets)
    {
        var displayName = _directory.Apps
            .Select(a => a.ListensFor.GetValueOrDefault(intent)?.DisplayName)
            .FirstOrDefault(n => n is not null);

        return new Fdc3AppIntent(
            new Fdc3IntentMetadata(intent, displayName ?? intent),
            targets.Select(t => ToAppMetadata(t.AppId, t.InstanceId, t.ResultType)).ToArray());
    }

    /// <summary>
    /// Everything an intent could be delivered to: running instances with a
    /// matching listener (InstanceId set), then App Directory apps that could
    /// be launched for it (InstanceId null).
    /// </summary>
    private List<IntentTarget> IntentTargets(string intent, string? contextType, string? resultType, bool requireDeclaredContext)
    {
        var targets = new List<IntentTarget>();

        foreach (var listener in _listeners.Where(l => l.Kind == ListenerKind.Intent && l.Intent == intent))
        {
            if (!_instances.TryGetValue(listener.InstanceId, out var instance)) continue;

            var declared = _directory.Find(instance.AppId)?.ListensFor.GetValueOrDefault(intent);
            if (declared is null && requireDeclaredContext) continue;
            if (declared is not null && !declared.Accepts(contextType)) continue;
            if (!ResultTypeMatches(declared?.ResultType, resultType)) continue;

            targets.Add(new IntentTarget(instance.AppId, instance.InstanceId, declared?.ResultType));
        }

        foreach (var app in _directory.Apps.OrderBy(a => a.AppId, StringComparer.Ordinal))
        {
            if (!app.ListensFor.TryGetValue(intent, out var declared)) continue;
            if (!declared.Accepts(contextType)) continue;
            if (!ResultTypeMatches(declared.ResultType, resultType)) continue;

            targets.Add(new IntentTarget(app.AppId, null, declared.ResultType));
        }

        return targets;
    }

    private static bool ResultTypeMatches(string? declared, string? requested) =>
        requested is null ||
        string.Equals(declared, requested, StringComparison.Ordinal) ||
        (requested == "channel" && declared?.StartsWith("channel", StringComparison.Ordinal) == true);

    internal Task<Fdc3IntentResolutionInfo> RaiseIntentAsync(AppInstance caller, Fdc3RaiseIntentRequest request)
    {
        var contextType = ContextTypeOf(request.Context);

        (string Intent, IntentTarget Target) choice;
        lock (_gate)
        {
            choice = (request.Intent, request.App is null
                ? ChooseTarget(IntentTargets(request.Intent, contextType, null, requireDeclaredContext: false))
                : ResolveExplicitTarget(request.Intent, contextType, request.App));
        }

        return DeliverIntentAsync(caller, choice.Intent, request.Context.Clone(), choice.Target);
    }

    internal Task<Fdc3IntentResolutionInfo> RaiseIntentForContextAsync(AppInstance caller, Fdc3RaiseIntentForContextRequest request)
    {
        var contextType = ContextTypeOf(request.Context);

        (string Intent, IntentTarget Target) choice;
        lock (_gate)
        {
            var candidates = IntentsForContext(contextType)
                .SelectMany(intent => IntentTargets(intent, contextType, null, requireDeclaredContext: true)
                    .Select(target => (Intent: intent, Target: target)))
                .Where(c => request.App is null ||
                            (SameApp(c.Target.AppId, request.App.AppId) &&
                             (request.App.InstanceId is null || c.Target.InstanceId is null || c.Target.InstanceId == request.App.InstanceId)))
                .ToList();

            if (request.App?.InstanceId is { } instanceId)
            {
                // Targeting a specific instance: prefer it over launching a fresh one.
                var onInstance = candidates.Where(c => c.Target.InstanceId == instanceId).ToList();
                if (onInstance.Count > 0) candidates = onInstance;
                else if (!_instances.TryGetValue(instanceId, out var instance) || !SameApp(instance.AppId, request.App.AppId))
                    throw new Fdc3Exception(Fdc3Errors.TargetInstanceUnavailable);
                else candidates = candidates.Select(c => (c.Intent, c.Target with { InstanceId = instanceId })).ToList();
            }

            var target = ChooseTarget(candidates.Select(c => c.Target).ToList());
            choice = candidates.First(c => c.Target == target);
        }

        return DeliverIntentAsync(caller, choice.Intent, request.Context.Clone(), choice.Target);
    }

    /// <summary>
    /// Stand-in for a resolver UI: deliver when there's exactly one sensible
    /// target (one running listener, else one launchable app), otherwise ask
    /// the caller to name one.
    /// </summary>
    private static IntentTarget ChooseTarget(List<IntentTarget> targets)
    {
        var running = targets.Where(t => t.InstanceId is not null).ToList();
        if (running.Count == 1) return running[0];

        var launchable = targets.Where(t => t.InstanceId is null).ToList();
        if (running.Count == 0 && launchable.Count == 1) return launchable[0];

        throw new Fdc3Exception(targets.Count == 0 ? Fdc3Errors.NoAppsFound : Fdc3Errors.ResolverUnavailable);
    }

    private IntentTarget ResolveExplicitTarget(string intent, string contextType, Fdc3AppIdentifier app)
    {
        var declared = _directory.Find(app.AppId)?.ListensFor.GetValueOrDefault(intent);
        if (declared is not null && !declared.Accepts(contextType))
            throw new Fdc3Exception(Fdc3Errors.NoAppsFound);

        if (app.InstanceId is not null)
        {
            if (!_instances.TryGetValue(app.InstanceId, out var instance) || !SameApp(instance.AppId, app.AppId))
                throw new Fdc3Exception(Fdc3Errors.TargetInstanceUnavailable);

            // Delivered now if it already listens, otherwise once it adds the listener (or times out).
            return new IntentTarget(instance.AppId, instance.InstanceId, declared?.ResultType);
        }

        var listening = _listeners
            .Where(l => l.Kind == ListenerKind.Intent && l.Intent == intent &&
                        _instances.TryGetValue(l.InstanceId, out var i) && SameApp(i.AppId, app.AppId))
            .Select(l => _instances[l.InstanceId])
            .FirstOrDefault();
        if (listening is not null)
            return new IntentTarget(listening.AppId, listening.InstanceId, declared?.ResultType);

        if (_directory.Find(app.AppId) is null) throw new Fdc3Exception(Fdc3Errors.TargetAppUnavailable);
        if (declared is null) throw new Fdc3Exception(Fdc3Errors.NoAppsFound);

        return new IntentTarget(app.AppId, null, declared.ResultType);
    }

    private async Task<Fdc3IntentResolutionInfo> DeliverIntentAsync(AppInstance caller, string intent, JsonElement context, IntentTarget target)
    {
        var resultId = Guid.NewGuid().ToString();
        var source = new Fdc3AppIdentifier(caller.AppId, caller.InstanceId);
        var instanceId = target.InstanceId;
        AppDefinition? launch = null;
        PendingDelivery? pending = null;
        var outbox = new Outbox();

        List<PendingResult> expired;
        lock (_gate)
        {
            if (instanceId is null)
            {
                launch = _directory.Find(target.AppId) ?? throw new Fdc3Exception(Fdc3Errors.TargetAppUnavailable);
                instanceId = Guid.NewGuid().ToString();
            }

            expired = TakeExpiredResults();
            _results[resultId] = new PendingResult(resultId, caller.InstanceId, instanceId, _time.GetUtcNow());

            var listener = _listeners.FirstOrDefault(l => l.InstanceId == instanceId && l.Kind == ListenerKind.Intent && l.Intent == intent);
            if (listener is not null && _instances.TryGetValue(instanceId, out var handler))
            {
                outbox.Add(handler.Sink, new Fdc3IntentEvent(listener.ListenerId, intent, context, source, resultId));
            }
            else
            {
                pending = new PendingDelivery(instanceId, PendingKind.Intent, intent,
                    listenerId => new Fdc3IntentEvent(listenerId, intent, context, source, resultId));
                _pendingDeliveries.Add(pending);
            }
        }

        outbox.Flush();
        foreach (var result in expired) result.Fail(Fdc3Errors.NoResultReturned);

        if (pending is not null)
        {
            if (launch is not null)
            {
                try
                {
                    Launch(launch, instanceId, pending);
                }
                catch
                {
                    lock (_gate) _results.Remove(resultId);
                    throw;
                }
            }

            if (!await WaitForDeliveryAsync(pending))
            {
                lock (_gate) _results.Remove(resultId);
                throw new Fdc3Exception(Fdc3Errors.IntentDeliveryFailed);
            }
        }

        return new Fdc3IntentResolutionInfo(new Fdc3AppIdentifier(target.AppId, instanceId), intent, resultId);
    }

    /// <summary>Removes results older than the retention window (the caller fails any still unfinished).</summary>
    private List<PendingResult> TakeExpiredResults()
    {
        var cutoff = _time.GetUtcNow() - _resultRetention;
        var expired = _results.Values.Where(r => r.CreatedAt < cutoff).ToList();
        foreach (var result in expired)
        {
            _results.Remove(result.ResultId);
            ReleasePrivateChannelIfAbandoned(result);
        }

        return expired;
    }

    internal async Task<Fdc3IntentResultInfo> GetIntentResultAsync(AppInstance caller, string resultId)
    {
        PendingResult? result;
        lock (_gate)
        {
            if (!_results.TryGetValue(resultId, out result) || result.RaiserInstanceId != caller.InstanceId)
                throw new Fdc3Exception(Fdc3Errors.InvalidArguments);
        }

        try
        {
            var info = await result.Completion.Task;

            // Returning a private channel from an intent is how it's shared:
            // the raiser becomes a member once it collects the result.
            if (info.Channel is { Type: "private" } channelInfo)
            {
                lock (_gate)
                {
                    if (_channels.TryGetValue(channelInfo.Id, out var channel) && !channel.DisconnectedMembers.Contains(caller.InstanceId))
                        channel.Members.Add(caller.InstanceId);
                }
            }

            return info;
        }
        finally
        {
            lock (_gate)
            {
                _results.Remove(resultId);
                ReleasePrivateChannelIfAbandoned(result);
            }
        }
    }

    internal void SendIntentResult(AppInstance caller, Fdc3SendIntentResultRequest request)
    {
        PendingResult? result;
        Fdc3IntentResultInfo? info = null;
        Fdc3Exception? invalid = null;

        lock (_gate)
        {
            if (!_results.TryGetValue(request.ResultId, out result) || result.HandlerInstanceId != caller.InstanceId)
                throw new Fdc3Exception(Fdc3Errors.InvalidArguments);

            if (request.Error is null)
            {
                try
                {
                    info = ValidateResult(caller, request);
                    result.ResultChannelId = info.Channel?.Id;
                }
                catch (Fdc3Exception ex)
                {
                    // E.g. a channel the handler already left: the raiser must
                    // still hear back, so this counts as a rejected handler.
                    invalid = ex;
                }
            }
        }

        if (info is null)
            result.Fail(Fdc3Errors.IntentHandlerRejected);
        else
            result.Completion.TrySetResult(info);

        if (invalid is not null) throw invalid;
    }

    private Fdc3IntentResultInfo ValidateResult(AppInstance caller, Fdc3SendIntentResultRequest request)
    {
        if (request.ChannelId is not null)
            return new Fdc3IntentResultInfo(null, AccessibleChannel(caller, request.ChannelId).Info);

        if (request.Context is { } context)
        {
            ContextTypeOf(context);
            return new Fdc3IntentResultInfo(context.Clone(), null);
        }

        return new Fdc3IntentResultInfo(null, null);
    }

    // open -----------------------------------------------------------------------------

    internal async Task<Fdc3AppIdentifier> OpenAsync(Fdc3OpenRequest request)
    {
        var app = _directory.Find(request.App.AppId) ?? throw new Fdc3Exception(Fdc3Errors.AppNotFound);
        var instanceId = Guid.NewGuid().ToString();

        // With a context: done once the app adds a matching context listener.
        // Without one: once it connects, so the returned identifier is usable
        // straight away (e.g. as a raiseIntent target).
        PendingDelivery pending;
        if (request.Context is { } context)
        {
            var contextType = ContextTypeOf(context);
            var copy = context.Clone();
            pending = new PendingDelivery(instanceId, PendingKind.Context, contextType,
                listenerId => new Fdc3ContextEvent(listenerId, copy, null));
        }
        else
        {
            pending = new PendingDelivery(instanceId, PendingKind.Connect, "", _ => throw new InvalidOperationException());
        }

        lock (_gate) _pendingDeliveries.Add(pending);

        Launch(app, instanceId, pending);

        if (!await WaitForDeliveryAsync(pending))
            throw new Fdc3Exception(Fdc3Errors.AppTimeout);

        return new Fdc3AppIdentifier(app.AppId, instanceId);
    }

    private void Launch(AppDefinition app, string instanceId, PendingDelivery? pending)
    {
        try
        {
            if (_launcher is null) throw new InvalidOperationException("No app launcher configured.");
            _launcher.Launch(app, instanceId);
        }
        catch (Exception)
        {
            if (pending is not null)
            {
                lock (_gate) _pendingDeliveries.Remove(pending);
            }

            throw new Fdc3Exception(Fdc3Errors.ErrorOnLaunch);
        }
    }

    private async Task<bool> WaitForDeliveryAsync(PendingDelivery pending)
    {
        var finished = await Task.WhenAny(pending.Delivered.Task, Task.Delay(_launchTimeout, _time));
        if (finished == pending.Delivered.Task) return pending.Delivered.Task.Result;

        lock (_gate) _pendingDeliveries.Remove(pending);
        return false;
    }

    private List<PendingDelivery> TakePendingDeliveries(string instanceId, PendingKind kind, Func<string, bool> matches)
    {
        var taken = _pendingDeliveries.Where(d => d.InstanceId == instanceId && d.Kind == kind && matches(d.Key)).ToList();
        foreach (var delivery in taken) _pendingDeliveries.Remove(delivery);
        return taken;
    }

    // Helpers --------------------------------------------------------------------------

    internal AppInstance GetInstance(string instanceId)
    {
        lock (_gate)
        {
            return _instances.TryGetValue(instanceId, out var instance)
                ? instance
                : throw new Fdc3Exception(Fdc3Errors.AccessDenied);
        }
    }

    /// <summary>Validates an FDC3 context (an object with a string <c>type</c>) and returns its type.</summary>
    private static string ContextTypeOf(JsonElement context)
    {
        if (context.ValueKind == JsonValueKind.Object &&
            context.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            type.GetString() is { Length: > 0 } value)
            return value;

        throw new Fdc3Exception(Fdc3Errors.MalformedContext);
    }

    private static bool SameApp(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal sealed class AppInstance(string appId, string instanceId, IFdc3AppSink sink)
    {
        public string AppId { get; } = appId;
        public string InstanceId { get; } = instanceId;
        public IFdc3AppSink Sink { get; } = sink;
        public string? CurrentUserChannelId { get; set; }
    }

    private sealed class ChannelState(string id, string type, Fdc3DisplayMetadata? displayMetadata)
    {
        private readonly Dictionary<string, JsonElement> _byType = new(StringComparer.Ordinal);
        private JsonElement? _latest;

        public string Id { get; } = id;
        public string Type { get; } = type;
        public Fdc3ChannelInfo Info { get; } = new(id, type, displayMetadata);

        /// <summary>Private channels only: instances allowed to use it.</summary>
        public HashSet<string> Members { get; } = new(StringComparer.Ordinal);

        /// <summary>Private channels only: instances that called disconnect() and may not rejoin.</summary>
        public HashSet<string> DisconnectedMembers { get; } = new(StringComparer.Ordinal);

        public void Store(string contextType, JsonElement context)
        {
            _byType[contextType] = context;
            _latest = context;
        }

        public JsonElement? CurrentContext(string? contextType) =>
            contextType is null ? _latest : _byType.TryGetValue(contextType, out var context) ? context : null;
    }

    private enum ListenerKind { Context, Intent, Event }

    private sealed record ListenerRegistration(string InstanceId, string ListenerId, ListenerKind Kind)
    {
        /// <summary>Context/event listeners: the app/private channel; null = follow the user channel (context) or agent-level (event).</summary>
        public string? ChannelId { get; init; }
        public string? ContextType { get; init; }
        public string? Intent { get; init; }
        public string? EventType { get; init; }
    }

    private sealed record IntentTarget(string AppId, string? InstanceId, string? ResultType);

    private sealed class PendingResult(string resultId, string raiserInstanceId, string handlerInstanceId, DateTimeOffset createdAt)
    {
        public string ResultId { get; } = resultId;
        public string RaiserInstanceId { get; } = raiserInstanceId;
        public string HandlerInstanceId { get; } = handlerInstanceId;
        public DateTimeOffset CreatedAt { get; } = createdAt;

        /// <summary>Set when the handler returns a channel; keeps a private one alive until collected.</summary>
        public string? ResultChannelId { get; set; }

        public TaskCompletionSource<Fdc3IntentResultInfo> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Fail(string error) => Completion.TrySetException(new Fdc3Exception(error));
    }

    /// <summary>Context/Intent: waiting for a matching listener. Connect: waiting for the instance to connect.</summary>
    private enum PendingKind { Context, Intent, Connect }

    /// <summary>
    /// A context (from open) or intent (from raiseIntent) addressed to an
    /// instance that hasn't added a matching listener yet — typically one
    /// that's still launching. <c>Key</c> is the context type or intent name.
    /// </summary>
    private sealed class PendingDelivery(string instanceId, PendingKind kind, string key, Func<string, object> createMessage)
    {
        public string InstanceId { get; } = instanceId;
        public PendingKind Kind { get; } = kind;
        public string Key { get; } = key;
        public object CreateMessage(string listenerId) => createMessage(listenerId);
        public TaskCompletionSource<bool> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Messages collected under the lock and posted after releasing it, so sinks never run while it's held.</summary>
    private sealed class Outbox
    {
        private readonly List<(IFdc3AppSink Sink, object Message)> _messages = [];

        public void Add(IFdc3AppSink sink, object message) => _messages.Add((sink, message));

        public void Flush()
        {
            foreach (var (sink, message) in _messages)
            {
                try
                {
                    sink.Post(message);
                }
                catch
                {
                    // One broken window mustn't stop delivery to the rest.
                }
            }
        }
    }
}
