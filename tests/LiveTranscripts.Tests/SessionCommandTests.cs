using System.Text.Json;

namespace LiveTranscripts.Tests;

[TestClass]
public sealed class SessionCommandTests
{
    [TestMethod]
    public async Task StartUsesCommunicationsDefaultsAndReturnsReadySessionAsJson()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var discovery = new ControlledAudioDeviceDiscovery(
            microphones:
            [
                new AudioDevice("microphone-default", "Headset microphone", true),
                new AudioDevice("microphone-other", "Webcam microphone", false),
            ],
            playback:
            [
                new AudioDevice("playback-default", "Headset", true),
            ]);
        var sessions = new ControlledLiveSessionController();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(discovery, sessions);

        var exitCode = await application.RunAsync(
            ["start", outputPath],
            standardOutput,
            standardError);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(string.Empty, standardError.ToString());

        using var result = JsonDocument.Parse(standardOutput.ToString());
        Assert.IsTrue(result.RootElement.GetProperty("success").GetBoolean());
        Assert.AreEqual("session-1", result.RootElement.GetProperty("sessionId").GetString());
        Assert.AreEqual("running", result.RootElement.GetProperty("state").GetString());
        Assert.AreEqual(Path.GetFullPath(outputPath), result.RootElement.GetProperty("outputPath").GetString());
        Assert.AreEqual("microphone-default", result.RootElement.GetProperty("microphoneId").GetString());
        Assert.AreEqual("playback-default", result.RootElement.GetProperty("playbackId").GetString());
    }

    [TestMethod]
    public async Task StartPinsExplicitAudioDevices()
    {
        var discovery = new ControlledAudioDeviceDiscovery(
            microphones:
            [
                new AudioDevice("microphone-default", "Headset microphone", true),
                new AudioDevice("microphone-selected", "Desk microphone", false),
            ],
            playback:
            [
                new AudioDevice("playback-default", "Headset", true),
                new AudioDevice("playback-selected", "Speakers", false),
            ]);
        var sessions = new ControlledLiveSessionController();
        var application = new CliApplication(discovery, sessions);
        var standardOutput = new StringWriter();

        var exitCode = await application.RunAsync(
            [
                "start",
                "meeting.md",
                "--microphone",
                "microphone-selected",
                "--playback",
                "playback-selected",
            ],
            standardOutput,
            new StringWriter());

        Assert.AreEqual(0, exitCode);
        using var result = JsonDocument.Parse(standardOutput.ToString());
        Assert.AreEqual("microphone-selected", result.RootElement.GetProperty("microphoneId").GetString());
        Assert.AreEqual("playback-selected", result.RootElement.GetProperty("playbackId").GetString());
    }

    [TestMethod]
    public async Task StartedSessionCanBeObservedAndStopped()
    {
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var sessions = new ControlledLiveSessionController();
        var application = new CliApplication(discovery, sessions);

        await application.RunAsync(
            ["start", "meeting.md"],
            new StringWriter(),
            new StringWriter());
        var statusOutput = new StringWriter();
        var stopOutput = new StringWriter();

        var statusExitCode = await application.RunAsync(
            ["status"],
            statusOutput,
            new StringWriter());
        var stopExitCode = await application.RunAsync(
            ["stop", "session-1"],
            stopOutput,
            new StringWriter());

        Assert.AreEqual(0, statusExitCode);
        Assert.AreEqual(0, stopExitCode);
        using var status = JsonDocument.Parse(statusOutput.ToString());
        Assert.AreEqual("session-1", status.RootElement.GetProperty("sessionId").GetString());
        Assert.AreEqual("running", status.RootElement.GetProperty("state").GetString());
        using var stopped = JsonDocument.Parse(stopOutput.ToString());
        Assert.AreEqual("session-1", stopped.RootElement.GetProperty("sessionId").GetString());
        Assert.AreEqual("stopped", stopped.RootElement.GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task SessionWritesFinalizedSpeechAndStopsBothSources()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");

        try
        {
            var discovery = new ControlledAudioDeviceDiscovery(
                [new AudioDevice("microphone-1", "Microphone", true)],
                [new AudioDevice("playback-1", "Playback", true)]);
            var captures = new ControlledAudioCaptureFactory();
            var recognizers = new ControlledSpeechRecognizerFactory();
            var stateStore = new ControlledSessionStateStore();
            var worker = new ControlledDetachedWorker(
                stateStore,
                captures,
                recognizers);
            var sessions = new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1");
            var startApplication = new CliApplication(discovery, sessions);

            var startOutput = new StringWriter();
            var startExitCode = await startApplication.RunAsync(
                ["start", outputPath],
                startOutput,
                new StringWriter());

            Assert.AreEqual(0, startExitCode);
            using var started = JsonDocument.Parse(startOutput.ToString());
            Assert.AreEqual("session-1", started.RootElement.GetProperty("sessionId").GetString());
            Assert.AreEqual("microphone-1", started.RootElement.GetProperty("microphoneId").GetString());
            Assert.AreEqual("playback-1", started.RootElement.GetProperty("playbackId").GetString());

            recognizers.You.EmitFinalized("Can everyone hear me?");
            recognizers.Meeting.EmitFinalized("Yes, we can.");
            recognizers.Meeting.EmitFinalized("   ");

            Assert.AreEqual(
                $"# Live Transcript{Environment.NewLine}{Environment.NewLine}" +
                $"**You:** Can everyone hear me?{Environment.NewLine}{Environment.NewLine}" +
                $"**Meeting:** Yes, we can.{Environment.NewLine}{Environment.NewLine}",
                await File.ReadAllTextAsync(outputPath));

            var statusApplication = new CliApplication(
                discovery,
                new DetachedLiveSessionController(
                    stateStore,
                    worker,
                    TimeProvider.System,
                    () => "unused"));
            var statusOutput = new StringWriter();
            var statusExitCode = await statusApplication.RunAsync(
                ["status"],
                statusOutput,
                new StringWriter());
            var mismatchOutput = new StringWriter();
            var mismatchExitCode = await statusApplication.RunAsync(
                ["status", "stale-session"],
                mismatchOutput,
                new StringWriter());
            var mismatchStopOutput = new StringWriter();
            var mismatchStopExitCode = await statusApplication.RunAsync(
                ["stop", "stale-session"],
                mismatchStopOutput,
                new StringWriter());

            Assert.AreEqual(0, statusExitCode);
            using var status = JsonDocument.Parse(statusOutput.ToString());
            Assert.AreEqual("running", status.RootElement.GetProperty("state").GetString());
            Assert.AreNotEqual(0, mismatchExitCode);
            using var mismatch = JsonDocument.Parse(mismatchOutput.ToString());
            Assert.AreEqual(
                "session-not-found",
                mismatch.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.AreNotEqual(0, mismatchStopExitCode);
            using var mismatchStop = JsonDocument.Parse(mismatchStopOutput.ToString());
            Assert.AreEqual(
                "session-not-found",
                mismatchStop.RootElement.GetProperty("error").GetProperty("code").GetString());
            var stopOutput = new StringWriter();
            var stopExitCode = await statusApplication.RunAsync(
                ["stop"],
                stopOutput,
                new StringWriter());
            Assert.AreEqual(0, stopExitCode);
            using var stopped = JsonDocument.Parse(stopOutput.ToString());
            Assert.AreEqual("stopped", stopped.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [TestMethod]
    public async Task SecondStartFailsAsJsonWhileFirstSessionRemainsActive()
    {
        var firstOutputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var secondOutputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(
            stateStore,
            new ControlledAudioCaptureFactory(),
            new ControlledSpeechRecognizerFactory());
        var sessions = new DetachedLiveSessionController(
            stateStore,
            worker,
            TimeProvider.System,
            () => "session-1");
        var application = new CliApplication(discovery, sessions);

        try
        {
            await application.RunAsync(
                ["start", firstOutputPath],
                new StringWriter(),
                new StringWriter());
            var standardOutput = new StringWriter();
            var standardError = new StringWriter();

            var exitCode = await application.RunAsync(
                ["start", secondOutputPath],
                standardOutput,
                standardError);

            Assert.AreNotEqual(0, exitCode);
            Assert.AreEqual(
                "{\"success\":false,\"error\":{\"code\":\"session-already-active\",\"message\":\"A live transcription session is already active.\"}}",
                standardOutput.ToString().TrimEnd());
            Assert.AreNotEqual(string.Empty, standardError.ToString());
            Assert.IsFalse(File.Exists(secondOutputPath));
            var statusOutput = new StringWriter();
            var statusExitCode = await application.RunAsync(
                ["status"],
                statusOutput,
                new StringWriter());
            Assert.AreEqual(0, statusExitCode);
            using var status = JsonDocument.Parse(statusOutput.ToString());
            Assert.AreEqual("session-1", status.RootElement.GetProperty("sessionId").GetString());
            Assert.AreEqual("running", status.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            await application.RunAsync(
                ["stop"],
                new StringWriter(),
                new StringWriter());
            File.Delete(firstOutputPath);
            File.Delete(secondOutputPath);
        }
    }

    [TestMethod]
    public async Task StopFailureIsReportedAsJsonAndStandardError()
    {
        var application = new CliApplication(
            new ControlledAudioDeviceDiscovery([], []),
            new FailingStopSessionController());
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();

        var exitCode = await application.RunAsync(
            ["stop", "session-1"],
            standardOutput,
            standardError);

        Assert.AreNotEqual(0, exitCode);
        Assert.AreEqual(
            "{\"success\":false,\"error\":{\"code\":\"session-stop-timeout\",\"message\":\"The session did not stop in time.\"}}",
            standardOutput.ToString().TrimEnd());
        Assert.AreEqual(
            $"Unable to stop live transcription: The session did not stop in time.{Environment.NewLine}",
            standardError.ToString());
    }

    [TestMethod]
    public async Task UnexpectedOperationalFailureStillReturnsOneJsonResult()
    {
        var application = new CliApplication(
            new ControlledAudioDeviceDiscovery([], []),
            new UnexpectedFailureSessionController());
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();

        var exitCode = await application.RunAsync(
            ["status"],
            standardOutput,
            standardError);

        Assert.AreNotEqual(0, exitCode);
        Assert.AreEqual(
            "{\"success\":false,\"error\":{\"code\":\"command-failed\",\"message\":\"The command could not be completed.\"}}",
            standardOutput.ToString().TrimEnd());
        Assert.AreEqual(
            $"Command failed: state unavailable{Environment.NewLine}",
            standardError.ToString());
    }

    private sealed class ControlledAudioDeviceDiscovery(
        IReadOnlyList<AudioDevice> microphones,
        IReadOnlyList<AudioDevice> playback) : IAudioDeviceDiscovery
    {
        public AudioDeviceInventory Discover() => new(microphones, playback);
    }

    private sealed class ControlledLiveSessionController : ILiveSessionController
    {
        private LiveSessionStatus? currentStatus;

        public Task<LiveSessionStatus> StartAsync(
            StartSessionRequest request,
            CancellationToken cancellationToken)
        {
            currentStatus = new LiveSessionStatus(
                "session-1",
                "running",
                request.OutputPath,
                request.MicrophoneId,
                request.PlaybackId);
            return Task.FromResult(currentStatus);
        }

        public Task<LiveSessionStatus?> GetStatusAsync(
            string? sessionId,
            CancellationToken cancellationToken) => Task.FromResult(
                sessionId is null || sessionId == currentStatus?.SessionId
                    ? currentStatus
                    : null);

        public Task<LiveSessionStatus?> StopAsync(
            string? sessionId,
            CancellationToken cancellationToken)
        {
            if (currentStatus is null ||
                (sessionId is not null && sessionId != currentStatus.SessionId))
            {
                return Task.FromResult<LiveSessionStatus?>(null);
            }

            currentStatus = currentStatus with { State = "stopped" };
            return Task.FromResult<LiveSessionStatus?>(currentStatus);
        }
    }

    private sealed class ControlledAudioCaptureFactory : IAudioCaptureFactory
    {
        public ControlledAudioCapture Microphone { get; } = new();

        public ControlledAudioCapture Playback { get; } = new();

        public IAudioCapture CreateMicrophone(string endpointId)
            => Microphone;

        public IAudioCapture CreatePlayback(string endpointId)
            => Playback;
    }

    private sealed class ControlledAudioCapture : IAudioCapture
    {
        public event Action<ReadOnlyMemory<byte>>? AudioAvailable;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void EmitAudio(ReadOnlyMemory<byte> audio) => AudioAvailable?.Invoke(audio);
    }

    private sealed class ControlledSpeechRecognizerFactory : ISpeechRecognizerFactory
    {
        public ControlledSpeechRecognizer You { get; } = new();

        public ControlledSpeechRecognizer Meeting { get; } = new();

        public ISpeechRecognizer Create(TranscriptSource source) => source switch
        {
            TranscriptSource.You => You,
            TranscriptSource.Meeting => Meeting,
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
    }

    private sealed class ControlledSpeechRecognizer : ISpeechRecognizer
    {
        public event Action<string>? Finalized;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void WriteAudio(ReadOnlyMemory<byte> audio)
        {
        }

        public void EmitFinalized(string text) => Finalized?.Invoke(text);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledSessionStateStore : ISessionStateStore
    {
        private SessionStateDocument? state;

        public Task WriteAsync(SessionStateDocument value, CancellationToken cancellationToken)
        {
            state = value;
            return Task.CompletedTask;
        }

        public Task<SessionStateDocument?> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(state);
    }

    private sealed class ControlledDetachedWorker(
        ISessionStateStore stateStore,
        IAudioCaptureFactory captures,
        ISpeechRecognizerFactory recognizers) : IDetachedWorkerControl
    {
        private InProcessLiveSessionController? session;
        private Task? startup;

        public bool IsActive => session is not null;

        public ValueTask<IAsyncDisposable> AcquireCommandLockAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable>(new ControlledCommandLock());

        public Task<IWorkerProcess> LaunchAsync(
            string launchedSessionId,
            StartSessionRequest launchedRequest,
            CancellationToken cancellationToken)
        {
            session = new InProcessLiveSessionController(captures, recognizers, () => launchedSessionId);
            startup = PublishReadyStateAsync(launchedSessionId, launchedRequest, cancellationToken);
            return Task.FromResult<IWorkerProcess>(new ControlledWorkerProcess(42));
        }

        public async Task SignalStopAsync(string stoppedSessionId, CancellationToken cancellationToken)
        {
            await startup!;
            var status = await session!.StopAsync(stoppedSessionId, cancellationToken);
            await stateStore.WriteAsync(
                SessionStateDocument.FromStatus(status!, processId: 42),
                cancellationToken);
            session = null;
        }

        public Task<bool> WaitForExitAsync(
            int processId,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(true);

        private async Task PublishReadyStateAsync(
            string launchedSessionId,
            StartSessionRequest launchedRequest,
            CancellationToken cancellationToken)
        {
            try
            {
                var status = await session!.StartAsync(launchedRequest, cancellationToken);
                await stateStore.WriteAsync(
                    SessionStateDocument.FromStatus(status, processId: 42),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                await stateStore.WriteAsync(
                    SessionStateDocument.Failed(launchedSessionId, launchedRequest, exception.Message),
                    cancellationToken);
            }
        }

        private sealed class ControlledCommandLock : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class ControlledWorkerProcess(int processId) : IWorkerProcess
        {
            public int ProcessId { get; } = processId;

            public bool HasExited => false;

            public void Kill()
            {
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class FailingStopSessionController : ILiveSessionController
    {
        public Task<LiveSessionStatus> StartAsync(
            StartSessionRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LiveSessionStatus?> GetStatusAsync(
            string? sessionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LiveSessionStatus?> StopAsync(
            string? sessionId,
            CancellationToken cancellationToken) => throw new LiveSessionException(
                "session-stop-timeout",
                "The session did not stop in time.");
    }

    private sealed class UnexpectedFailureSessionController : ILiveSessionController
    {
        public Task<LiveSessionStatus> StartAsync(
            StartSessionRequest request,
            CancellationToken cancellationToken) => throw new IOException("state unavailable");

        public Task<LiveSessionStatus?> GetStatusAsync(
            string? sessionId,
            CancellationToken cancellationToken) => throw new IOException("state unavailable");

        public Task<LiveSessionStatus?> StopAsync(
            string? sessionId,
            CancellationToken cancellationToken) => throw new IOException("state unavailable");
    }
}