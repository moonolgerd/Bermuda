using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bermuda.Fdc3;

/// <summary>
/// The subset of an FDC3 App Directory (AppD v2) application record that the
/// desktop agent uses: identity/display fields, the launch URL, and the
/// intents it listens for. Loaded from <c>fdc3-appd.json</c> in the host's
/// output folder — same shape as the <c>applications</c> array of an AppD
/// <c>GET /v2/apps</c> response, so records can be copied from a real AppD.
/// </summary>
public sealed record AppDefinition
{
    public required string AppId { get; init; }
    public string? Name { get; init; }
    public string? Title { get; init; }
    public string? Tooltip { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }
    public string Type { get; init; } = "web";
    public AppDetails? Details { get; init; }
    public AppInterop? Interop { get; init; }

    /// <summary>Intents this app declares it listens for, keyed by intent name.</summary>
    public IReadOnlyDictionary<string, IntentDefinition> ListensFor =>
        Interop?.Intents?.ListensFor ?? EmptyIntents;

    private static readonly IReadOnlyDictionary<string, IntentDefinition> EmptyIntents =
        new Dictionary<string, IntentDefinition>();
}

public sealed record AppDetails(string? Url);

public sealed record AppInterop(AppInteropIntents? Intents);

public sealed record AppInteropIntents(Dictionary<string, IntentDefinition>? ListensFor);

/// <summary>An AppD <c>interop.intents.listensFor</c> entry.</summary>
public sealed record IntentDefinition
{
    public string? DisplayName { get; init; }

    /// <summary>Context types the app accepts for this intent. Empty = any.</summary>
    public string[] Contexts { get; init; } = [];

    public string? ResultType { get; init; }

    public bool Accepts(string? contextType) =>
        contextType is null || Contexts.Length == 0 || Contexts.Contains(contextType, StringComparer.Ordinal);
}

public sealed class AppDirectory
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, AppDefinition> _apps;

    public AppDirectory(IEnumerable<AppDefinition> apps)
    {
        _apps = apps.ToDictionary(a => a.AppId, StringComparer.OrdinalIgnoreCase);
    }

    public static AppDirectory Empty { get; } = new([]);

    public IEnumerable<AppDefinition> Apps => _apps.Values;

    public AppDefinition? Find(string appId) => _apps.GetValueOrDefault(appId);

    /// <summary>Parses an AppD-style document: <c>{ "applications": [ ... ] }</c>.</summary>
    public static AppDirectory Parse(string json)
    {
        var document = JsonSerializer.Deserialize<AppDirectoryDocument>(json, Json);
        return new AppDirectory(document?.Applications ?? []);
    }

    public static AppDirectory Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty;

    private sealed record AppDirectoryDocument(
        [property: JsonPropertyName("applications")] AppDefinition[]? Applications);
}
