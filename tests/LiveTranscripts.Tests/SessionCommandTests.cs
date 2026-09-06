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
    public async Task SessionOrdersFinalizedSpeechByAudioOffsetInsteadOfCallbackOrder()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var recognizers = new ControlledSpeechRecognizerFactory();
        var holdback = new ControlledTranscriptHoldback();
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(
            stateStore,
            new ControlledAudioCaptureFactory(),
            recognizers,
            holdback.WaitAsync);
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["start", outputPath],
                    new StringWriter(),
                    new StringWriter()));

            recognizers.You.EmitFinalized("This is damn important.", TimeSpan.FromSeconds(2));
            recognizers.Meeting.EmitFinalized("What do you think?", TimeSpan.FromSeconds(1));

            Assert.AreEqual(
                $"# Live Transcript{Environment.NewLine}{Environment.NewLine}",
                await File.ReadAllTextAsync(outputPath));
            holdback.ReleaseNext();
            Assert.AreEqual(
                $"# Live Transcript{Environment.NewLine}{Environment.NewLine}",
                await File.ReadAllTextAsync(outputPath));
            holdback.ReleaseNext();
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["stop"],
                    new StringWriter(),
                    new StringWriter()));

            var expectedTranscript =
                $"# Live Transcript{Environment.NewLine}{Environment.NewLine}" +
                $"**Meeting:** What do you think?{Environment.NewLine}{Environment.NewLine}" +
                $"**You:** This is damn important.{Environment.NewLine}{Environment.NewLine}";
            Assert.AreEqual(expectedTranscript, await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            await application.RunAsync(["stop"], new StringWriter(), new StringWriter());
            File.Delete(outputPath);
        }
    }

    [TestMethod]
    public async Task InterruptedSourceBuffersAndReplaysAudioWhileOtherSourceContinues()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var captures = new ControlledAudioCaptureFactory();
        var recognizers = new ControlledSpeechRecognizerFactory();
        recognizers.You.AudioWritten += audio =>
        {
            if (audio.Span[0] == 1)
            {
                recognizers.You.EmitFinalized("You recovered.", TimeSpan.FromSeconds(1));
            }
        };
        recognizers.Meeting.AudioWritten += audio =>
            recognizers.Meeting.EmitFinalized("Meeting continued.", TimeSpan.FromSeconds(1));
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(stateStore, captures, recognizers);
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["start", outputPath],
                    new StringWriter(),
                    new StringWriter()));

            captures.Microphone.EmitAudio(Enumerable.Repeat((byte)0, 2 * 32_000).ToArray());
            recognizers.You.Interrupt();
            captures.Microphone.EmitAudio(new byte[] { 1, 2, 3, 4 });
            captures.Playback.EmitAudio(new byte[] { 5, 6 });

            var degraded = await WaitForSourceStateAsync(application, "you", "degraded");
            Assert.AreEqual("degraded", degraded.GetProperty("state").GetString());
            Assert.AreEqual(
                "running",
                degraded.GetProperty("sources").GetProperty("meeting").GetProperty("state").GetString());
            StringAssert.Contains(
                await File.ReadAllTextAsync(outputPath),
                "**Meeting:** Meeting continued.");
            Assert.IsFalse(
                (await File.ReadAllTextAsync(outputPath)).Contains("**You:** You recovered."));

            recognizers.You.CompleteRecovery();

            var recovered = await WaitForSourceStateAsync(application, "you", "running");
            Assert.AreEqual("running", recovered.GetProperty("state").GetString());
            StringAssert.Contains(
                await File.ReadAllTextAsync(outputPath),
                "**You:** You recovered.");
        }
        finally
        {
            await application.RunAsync(["stop"], new StringWriter(), new StringWriter());
            File.Delete(outputPath);
        }
    }

    [TestMethod]
    public async Task OverflowReportsPersistentLossAndWritesOneMarkerAfterReplayedSpeech()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(rootPath, "meeting.md");
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var captures = new ControlledAudioCaptureFactory();
        var recognizers = new ControlledSpeechRecognizerFactory();
        recognizers.You.AudioWritten += audio =>
            recognizers.You.EmitFinalized(
                $"Recovered second {audio.Span[0]}.",
                TimeSpan.FromSeconds(audio.Span[0] - 2));
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(stateStore, captures, recognizers);
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["start", outputPath],
                    new StringWriter(),
                    new StringWriter()));

            recognizers.You.Interrupt();
            recognizers.You.Interrupt();
            recognizers.You.Interrupt();

            for (var second = 1; second <= 31; second++)
            {
                captures.Microphone.EmitAudio(
                    Enumerable.Repeat((byte)second, 32_000).ToArray());
            }

            var degraded = await WaitForSourceStateAsync(application, "you", "degraded", speechLost: true);
            Assert.IsFalse(
                degraded.GetProperty("sources").GetProperty("meeting").GetProperty("speechLost").GetBoolean());

            recognizers.You.CompleteRecovery();

            var recovered = await WaitForSourceStateAsync(application, "you", "running", speechLost: true);
            Assert.AreEqual("running", recovered.GetProperty("state").GetString());

            var expectedMarker =
                "> **You:** Transcription was interrupted and some speech may be missing.";
            var transcript = await File.ReadAllTextAsync(outputPath);
            Assert.IsFalse(transcript.Contains("Recovered second 1."));
            StringAssert.Contains(transcript, "**You:** Recovered second 2.");
            StringAssert.Contains(transcript, "**You:** Recovered second 31.");
            Assert.AreEqual(30, transcript.Split("**You:** Recovered second ").Length - 1);
            Assert.IsTrue(
                transcript.IndexOf(expectedMarker, StringComparison.Ordinal) >
                transcript.IndexOf("**You:** Recovered second 31.", StringComparison.Ordinal));
            Assert.AreEqual(1, transcript.Split(expectedMarker).Length - 1);
            CollectionAssert.AreEquivalent(
                new[] { outputPath },
                Directory.GetFiles(rootPath, "*", SearchOption.AllDirectories));
        }
        finally
        {
            await application.RunAsync(["stop"], new StringWriter(), new StringWriter());

            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RecoveredResultsRetainMonotonicAudioTimeline()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var captures = new ControlledAudioCaptureFactory();
        var recognizers = new ControlledSpeechRecognizerFactory();
        recognizers.You.AudioWritten += audio =>
        {
            if (audio.Span[0] == 2)
            {
                recognizers.You.EmitFinalized("Recovered at thirty-one seconds.", TimeSpan.FromSeconds(1));
            }
        };
        var holdback = new ControlledTranscriptHoldback();
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(
            stateStore,
            captures,
            recognizers,
            holdback.WaitAsync);
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["start", outputPath],
                    new StringWriter(),
                    new StringWriter()));

            captures.Microphone.EmitAudio(Enumerable.Repeat((byte)1, 30 * 32_000).ToArray());
            recognizers.You.Interrupt();
            captures.Microphone.EmitAudio(Enumerable.Repeat((byte)2, 32_000).ToArray());
            recognizers.Meeting.EmitFinalized("Meeting at twenty seconds.", TimeSpan.FromSeconds(20));
            recognizers.You.CompleteRecovery();
            await WaitForSourceStateAsync(application, "you", "running");

            holdback.ReleaseNext();
            holdback.ReleaseNext();
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["stop"],
                    new StringWriter(),
                    new StringWriter()));

            Assert.AreEqual(
                $"# Live Transcript{Environment.NewLine}{Environment.NewLine}" +
                $"**Meeting:** Meeting at twenty seconds.{Environment.NewLine}{Environment.NewLine}" +
                $"**You:** Recovered at thirty-one seconds.{Environment.NewLine}{Environment.NewLine}",
                await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            await application.RunAsync(["stop"], new StringWriter(), new StringWriter());
            File.Delete(outputPath);
        }
    }

    [TestMethod]
    public async Task RecoveryHoldsLaterOtherSourceResultsUntilReplayCompletes()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var captures = new ControlledAudioCaptureFactory();
        var recognizers = new ControlledSpeechRecognizerFactory();
        recognizers.You.AudioWritten += audio =>
        {
            if (audio.Span[0] == 2)
            {
                recognizers.You.EmitFinalized("Recovered first.", TimeSpan.FromSeconds(1));
            }
        };
        var holdback = new ControlledTranscriptHoldback();
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(
            stateStore,
            captures,
            recognizers,
            holdback.WaitAsync);
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["start", outputPath],
                    new StringWriter(),
                    new StringWriter()));

            captures.Microphone.EmitAudio(Enumerable.Repeat((byte)1, 2 * 32_000).ToArray());
            recognizers.You.Interrupt();
            captures.Microphone.EmitAudio(Enumerable.Repeat((byte)2, 32_000).ToArray());
            recognizers.Meeting.EmitFinalized("Meeting second.", TimeSpan.FromSeconds(4));
            holdback.ReleaseNext();

            Assert.AreEqual(
                $"# Live Transcript{Environment.NewLine}{Environment.NewLine}",
                await File.ReadAllTextAsync(outputPath));

            recognizers.You.CompleteRecovery();
            await WaitForSourceStateAsync(application, "you", "running");
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["stop"],
                    new StringWriter(),
                    new StringWriter()));

            Assert.AreEqual(
                $"# Live Transcript{Environment.NewLine}{Environment.NewLine}" +
                $"**You:** Recovered first.{Environment.NewLine}{Environment.NewLine}" +
                $"**Meeting:** Meeting second.{Environment.NewLine}{Environment.NewLine}",
                await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            await application.RunAsync(["stop"], new StringWriter(), new StringWriter());
            File.Delete(outputPath);
        }
    }

    [TestMethod]
    public async Task StopDuringRecoveryDisclosesLossAndFlushesOtherSource()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var captures = new ControlledAudioCaptureFactory();
        var recognizers = new ControlledSpeechRecognizerFactory();
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(stateStore, captures, recognizers);
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["start", outputPath],
                    new StringWriter(),
                    new StringWriter()));

            captures.Microphone.EmitAudio(Enumerable.Repeat((byte)1, 2 * 32_000).ToArray());
            recognizers.You.Interrupt();
            captures.Microphone.EmitAudio(Enumerable.Repeat((byte)2, 32_000).ToArray());
            recognizers.Meeting.EmitFinalized("Meeting after interruption.", TimeSpan.FromSeconds(4));

            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["stop"],
                    new StringWriter(),
                    new StringWriter()));

            Assert.AreEqual(
                $"# Live Transcript{Environment.NewLine}{Environment.NewLine}" +
                $"> **You:** Transcription was interrupted and some speech may be missing.{Environment.NewLine}{Environment.NewLine}" +
                $"**Meeting:** Meeting after interruption.{Environment.NewLine}{Environment.NewLine}",
                await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            await application.RunAsync(["stop"], new StringWriter(), new StringWriter());
            File.Delete(outputPath);
        }
    }

    [TestMethod]
    public async Task AudioBufferedDuringReplayFlushPrecedesLossMarker()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var captures = new ControlledAudioCaptureFactory();
        var recognizers = new ControlledSpeechRecognizerFactory();
        var replayCompletions = 0;
        recognizers.You.ReplayCompleting += () =>
        {
            replayCompletions++;

            if (replayCompletions == 1)
            {
                captures.Microphone.EmitAudio(Enumerable.Repeat((byte)32, 32_000).ToArray());
            }
            else
            {
                recognizers.You.EmitFinalized("Speech during replay flush.", TimeSpan.Zero);
            }
        };
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(stateStore, captures, recognizers);
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["start", outputPath],
                    new StringWriter(),
                    new StringWriter()));
            recognizers.You.Interrupt();

            for (var second = 1; second <= 31; second++)
            {
                captures.Microphone.EmitAudio(
                    Enumerable.Repeat((byte)second, 32_000).ToArray());
            }

            recognizers.You.CompleteRecovery();
            await WaitForSourceStateAsync(application, "you", "running", speechLost: true);

            Assert.AreEqual(
                $"# Live Transcript{Environment.NewLine}{Environment.NewLine}" +
                $"**You:** Speech during replay flush.{Environment.NewLine}{Environment.NewLine}" +
                $"> **You:** Transcription was interrupted and some speech may be missing.{Environment.NewLine}{Environment.NewLine}",
                await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            await application.RunAsync(["stop"], new StringWriter(), new StringWriter());
            File.Delete(outputPath);
        }
    }

    [TestMethod]
    public async Task LossMarkerUsesMonotonicOrderingAfterReplayedSpeech()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var captures = new ControlledAudioCaptureFactory();
        var recognizers = new ControlledSpeechRecognizerFactory();
        recognizers.You.ReplayCompleting += () =>
            recognizers.You.EmitFinalized("Last recovered words.", TimeSpan.FromSeconds(30));
        var holdback = new ControlledTranscriptHoldback();
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(
            stateStore,
            captures,
            recognizers,
            holdback.WaitAsync);
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["start", outputPath],
                    new StringWriter(),
                    new StringWriter()));

            recognizers.You.Interrupt();

            for (var second = 1; second <= 31; second++)
            {
                captures.Microphone.EmitAudio(
                    Enumerable.Repeat((byte)second, 32_000).ToArray());
            }

            recognizers.Meeting.EmitFinalized("Speech after recovery.", TimeSpan.FromSeconds(40));
            recognizers.You.CompleteRecovery();
            await WaitForSourceStateAsync(application, "you", "running", speechLost: true);

            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["stop"],
                    new StringWriter(),
                    new StringWriter()));

            Assert.AreEqual(
                $"# Live Transcript{Environment.NewLine}{Environment.NewLine}" +
                $"**You:** Last recovered words.{Environment.NewLine}{Environment.NewLine}" +
                $"> **You:** Transcription was interrupted and some speech may be missing.{Environment.NewLine}{Environment.NewLine}" +
                $"**Meeting:** Speech after recovery.{Environment.NewLine}{Environment.NewLine}",
                await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            await application.RunAsync(["stop"], new StringWriter(), new StringWriter());
            File.Delete(outputPath);
        }
    }

    [TestMethod]
    public async Task StartRefusesExistingDestinationWithoutLeavingWorkerActive()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var existingBytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x65, 0x78, 0x69, 0x73, 0x74, 0x69, 0x6E, 0x67 };
        await File.WriteAllBytesAsync(outputPath, existingBytes);
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(
            stateStore,
            new ControlledAudioCaptureFactory(),
            new ControlledSpeechRecognizerFactory());
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            var standardOutput = new StringWriter();

            var exitCode = await application.RunAsync(
                ["start", outputPath],
                standardOutput,
                new StringWriter());

            Assert.AreNotEqual(0, exitCode);
            using var result = JsonDocument.Parse(standardOutput.ToString());
            Assert.AreEqual(
                "transcript-already-exists",
                result.RootElement.GetProperty("error").GetProperty("code").GetString());
            CollectionAssert.AreEqual(existingBytes, await File.ReadAllBytesAsync(outputPath));
            Assert.IsFalse(worker.IsActive);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [TestMethod]
    public async Task StartResolvesCallerRelativePathAndCreatesParentDirectories()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(rootPath, "nested", "meeting.md");
        var relativeOutputPath = Path.GetRelativePath(Environment.CurrentDirectory, outputPath);
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(
            stateStore,
            new ControlledAudioCaptureFactory(),
            new ControlledSpeechRecognizerFactory());
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            var standardOutput = new StringWriter();

            var exitCode = await application.RunAsync(
                ["start", relativeOutputPath],
                standardOutput,
                new StringWriter());

            Assert.AreEqual(0, exitCode);
            using var result = JsonDocument.Parse(standardOutput.ToString());
            Assert.AreEqual(outputPath, result.RootElement.GetProperty("outputPath").GetString());
            Assert.AreEqual(
                $"# Live Transcript{Environment.NewLine}{Environment.NewLine}",
                await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            await application.RunAsync(["stop"], new StringWriter(), new StringWriter());
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task AppendPreservesExistingBytesAndStartsNewTranscriptSection()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var existingBytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x23, 0x20, 0x50, 0x72, 0x69, 0x6F, 0x72 };
        await File.WriteAllBytesAsync(outputPath, existingBytes);
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(
            stateStore,
            new ControlledAudioCaptureFactory(),
            new ControlledSpeechRecognizerFactory());
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            var exitCode = await application.RunAsync(
                ["start", outputPath, "--append"],
                new StringWriter(),
                new StringWriter());

            Assert.AreEqual(0, exitCode);
            var actualBytes = await File.ReadAllBytesAsync(outputPath);
            var expectedSuffix = System.Text.Encoding.UTF8.GetBytes(
                $"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}" +
                $"# Live Transcript{Environment.NewLine}{Environment.NewLine}");
            CollectionAssert.AreEqual(existingBytes.Concat(expectedSuffix).ToArray(), actualBytes);
        }
        finally
        {
            await application.RunAsync(["stop"], new StringWriter(), new StringWriter());
            File.Delete(outputPath);
        }
    }

    [TestMethod]
    public async Task DeletedDestinationStopsCaptureAndRetainsTerminalWriteFailure()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var captures = new ControlledAudioCaptureFactory();
        var recognizers = new ControlledSpeechRecognizerFactory();
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(stateStore, captures, recognizers);
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["start", outputPath],
                    new StringWriter(),
                    new StringWriter()));

            File.Delete(outputPath);
            recognizers.You.EmitFinalized("This cannot be persisted.");

            var status = await WaitForStateAsync(application, "failed");
            Assert.AreEqual(
                "transcript-write-failed",
                status.GetProperty("error").GetProperty("code").GetString());
            Assert.IsFalse(File.Exists(outputPath));
            Assert.IsTrue(captures.Microphone.IsStopped);
            Assert.IsTrue(captures.Playback.IsStopped);
            Assert.IsTrue(recognizers.You.IsStopped);
            Assert.IsTrue(recognizers.Meeting.IsStopped);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [TestMethod]
    public async Task FinalizedUtteranceIsUtf8AndVisibleToConcurrentReaderBeforeStop()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var recognizers = new ControlledSpeechRecognizerFactory();
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(
            stateStore,
            new ControlledAudioCaptureFactory(),
            recognizers);
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["start", outputPath],
                    new StringWriter(),
                    new StringWriter()));
            await using var concurrentReader = new FileStream(
                outputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            recognizers.Meeting.EmitFinalized("The café is open.");

            var expectedTranscript =
                $"# Live Transcript{Environment.NewLine}{Environment.NewLine}" +
                $"**Meeting:** The café is open.{Environment.NewLine}{Environment.NewLine}";
            using var visibleBytes = new MemoryStream();
            await concurrentReader.CopyToAsync(visibleBytes);
            CollectionAssert.AreEqual(
                System.Text.Encoding.UTF8.GetBytes(expectedTranscript),
                visibleBytes.ToArray());
            var status = await WaitForStateAsync(application, "running");
            Assert.AreEqual("session-1", status.GetProperty("sessionId").GetString());
        }
        finally
        {
            await application.RunAsync(["stop"], new StringWriter(), new StringWriter());
            File.Delete(outputPath);
        }
    }

    [TestMethod]
    public async Task LossOfWriteAccessStopsCaptureAndRetainsTerminalWriteFailure()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.md");
        var discovery = new ControlledAudioDeviceDiscovery(
            [new AudioDevice("microphone-1", "Microphone", true)],
            [new AudioDevice("playback-1", "Playback", true)]);
        var captures = new ControlledAudioCaptureFactory();
        var recognizers = new ControlledSpeechRecognizerFactory();
        var stateStore = new ControlledSessionStateStore();
        var worker = new ControlledDetachedWorker(stateStore, captures, recognizers);
        var application = new CliApplication(
            discovery,
            new DetachedLiveSessionController(
                stateStore,
                worker,
                TimeProvider.System,
                () => "session-1"));

        try
        {
            Assert.AreEqual(
                0,
                await application.RunAsync(
                    ["start", outputPath],
                    new StringWriter(),
                    new StringWriter()));
            await using var writeBlocker = new FileStream(
                outputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            recognizers.Meeting.EmitFinalized("This write is blocked.");

            var status = await WaitForStateAsync(application, "failed");
            Assert.AreEqual(
                "transcript-write-failed",
                status.GetProperty("error").GetProperty("code").GetString());
            Assert.IsTrue(captures.Microphone.IsStopped);
            Assert.IsTrue(captures.Playback.IsStopped);
            Assert.IsTrue(recognizers.You.IsStopped);
            Assert.IsTrue(recognizers.Meeting.IsStopped);
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

        public bool IsStopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            IsStopped = true;
            return Task.CompletedTask;
        }

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
        private TaskCompletionSource recovery = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<FinalizedRecognition>? Finalized;

        public event Action? RecoverableInterruption;

        public bool IsStopped { get; private set; }

        public event Action<ReadOnlyMemory<byte>>? AudioWritten;

        public event Action? ReplayCompleting;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void WriteAudio(ReadOnlyMemory<byte> audio)
        {
            AudioWritten?.Invoke(audio);
        }

        public Task RecoverAsync(CancellationToken cancellationToken) =>
            recovery.Task.WaitAsync(cancellationToken);

        public Task CompleteReplayAsync(CancellationToken cancellationToken)
        {
            ReplayCompleting?.Invoke();
            return Task.CompletedTask;
        }

        public void Interrupt() => RecoverableInterruption?.Invoke();

        public void CompleteRecovery()
        {
            recovery.TrySetResult();
            recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void EmitFinalized(string text) => EmitFinalized(text, TimeSpan.Zero);

        public void EmitFinalized(string text, TimeSpan audioOffset) =>
            Finalized?.Invoke(new FinalizedRecognition(text, audioOffset));

        public Task StopAsync(CancellationToken cancellationToken)
        {
            IsStopped = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<JsonElement> WaitForStateAsync(
        CliApplication application,
        string expectedState)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var standardOutput = new StringWriter();
            await application.RunAsync(["status"], standardOutput, new StringWriter());
            using var result = JsonDocument.Parse(standardOutput.ToString());
            var root = result.RootElement;

            if (root.TryGetProperty("state", out var state) && state.GetString() == expectedState)
            {
                return root.Clone();
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Session did not reach the '{expectedState}' state.");
        return default;
    }

    private static async Task<JsonElement> WaitForSourceStateAsync(
        CliApplication application,
        string source,
        string expectedState,
        bool? speechLost = null)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var standardOutput = new StringWriter();
            await application.RunAsync(["status"], standardOutput, new StringWriter());
            using var result = JsonDocument.Parse(standardOutput.ToString());
            var root = result.RootElement;

            if (root.TryGetProperty("sources", out var sources) &&
                sources.GetProperty(source).GetProperty("state").GetString() == expectedState &&
                (speechLost is null ||
                    sources.GetProperty(source).GetProperty("speechLost").GetBoolean() == speechLost))
            {
                return root.Clone();
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Source '{source}' did not reach the '{expectedState}' state.");
        return default;
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
        ISpeechRecognizerFactory recognizers,
        Func<CancellationToken, Task>? transcriptHoldback = null) : IDetachedWorkerControl
    {
        private InProcessLiveSessionController? session;
        private SessionStatePublisher? statePublisher;
        private Task? startup;
        private Task? terminalState;

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
            session = new InProcessLiveSessionController(
                captures,
                recognizers,
                () => launchedSessionId,
                transcriptHoldback ?? (_ => Task.CompletedTask));
            statePublisher = new SessionStatePublisher(stateStore, processId: 42);
            session.StatusChanged += status =>
                _ = statePublisher.PublishAsync(status, CancellationToken.None);
            startup = PublishReadyStateAsync(launchedSessionId, launchedRequest, cancellationToken);
            return Task.FromResult<IWorkerProcess>(new ControlledWorkerProcess(42));
        }

        public async Task SignalStopAsync(string stoppedSessionId, CancellationToken cancellationToken)
        {
            await startup!;
            var status = await session!.StopAsync(stoppedSessionId, cancellationToken);
            await statePublisher!.PublishAsync(status!, cancellationToken);
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
                terminalState = PublishTerminalStateAsync(session, cancellationToken);
            }
            catch (Exception exception)
            {
                session = null;
                await stateStore.WriteAsync(
                    SessionStateDocument.Failed(
                        launchedSessionId,
                        launchedRequest,
                        exception.Message,
                        exception is LiveSessionException sessionException
                            ? sessionException.Code
                            : "session-start-failed"),
                    cancellationToken);
            }
        }

        private async Task PublishTerminalStateAsync(
            InProcessLiveSessionController activeSession,
            CancellationToken cancellationToken)
        {
            var status = await activeSession.WaitForTerminalStatusAsync(cancellationToken);
            session = null;
            await statePublisher!.PublishAsync(status, cancellationToken);
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

    private sealed class ControlledTranscriptHoldback
    {
        private readonly Queue<TaskCompletionSource> pending = new();

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            var release = new TaskCompletionSource();
            pending.Enqueue(release);
            return release.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseNext() => pending.Dequeue().TrySetResult();
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