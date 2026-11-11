#:property NuGetAudit=false
#:property PublishAot=false

// Reads the FDC3 App Directory (fdc3-appd.json) for the native side apps that ship in Bermuda's MSIX.
// A .NET file-based app, run by src/Bermuda/Bermuda.csproj (the CreateMsix and PublishNativeApps targets):
//
//   dotnet run --file tools/native-apps.cs -- list  <fdc3-appd.json>
//       prints one line per native app: appId|exe|project   (project is an absolute path, or empty)
//   dotnet run --file tools/native-apps.cs -- patch <fdc3-appd.json> <appxmanifest.xml>
//       replaces the <!-- @NATIVE_APPS@ --> marker in the manifest with one <Application> per native app
//
// A native app is an entry with "type": "native". It needs details.path (its exe in the package);
// details.project (the .csproj that builds it, relative to the directory file) is needed to build it.

using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args is not ([var mode, var directory, ..] and var rest) || mode is not ("list" or "patch")
    || (mode == "patch" && rest.Length < 3))
{
    Console.Error.WriteLine("usage: native-apps.cs list <fdc3-appd.json> | patch <fdc3-appd.json> <appxmanifest.xml>");
    return 2;
}

var baseDir = Path.GetDirectoryName(Path.GetFullPath(directory))!;
var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
using var document = JsonDocument.Parse(File.ReadAllText(directory), options);

var apps = document.RootElement.GetProperty("applications").EnumerateArray()
    .Where(a => a.GetProperty("type").GetString() == "native")
    .ToList();

string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;

foreach (var app in apps)
{
    if (!app.TryGetProperty("details", out var d) || Text(d, "path") is null)
    {
        Console.Error.WriteLine($"Native app '{Text(app, "appId")}' in fdc3-appd.json needs details.path (its exe in the package).");
        return 1;
    }
}

if (mode == "list")
{
    foreach (var app in apps)
    {
        var d = app.GetProperty("details");
        var project = Text(d, "project") is { } p ? Path.GetFullPath(Path.Combine(baseDir, p)) : "";
        Console.WriteLine($"{Text(app, "appId")}|{Text(d, "path")}|{project}");
    }
    return 0;
}

var manifestPath = rest[2];
const string marker = "<!-- @NATIVE_APPS@ -->";
var xml = new StringBuilder();
foreach (var app in apps)
{
    var id = Text(app, "appId")!;
    var d = app.GetProperty("details");
    var title = Text(app, "title") ?? id;
    var description = Text(app, "description") ?? title;
    // Manifest Application ids are letters and digits only.
    var key = "Native" + Regex.Replace(id, "[^A-Za-z0-9]", "");
    xml.Append($"""

            <!-- Native FDC3 side app {SecurityElement.Escape(id)} (from fdc3-appd.json): shipped in this package so it installs and
                 updates with Bermuda. A background app with no Start menu entry; Bermuda starts and stops it. -->
            <Application Id="{key}" Executable="{SecurityElement.Escape(Text(d, "path"))}" EntryPoint="Windows.FullTrustApplication">
              <uap:VisualElements DisplayName="{SecurityElement.Escape(title)}" Description="{SecurityElement.Escape(description)}" BackgroundColor="transparent"
                Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" AppListEntry="none" />
            </Application>

        """);
}

var manifest = File.ReadAllText(manifestPath);
if (!manifest.Contains(marker))
{
    Console.Error.WriteLine($"The manifest has no {marker} marker.");
    return 1;
}
File.WriteAllText(manifestPath, manifest.Replace(marker, xml.ToString()));
return 0;
