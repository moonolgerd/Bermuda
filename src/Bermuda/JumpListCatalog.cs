using Bermuda.Fdc3;

namespace Bermuda;

/// <summary>
/// Jump-list tasks: "New window", then one task per web app in the App Directory (native side apps
/// are started by Bermuda itself and are not tasks). The argument string is the same either way.
/// An unpackaged task also stores the path of <c>Bermuda.exe</c>, because a WPF <c>JumpTask</c> is a
/// shortcut to that file. A packaged task must not: the MSIX is laid out under
/// <c>C:\Program Files\WindowsApps</c> and that folder changes on every update, so a stored path would
/// point at an exe that is no longer there. The shell instead launches the package's foreground
/// executable — <c>Bermuda.exe</c>, Application Id <c>App</c> in <c>Package.appxmanifest</c> — and
/// delivers <see cref="Entry.Arguments"/> alone.
/// </summary>
internal static class JumpListCatalog
{
    internal sealed record Entry(string Title, string Description, string Arguments, string? ExecutablePath);

    public static IReadOnlyList<Entry> Create(AppDirectory directory, bool packaged, string? unpackagedExecutable)
    {
        // No path means an unpackaged shortcut would have nothing to launch. Packaged tasks ignore it.
        if (!packaged && string.IsNullOrEmpty(unpackagedExecutable)) return [];

        var executable = packaged ? null : unpackagedExecutable;
        var entries = new List<Entry>
        {
            new("New window", "Open a new Bermuda window", LaunchArguments.NewWindow, executable),
        };

        foreach (var app in directory.Apps)
        {
            if (!string.Equals(app.Type, "web", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(app.AppId)) continue;

            var title = First(app.Title, app.Name, app.AppId);
            var description = First(app.Description, app.Tooltip, "Open " + title);
            entries.Add(new(title, description, LaunchArguments.OpenCommand(app.AppId), executable));
        }

        return entries;
    }

    private static string First(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value;
        return "";
    }
}
