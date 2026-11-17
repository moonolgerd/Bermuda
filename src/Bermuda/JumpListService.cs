using System.Windows;
using System.Windows.Shell;
using Bermuda.Fdc3;
using PackagedJumpList = Windows.UI.StartScreen.JumpList;

namespace Bermuda;

/// <summary>
/// Publishes <see cref="JumpListCatalog"/> to the taskbar. Unpackaged runs use the WPF
/// <see cref="JumpList"/> (a shortcut per task, aimed at this exe). Packaged runs use the WinRT
/// jump list, which requires package identity and stores arguments only — the right shape for an
/// MSIX, whose exe path is not stable. See <see cref="JumpListCatalog"/>.
/// </summary>
internal static class JumpListService
{
    private static readonly Uri PackagedLogo = new("ms-appx:///Assets/Square44x44Logo.png");

    public static void Register(AppDirectory directory)
    {
        try
        {
            var packaged = UpdateService.IsPackaged();
            var entries = JumpListCatalog.Create(directory, packaged, Environment.ProcessPath);
            if (entries.Count == 0) return;

            if (packaged) RegisterPackaged(entries);
            else RegisterUnpackaged(entries);
        }
        catch (Exception ex)
        {
            // The jump list is a shortcut. Failing to publish it must not stop the app from opening.
            System.Diagnostics.Debug.WriteLine($"Jump list failed to register: {ex}");
        }
    }

    private static void RegisterUnpackaged(IReadOnlyList<JumpListCatalog.Entry> entries)
    {
        if (Application.Current is null) return;
        var exe = entries[0].ExecutablePath;
        if (string.IsNullOrEmpty(exe)) return;

        var list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
        foreach (var entry in entries)
        {
            // Arguments stay "--new-window" / "--open <appId>". The shell puts the exe on the
            // command line itself; activation then echoes that full line back (see LaunchArguments).
            list.JumpItems.Add(new JumpTask
            {
                Title = entry.Title,
                Description = entry.Description,
                ApplicationPath = exe,
                Arguments = entry.Arguments,
                // The exe's embedded icon (ApplicationIcon), not the loose png — a JumpTask icon has to be a native resource.
                IconResourcePath = exe,
                IconResourceIndex = 0,
            });
        }

        JumpList.SetJumpList(Application.Current, list);
    }

    private static void RegisterPackaged(IReadOnlyList<JumpListCatalog.Entry> entries)
    {
        // Not awaited: LoadCurrentAsync/SaveAsync must not block the UI thread (an STA that waits
        // on a WinRT call which posts back can deadlock), and startup does not depend on the result.
        _ = SavePackagedAsync(entries);
    }

    private static async Task SavePackagedAsync(IReadOnlyList<JumpListCatalog.Entry> entries)
    {
        try
        {
            await SavePackagedAsync(entries, withLogo: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Jump list logo rejected, retrying without it: {ex.Message}");
            try
            {
                await SavePackagedAsync(entries, withLogo: false);
            }
            catch (Exception retry)
            {
                System.Diagnostics.Debug.WriteLine($"Jump list failed to register: {retry}");
            }
        }
    }

    private static async Task SavePackagedAsync(IReadOnlyList<JumpListCatalog.Entry> entries, bool withLogo)
    {
        var list = await PackagedJumpList.LoadCurrentAsync();
        list.SystemGroupKind = Windows.UI.StartScreen.JumpListSystemGroupKind.None;
        list.Items.Clear();

        foreach (var entry in entries)
        {
            // CreateWithArguments is the whole launch contract: no exe path. Windows starts the
            // package's Bermuda.exe and sets ILaunchActivatedEventArgs.Arguments to this string.
            var item = Windows.UI.StartScreen.JumpListItem.CreateWithArguments(entry.Arguments, entry.Title);
            item.Description = entry.Description;
            if (withLogo) item.Logo = PackagedLogo;
            list.Items.Add(item);
        }

        await list.SaveAsync();
    }
}
