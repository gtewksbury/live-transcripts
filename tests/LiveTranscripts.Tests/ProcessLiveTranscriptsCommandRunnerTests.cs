using Microsoft.Extensions.Configuration;

namespace LiveTranscripts.Tests;

[TestClass]
public sealed class ProcessLiveTranscriptsCommandRunnerTests
{
    [TestMethod]
    public async Task ReturnsAfterCommandExitsWhenDescendantKeepsOutputPipeOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The production command runner is supported only on Windows.");
            return;
        }

        var fixture = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "LiveTranscripts.ProcessFixture",
            "bin",
            "Release",
            "net10.0-windows",
            "LiveTranscripts.ProcessFixture.exe");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LiveTranscripts:ExecutablePath"] = fixture,
            })
            .Build();
        var runner = new ProcessLiveTranscriptsCommandRunner(configuration);

        var command = runner.RunAsync(["parent"]);
        var completed = await Task.WhenAny(command, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.AreSame(command, completed, "The runner waited for the descendant to close stdout.");
        var result = await command;
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.AreEqual("{\"success\":true}", result.StandardOutput);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LiveTranscripts.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}