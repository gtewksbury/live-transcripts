using System.Text;

namespace LiveTranscripts;

internal enum TranscriptSource
{
    You,
    Meeting,
}

internal interface IAudioCapture : IAsyncDisposable
{
    event Action<ReadOnlyMemory<byte>>? AudioAvailable;

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

internal interface IAudioCaptureFactory
{
    IAudioCapture CreateMicrophone(string endpointId);

    IAudioCapture CreatePlayback(string endpointId);
}

internal interface ISpeechRecognizer : IAsyncDisposable
{
    event Action<string>? Finalized;

    Task StartAsync(CancellationToken cancellationToken);

    void WriteAudio(ReadOnlyMemory<byte> audio);

    Task StopAsync(CancellationToken cancellationToken);
}

internal interface ISpeechRecognizerFactory
{
    ISpeechRecognizer Create(TranscriptSource source);
}

internal sealed class InProcessLiveSessionController(
    IAudioCaptureFactory audioCaptureFactory,
    ISpeechRecognizerFactory speechRecognizerFactory,
    Func<string>? sessionIdFactory = null) : ILiveSessionController
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<string> createSessionId = sessionIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    private LiveTranscriptionSession? session;
    private LiveSessionStatus? status;

    public async Task<LiveSessionStatus> StartAsync(
        StartSessionRequest request,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (session is not null)
            {
                throw new LiveSessionException(
                    "session-already-active",
                    "A live transcription session is already active.");
            }

            var sessionId = createSessionId();
            var candidate = new LiveTranscriptionSession(
                request,
                audioCaptureFactory,
                speechRecognizerFactory);

            try
            {
                await candidate.StartAsync(cancellationToken);
            }
            catch
            {
                await candidate.DisposeAsync();
                throw;
            }

            session = candidate;
            status = new LiveSessionStatus(
                sessionId,
                "running",
                request.OutputPath,
                request.MicrophoneId,
                request.PlaybackId);
            return status;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LiveSessionStatus?> GetStatusAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            return Matches(sessionId) ? status : null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LiveSessionStatus?> StopAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (session is null || !Matches(sessionId))
            {
                return null;
            }

            await session.StopAsync(cancellationToken);
            await session.DisposeAsync();
            session = null;
            status = status! with { State = "stopped" };
            return status;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool Matches(string? sessionId) => status is not null &&
        (sessionId is null || string.Equals(sessionId, status.SessionId, StringComparison.Ordinal));
}

internal sealed class LiveTranscriptionSession : IAsyncDisposable
{
    private readonly StartSessionRequest request;
    private readonly IAudioCapture microphoneCapture;
    private readonly IAudioCapture playbackCapture;
    private readonly ISpeechRecognizer youRecognizer;
    private readonly ISpeechRecognizer meetingRecognizer;
    private readonly object writerGate = new();

    public LiveTranscriptionSession(
        StartSessionRequest request,
        IAudioCaptureFactory audioCaptureFactory,
        ISpeechRecognizerFactory speechRecognizerFactory)
    {
        this.request = request;
        microphoneCapture = audioCaptureFactory.CreateMicrophone(request.MicrophoneId);
        playbackCapture = audioCaptureFactory.CreatePlayback(request.PlaybackId);
        youRecognizer = speechRecognizerFactory.Create(TranscriptSource.You);
        meetingRecognizer = speechRecognizerFactory.Create(TranscriptSource.Meeting);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            request.OutputPath,
            $"# Live Transcript{Environment.NewLine}{Environment.NewLine}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        microphoneCapture.AudioAvailable += youRecognizer.WriteAudio;
        playbackCapture.AudioAvailable += meetingRecognizer.WriteAudio;
        youRecognizer.Finalized += WriteYou;
        meetingRecognizer.Finalized += WriteMeeting;

        await Task.WhenAll(
            youRecognizer.StartAsync(cancellationToken),
            meetingRecognizer.StartAsync(cancellationToken));
        await Task.WhenAll(
            microphoneCapture.StartAsync(cancellationToken),
            playbackCapture.StartAsync(cancellationToken));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            microphoneCapture.StopAsync(cancellationToken),
            playbackCapture.StopAsync(cancellationToken));
        await Task.WhenAll(
            youRecognizer.StopAsync(cancellationToken),
            meetingRecognizer.StopAsync(cancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        microphoneCapture.AudioAvailable -= youRecognizer.WriteAudio;
        playbackCapture.AudioAvailable -= meetingRecognizer.WriteAudio;
        youRecognizer.Finalized -= WriteYou;
        meetingRecognizer.Finalized -= WriteMeeting;
        await microphoneCapture.DisposeAsync();
        await playbackCapture.DisposeAsync();
        await youRecognizer.DisposeAsync();
        await meetingRecognizer.DisposeAsync();
    }

    private void WriteYou(string text) => WriteFinalized("You", text);

    private void WriteMeeting(string text) => WriteFinalized("Meeting", text);

    private void WriteFinalized(string label, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (writerGate)
        {
            File.AppendAllText(
                request.OutputPath,
                $"**{label}:** {text.Trim()}{Environment.NewLine}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}