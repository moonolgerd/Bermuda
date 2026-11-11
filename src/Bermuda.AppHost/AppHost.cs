var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Bermuda_Api>("api");

var web = builder.AddViteApp("web", "../Bermuda.Web")
    .WithPnpm()
    .WithEnvironment("PORT", "5555")
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"));

builder.AddExecutable("relay", "pnpm", "../Bermuda.Web", "run", "relay:watch")
    .WithEnvironment("RELAY_SCHEMA_URL", api.GetEndpoint("http"))
    .WaitFor(api);

var host = builder.AddProject<Projects.Bermuda>("host")
    .WithEnvironment("WEB_URL", web.GetEndpoint("http"))
    .WithEnvironment("CDP_PORT", "9222")
    .WaitFor(web)
    .WaitFor(api);

// A native FDC3 side app: joins the host's agent over the gRPC named pipe
// (both use the default per-user pipe name; it reconnects until the pipe is up).
builder.AddProject<Projects.Bermuda_NativeApp>("native-app")
    .WaitFor(host);

builder.Build().Run();

