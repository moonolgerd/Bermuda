using System.Reflection;

namespace Bermuda.IpcContract;

/// <summary>
/// Runtime view of the IPC contract. Commands are discovered by reflecting over
/// the <see cref="IpCommandAttribute"/> declarations in this assembly, so the
/// allow-list and the generated TypeScript can never drift apart.
/// </summary>
public static class IpcContract
{
    public static readonly IReadOnlyList<IpcCommand> Commands = Discover();

    private static IReadOnlyList<IpcCommand> Discover()
    {
        var assembly = typeof(IpCommandAttribute).Assembly;

        var commands = new List<IpcCommand>();
        foreach (var type in assembly.GetTypes())
        {
            var attribute = type.GetCustomAttribute<IpCommandAttribute>();
            if (attribute is null) continue;

            commands.Add(new IpcCommand(attribute.Name, attribute.InputType, attribute.OutputType, attribute.Description));
        }

        commands.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return commands;
    }

    public static IpcCommand? Find(string name) =>
        Commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A discovered <c>[IpCommand]</c> declaration.</summary>
public sealed record IpcCommand(string Name, string InputType, string OutputType, string Description);
