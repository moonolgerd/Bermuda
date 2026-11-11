using System.Runtime.InteropServices;
using Bermuda.IpcContract;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Bermuda;

/// <summary>
/// Update checks for the MSIX build. Windows owns the actual update: the .appinstaller
/// that installed the app records its own URL, and <see cref="Package.CheckUpdateAvailabilityAsync"/>
/// compares the installed package version with whatever that file currently points at.
/// Outside a package (dev runs) or for a package installed without an .appinstaller there
/// is nothing to check, which is reported as <see cref="UpdateStatus.Unsupported"/>.
/// </summary>
internal static class UpdateService
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    private static bool IsPackaged()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    private static string CurrentVersion()
    {
        if (!IsPackaged()) return "dev";
        var v = Package.Current.Id.Version;
        return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
    }

    public static async Task<UpdateCheckResult> CheckAsync()
    {
        if (!IsPackaged())
            return new(UpdateStatus.Unsupported, CurrentVersion(), "Not running from an installed package.");

        if (Package.Current.GetAppInstallerInfo() is null)
            return new(UpdateStatus.Unsupported, CurrentVersion(), "Installed without an .appinstaller, so there is no update source.");

        var availability = await Package.Current.CheckUpdateAvailabilityAsync();
        var status = availability.Availability switch
        {
            PackageUpdateAvailability.NoUpdates => UpdateStatus.None,
            PackageUpdateAvailability.Available => UpdateStatus.Available,
            PackageUpdateAvailability.Required => UpdateStatus.Required,
            PackageUpdateAvailability.Error => UpdateStatus.Error,
            _ => UpdateStatus.Unknown,
        };
        return new(status, CurrentVersion(), status == UpdateStatus.Error ? availability.ExtendedError.Message : "");
    }

    public static async Task<UpdateApplyResult> ApplyAsync()
    {
        if (!IsPackaged() || Package.Current.GetAppInstallerInfo() is not { } info)
            return new(false, "No .appinstaller update source.");

        // Windows stops the app, swaps the package and the user relaunches it.
        var result = await new PackageManager().RequestAddPackageByAppInstallerFileAsync(
            info.Uri, AddPackageByAppInstallerOptions.ForceTargetAppShutdown, targetVolume: null!);

        return result.ExtendedErrorCode is null
            ? new(true, "")
            : new(false, result.ErrorText);
    }
}
