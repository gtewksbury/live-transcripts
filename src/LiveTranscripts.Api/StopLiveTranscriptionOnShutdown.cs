internal sealed class StopLiveTranscriptionOnShutdown(
    ILiveTranscriptsCommandRunner runner) : IHostedService
{
    private int hasStopped;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref hasStopped, 1) != 0)
        {
            return;
        }

        try
        {
            await runner.RunAsync(["stop"]);
        }
        catch (Exception)
        {
            // Shutdown cleanup is best-effort and cannot report a response to a caller.
        }
    }
}