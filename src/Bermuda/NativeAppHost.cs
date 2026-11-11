using System.Diagnostics;
using System.IO;
using Bermuda.Fdc3;

namespace Bermuda;

/// <summary>
/// Starts the native side apps of the App Directory (type "native", details.path relative to this exe) and stops
/// the ones it started when Bermuda exits. Used at startup and by <see cref="WindowAppLauncher"/> when
/// fdc3.open or an intent needs an app that is not running. Each app allows one copy per user, so starting one
/// that is already running is harmless.
/// </summary>
public sealed class NativeAppHost(string? pipeName) : IDisposable
{
    private readonly List<Process> _processes = [];

    /// <summary>Resolves details.path; it must stay inside the folder Bermuda runs from.</summary>
    public static string? ResolvePath(AppDefinition app)
    {
        if (app.Type != "native" || app.Details?.Path is not { Length: > 0 } path) return null;
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var full = Path.GetFullPath(Path.Combine(root, path));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    /// <summary>Starts the app with the pipe name (and, when launched for FDC3, the instance id) in its environment.</summary>
    public void Start(AppDefinition app, string? instanceId = null)
    {
        var exe = ResolvePath(app) ?? throw new InvalidOperationException($"App '{app.AppId}' has no native exe inside the package.");
        var info = new ProcessStartInfo(exe, app.Details!.Arguments ?? "")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        if (!string.IsNullOrWhiteSpace(pipeName)) info.Environment["FDC3_GRPC_PIPE"] = pipeName;
        if (instanceId is not null) info.Environment["FDC3_INSTANCE_ID"] = instanceId;

        if (Process.Start(info) is { } process)
        {
            lock (_processes) _processes.Add(process);
        }
    }

    /// <summary>
    /// Closes the apps this host started. One that was already running (so its copy exited at once) or that
    /// someone else started has a dead handle here and nothing is killed.
    /// </summary>
    public void Dispose()
    {
        lock (_processes)
        {
            foreach (var process in _processes)
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Native FDC3 app did not stop: {ex.Message}");
                }
            }
            _processes.Clear();
        }
    }
}
