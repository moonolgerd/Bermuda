using Bermuda.Fdc3;

namespace Bermuda.Tests;

/// <summary>
/// Jump-list tasks and the activation strings a click comes back as. An MSIX cannot be installed
/// in this run, so the packaged and unpackaged shapes are checked here against the strings Windows
/// actually delivers (Windows App SDK issue 3863).
/// </summary>
public class JumpListTests
{
    private const string WindowsAppsExe =
        @"C:\Program Files\WindowsApps\com.bermuda.app_1.0.0.0_x64__hash\Bermuda.exe";

    public static TheoryData<string, string[]> ActivationShapes => new()
    {
        // Packaged: ILaunchActivatedEventArgs.Arguments is only the jump-list string.
        { "--open fdc3-probe", ["--open", "fdc3-probe"] },
        { "--new-window", ["--new-window"] },
        { "", [] },
        { "   ", [] },
        // Unpackaged: Arguments is GetCommandLine(), exe first. Quoted when the path has spaces.
        { @"E:\app\Bermuda.exe --open fdc3-probe", ["--open", "fdc3-probe"] },
        { @"E:\app\Bermuda.exe --new-window", ["--new-window"] },
        { $@"""{WindowsAppsExe}"" --open fdc3-probe", ["--open", "fdc3-probe"] },
        { $@"""{WindowsAppsExe}"" --new-window", ["--new-window"] },
        // A second launch with no task (the user opened Bermuda again) is just the exe.
        { $@"""{WindowsAppsExe}""", [] },
        { @"E:\app\Bermuda.exe", [] },
    };

    [Theory]
    [MemberData(nameof(ActivationShapes))]
    public void Parse_accepts_packaged_arguments_and_unpackaged_command_lines(string activation, string[] expected) =>
        Assert.Equal(expected, LaunchArguments.Parse(activation));

    [Fact]
    public void Open_command_round_trips_for_an_app_id_with_spaces()
    {
        var command = LaunchArguments.OpenCommand("fdc3 probe");
        Assert.Equal(["--open", "fdc3 probe"], LaunchArguments.Parse(command));
        Assert.Equal(["--open", "fdc3 probe"], LaunchArguments.Parse($@"""{WindowsAppsExe}"" {command}"));
    }

    [Fact]
    public void Tasks_are_new_window_plus_each_web_app()
    {
        var directory = AppDirectory.Load(Path.Combine(AppContext.BaseDirectory, "fdc3-appd.json"));
        var web = directory.Apps.Where(a => string.Equals(a.Type, "web", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(directory.Apps, a => a.AppId == "native-incidents");
        Assert.Contains(web, a => a.AppId == "fdc3-probe");

        var tasks = JumpListCatalog.Create(directory, WindowsAppsExe);

        Assert.Equal(web.Count + 1, tasks.Count);
        Assert.Equal("New window", tasks[0].Title);
        Assert.Equal("--new-window", tasks[0].Arguments);
        Assert.All(tasks, entry => Assert.Equal(WindowsAppsExe, entry.ExecutablePath));
        Assert.DoesNotContain(tasks, entry => entry.Arguments.Contains("native-incidents", StringComparison.Ordinal));
        Assert.DoesNotContain(tasks, entry => entry.Arguments.Contains(".exe", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("FDC3 Probe", tasks.Single(e => e.Arguments == "--open fdc3-probe").Title);

        for (var i = 0; i < web.Count; i++)
        {
            Assert.Equal(LaunchArguments.OpenCommand(web[i].AppId), tasks[i + 1].Arguments);
            var commandLine = $@"""{tasks[i + 1].ExecutablePath}"" {tasks[i + 1].Arguments}";
            Assert.Equal(LaunchArguments.Parse(tasks[i + 1].Arguments), LaunchArguments.Parse(commandLine));
        }
    }

    [Fact]
    public void Without_an_exe_path_registers_nothing()
    {
        var directory = AppDirectory.Parse("""
            { "applications": [ { "appId": "fdc3-probe", "type": "web", "title": "FDC3 Probe", "details": { "url": "/fdc3-probe.html" } } ] }
            """);

        Assert.Empty(JumpListCatalog.Create(directory, null));
        Assert.Empty(JumpListCatalog.Create(directory, ""));
    }

    [Fact]
    public void HasTask_ignores_a_bare_executable()
    {
        Assert.True(LaunchArguments.HasTask(LaunchArguments.Parse("--new-window")));
        Assert.True(LaunchArguments.HasTask(LaunchArguments.Parse("--open fdc3-probe")));
        Assert.False(LaunchArguments.HasTask(LaunchArguments.Parse($@"""{WindowsAppsExe}""")));
        Assert.False(LaunchArguments.HasTask([]));
    }
}
