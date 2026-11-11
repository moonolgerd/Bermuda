using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bermuda.Fdc3.Grpc;

/// <summary>
/// Serves a <see cref="DesktopAgent"/> to native apps over gRPC on a Windows
/// named pipe (Kestrel's named-pipe transport, HTTP/2 without TLS). The pipe
/// is restricted to the current user. Dispose stops the server and
/// disconnects every pipe client's instance.
/// </summary>
public sealed class Fdc3GrpcHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private Fdc3GrpcHost(WebApplication app, string pipeName)
    {
        _app = app;
        PipeName = pipeName;
    }

    public string PipeName { get; }

    /// <summary>
    /// <c>bermuda-fdc3-&lt;user name&gt;</c>. Pipe names are machine-wide, so the
    /// user name keeps two signed-in users from sharing (or fighting over) one.
    /// </summary>
    public static string DefaultPipeName { get; } =
        "bermuda-fdc3-" + Regex.Replace(Environment.UserName, @"[^A-Za-z0-9_.-]", "_");

    /// <param name="pipeName">The pipe name (without <c>\\.\pipe\</c>); null = <see cref="DefaultPipeName"/>.</param>
    public static async Task<Fdc3GrpcHost> StartAsync(DesktopAgent agent, string? pipeName = null, CancellationToken cancellationToken = default)
    {
        pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        // Only the pipe: ignore ASPNETCORE_URLS and friends.
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, "");
        // Named pipes are Windows-only (this is a Windows desktop host); elsewhere Start fails.
#pragma warning disable CA1416
        builder.WebHost.UseNamedPipes(options => options.CurrentUserOnly = true);
#pragma warning restore CA1416
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenNamedPipe(pipeName, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
        }));
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddSingleton(agent);
        builder.Services.AddSingleton<Fdc3ConnectionRegistry>();
        builder.Services.AddGrpc(o => o.Interceptors.Add<Fdc3ErrorInterceptor>());

        var app = builder.Build();
        app.MapGrpcService<Fdc3AgentService>();
        await app.StartAsync(cancellationToken);
        return new Fdc3GrpcHost(app, pipeName);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _app.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }
        await _app.DisposeAsync();
    }
}
