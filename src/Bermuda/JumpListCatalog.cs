using Bermuda.Fdc3;

namespace Bermuda;

/// <summary>
/// Jump-list tasks: "New window", then one task per web app in the App Directory (native side apps
/// are started by Bermuda itself and are not tasks). Every task is a WPF <c>JumpTask</c> aimed at
/// <c>Bermuda.exe</c> with <c>--new-window</c> or <c>--open &lt;appId&gt;</c>.
/// </summary>
internal static class JumpListCatalog
{
    internal sealed record Entry(string Title, string Description, string Arguments, string ExecutablePath);

    public static IReadOnlyList<Entry> Create(AppDirectory directory, string? executable)
    {
        // A JumpTask is a shortcut. With no exe there is nothing to launch.
        if (string.IsNullOrEmpty(executable)) return [];

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
