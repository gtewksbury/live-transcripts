using System.Text.Encodings.Web;
using System.Text.Json;

namespace LiveTranscripts;

internal sealed class CliApplication(IAudioDeviceDiscovery audioDeviceDiscovery)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<int> RunAsync(
        string[] arguments,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        if (arguments.Length != 1 || !string.Equals(arguments[0], "devices", StringComparison.Ordinal))
        {
            var failure = new
            {
                Success = false,
                Error = new
                {
                    Code = "invalid-arguments",
                    Message = "Expected the 'devices' command.",
                },
            };

            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(failure, JsonOptions));
            await standardError.WriteLineAsync("Usage: LiveTranscripts devices");
            return 1;
        }

        AudioDeviceInventory inventory;

        try
        {
            inventory = audioDeviceDiscovery.Discover();
        }
        catch (Exception exception)
        {
            var failure = new
            {
                Success = false,
                Error = new
                {
                    Code = "device-discovery-failed",
                    Message = "Unable to enumerate Windows audio devices.",
                },
            };

            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(failure, JsonOptions));
            await standardError.WriteLineAsync($"Audio device discovery failed: {exception.Message}");
            return 2;
        }

        var result = new
        {
            Success = true,
            inventory.Microphones,
            inventory.Playback,
        };

        await standardOutput.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
    }
}