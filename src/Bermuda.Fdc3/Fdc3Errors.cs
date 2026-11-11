namespace Bermuda.Fdc3;

/// <summary>
/// FDC3 2.2 error strings (the values of the standard's OpenError /
/// ResolveError / ResultError / ChannelError enums). The frontend shim rejects
/// with <c>new Error(message)</c>, which is how the standard expects agents to
/// surface them.
/// </summary>
public static class Fdc3Errors
{
    // OpenError
    public const string AppNotFound = "AppNotFound";
    public const string ErrorOnLaunch = "ErrorOnLaunch";
    public const string AppTimeout = "AppTimeout";

    // ResolveError
    public const string NoAppsFound = "NoAppsFound";
    public const string ResolverUnavailable = "ResolverUnavailable";
    public const string TargetAppUnavailable = "TargetAppUnavailable";
    public const string TargetInstanceUnavailable = "TargetInstanceUnavailable";
    public const string IntentDeliveryFailed = "IntentDeliveryFailed";
    public const string IntentListenerConflict = "IntentListenerConflict";

    // ResultError
    public const string NoResultReturned = "NoResultReturned";
    public const string IntentHandlerRejected = "IntentHandlerRejected";

    // ChannelError
    public const string NoChannelFound = "NoChannelFound";
    public const string AccessDenied = "AccessDenied";
    public const string CreationFailed = "CreationFailed";

    // Shared by several of the above
    public const string MalformedContext = "MalformedContext";
    public const string InvalidArguments = "InvalidArguments";
}

/// <summary>Thrown by <see cref="DesktopAgent"/>; <see cref="Exception.Message"/> is one of <see cref="Fdc3Errors"/>.</summary>
public sealed class Fdc3Exception(string error) : Exception(error);

/// <summary>How the agent pushes messages (context/intent/event deliveries) to one connected app instance.</summary>
public interface IFdc3AppSink
{
    void Post(object message);
}

/// <summary>
/// Starts a new instance of an App Directory app (for <c>open</c> and for
/// raising an intent at an app that isn't running). The new instance must
/// <see cref="DesktopAgent.Connect"/> with the given <c>instanceId</c> so the
/// agent can deliver any pending context/intent to it.
/// </summary>
public interface IFdc3AppLauncher
{
    void Launch(AppDefinition app, string instanceId);
}
