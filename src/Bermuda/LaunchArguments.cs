namespace Bermuda;

/// <summary>
/// Turns a launch-activation string into the tokens <see cref="App"/> acts on
/// (<c>--new-window</c>, <c>--open &lt;appId&gt;</c>).
/// </summary>
/// <remarks>
/// <see cref="Microsoft.Windows.AppLifecycle.AppInstance.GetActivatedEventArgs"/> does not report
/// launch arguments the same way for an MSIX process and an unpackaged one (Windows App SDK issue 3863):
/// a packaged <c>ILaunchActivatedEventArgs.Arguments</c> is only the string the shell passed
/// (<c>--open fdc3-probe</c>), while an unpackaged one is the whole command line from
/// <c>GetCommandLine</c>, executable first and quoted when the path contains spaces
/// (<c>"C:\Program Files\WindowsApps\...\Bermuda.exe" --open fdc3-probe</c>).
/// A jump list click is forwarded to the running instance as that activation string, so both shapes
/// have to yield the same tokens.
/// </remarks>
internal static class LaunchArguments
{
    public const string Open = "--open";
    public const string NewWindow = "--new-window";

    public static bool HasTask(IReadOnlyList<string> args) =>
        args.Any(a =>
            string.Equals(a, Open, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, NewWindow, StringComparison.OrdinalIgnoreCase));

    /// <summary>The argument string a jump-list task stores for <paramref name="appId"/>.</summary>
    public static string OpenCommand(string appId) =>
        appId.IndexOfAny([' ', '\t', '"']) < 0 ? $"{Open} {appId}" : $"{Open} \"{appId.Replace("\"", "")}\"";

    public static string[] Parse(string? activationArguments)
    {
        if (string.IsNullOrWhiteSpace(activationArguments)) return [];

        var tokens = Tokenize(activationArguments.Trim());
        // Only the unpackaged shape starts with the executable. A packaged argument string starts
        // with the flag, and an app id is never a path, so a leading "*.exe" token is argv0.
        if (tokens.Count > 0 && IsExecutablePath(tokens[0]))
            tokens.RemoveAt(0);

        return [.. tokens];
    }

    private static bool IsExecutablePath(string token) =>
        !token.StartsWith('-') && token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a command line the way <c>CommandLineToArgvW</c> does for the lines we produce:
    /// whitespace separates tokens, and quotes group a path that contains spaces. Quotes are removed.
    /// </summary>
    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
