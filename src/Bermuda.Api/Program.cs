using Bermuda.Api.GraphQL;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddSingleton<IncidentRepository>();

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.SetIsOriginAllowed(origin =>
                  Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                  uri.Host == "localhost")
              .AllowAnyHeader()
              .AllowAnyMethod()));

builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>();

var app = builder.Build();

app.UseCors();
app.MapDefaultEndpoints();
app.MapGraphQL();

app.Run();

