using System.Text.Encodings.Web;
using System.Text.Json;

namespace LiveTranscripts;

internal sealed class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IAudioDeviceDiscovery audioDeviceDiscovery;
    private readonly ILiveSessionController liveSessionController;

    public CliApplication(IAudioDeviceDiscovery audioDeviceDiscovery)
        : this(audioDeviceDiscovery, new UnavailableLiveSessionController())
    {
    }

    public CliApplication(
        IAudioDeviceDiscovery audioDeviceDiscovery,
        ILiveSessionController liveSessionController)
    {
        this.audioDeviceDiscovery = audioDeviceDiscovery;
        this.liveSessionController = liveSessionController;
    }

    public async Task<int> RunAsync(
        string[] arguments,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        try
        {
            return await RunCoreAsync(arguments, standardOutput, standardError);
        }
        catch (Exception exception)
        {
            return await WriteFailureAsync(
                "command-failed",
                "The command could not be completed.",
                $"Command failed: {exception.Message}",
                5,
                standardOutput,
                standardError);
        }
    }

    private async Task<int> RunCoreAsync(
        string[] arguments,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        if (arguments is ["devices"])
        {
            return await RunDevicesAsync(standardOutput, standardError);
        }

        if (TryParseStartArguments(arguments, out var startArguments))
        {
            return await RunStartAsync(startArguments, standardOutput, standardError);
        }

        if (arguments is ["status"] or ["status", _])
        {
            return await RunStatusAsync(
                arguments.Length == 2 ? arguments[1] : null,
                standardOutput,
                standardError);
        }

        if (arguments is ["stop"] or ["stop", _])
        {
            return await RunStopAsync(
                arguments.Length == 2 ? arguments[1] : null,
                standardOutput,
                standardError);
        }

        var failure = new
        {
            Success = false,
            Error = new
            {
                Code = "invalid-arguments",
                Message = "Expected a devices, start, status, or stop command.",
            },
        };

        await standardOutput.WriteLineAsync(JsonSerializer.Serialize(failure, JsonOptions));
        await standardError.WriteLineAsync("Usage: LiveTranscripts <devices|start|status|stop>");
        return 1;
    }

    private async Task<int> RunStartAsync(
        StartArguments arguments,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        AudioDeviceInventory inventory;

        try
        {
            inventory = audioDeviceDiscovery.Discover();
        }
        catch (Exception exception)
        {
            return await WriteFailureAsync(
                "device-discovery-failed",
                "Unable to enumerate Windows audio devices.",
                $"Audio device discovery failed: {exception.Message}",
                2,
                standardOutput,
                standardError);
        }

        var microphone = ResolveDevice(inventory.Microphones, arguments.MicrophoneId);
        var playback = ResolveDevice(inventory.Playback, arguments.PlaybackId);

        if (microphone is null || playback is null)
        {
            return await WriteFailureAsync(
                "audio-device-not-found",
                "The selected microphone and playback devices must be available.",
                "Unable to resolve the selected Windows audio devices.",
                2,
                standardOutput,
                standardError);
        }

        var request = new StartSessionRequest(
            Path.GetFullPath(arguments.OutputPath),
            microphone.Id,
            playback.Id,
            arguments.Append);
        LiveSessionStatus status;

        try
        {
            status = await liveSessionController.StartAsync(request, CancellationToken.None);
        }
        catch (LiveSessionException exception)
        {
            return await WriteFailureAsync(
                exception.Code,
                exception.Message,
                $"Unable to start live transcription: {exception.Message}",
                4,
                standardOutput,
                standardError);
        }

        await WriteStatusAsync(status, standardOutput);
        return 0;
    }

    private async Task<int> RunStatusAsync(
        string? sessionId,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        try
        {
            var status = await liveSessionController.GetStatusAsync(sessionId, CancellationToken.None);
            return await WriteSessionResultAsync(status, "query", standardOutput, standardError);
        }
        catch (LiveSessionException exception)
        {
            return await WriteFailureAsync(
                exception.Code,
                exception.Message,
                $"Unable to query live transcription: {exception.Message}",
                4,
                standardOutput,
                standardError);
        }
    }

    private async Task<int> RunStopAsync(
        string? sessionId,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        try
        {
            var status = await liveSessionController.StopAsync(sessionId, CancellationToken.None);
            return await WriteSessionResultAsync(status, "stop", standardOutput, standardError);
        }
        catch (LiveSessionException exception)
        {
            return await WriteFailureAsync(
                exception.Code,
                exception.Message,
                $"Unable to stop live transcription: {exception.Message}",
                4,
                standardOutput,
                standardError);
        }
    }

    private static async Task<int> WriteSessionResultAsync(
        LiveSessionStatus? status,
        string operation,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        if (status is null)
        {
            return await WriteFailureAsync(
                "session-not-found",
                "No matching live transcription session was found.",
                $"Unable to {operation} the requested live transcription session.",
                3,
                standardOutput,
                standardError);
        }

        await WriteStatusAsync(status, standardOutput);
        return 0;
    }

    private static Task WriteStatusAsync(
        LiveSessionStatus status,
        TextWriter standardOutput)
    {
        object result = status.ErrorCode is null
            ? new
            {
                Success = true,
                status.SessionId,
                status.State,
                status.OutputPath,
                status.MicrophoneId,
                status.PlaybackId,
                Sources = new
                {
                    You = new
                    {
                        State = status.YouRecognitionState,
                        SpeechLost = status.YouSpeechLost,
                    },
                    Meeting = new
                    {
                        State = status.MeetingRecognitionState,
                        SpeechLost = status.MeetingSpeechLost,
                    },
                },
            }
            : new
            {
                Success = true,
                status.SessionId,
                status.State,
                status.OutputPath,
                status.MicrophoneId,
                status.PlaybackId,
                Sources = new
                {
                    You = new
                    {
                        State = status.YouRecognitionState,
                        SpeechLost = status.YouSpeechLost,
                    },
                    Meeting = new
                    {
                        State = status.MeetingRecognitionState,
                        SpeechLost = status.MeetingSpeechLost,
                    },
                },
                Error = new
                {
                    Code = status.ErrorCode,
                    Message = status.ErrorMessage,
                },
            };

        return standardOutput.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
    }

    private static AudioDevice? ResolveDevice(
        IReadOnlyList<AudioDevice> devices,
        string? requestedId) => requestedId is null
            ? devices.SingleOrDefault(device => device.IsDefaultCommunications)
            : devices.SingleOrDefault(device => string.Equals(
                device.Id,
                requestedId,
                StringComparison.Ordinal));

    private static bool TryParseStartArguments(
        string[] arguments,
        out StartArguments parsed)
    {
        parsed = default;

        if (arguments.Length < 2 || !string.Equals(arguments[0], "start", StringComparison.Ordinal))
        {
            return false;
        }

        string? microphoneId = null;
        string? playbackId = null;
        var append = false;

        for (var index = 2; index < arguments.Length; index++)
        {
            if (string.Equals(arguments[index], "--append", StringComparison.Ordinal) && !append)
            {
                append = true;
                continue;
            }

            if (index + 1 >= arguments.Length)
            {
                return false;
            }

            switch (arguments[index])
            {
                case "--microphone" when microphoneId is null:
                    microphoneId = arguments[index + 1];
                    break;
                case "--playback" when playbackId is null:
                    playbackId = arguments[index + 1];
                    break;
                default:
                    return false;
            }

            index++;
        }

        parsed = new StartArguments(arguments[1], microphoneId, playbackId, append);
        return true;
    }

    private async Task<int> RunDevicesAsync(
        TextWriter standardOutput,
        TextWriter standardError)
    {

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

    private static async Task<int> WriteFailureAsync(
        string code,
        string message,
        string diagnostic,
        int exitCode,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        var failure = new
        {
            Success = false,
            Error = new
            {
                Code = code,
                Message = message,
            },
        };

        await standardOutput.WriteLineAsync(JsonSerializer.Serialize(failure, JsonOptions));
        await standardError.WriteLineAsync(diagnostic);
        return exitCode;
    }

    private readonly record struct StartArguments(
        string OutputPath,
        string? MicrophoneId,
        string? PlaybackId,
        bool Append);
}