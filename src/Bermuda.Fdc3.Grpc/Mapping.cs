using System.Text.Json;

namespace Bermuda.Fdc3.Grpc;

/// <summary>
/// The hand-written part of the proto &lt;-&gt; contract mapping: how a JSON
/// text field becomes a context. Everything else is in
/// <c>Generated/Fdc3Generated.g.cs</c> (see Bermuda.Fdc3.Grpc.Generator).
/// </summary>
internal static class Mapping
{
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
}
