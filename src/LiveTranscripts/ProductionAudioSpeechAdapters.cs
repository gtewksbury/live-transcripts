using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Text.Json;

namespace LiveTranscripts;

internal sealed class WindowsAudioCaptureFactory : IAudioCaptureFactory
{
    public IAudioCapture CreateMicrophone(string endpointId) =>
        new WasapiPcmCapture(endpointId, loopback: false);

    public IAudioCapture CreatePlayback(string endpointId) =>
        new WasapiPcmCapture(endpointId, loopback: true);
}

internal sealed class WasapiPcmCapture : IAudioCapture
{
    private static readonly WaveFormat SpeechWaveFormat = new(16000, 16, 1);

    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly BufferedWaveProvider bufferedAudio;
    private readonly MediaFoundationResampler resampler;
    private readonly CancellationTokenSource pumpCancellation = new();
    private Task? pumpTask;

    public WasapiPcmCapture(string endpointId, bool loopback)
    {
        using var enumerator = new MMDeviceEnumerator();
        device = enumerator.GetDevice(endpointId);
        capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        bufferedAudio = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = false,
            ReadFully = false,
        };
        resampler = new MediaFoundationResampler(bufferedAudio, SpeechWaveFormat)
        {
            ResamplerQuality = 60,
        };
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
    }

    public event Action<ReadOnlyMemory<byte>>? AudioAvailable;

    public event Action? TerminalFailure;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        capture.StartRecording();
        pumpTask = Task.Run(() => PumpAsync(pumpCancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        capture.StopRecording();
        pumpCancellation.Cancel();

        if (pumpTask is not null)
        {
            await pumpTask.WaitAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        pumpCancellation.Cancel();

        if (pumpTask is not null)
        {
            try
            {
                await pumpTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        resampler.Dispose();
        capture.Dispose();
        device.Dispose();
        pumpCancellation.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs) =>
        bufferedAudio.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded);

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is not null && !pumpCancellation.IsCancellationRequested)
        {
            TerminalFailure?.Invoke();
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[3200];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = resampler.Read(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                {
                    await Task.Delay(10, cancellationToken);
                    continue;
                }

                AudioAvailable?.Invoke(buffer.AsMemory(0, bytesRead).ToArray());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            TerminalFailure?.Invoke();
        }
    }
}

internal sealed class AzureSpeechRecognizerFactory : ISpeechRecognizerFactory
{
    private readonly string key;
    private readonly string region;

    public AzureSpeechRecognizerFactory()
    {
        var settings = AzureSpeechSettings.Load(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        key = settings.Key;
        region = settings.Region;
    }

    public ISpeechRecognizer Create(TranscriptSource source) =>
        new AzureSpeechRecognizer(key, region);
}

internal sealed record AzureSpeechSettings(string Key, string Region)
{
    public static AzureSpeechSettings Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var azureSpeech = document.RootElement.GetProperty("AzureSpeech");
            var key = azureSpeech.GetProperty("Key").GetString();
            var region = azureSpeech.GetProperty("Region").GetString();

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new LiveSessionException(
                    "speech-configuration-missing",
                    "AzureSpeech:Key is required in appsettings.json.");
            }

            if (string.IsNullOrWhiteSpace(region))
            {
                throw new LiveSessionException(
                    "speech-configuration-missing",
                    "AzureSpeech:Region is required in appsettings.json.");
            }

            if (Uri.TryCreate(region, UriKind.Absolute, out _))
            {
                throw new LiveSessionException(
                    "speech-configuration-invalid",
                    "AzureSpeech:Region must be an Azure region name such as 'eastus', not an endpoint URL.");
            }

            return new AzureSpeechSettings(key, region);
        }
        catch (LiveSessionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new LiveSessionException(
                "speech-configuration-invalid",
                "Unable to read Azure Speech settings from appsettings.json.");
        }
    }
}

internal sealed class AzureSpeechRecognizer : ISpeechRecognizer
{
    private readonly PushAudioInputStream audioStream;
    private readonly AudioConfig audioConfig;
    private readonly SpeechRecognizer recognizer;

    public AzureSpeechRecognizer(string key, string region)
    {
        var speechConfig = SpeechConfig.FromSubscription(key, region);
        speechConfig.SpeechRecognitionLanguage = "en-US";
        speechConfig.SetProfanity(ProfanityOption.Raw);

        var format = AudioStreamFormat.GetWaveFormatPCM(
            samplesPerSecond: 16000,
            bitsPerSample: 16,
            channels: 1);
        audioStream = AudioInputStream.CreatePushStream(format);
        audioConfig = AudioConfig.FromStreamInput(audioStream);
        recognizer = new SpeechRecognizer(speechConfig, audioConfig);
        recognizer.Recognized += OnRecognized;
        recognizer.Canceled += OnCanceled;
    }

    public event Action<FinalizedRecognition>? Finalized;

    public event Action? RecoverableInterruption;

    public event Action<string>? TerminalFailure;

    public Task StartAsync(CancellationToken cancellationToken) =>
        recognizer.StartContinuousRecognitionAsync().WaitAsync(cancellationToken);

    public void WriteAudio(ReadOnlyMemory<byte> audio)
    {
        var buffer = audio.ToArray();
        audioStream.Write(buffer, buffer.Length);
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await recognizer.StopContinuousRecognitionAsync().WaitAsync(cancellationToken);
        }
        catch
        {
        }

        await recognizer.StartContinuousRecognitionAsync().WaitAsync(cancellationToken);
    }

    public async Task CompleteReplayAsync(CancellationToken cancellationToken)
    {
        await recognizer.StopContinuousRecognitionAsync().WaitAsync(cancellationToken);
        await recognizer.StartContinuousRecognitionAsync().WaitAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        recognizer.StopContinuousRecognitionAsync().WaitAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        recognizer.Recognized -= OnRecognized;
        recognizer.Canceled -= OnCanceled;
        recognizer.Dispose();
        audioConfig.Dispose();
        audioStream.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnCanceled(object? sender, SpeechRecognitionCanceledEventArgs eventArgs)
    {
        if (eventArgs.Reason == CancellationReason.Error)
        {
            if (eventArgs.ErrorCode is CancellationErrorCode.ConnectionFailure or
                CancellationErrorCode.ServiceTimeout or
                CancellationErrorCode.ServiceUnavailable)
            {
                RecoverableInterruption?.Invoke();
            }
            else
            {
                TerminalFailure?.Invoke(eventArgs.ErrorCode.ToString());
            }
        }
    }

    private void OnRecognized(object? sender, SpeechRecognitionEventArgs eventArgs)
    {
        if (eventArgs.Result.Reason == ResultReason.RecognizedSpeech &&
            !string.IsNullOrWhiteSpace(eventArgs.Result.Text))
        {
            Finalized?.Invoke(new FinalizedRecognition(
                eventArgs.Result.Text,
                TimeSpan.FromTicks(checked((long)eventArgs.Result.OffsetInTicks))));
        }
    }
}