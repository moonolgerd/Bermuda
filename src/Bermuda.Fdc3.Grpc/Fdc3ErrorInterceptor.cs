using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Bermuda.Fdc3.Grpc;

/// <summary>
/// Turns <see cref="Fdc3Exception"/> into an <see cref="RpcException"/> whose
/// status detail is the FDC3 error string (<see cref="Fdc3Errors"/>), so
/// native clients branch on <c>ex.Status.Detail</c> exactly as web apps branch
/// on <c>error.message</c>.
/// </summary>
internal sealed class Fdc3ErrorInterceptor : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (Fdc3Exception ex)
        {
            throw ToRpc(ex);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(request, responseStream, context);
        }
        catch (Fdc3Exception ex)
        {
            throw ToRpc(ex);
        }
    }

    internal static RpcException ToRpc(Fdc3Exception ex) => new(new Status(CodeFor(ex.Message), ex.Message));

    private static StatusCode CodeFor(string error) => error switch
    {
        Fdc3Errors.InvalidArguments or Fdc3Errors.MalformedContext => StatusCode.InvalidArgument,
        Fdc3Errors.AccessDenied => StatusCode.PermissionDenied,
        Fdc3Errors.AppNotFound or Fdc3Errors.NoAppsFound or Fdc3Errors.NoChannelFound
            or Fdc3Errors.TargetAppUnavailable or Fdc3Errors.TargetInstanceUnavailable => StatusCode.NotFound,
        Fdc3Errors.AppTimeout => StatusCode.DeadlineExceeded,
        _ => StatusCode.FailedPrecondition,
    };
}
