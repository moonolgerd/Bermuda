using System.Windows;
using System.Windows.Shell;
using Bermuda.Fdc3;

namespace Bermuda;

/// <summary>
/// Publishes <see cref="JumpListCatalog"/> as a WPF <see cref="JumpList"/> on this process's
/// taskbar button. Each <see cref="JumpTask"/> is a shortcut to the running <c>Bermuda.exe</c>
/// plus <c>--new-window</c> or <c>--open &lt;appId&gt;</c>. Startup rewrites the list, so a packaged
/// install points at the current copy under WindowsApps.
/// </summary>
internal static class JumpListService
{
    public static void Register(AppDirectory directory)
    {
        try
        {
            if (Application.Current is null) return;

            var entries = JumpListCatalog.Create(directory, Environment.ProcessPath);
            if (entries.Count == 0) return;

            var list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
            foreach (var entry in entries)
            {
                // Arguments stay "--new-window" / "--open <appId>". The shell puts the exe on the
                // command line itself; activation then echoes that full line back (see LaunchArguments).
                list.JumpItems.Add(new JumpTask
                {
                    Title = entry.Title,
                    Description = entry.Description,
                    ApplicationPath = entry.ExecutablePath,
                    Arguments = entry.Arguments,
                    // The exe's embedded icon (ApplicationIcon), not the loose png — a JumpTask icon has to be a native resource.
                    IconResourcePath = entry.ExecutablePath,
                    IconResourceIndex = 0,
                });
            }

            JumpList.SetJumpList(Application.Current, list);
        }
        catch (Exception ex)
        {
            // The jump list is a shortcut. Failing to publish it must not stop the app from opening.
            System.Diagnostics.Debug.WriteLine($"Jump list failed to register: {ex}");
        }
    }
}
