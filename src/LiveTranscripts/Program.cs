namespace LiveTranscripts;

internal static class Program
{
    private static Task<int> Main(string[] arguments)
    {
        var application = new CliApplication(new WindowsAudioDeviceDiscovery());
        return application.RunAsync(arguments, Console.Out, Console.Error);
    }
}