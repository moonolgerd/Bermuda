#:property NuGetAudit=false
#:property PublishAot=false

// Packaging steps for Bermuda's MSIX that depend on the FDC3 App Directory (fdc3-appd.json).
// A .NET file-based app, run by the CreateMsix and PublishNativeApps targets in src/Bermuda/Bermuda.csproj:
//
//   dotnet run --file tools/native-apps.cs -- publish     <fdc3-appd.json> <outDir> <configuration>
//       publishes every native app's project (self-contained, win-x64) into outDir, next to Bermuda,
//       and checks that each app's exe is there afterwards
//   dotnet run --file tools/native-apps.cs -- manifest    <fdc3-appd.json> <Package.appxmanifest> <dest> <version>
//       writes dest: the manifest with Identity/@Version = version and one <Application> per native app
//   dotnet run --file tools/native-apps.cs -- appinstaller <Package.appxmanifest> <template> <dest> <version> <msixUrl> <appInstallerUrl>
//       writes the .appinstaller, taking the package name and publisher from the manifest
//
// A native app is an entry with "type": "native". It needs details.path (its exe, relative to Bermuda's folder)
// and, to be built into the package, details.project (the .csproj, relative to the directory file).
// Every problem is reported as one "native-apps: ..." error line and exit code 1.

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

try
{
    return args switch
    {
        ["publish", var appd, var outDir, var configuration] => Publish(LoadApps(appd), outDir, configuration),
        ["manifest", var appd, var source, var dest, var version] => WriteManifest(LoadApps(appd), source, dest, version),
        ["appinstaller", var manifest, var template, var dest, var version, var msixUrl, var appInstallerUrl] =>
            WriteAppInstaller(manifest, template, dest, version, msixUrl, appInstallerUrl),
        _ => throw new ToolException("usage: publish <appd> <outDir> <configuration> | manifest <appd> <manifest> <dest> <version> | appinstaller <manifest> <template> <dest> <version> <msixUrl> <appInstallerUrl>"),
    };
}
catch (Exception ex) when (ex is ToolException or JsonException or XmlException or IOException)
{
    Console.Error.WriteLine("native-apps: " + ex.Message);
    return 1;
}

// ---------------------------------------------------------------------------------------------------------------

static List<NativeApp> LoadApps(string appdPath)
{
    var baseDir = Path.GetDirectoryName(Path.GetFullPath(appdPath))!;
    var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    using var document = JsonDocument.Parse(File.ReadAllText(appdPath), options);

    if (!document.RootElement.TryGetProperty("applications", out var applications) || applications.ValueKind != JsonValueKind.Array)
        throw new ToolException($"{appdPath} has no \"applications\" array.");

    var apps = new List<NativeApp>();
    foreach (var app in applications.EnumerateArray())
    {
        if (Text(app, "type") != "native") continue;

        var id = Text(app, "appId") ?? throw new ToolException("A native app in fdc3-appd.json has no appId.");
        var details = app.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object ? d : (JsonElement?)null;

        var exe = details is { } dd ? Text(dd, "path") : null;
        if (string.IsNullOrWhiteSpace(exe)) throw new ToolException($"Native app '{id}' needs details.path (its exe in the package).");
        if (Path.IsPathRooted(exe) || exe.Split('/', '\\').Contains(".."))
            throw new ToolException($"Native app '{id}': details.path '{exe}' must be relative and stay inside Bermuda's folder.");

        string? project = null;
        if (details is { } pd && Text(pd, "project") is { Length: > 0 } relative)
        {
            project = Path.GetFullPath(Path.Combine(baseDir, relative));
            if (!File.Exists(project)) throw new ToolException($"Native app '{id}': details.project '{relative}' does not exist ({project}).");
        }

        // Manifest Application ids are letters and digits only, and must be unique.
        var key = "Native" + Regex.Replace(id, "[^A-Za-z0-9]", "");
        if (apps.Any(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            throw new ToolException($"Native app '{id}' maps to the manifest application id '{key}', which another native app already uses.");

        var title = Text(app, "title") ?? id;
        apps.Add(new NativeApp(id, key, title, Text(app, "description") ?? title, exe.Replace('/', '\\'), project));
    }

    return apps;
}

static int Publish(List<NativeApp> apps, string outDir, string configuration)
{
    foreach (var app in apps)
    {
        if (app.Project is null)
            throw new ToolException($"Native app '{app.AppId}' has no details.project, so it cannot be built into the package.");

        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var arg in new[]
                 {
                     "publish", app.Project, "--configuration", configuration, "--runtime", "win-x64", "--self-contained", "true",
                     "--output", Path.GetFullPath(outDir), "-p:PublishSingleFile=false", "-p:PublishReadyToRun=false", "--nologo",
                 })
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info) ?? throw new ToolException("Could not start dotnet.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new ToolException($"Publishing '{app.AppId}' ({app.Project}) failed with exit code {process.ExitCode}.");
    }

    foreach (var app in apps)
    {
        if (!File.Exists(Path.Combine(outDir, app.Exe)))
            throw new ToolException($"'{outDir}' has no {app.Exe} after publishing '{app.AppId}': details.path must be the exe its project produces.");
    }

    return 0;
}

static int WriteManifest(List<NativeApp> apps, string sourcePath, string destPath, string version)
{
    if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+\.\d+$"))
        throw new ToolException($"MsixVersion must have four parts (e.g. 1.2.3.0), got '{version}'.");

    var manifest = XDocument.Load(sourcePath, LoadOptions.PreserveWhitespace);
    var root = manifest.Root ?? throw new ToolException($"{sourcePath} is empty.");
    XNamespace foundation = root.Name.Namespace;
    var uap = root.GetNamespaceOfPrefix("uap") ?? throw new ToolException($"{sourcePath} does not declare the uap namespace.");

    // The Identity Version is stamped from the build: Windows compares it, not the file name, to decide whether an
    // .appinstaller update is newer.
    (root.Element(foundation + "Identity") ?? throw new ToolException($"{sourcePath} has no Identity element.")).SetAttributeValue("Version", version);

    var applications = root.Element(foundation + "Applications") ?? throw new ToolException($"{sourcePath} has no Applications element.");
    foreach (var app in apps)
    {
        if (applications.Elements(foundation + "Application").Any(a => (string?)a.Attribute("Id") == app.Key))
            throw new ToolException($"{sourcePath} already declares an application with id '{app.Key}'; it is generated from fdc3-appd.json.");

        // A background app: no Start menu entry (Bermuda starts and stops it), and no startup task of its own.
        var application = new XElement(foundation + "Application",
            new XAttribute("Id", app.Key),
            new XAttribute("Executable", app.Exe),
            new XAttribute("EntryPoint", "Windows.FullTrustApplication"),
            new XText("\n      "),
            new XElement(uap + "VisualElements",
                new XAttribute("DisplayName", app.Title),
                new XAttribute("Description", app.Description),
                new XAttribute("BackgroundColor", "transparent"),
                new XAttribute("Square150x150Logo", @"Assets\Square150x150Logo.png"),
                new XAttribute("Square44x44Logo", @"Assets\Square44x44Logo.png"),
                new XAttribute("AppListEntry", "none")),
            new XText("\n    "));

        applications.Add(new XText("\n    "), new XComment($" Native FDC3 side app {app.AppId}, from fdc3-appd.json "), new XText("\n    "), application, new XText("\n  "));
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
    using var writer = XmlWriter.Create(destPath, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false) });
    manifest.Save(writer);
    return 0;
}

static int WriteAppInstaller(string manifestPath, string templatePath, string destPath, string version, string msixUrl, string appInstallerUrl)
{
    if (string.IsNullOrWhiteSpace(msixUrl)) throw new ToolException("AppInstallerUrl needs MsixUrl as well.");

    var manifest = XDocument.Load(manifestPath);
    var identity = manifest.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")
        ?? throw new ToolException($"{manifestPath} has no Identity element.");

    var text = File.ReadAllText(templatePath)
        .Replace("@VERSION@", version)
        .Replace("@NAME@", (string?)identity.Attribute("Name") ?? throw new ToolException("The manifest Identity has no Name."))
        .Replace("@PUBLISHER@", System.Security.SecurityElement.Escape((string?)identity.Attribute("Publisher") ?? throw new ToolException("The manifest Identity has no Publisher.")))
        .Replace("@MSIX_URL@", msixUrl)
        .Replace("@APPINSTALLER_URL@", appInstallerUrl);

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
    File.WriteAllText(destPath, text);
    return 0;
}

static string? Text(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

record NativeApp(string AppId, string Key, string Title, string Description, string Exe, string? Project);

sealed class ToolException(string message) : Exception(message);
