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
    Func<string>? sessionIdFactory = null,
    Func<CancellationToken, Task>? transcriptHoldback = null) : ILiveSessionController
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<string> createSessionId = sessionIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    private LiveTranscriptionSession? session;
    private LiveSessionStatus? status;
    private TaskCompletionSource<LiveSessionStatus>? terminalStatus;

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
            terminalStatus = new TaskCompletionSource<LiveSessionStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                await candidate.StartAsync(cancellationToken);
            }
            catch
            {
                candidate.WriteFailed -= HandleWriteFailure;
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

            session.WriteFailed -= HandleWriteFailure;
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

    internal Task<LiveSessionStatus> WaitForTerminalStatusAsync(
        CancellationToken cancellationToken) => terminalStatus is null
            ? Task.FromException<LiveSessionStatus>(new InvalidOperationException("The session has not started."))
            : terminalStatus.Task.WaitAsync(cancellationToken);

    private void HandleWriteFailure(LiveSessionException exception) =>
        _ = TransitionToWriteFailureAsync(exception);

    private async Task TransitionToWriteFailureAsync(LiveSessionException exception)
    {
        await gate.WaitAsync(CancellationToken.None);

        try
        {
            if (session is null || status is null)
            {
                return;
            }

            session.WriteFailed -= HandleWriteFailure;

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
            };
            terminalStatus!.TrySetResult(status);
        }
        finally
        {
            gate.Release();
        }
    }
}

internal sealed class LiveTranscriptionSession : IAsyncDisposable
{
    private readonly StartSessionRequest request;
    private readonly IAudioCapture microphoneCapture;
    private readonly IAudioCapture playbackCapture;
    private readonly ISpeechRecognizer youRecognizer;
    private readonly ISpeechRecognizer meetingRecognizer;
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
        transcriptOrdering = new TranscriptOrderingBuffer(WriteFinalized, transcriptHoldback);
    }

    public event Action<LiveSessionException>? WriteFailed;

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
        await transcriptOrdering.CompleteAsync();
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
        await transcriptOrdering.CompleteAsync();
    }

    private void WriteYou(FinalizedRecognition result) =>
        transcriptOrdering.Add(TranscriptSource.You, result);

    private void WriteMeeting(FinalizedRecognition result) =>
        transcriptOrdering.Add(TranscriptSource.Meeting, result);

    private void WriteFinalized(TranscriptSource source, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Volatile.Read(ref writeFailureSignaled) != 0)
        {
            return;
        }

        var label = source == TranscriptSource.You ? "You" : "Meeting";

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
                    $"**{label}:** {text.Trim()}{Environment.NewLine}{Environment.NewLine}",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (Interlocked.Exchange(ref writeFailureSignaled, 1) == 0)
            {
                WriteFailed?.Invoke(new LiveSessionException(
                    "transcript-write-failed",
                    $"The transcript could not be written: {exception.Message}"));
            }
        }
    }
}

internal sealed class TranscriptOrderingBuffer
{
    private static readonly TimeSpan Holdback = TimeSpan.FromMilliseconds(500);
    private readonly object gate = new();
    private readonly List<PendingTranscriptParagraph> pending = [];
    private readonly CancellationTokenSource holdbackCancellation = new();
    private readonly Action<TranscriptSource, string> emit;
    private readonly Func<CancellationToken, Task> waitForHoldback;
    private readonly HashSet<Task> holdbackTasks = [];
    private bool completed;
    private long nextSequence;

    public TranscriptOrderingBuffer(
        Action<TranscriptSource, string> emit,
        Func<CancellationToken, Task>? waitForHoldback = null)
    {
        this.emit = emit;
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

            while (pending.Count > 0 && pending[0].IsEligible)
            {
                var next = pending[0];
                pending.RemoveAt(0);
                emit(next.Source, next.Text);
            }
        }
    }

    private sealed class PendingTranscriptParagraph(
        TranscriptSource source,
        string text,
        TimeSpan audioOffset,
        long sequence)
    {
        public TranscriptSource Source { get; } = source;

        public string Text { get; } = text;

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