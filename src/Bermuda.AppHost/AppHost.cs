using System.Text.Json;

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Bermuda_Api>("api");

// Ports and the FDC3 pipe name can be overridden (WEB_PORT, CDP_PORT, FDC3_GRPC_PIPE in the AppHost's
// configuration or environment), so a CI run can be isolated from anything else on the machine.
var webPort = builder.Configuration["WEB_PORT"] is { Length: > 0 } wp ? wp : "5555";
var cdpPort = builder.Configuration["CDP_PORT"] is { Length: > 0 } cp ? cp : "9222";

var web = builder.AddViteApp("web", "../Bermuda.Web")
    .WithPnpm()
    .WithEnvironment("PORT", webPort)
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"));

builder.AddExecutable("relay", "pnpm", "../Bermuda.Web", "run", "relay:watch")
    .WithEnvironment("RELAY_SCHEMA_URL", api.GetEndpoint("http"))
    .WaitFor(api);

// An optional pipe name for the host and the native apps.
var pipeName = builder.Configuration["FDC3_GRPC_PIPE"];

var host = builder.AddProject<Projects.Bermuda>("host")
    .WithEnvironment("WEB_URL", web.GetEndpoint("http"))
    .WithEnvironment("CDP_PORT", cdpPort)
    .WaitFor(web)
    .WaitFor(api);
if (!string.IsNullOrWhiteSpace(pipeName)) host.WithEnvironment("FDC3_GRPC_PIPE", pipeName);

// The native FDC3 side apps are discovered from the App Directory (type "native" with details.project), so
// adding one is a one-line edit of fdc3-appd.json. They join the host's agent over the gRPC named pipe (the
// default per-user pipe name) and reconnect until it is up.
var appDirectory = Path.Combine(builder.AppHostDirectory, "..", "Bermuda", "fdc3-appd.json");
foreach (var app in JsonDocument.Parse(File.ReadAllText(appDirectory), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
             .RootElement.GetProperty("applications").EnumerateArray())
{
    if (app.GetProperty("type").GetString() != "native"
        || !app.TryGetProperty("details", out var details)
        || !details.TryGetProperty("project", out var project)) continue;

#pragma warning disable ASPIRECSHARPAPPS001 // AddCSharpApp is Aspire's new file/path-based project API (experimental)
    var native = builder.AddCSharpApp(app.GetProperty("appId").GetString()!, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(appDirectory)!, project.GetString()!)))
        .WaitFor(host);
    if (!string.IsNullOrWhiteSpace(pipeName)) native.WithEnvironment("FDC3_GRPC_PIPE", pipeName);
#pragma warning restore ASPIRECSHARPAPPS001
}

builder.Build().Run();

