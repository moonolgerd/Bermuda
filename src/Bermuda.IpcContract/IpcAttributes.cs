namespace Bermuda.IpcContract;

/// <summary>
/// Marks a type as declaring one command on the <c>invoke()</c> postMessage
/// channel. <see cref="IpcContract"/> discovers these at runtime to build the
/// host's command allow-list; <c>Bermuda.IpcContract.SourceGenerator</c>
/// discovers them at compile time to emit the matching TypeScript.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class IpCommandAttribute : Attribute
{
    public string Name { get; }
    public string InputType { get; }
    public string OutputType { get; }
    public string Description { get; }

    public IpCommandAttribute(string name, string inputType, string outputType, string description = "")
    {
        Name = name;
        InputType = inputType;
        OutputType = outputType;
        Description = description;
    }
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
/// though it isn't the input/output of an <see cref="IpCommandAttribute"/>
/// command — e.g. a shape a hand-written client-side helper constructs by
/// combining several <see cref="IpcHostObjectAttribute"/> members (WebView2
/// host objects can't return nested structured data directly, only
/// primitives/arrays/other host objects, so that kind of combining has to
/// happen in TS — this just keeps its return shape drift-checked against C#).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum)]
public sealed class IpcTypeAttribute : Attribute;
