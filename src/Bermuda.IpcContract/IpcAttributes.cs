namespace Bermuda.IpcContract;

/// <summary>
/// Marks an interface whose methods are commands on the <c>invoke()</c>
/// postMessage channel. The method signature is the contract: its single
/// parameter (if any) is the payload, and its return type is the result.
/// <c>Task</c>/<c>Task&lt;T&gt;</c> are unwrapped. Each command's name is
/// <c>&lt;prefix&gt;.&lt;camelCaseMethodName&gt;</c>, minus any <c>Async</c>
/// suffix (just the method name when the prefix is empty).
/// <see cref="IpcContract"/> discovers these at runtime for the host's
/// allow-list and dispatch. <c>Bermuda.IpcContract.SourceGenerator</c> discovers
/// them at compile time to emit the matching TypeScript, nullability included.
/// </summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class IpcCommandsAttribute(string prefix = "") : Attribute
{
    public string Prefix { get; } = prefix;
}

/// <summary>
/// Marks a class that gets projected into script via
/// <c>CoreWebView2.AddHostObjectToScript</c>. <c>Bermuda.IpcContract.SourceGenerator</c>
/// reflects the type's public members and emits a matching TypeScript
/// interface, since WebView2 maps them 1:1 onto identically-named promises at
/// <c>chrome.webview.hostObjects.&lt;name&gt;</c> — no command dispatch involved.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class IpcHostObjectAttribute : Attribute
{
    public string Name { get; }

    public IpcHostObjectAttribute(string name)
    {
        Name = name;
    }
}

/// <summary>
/// Marks a record whose shape should be emitted as a TypeScript interface even
/// though it isn't the input/output of an <see cref="IpcCommandsAttribute"/>
/// command — e.g. a shape a hand-written client-side helper constructs by
/// combining several <see cref="IpcHostObjectAttribute"/> members (WebView2
/// host objects can't return nested structured data directly, only
/// primitives/arrays/other host objects, so that kind of combining has to
/// happen in TS — this just keeps its return shape drift-checked against C#).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum)]
public sealed class IpcTypeAttribute : Attribute;
