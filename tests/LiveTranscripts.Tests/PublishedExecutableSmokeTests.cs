using System.Diagnostics;
using System.Text.Json;

namespace LiveTranscripts.Tests;

[TestClass]
public sealed class PublishedExecutableSmokeTests
{
    [TestMethod]
    public async Task PublishedExecutableKeepsJsonAndDiagnosticsOnSeparateStreams()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The published executable is supported only on Windows.");
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var publishScript = Path.Combine(repositoryRoot, "scripts", "publish-windows.ps1");
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var publish = await RunProcessAsync(
            powershell,
            repositoryRoot,
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            publishScript);

        Assert.AreEqual(0, publish.ExitCode, publish.StandardError);

        var executable = Path.Combine(
            repositoryRoot,
            "artifacts",
            "windows-x64",
            "live-transcripts.exe");
        Assert.IsTrue(File.Exists(executable), $"Published executable not found at {executable}.");

        var devices = await RunProcessAsync(executable, repositoryRoot, "devices");

        Assert.AreEqual(0, devices.ExitCode, devices.StandardError);
        using (var devicesResult = JsonDocument.Parse(devices.StandardOutput))
        {
            Assert.IsTrue(devicesResult.RootElement.GetProperty("success").GetBoolean());
        }

        Assert.AreEqual(string.Empty, devices.StandardError);

        var command = await RunProcessAsync(executable, repositoryRoot, "unknown");

        Assert.AreEqual(1, command.ExitCode);
        using var result = JsonDocument.Parse(command.StandardOutput);
        Assert.IsFalse(result.RootElement.GetProperty("success").GetBoolean());
        Assert.AreEqual(
            "invalid-arguments",
            result.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.AreEqual(
            $"Usage: live-transcripts <devices|start|status|stop>{Environment.NewLine}",
            command.StandardError);
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

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Could not start {fileName}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}