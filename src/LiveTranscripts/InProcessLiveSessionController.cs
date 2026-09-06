using System.Text;

namespace LiveTranscripts;

internal enum TranscriptSource
{
    You,
    Meeting,
}

internal sealed record FinalizedRecognition(string Text, TimeSpan AudioOffset);

internal interface IAudioCapture : IAsyncDisposable
{
    event Action<ReadOnlyMemory<byte>>? AudioAvailable;

    event Action? TerminalFailure;

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
    event Action<FinalizedRecognition>? Finalized;

    event Action? RecoverableInterruption;

    event Action<string>? TerminalFailure;

    Task StartAsync(CancellationToken cancellationToken);

    void WriteAudio(ReadOnlyMemory<byte> audio);

    Task RecoverAsync(CancellationToken cancellationToken);

    Task CompleteReplayAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

internal interface ISpeechRecognizerFactory
{
    ISpeechRecognizer Create(TranscriptSource source);
}

internal sealed class InProcessLiveSessionController(
    IAudioCaptureFactory audioCaptureFactory,
    ISpeechRecognizerFactory speechRecognizerFactory,
    Func<string>? sessionIdFactory = null,
    Func<CancellationToken, Task>? transcriptHoldback = null,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? durationDelay = null) : ILiveSessionController
{
    private static readonly TimeSpan SessionDurationLimit = TimeSpan.FromHours(2);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<string> createSessionId = sessionIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> waitForDurationLimit = durationDelay ??
        ((duration, cancellationToken) => Task.Delay(
            duration,
            timeProvider ?? TimeProvider.System,
            cancellationToken));
    private LiveTranscriptionSession? session;
    private LiveSessionStatus? status;
    private TaskCompletionSource<LiveSessionStatus>? terminalStatus;
    private CancellationTokenSource? durationCancellation;

    internal event Action<LiveSessionStatus>? StatusChanged;

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
                speechRecognizerFactory,
                transcriptHoldback);
            candidate.WriteFailed += HandleWriteFailure;
            candidate.TerminalFailure += HandleTerminalFailure;
            candidate.RecognitionStateChanged += HandleRecognitionStateChanged;
            terminalStatus = new TaskCompletionSource<LiveSessionStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                await candidate.StartAsync(cancellationToken);
            }
            catch
            {
                candidate.WriteFailed -= HandleWriteFailure;
                candidate.TerminalFailure -= HandleTerminalFailure;
                candidate.RecognitionStateChanged -= HandleRecognitionStateChanged;
                await candidate.DisposeAsync();
                throw;
            }

            session = candidate;
            status = new LiveSessionStatus(
                sessionId,
                "running",
                request.OutputPath,
                request.MicrophoneId,
                request.PlaybackId,
                StartedAtUtc: clock.GetUtcNow());
            StatusChanged?.Invoke(status);
            durationCancellation = new CancellationTokenSource();
            _ = StopAtDurationLimitAsync(sessionId, durationCancellation.Token);
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

            durationCancellation?.Cancel();
            session.WriteFailed -= HandleWriteFailure;
            session.TerminalFailure -= HandleTerminalFailure;
            session.RecognitionStateChanged -= HandleRecognitionStateChanged;
            await session.StopAsync(cancellationToken);
            await session.DisposeAsync();
            session = null;
            status = WithElapsed(status! with
            {
                State = "stopped",
                StopReason = LiveSessionStopReasons.Requested,
            });
            return status;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool Matches(string? sessionId) => status is not null &&
        (sessionId is null || string.Equals(sessionId, status.SessionId, StringComparison.Ordinal));

    private async Task StopAtDurationLimitAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await waitForDurationLimit(SessionDurationLimit, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await gate.WaitAsync(CancellationToken.None);

        try
        {
            if (session is null || status?.SessionId != sessionId)
            {
                return;
            }

            session.WriteFailed -= HandleWriteFailure;
            session.TerminalFailure -= HandleTerminalFailure;
            session.RecognitionStateChanged -= HandleRecognitionStateChanged;
            await session.StopAsync(CancellationToken.None);
            var markerWritten = session.AppendDurationLimitMarker();
            await session.DisposeAsync();
            session = null;
            status = markerWritten
                ? WithElapsed(status with
                {
                    State = "stopped",
                    StopReason = LiveSessionStopReasons.DurationLimit,
                })
                : status with
                {
                    State = "failed",
                    StopReason = LiveSessionStopReasons.WriteFailure,
                    ErrorCode = "transcript-write-failed",
                    ErrorMessage = "The duration-limit marker could not be written.",
                    ElapsedDuration = GetElapsedDuration(status),
                };
            terminalStatus!.TrySetResult(status);
        }
        finally
        {
            gate.Release();
        }
    }

    internal Task<LiveSessionStatus> WaitForTerminalStatusAsync(
        CancellationToken cancellationToken) => terminalStatus is null
            ? Task.FromException<LiveSessionStatus>(new InvalidOperationException("The session has not started."))
            : terminalStatus.Task.WaitAsync(cancellationToken);

    private void HandleWriteFailure(LiveSessionException exception) =>
        _ = TransitionToTerminalFailureAsync(exception, LiveSessionStopReasons.WriteFailure);

    private void HandleTerminalFailure(LiveSessionException exception, string stopReason) =>
        _ = TransitionToTerminalFailureAsync(exception, stopReason);

    private void HandleRecognitionStateChanged(
        TranscriptSource source,
        bool degraded,
        bool speechLost) => _ = TransitionRecognitionStateAsync(source, degraded, speechLost);

    private async Task TransitionRecognitionStateAsync(
        TranscriptSource source,
        bool degraded,
        bool speechLost)
    {
        await gate.WaitAsync(CancellationToken.None);

        try
        {
            if (session is null || status is null)
            {
                return;
            }

            status = source == TranscriptSource.You
                ? status with
                {
                    YouRecognitionState = degraded ? "degraded" : "running",
                    YouSpeechLost = status.YouSpeechLost || speechLost,
                }
                : status with
                {
                    MeetingRecognitionState = degraded ? "degraded" : "running",
                    MeetingSpeechLost = status.MeetingSpeechLost || speechLost,
                };
            status = status with
            {
                State = status.YouRecognitionState == "degraded" ||
                    status.MeetingRecognitionState == "degraded"
                        ? "degraded"
                        : "running",
            };
            StatusChanged?.Invoke(status);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task TransitionToTerminalFailureAsync(
        LiveSessionException exception,
        string stopReason)
    {
        await gate.WaitAsync(CancellationToken.None);

        try
        {
            if (session is null || status is null)
            {
                return;
            }

            session.WriteFailed -= HandleWriteFailure;
            session.TerminalFailure -= HandleTerminalFailure;
            session.RecognitionStateChanged -= HandleRecognitionStateChanged;

            try
            {
                await session.StopAsync(CancellationToken.None);
            }
            catch
            {
            }

            try
            {
                await session.DisposeAsync();
            }
            catch
            {
            }

            session = null;
            status = status with
            {
                State = "failed",
                ErrorCode = exception.Code,
                ErrorMessage = exception.Message,
                StopReason = stopReason,
                ElapsedDuration = GetElapsedDuration(status),
            };
            terminalStatus!.TrySetResult(status);
        }
        finally
        {
            gate.Release();
        }
    }

    private LiveSessionStatus WithElapsed(LiveSessionStatus value) =>
        value with { ElapsedDuration = GetElapsedDuration(value) };

    private TimeSpan GetElapsedDuration(LiveSessionStatus value) => value.StartedAtUtc is null
        ? value.ElapsedDuration
        : clock.GetUtcNow() - value.StartedAtUtc.Value;
}

internal sealed class LiveTranscriptionSession : IAsyncDisposable
{
    private readonly StartSessionRequest request;
    private readonly IAudioCapture microphoneCapture;
    private readonly IAudioCapture playbackCapture;
    private readonly ISpeechRecognizer youRecognizer;
    private readonly ISpeechRecognizer meetingRecognizer;
    private readonly RecoveringRecognitionStream youStream;
    private readonly RecoveringRecognitionStream meetingStream;
    private readonly TranscriptOrderingBuffer transcriptOrdering;
    private readonly object writerGate = new();
    private int writeFailureSignaled;

    public LiveTranscriptionSession(
        StartSessionRequest request,
        IAudioCaptureFactory audioCaptureFactory,
        ISpeechRecognizerFactory speechRecognizerFactory,
        Func<CancellationToken, Task>? transcriptHoldback = null)
    {
        this.request = request;
        microphoneCapture = audioCaptureFactory.CreateMicrophone(request.MicrophoneId);
        playbackCapture = audioCaptureFactory.CreatePlayback(request.PlaybackId);
        youRecognizer = speechRecognizerFactory.Create(TranscriptSource.You);
        meetingRecognizer = speechRecognizerFactory.Create(TranscriptSource.Meeting);
        transcriptOrdering = new TranscriptOrderingBuffer(
            WriteFinalized,
            WriteInterruptionMarker,
            transcriptHoldback);
        youStream = new RecoveringRecognitionStream(
            TranscriptSource.You,
            youRecognizer,
            OnRecognitionStateChanged,
            transcriptOrdering.AddInterruptionMarker);
        meetingStream = new RecoveringRecognitionStream(
            TranscriptSource.Meeting,
            meetingRecognizer,
            OnRecognitionStateChanged,
            transcriptOrdering.AddInterruptionMarker);
        microphoneCapture.TerminalFailure += HandleMicrophoneFailure;
        playbackCapture.TerminalFailure += HandlePlaybackFailure;
        youRecognizer.TerminalFailure += HandleYouRecognitionFailure;
        meetingRecognizer.TerminalFailure += HandleMeetingRecognitionFailure;
    }

    public event Action<LiveSessionException>? WriteFailed;

    public event Action<LiveSessionException, string>? TerminalFailure;

    public event Action<TranscriptSource, bool, bool>? RecognitionStateChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
        var heading = $"# Live Transcript{Environment.NewLine}{Environment.NewLine}";

        if (request.Append && File.Exists(request.OutputPath))
        {
            await File.AppendAllTextAsync(
                request.OutputPath,
                $"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}{heading}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
        }
        else
        {
            await using var stream = new FileStream(
                request.OutputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            await using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteAsync(heading.AsMemory(), cancellationToken);
        }

        microphoneCapture.AudioAvailable += youStream.WriteAudio;
        playbackCapture.AudioAvailable += meetingStream.WriteAudio;
        youStream.Finalized += WriteYou;
        meetingStream.Finalized += WriteMeeting;

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
            youStream.StopAsync(cancellationToken),
            meetingStream.StopAsync(cancellationToken));
        await transcriptOrdering.CompleteAsync();
    }

    public async ValueTask DisposeAsync()
    {
        microphoneCapture.AudioAvailable -= youStream.WriteAudio;
        playbackCapture.AudioAvailable -= meetingStream.WriteAudio;
        microphoneCapture.TerminalFailure -= HandleMicrophoneFailure;
        playbackCapture.TerminalFailure -= HandlePlaybackFailure;
        youRecognizer.TerminalFailure -= HandleYouRecognitionFailure;
        meetingRecognizer.TerminalFailure -= HandleMeetingRecognitionFailure;
        youStream.Finalized -= WriteYou;
        meetingStream.Finalized -= WriteMeeting;
        await microphoneCapture.DisposeAsync();
        await playbackCapture.DisposeAsync();
        await youRecognizer.DisposeAsync();
        await meetingRecognizer.DisposeAsync();
        await transcriptOrdering.CompleteAsync();
    }

    private void OnRecognitionStateChanged(
        TranscriptSource source,
        bool degraded,
        bool speechLost,
        TimeSpan audioOffset)
    {
        if (degraded)
        {
            transcriptOrdering.SuspendAfter(source, audioOffset);
        }
        else
        {
            transcriptOrdering.Resume(source);
        }

        RecognitionStateChanged?.Invoke(source, degraded, speechLost);
    }

    private void WriteYou(FinalizedRecognition result) =>
        transcriptOrdering.Add(TranscriptSource.You, result);

    private void WriteMeeting(FinalizedRecognition result) =>
        transcriptOrdering.Add(TranscriptSource.Meeting, result);

    public bool AppendDurationLimitMarker() => AppendTranscriptText(
        $"> Transcription stopped: two-hour session limit reached.{Environment.NewLine}{Environment.NewLine}");

    private void HandleMicrophoneFailure() => TerminalFailure?.Invoke(
        new LiveSessionException(
            "audio-device-lost",
            "The selected microphone audio endpoint is no longer available."),
        LiveSessionStopReasons.DeviceFailure);

    private void HandlePlaybackFailure() => TerminalFailure?.Invoke(
        new LiveSessionException(
            "audio-device-lost",
            "The selected playback audio endpoint is no longer available."),
        LiveSessionStopReasons.DeviceFailure);

    private void HandleYouRecognitionFailure(string errorCode) =>
        HandleRecognitionFailure(TranscriptSource.You, errorCode);

    private void HandleMeetingRecognitionFailure(string errorCode) =>
        HandleRecognitionFailure(TranscriptSource.Meeting, errorCode);

    private void HandleRecognitionFailure(TranscriptSource source, string errorCode)
    {
        var sanitizedCode = new string(errorCode
            .Where(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            .Take(64)
            .ToArray());
        var sourceName = source == TranscriptSource.You ? "you" : "meeting";
        TerminalFailure?.Invoke(
            new LiveSessionException(
                "azure-recognition-failed",
                $"Azure Speech recognition failed for {sourceName} ({sanitizedCode})."),
            LiveSessionStopReasons.AzureFailure);
    }

    private void WriteFinalized(TranscriptSource source, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Volatile.Read(ref writeFailureSignaled) != 0)
        {
            return;
        }

        var label = source == TranscriptSource.You ? "You" : "Meeting";
        AppendTranscriptText($"**{label}:** {text.Trim()}{Environment.NewLine}{Environment.NewLine}");
    }

    private void WriteInterruptionMarker(TranscriptSource source)
    {
        var label = source == TranscriptSource.You ? "You" : "Meeting";
        AppendTranscriptText(
            $"> **{label}:** Transcription was interrupted and some speech may be missing." +
            $"{Environment.NewLine}{Environment.NewLine}");
    }

    private bool AppendTranscriptText(string text)
    {
        try
        {
            lock (writerGate)
            {
                if (!File.Exists(request.OutputPath))
                {
                    throw new IOException("The transcript destination is no longer available.");
                }

                File.AppendAllText(
                    request.OutputPath,
                    text,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

                    return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (Interlocked.Exchange(ref writeFailureSignaled, 1) == 0)
            {
                WriteFailed?.Invoke(new LiveSessionException(
                    "transcript-write-failed",
                    $"The transcript could not be written: {exception.Message}"));
            }

            return false;
        }
    }
}

internal sealed class RecoveringRecognitionStream
{
    private const int BytesPerSecond = 16_000 * 2;
    private const int MaxBufferedBytes = BytesPerSecond * 30;

    private readonly TranscriptSource source;
    private readonly ISpeechRecognizer recognizer;
    private readonly Action<TranscriptSource, bool, bool, TimeSpan> stateChanged;
    private readonly Action<TranscriptSource, TimeSpan> writeInterruptionMarker;
    private readonly object gate = new();
    private readonly Queue<byte[]> bufferedAudio = [];
    private readonly CancellationTokenSource recoveryCancellation = new();
    private Task recoveryTask = Task.CompletedTask;
    private int bufferedBytes;
    private long capturedAudioBytes;
    private long recoveryStartBytes;
    private long droppedAudioBytes;
    private long recognizerOffsetBaseBytes;
    private bool recovering;
    private bool speechLost;
    private bool lossDuringRecovery;

    public RecoveringRecognitionStream(
        TranscriptSource source,
        ISpeechRecognizer recognizer,
        Action<TranscriptSource, bool, bool, TimeSpan> stateChanged,
        Action<TranscriptSource, TimeSpan> writeInterruptionMarker)
    {
        this.source = source;
        this.recognizer = recognizer;
        this.stateChanged = stateChanged;
        this.writeInterruptionMarker = writeInterruptionMarker;
        recognizer.Finalized += HandleFinalized;
        recognizer.RecoverableInterruption += HandleRecoverableInterruption;
    }

    public event Action<FinalizedRecognition>? Finalized;

    public void WriteAudio(ReadOnlyMemory<byte> audio)
    {
        lock (gate)
        {
            if (!recovering)
            {
                recognizer.WriteAudio(audio);
                capturedAudioBytes += audio.Length;
                return;
            }

            var bufferedChunk = audio.ToArray();
            bufferedAudio.Enqueue(bufferedChunk);
            bufferedBytes += bufferedChunk.Length;
            capturedAudioBytes += bufferedChunk.Length;

            while (bufferedBytes > MaxBufferedBytes && bufferedAudio.Count > 0)
            {
                var droppedBytes = bufferedAudio.Dequeue().Length;
                bufferedBytes -= droppedBytes;
                droppedAudioBytes += droppedBytes;

                if (!lossDuringRecovery)
                {
                    lossDuringRecovery = true;
                    speechLost = true;
                    stateChanged(
                        source,
                        true,
                        true,
                        TimeSpan.FromSeconds((double)recoveryStartBytes / BytesPerSecond));
                }
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        recoveryCancellation.Cancel();

        try
        {
            await recoveryTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested)
        {
        }

        await recognizer.StopAsync(cancellationToken);
    }

    private void HandleRecoverableInterruption()
    {
        lock (gate)
        {
            if (recovering)
            {
                return;
            }

            recovering = true;
            recoveryStartBytes = capturedAudioBytes;
            droppedAudioBytes = 0;
            stateChanged(
                source,
                true,
                speechLost,
                TimeSpan.FromSeconds((double)recoveryStartBytes / BytesPerSecond));
            recoveryTask = RecoverAsync(recoveryCancellation.Token);
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await recognizer.RecoverAsync(cancellationToken);
            long replayBoundaryBytes;
            long droppedBeforeReplayBarrier;

            lock (gate)
            {
                recognizerOffsetBaseBytes = recoveryStartBytes + droppedAudioBytes;

                while (bufferedAudio.Count > 0)
                {
                    recognizer.WriteAudio(bufferedAudio.Dequeue());
                }

                bufferedBytes = 0;
                replayBoundaryBytes = capturedAudioBytes;
                droppedBeforeReplayBarrier = droppedAudioBytes;
            }

            await recognizer.CompleteReplayAsync(cancellationToken);
            var bufferedDuringReplay = false;

            lock (gate)
            {
                recognizerOffsetBaseBytes = replayBoundaryBytes +
                    droppedAudioBytes -
                    droppedBeforeReplayBarrier;
                bufferedDuringReplay = bufferedAudio.Count > 0;

                while (bufferedAudio.Count > 0)
                {
                    recognizer.WriteAudio(bufferedAudio.Dequeue());
                }

                bufferedBytes = 0;
                replayBoundaryBytes = capturedAudioBytes;
                droppedBeforeReplayBarrier = droppedAudioBytes;
            }

            if (bufferedDuringReplay)
            {
                await recognizer.CompleteReplayAsync(cancellationToken);
            }

            lock (gate)
            {
                recognizerOffsetBaseBytes = replayBoundaryBytes +
                    droppedAudioBytes -
                    droppedBeforeReplayBarrier;
                var writeMarker = lossDuringRecovery;
                lossDuringRecovery = false;

                if (writeMarker)
                {
                    writeInterruptionMarker(
                        source,
                        TimeSpan.FromSeconds((double)recognizerOffsetBaseBytes / BytesPerSecond));
                }

                while (bufferedAudio.Count > 0)
                {
                    recognizer.WriteAudio(bufferedAudio.Dequeue());
                }

                bufferedBytes = 0;
                recovering = false;
                stateChanged(
                    source,
                    false,
                    speechLost,
                    TimeSpan.FromSeconds((double)capturedAudioBytes / BytesPerSecond));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (gate)
            {
                var bufferedSpeechLost = bufferedAudio.Count > 0;
                bufferedAudio.Clear();
                bufferedBytes = 0;
                recovering = false;
                speechLost |= bufferedSpeechLost;

                if (bufferedSpeechLost && !lossDuringRecovery)
                {
                    writeInterruptionMarker(
                        source,
                        TimeSpan.FromSeconds((double)capturedAudioBytes / BytesPerSecond));
                }

                lossDuringRecovery = false;
                stateChanged(
                    source,
                    false,
                    speechLost,
                    TimeSpan.FromSeconds((double)capturedAudioBytes / BytesPerSecond));
            }
        }
    }

    private void HandleFinalized(FinalizedRecognition result)
    {
        lock (gate)
        {
            Finalized?.Invoke(result with
            {
                AudioOffset = result.AudioOffset + TimeSpan.FromSeconds(
                    (double)recognizerOffsetBaseBytes / BytesPerSecond),
            });
        }
    }
}

internal sealed class TranscriptOrderingBuffer
{
    private static readonly TimeSpan Holdback = TimeSpan.FromMilliseconds(500);
    private readonly object gate = new();
    private readonly List<PendingTranscriptParagraph> pending = [];
    private readonly Dictionary<TranscriptSource, TimeSpan> suspendedAfter = [];
    private readonly CancellationTokenSource holdbackCancellation = new();
    private readonly Action<TranscriptSource, string> emit;
    private readonly Action<TranscriptSource> emitInterruptionMarker;
    private readonly Func<CancellationToken, Task> waitForHoldback;
    private readonly HashSet<Task> holdbackTasks = [];
    private bool completed;
    private long nextSequence;

    public TranscriptOrderingBuffer(
        Action<TranscriptSource, string> emit,
        Action<TranscriptSource> emitInterruptionMarker,
        Func<CancellationToken, Task>? waitForHoldback = null)
    {
        this.emit = emit;
        this.emitInterruptionMarker = emitInterruptionMarker;
        this.waitForHoldback = waitForHoldback ??
            (cancellationToken => Task.Delay(Holdback, cancellationToken));
    }

    public void Add(TranscriptSource source, FinalizedRecognition result)
    {
        if (string.IsNullOrWhiteSpace(result.Text))
        {
            return;
        }

        lock (gate)
        {
            if (completed)
            {
                return;
            }

            pending.Add(new PendingTranscriptParagraph(
                source,
                result.Text,
                result.AudioOffset,
                Interlocked.Increment(ref nextSequence)));
            TrackHoldback(ReleaseAfterHoldbackAsync(pending[^1]));
        }
    }

    public void AddInterruptionMarker(TranscriptSource source, TimeSpan audioOffset)
    {
        lock (gate)
        {
            if (completed)
            {
                return;
            }

            pending.Add(new PendingTranscriptParagraph(
                source,
                null,
                audioOffset,
                Interlocked.Increment(ref nextSequence)));
            TrackHoldback(ReleaseAfterHoldbackAsync(pending[^1]));
        }
    }

    public void SuspendAfter(TranscriptSource source, TimeSpan audioOffset)
    {
        lock (gate)
        {
            suspendedAfter.TryAdd(source, audioOffset);
        }
    }

    public void Resume(TranscriptSource source)
    {
        lock (gate)
        {
            suspendedAfter.Remove(source);
            pending.Sort(PendingTranscriptParagraphComparer.Instance);
            EmitEligible();
        }
    }

    public async Task CompleteAsync()
    {
        Task completion;
        var disposeCancellation = false;

        lock (gate)
        {
            if (!completed)
            {
                completed = true;
                holdbackCancellation.Cancel();
                disposeCancellation = true;
            }

            completion = Task.WhenAll(holdbackTasks);
        }

        try
        {
            await completion;
        }
        finally
        {
            if (disposeCancellation)
            {
                holdbackCancellation.Dispose();
            }
        }
    }

    private void TrackHoldback(Task holdbackTask)
    {
        if (holdbackTask.IsCompleted)
        {
            return;
        }

        holdbackTasks.Add(holdbackTask);
        _ = holdbackTask.ContinueWith(
            completedTask =>
            {
                lock (gate)
                {
                    holdbackTasks.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ReleaseAfterHoldbackAsync(PendingTranscriptParagraph paragraph)
    {
        try
        {
            await waitForHoldback(holdbackCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (holdbackCancellation.IsCancellationRequested)
        {
        }

        lock (gate)
        {
            paragraph.IsEligible = true;
            pending.Sort(PendingTranscriptParagraphComparer.Instance);
            EmitEligible();
        }
    }

    private void EmitEligible()
    {
        var emissionBoundary = suspendedAfter.Count == 0
            ? TimeSpan.MaxValue
            : suspendedAfter.Values.Min();

        while (pending.Count > 0 &&
            pending[0].IsEligible &&
            pending[0].AudioOffset <= emissionBoundary)
        {
            var next = pending[0];
            pending.RemoveAt(0);

            if (next.Text is null)
            {
                emitInterruptionMarker(next.Source);
            }
            else
            {
                emit(next.Source, next.Text);
            }
        }
    }

    private sealed class PendingTranscriptParagraph(
        TranscriptSource source,
        string? text,
        TimeSpan audioOffset,
        long sequence)
    {
        public TranscriptSource Source { get; } = source;

        public string? Text { get; } = text;

        public TimeSpan AudioOffset { get; } = audioOffset;

        public long Sequence { get; } = sequence;

        public bool IsEligible { get; set; }
    }

    private sealed class PendingTranscriptParagraphComparer : IComparer<PendingTranscriptParagraph>
    {
        public static PendingTranscriptParagraphComparer Instance { get; } = new();

        public int Compare(PendingTranscriptParagraph? left, PendingTranscriptParagraph? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var offsetComparison = left.AudioOffset.CompareTo(right.AudioOffset);

            if (offsetComparison != 0)
            {
                return offsetComparison;
            }

            var sourceComparison = left.Source.CompareTo(right.Source);
            return sourceComparison != 0
                ? sourceComparison
                : left.Sequence.CompareTo(right.Sequence);
        }
    }
}