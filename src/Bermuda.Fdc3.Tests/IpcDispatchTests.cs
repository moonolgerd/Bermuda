using System.Text.Json;
using Bermuda.IpcContract;
using static Bermuda.Fdc3.Tests.DesktopAgentTests;

namespace Bermuda.Fdc3.Tests;

/// <summary>
/// The [IpcCommands] contract as the host sees it: command names derived from
/// the interfaces, and camelCase JSON payloads bound to typed parameters —
/// exactly what HostBridge does with a page's invoke() messages.
/// </summary>
public class IpcDispatchTests
{
    private readonly DesktopAgent _agent = new(AppDirectory.Empty);

    [Theory]
    [InlineData("ping", typeof(IHostCommands))]
    [InlineData("fdc3.getInfo", typeof(IFdc3Commands))]
    [InlineData("fdc3.raiseIntent", typeof(IFdc3Commands))] // "Async" suffix dropped
    [InlineData("fdc3.getIntentResult", typeof(IFdc3Commands))]
    public void Commands_are_named_from_their_interface_methods(string name, Type declaringInterface)
    {
        var command = IpcContract.IpcContract.Find(name);

        Assert.NotNull(command);
        Assert.Equal(declaringInterface, command.Interface);
    }

    [Fact]
    public void Every_fdc3_command_is_discovered()
    {
        var discovered = IpcContract.IpcContract.Commands.Count(c => c.Interface == typeof(IFdc3Commands));
        Assert.Equal(typeof(IFdc3Commands).GetMethods().Length, discovered);
    }

    [Fact]
    public async Task CamelCase_payloads_bind_to_typed_parameters()
    {
        var a = TestApp.Connect(_agent, "a");
        var b = TestApp.Connect(_agent, "b");
        b.Fdc3.JoinUserChannel(new("fdc3.channel.1"));
        b.Fdc3.AddContextListener(new("lb", null, null));

        await Invoke(a.Fdc3, "fdc3.joinUserChannel", """{ "channelId": "fdc3.channel.1" }""");
        await Invoke(a.Fdc3, "fdc3.broadcast", """{ "channelId": null, "context": { "type": "fdc3.nothing" } }""");

        Assert.IsType<Fdc3ContextEvent>(Assert.Single(b.Sink.Messages));
    }

    [Fact]
    public async Task Async_commands_return_their_awaited_result()
    {
        var a = TestApp.Connect(_agent, "a");

        var channel = await Invoke(a.Fdc3, "fdc3.getCurrentChannel", null);
        Assert.Null(channel);

        var info = Assert.IsType<Fdc3ImplementationMetadata>(await Invoke(a.Fdc3, "fdc3.getInfo", null));
        Assert.Equal("2.2", info.Fdc3Version);

        await Assert.ThrowsAsync<Fdc3Exception>(() => Invoke(a.Fdc3, "fdc3.open", """{ "app": { "appId": "nope" } }"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{ "channelId": 42 }""")]
    public async Task Missing_or_mistyped_payloads_are_InvalidArguments(string? payload)
    {
        var a = TestApp.Connect(_agent, "a");

        var error = await Assert.ThrowsAsync<IpcInvalidPayloadException>(() => Invoke(a.Fdc3, "fdc3.joinUserChannel", payload));
        Assert.Equal("InvalidArguments", error.Message);
    }

    [Fact]
    public async Task Command_errors_propagate_unwrapped()
    {
        var a = TestApp.Connect(_agent, "a");

        var error = await Assert.ThrowsAsync<Fdc3Exception>(() =>
            Invoke(a.Fdc3, "fdc3.joinUserChannel", """{ "channelId": "nope" }"""));
        Assert.Equal(Fdc3Errors.NoChannelFound, error.Message);
    }

    private static Task<object?> Invoke(object target, string name, string? payload) =>
        IpcContract.IpcContract.Find(name)!.InvokeAsync(
            target,
            payload is null ? null : JsonDocument.Parse(payload).RootElement.Clone(),
            JsonSerializerOptions.Web);
}
