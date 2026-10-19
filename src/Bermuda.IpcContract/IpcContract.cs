using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Bermuda.IpcContract;

/// <summary>
/// Runtime view of the IPC contract. Commands are discovered by reflecting over
/// the <see cref="IpcCommandsAttribute"/> interfaces in this assembly, which are
/// also what the source generator reads. That keeps the allow-list, the dispatch
/// and the generated TypeScript in sync.
/// </summary>
public static class IpcContract
{
    public static readonly IReadOnlyList<IpcCommand> Commands = Discover();

    private static IReadOnlyList<IpcCommand> Discover()
    {
        var commands =
            from type in typeof(IpcCommandsAttribute).Assembly.GetTypes()
            let attribute = type.GetCustomAttribute<IpcCommandsAttribute>()
            where attribute is not null && type.IsInterface
            from method in type.GetMethods()
            select new IpcCommand(CommandName(attribute.Prefix, method.Name), type, method);

        return [.. commands.OrderBy(c => c.Name, StringComparer.Ordinal)];
    }

    public static IpcCommand? Find(string name) =>
        Commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>Fdc3.GetInfo</c> → <c>fdc3.getInfo</c>, <c>OpenAsync</c> → <c>open</c>.
    /// Mirrored by the source generator, which can't reference this assembly.
    /// </summary>
    public static string CommandName(string prefix, string methodName)
    {
        var name = methodName.EndsWith("Async", StringComparison.Ordinal) ? methodName[..^"Async".Length] : methodName;
        name = char.ToLowerInvariant(name[0]) + name[1..];
        return prefix.Length == 0 ? name : $"{prefix}.{name}";
    }
}

/// <summary>Thrown when an <c>invoke()</c> payload doesn't bind to the command's parameter.</summary>
public sealed class IpcInvalidPayloadException() : Exception("InvalidArguments");

/// <summary>One method on an <see cref="IpcCommandsAttribute"/> interface.</summary>
public sealed record IpcCommand(string Name, Type Interface, MethodInfo Method)
{
    public Type? PayloadType { get; } = Method.GetParameters() is [var parameter] ? parameter.ParameterType : null;

    /// <summary>
    /// Binds <paramref name="payload"/> to the command's parameter, calls it on
    /// <paramref name="target"/> (an implementation of <see cref="Interface"/>),
    /// and awaits it if it's async. Exceptions from the command propagate unwrapped.
    /// </summary>
    public async Task<object?> InvokeAsync(object target, JsonElement? payload, JsonSerializerOptions options)
    {
        object?[] arguments = PayloadType is null ? [] : [BindPayload(payload, options)];

        object? result;
        try
        {
            result = Method.Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }

        if (result is not Task task) return result;

        await task;
        return Method.ReturnType.IsGenericType
            ? Method.ReturnType.GetProperty(nameof(Task<object>.Result))!.GetValue(task)
            : null;
    }

    private object BindPayload(JsonElement? payload, JsonSerializerOptions options)
    {
        try
        {
            return payload?.Deserialize(PayloadType!, options) ?? throw new IpcInvalidPayloadException();
        }
        catch (JsonException)
        {
            throw new IpcInvalidPayloadException();
        }
    }
}
