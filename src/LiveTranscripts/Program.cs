namespace LiveTranscripts;

internal static class Program
{
    private static Task<int> Main(string[] arguments)
    {
        if (arguments is ["worker", ..])
        {
            return ProductionSessionWorker.RunAsync(arguments);
        }

        var application = new CliApplication(
            new WindowsAudioDeviceDiscovery(),
            new DetachedLiveSessionController());
        return application.RunAsync(arguments, Console.Out, Console.Error);
    }
}