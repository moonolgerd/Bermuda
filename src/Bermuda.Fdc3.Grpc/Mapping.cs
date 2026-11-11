using System.Text.Json;
using Bermuda.IpcContract;
using Proto = Bermuda.Fdc3.Grpc.Proto;

namespace Bermuda.Fdc3.Grpc;

/// <summary>Converts between the protobuf messages and the <c>Fdc3Contract</c> records.</summary>
internal static class Mapping
{
    // Contexts ------------------------------------------------------------------

    public static JsonElement ParseContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new Fdc3Exception(Fdc3Errors.MalformedContext);
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            throw new Fdc3Exception(Fdc3Errors.MalformedContext);
        }
    }

    public static JsonElement? ParseOptionalContext(string? json) =>
        json is null ? null : ParseContext(json);

    // Requests (proto -> contract) ---------------------------------------------------

    public static Fdc3AppIdentifier ToContract(Proto.AppIdentifier? app) =>
        app is null ? throw new Fdc3Exception(Fdc3Errors.InvalidArguments) : new(app.AppId, app.HasInstanceId ? app.InstanceId : null);

    public static Fdc3AppIdentifier? ToContractOptional(Proto.AppIdentifier? app) =>
        app is null ? null : ToContract(app);

    public static Fdc3ChannelIdRequest ToContract(Proto.ChannelIdRequest r) => new(r.ChannelId);

    public static Fdc3BroadcastRequest ToContract(Proto.BroadcastRequest r) =>
        new(r.HasChannelId ? r.ChannelId : null, ParseContext(r.ContextJson));

    public static Fdc3GetCurrentContextRequest ToContract(Proto.GetCurrentContextRequest r) =>
        new(r.ChannelId, r.HasContextType ? r.ContextType : null);

    public static Fdc3AddContextListenerRequest ToContract(Proto.AddContextListenerRequest r) =>
        new(r.ListenerId, r.HasChannelId ? r.ChannelId : null, r.HasContextType ? r.ContextType : null);

    public static Fdc3AddIntentListenerRequest ToContract(Proto.AddIntentListenerRequest r) =>
        new(r.ListenerId, r.Intent);

    public static Fdc3AddEventListenerRequest ToContract(Proto.AddEventListenerRequest r) =>
        new(r.ListenerId, r.HasEventType ? r.EventType : null, r.HasChannelId ? r.ChannelId : null);

    public static Fdc3ListenerIdRequest ToContract(Proto.ListenerIdRequest r) => new(r.ListenerId);

    public static Fdc3FindIntentRequest ToContract(Proto.FindIntentRequest r) =>
        new(r.Intent, ParseOptionalContext(r.HasContextJson ? r.ContextJson : null), r.HasResultType ? r.ResultType : null);

    public static Fdc3FindIntentsByContextRequest ToContract(Proto.FindIntentsByContextRequest r) =>
        new(ParseContext(r.ContextJson), r.HasResultType ? r.ResultType : null);

    public static Fdc3OpenRequest ToContract(Proto.OpenRequest r) =>
        new(ToContract(r.App), ParseOptionalContext(r.HasContextJson ? r.ContextJson : null));

    public static Fdc3RaiseIntentRequest ToContract(Proto.RaiseIntentRequest r) =>
        new(r.Intent, ParseContext(r.ContextJson), ToContractOptional(r.App));

    public static Fdc3RaiseIntentForContextRequest ToContract(Proto.RaiseIntentForContextRequest r) =>
        new(ParseContext(r.ContextJson), ToContractOptional(r.App));

    public static Fdc3ResultIdRequest ToContract(Proto.ResultIdRequest r) => new(r.ResultId);

    public static Fdc3SendIntentResultRequest ToContract(Proto.SendIntentResultRequest r) =>
        new(r.ResultId, ParseOptionalContext(r.HasContextJson ? r.ContextJson : null), r.HasChannelId ? r.ChannelId : null, r.HasError ? r.Error : null);

    // Responses (contract -> proto) -------------------------------------------------

    public static Proto.AppIdentifier ToProto(Fdc3AppIdentifier a)
    {
        var app = new Proto.AppIdentifier { AppId = a.AppId };
        if (a.InstanceId is not null) app.InstanceId = a.InstanceId;
        return app;
    }

    public static Proto.ChannelInfo ToProto(Fdc3ChannelInfo c)
    {
        var info = new Proto.ChannelInfo { Id = c.Id, Type = c.Type };
        if (c.DisplayMetadata is { } d)
        {
            var display = new Proto.DisplayMetadata();
            if (d.Name is not null) display.Name = d.Name;
            if (d.Color is not null) display.Color = d.Color;
            if (d.Glyph is not null) display.Glyph = d.Glyph;
            info.DisplayMetadata = display;
        }
        return info;
    }

    public static Proto.AppMetadata ToProto(Fdc3AppMetadata a)
    {
        var app = new Proto.AppMetadata { AppId = a.AppId };
        if (a.InstanceId is not null) app.InstanceId = a.InstanceId;
        if (a.Name is not null) app.Name = a.Name;
        if (a.Version is not null) app.Version = a.Version;
        if (a.Title is not null) app.Title = a.Title;
        if (a.Tooltip is not null) app.Tooltip = a.Tooltip;
        if (a.Description is not null) app.Description = a.Description;
        if (a.ResultType is not null) app.ResultType = a.ResultType;
        return app;
    }

    public static Proto.AppIntent ToProto(Fdc3AppIntent a)
    {
        var metadata = new Proto.IntentMetadata { Name = a.Intent.Name };
        if (a.Intent.DisplayName is not null) metadata.DisplayName = a.Intent.DisplayName;
        var intent = new Proto.AppIntent { Intent = metadata };
        intent.Apps.AddRange(a.Apps.Select(ToProto));
        return intent;
    }

    public static Proto.ImplementationMetadata ToProto(Fdc3ImplementationMetadata m)
    {
        var info = new Proto.ImplementationMetadata
        {
            Fdc3Version = m.Fdc3Version,
            Provider = m.Provider,
            OptionalFeatures = new Proto.OptionalFeatures
            {
                OriginatingAppMetadata = m.OptionalFeatures.OriginatingAppMetadata,
                UserChannelMembershipApis = m.OptionalFeatures.UserChannelMembershipAPIs,
                DesktopAgentBridging = m.OptionalFeatures.DesktopAgentBridging,
            },
            AppMetadata = ToProto(m.AppMetadata),
        };
        if (m.ProviderVersion is not null) info.ProviderVersion = m.ProviderVersion;
        return info;
    }

    public static Proto.IntentResolutionInfo ToProto(Fdc3IntentResolutionInfo r) =>
        new() { Source = ToProto(r.Source), Intent = r.Intent, ResultId = r.ResultId };

    public static Proto.IntentResultInfo ToProto(Fdc3IntentResultInfo r)
    {
        var result = new Proto.IntentResultInfo();
        if (r.Context is { } context) result.ContextJson = context.GetRawText();
        if (r.Channel is { } channel) result.Channel = ToProto(channel);
        return result;
    }

    public static Proto.CurrentContext ToCurrentContext(JsonElement? context)
    {
        var result = new Proto.CurrentContext();
        if (context is { } value) result.ContextJson = value.GetRawText();
        return result;
    }

    // Events (contract -> proto) --------------------------------------------------

    /// <summary>Maps what the agent posts to a sink; null for a message type this transport doesn't know.</summary>
    public static Proto.AgentEvent? ToProtoEvent(object message) => message switch
    {
        Fdc3ContextEvent e => new Proto.AgentEvent { Context = ContextEvent(e) },
        Fdc3IntentEvent e => new Proto.AgentEvent { Intent = IntentEvent(e) },
        Fdc3ApiEvent e => new Proto.AgentEvent { Api = ApiEvent(e) },
        _ => null,
    };

    private static Proto.ContextEvent ContextEvent(Fdc3ContextEvent e)
    {
        var result = new Proto.ContextEvent { ListenerId = e.ListenerId, ContextJson = e.Context.GetRawText() };
        if (e.Source is not null) result.Source = ToProto(e.Source);
        return result;
    }

    private static Proto.IntentEvent IntentEvent(Fdc3IntentEvent e)
    {
        var result = new Proto.IntentEvent
        {
            ListenerId = e.ListenerId,
            Intent = e.Intent,
            ContextJson = e.Context.GetRawText(),
            ResultId = e.ResultId,
        };
        if (e.Source is not null) result.Source = ToProto(e.Source);
        return result;
    }

    private static Proto.ApiEvent ApiEvent(Fdc3ApiEvent e)
    {
        var result = new Proto.ApiEvent { ListenerId = e.ListenerId, EventType = e.EventType };
        if (e.Details is not null) result.DetailsJson = JsonSerializer.Serialize(e.Details, JsonSerializerOptions.Web);
        return result;
    }
}
