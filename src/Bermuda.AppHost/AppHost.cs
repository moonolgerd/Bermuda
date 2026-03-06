var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.Bermuda_Api>("api");

var web = builder.AddViteApp("web", "../Bermuda.Web")
    .WithPnpm()
    .WithEnvironment("PORT", "5555")
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("http"));

builder.AddExecutable("relay", "pnpm", "../Bermuda.Web", "run", "relay:watch")
    .WithEnvironment("RELAY_SCHEMA_URL", api.GetEndpoint("http"))
    .WaitFor(api);

builder.AddProject<Projects.Bermuda>("host")
    .WithEnvironment("WEB_URL", web.GetEndpoint("http"))
    .WithEnvironment("CDP_PORT", "9222")
    .WaitFor(web)
    .WaitFor(api);

builder.Build().Run();

