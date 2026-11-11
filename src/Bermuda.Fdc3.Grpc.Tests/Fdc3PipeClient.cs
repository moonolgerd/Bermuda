using System.IO.Pipes;
using System.Net.Http;
using System.Threading.Channels;
using Bermuda.Fdc3.Grpc.Proto;
using Grpc.Core;
using Grpc.Net.Client;

namespace Bermuda.Fdc3.Grpc.Tests;

/// <summary>
/// A minimal native FDC3 app: connects to Bermuda's named pipe, subscribes as
/// <c>appId</c> and exposes the generated client plus the agent's pushed
/// events. This is the pattern a real side app follows (see docs/fdc3.md).
/// </summary>
public sealed class Fdc3PipeClient : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
    private readonly Task _pump;
    private int _disposed;

    private Fdc3PipeClient(GrpcChannel channel, Fdc3Agent.Fdc3AgentClient rpc, string instanceId, string appId,
        AsyncServerStreamingCall<AgentEvent> stream)
    {
        _channel = channel;
        Rpc = rpc;
        InstanceId = instanceId;
        AppId = appId;
        Headers = new Metadata { { Fdc3Headers.Instance, instanceId } };
        _pump = Pump(stream);
    }

    public Fdc3Agent.Fdc3AgentClient Rpc { get; }
    public string InstanceId { get; }
    public string AppId { get; }

    /// <summary>Pass as <c>headers:</c> to every call other than Subscribe.</summary>
    public Metadata Headers { get; }

    /// <summary>Throws <see cref="RpcException"/> (detail = FDC3 error string) if the agent rejects the app id.</summary>
    public static async Task<Fdc3PipeClient> ConnectAsync(string pipeName, string appId, CancellationToken cancellationToken = default)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.ConnectAsync(token);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
            },
        };
        var channel = GrpcChannel.ForAddress("http://pipe.invalid", new GrpcChannelOptions { HttpHandler = handler });
        try
        {
            var rpc = new Fdc3Agent.Fdc3AgentClient(channel);
            var stream = rpc.Subscribe(new SubscribeRequest { AppId = appId }, cancellationToken: cancellationToken);
            if (!await stream.ResponseStream.MoveNext(cancellationToken) || stream.ResponseStream.Current.Connected is not { } connected)
                throw new InvalidOperationException("The agent closed the stream before connecting.");
            return new Fdc3PipeClient(channel, rpc, connected.InstanceId, connected.AppId, stream);
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    private async Task Pump(AsyncServerStreamingCall<AgentEvent> stream)
    {
        try
        {
            while (await stream.ResponseStream.MoveNext(_lifetime.Token))
                _events.Writer.TryWrite(stream.ResponseStream.Current);
            _events.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _events.Writer.TryComplete(ex);
        }
        finally
        {
            stream.Dispose();
        }
    }

    /// <summary>The next event the agent pushed; throws <see cref="TimeoutException"/> if none arrives.</summary>
    public async Task<AgentEvent> NextEventAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            return await _events.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("No FDC3 event arrived.");
        }
    }

    /// <summary>Ends the Subscribe stream, which disconnects the instance.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _lifetime.CancelAsync();
        try { await _pump; } catch { /* the stream was cancelled */ }
        _channel.Dispose();
        _lifetime.Dispose();
    }
}
