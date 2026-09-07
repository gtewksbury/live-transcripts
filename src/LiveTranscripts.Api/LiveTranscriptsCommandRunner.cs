using System.Diagnostics;

public interface ILiveTranscriptsCommandRunner
{
    Task<LiveTranscriptsCommandResult> RunAsync(IReadOnlyList<string> arguments);
}

public sealed record LiveTranscriptsCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal sealed class ProcessLiveTranscriptsCommandRunner(
    IConfiguration configuration) : ILiveTranscriptsCommandRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public async Task<LiveTranscriptsCommandResult> RunAsync(IReadOnlyList<string> arguments)
    {
        var executablePath = configuration["LiveTranscripts:ExecutablePath"];

        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            throw new InvalidOperationException(
                "LiveTranscripts:ExecutablePath must be configured as an absolute path.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(DefaultTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException("The live-transcripts CLI command timed out.");
        }

        return new LiveTranscriptsCommandResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }
}