using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace LiveTranscripts;

internal sealed class DetachedLiveSessionController : ILiveSessionController
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ISessionStateStore stateStore;
    private readonly IDetachedWorkerControl workerControl;
    private readonly TimeProvider timeProvider;
    private readonly Func<string> sessionIdFactory;

    public DetachedLiveSessionController()
        : this(
            new SessionStateStore(),
            new WindowsDetachedWorkerControl(),
            TimeProvider.System,
            () => Guid.NewGuid().ToString("N"))
    {
    }

    internal DetachedLiveSessionController(
        ISessionStateStore stateStore,
        IDetachedWorkerControl workerControl,
        TimeProvider timeProvider,
        Func<string> sessionIdFactory)
    {
        this.stateStore = stateStore;
        this.workerControl = workerControl;
        this.timeProvider = timeProvider;
        this.sessionIdFactory = sessionIdFactory;
    }

    public async Task<LiveSessionStatus> StartAsync(
        StartSessionRequest request,
        CancellationToken cancellationToken)
    {
        await using var commandLock = await workerControl.AcquireCommandLockAsync(
            TimeSpan.FromSeconds(5),
            cancellationToken);

        if (workerControl.IsActive)
        {
            throw new LiveSessionException(
                "session-already-active",
                "A live transcription session is already active.");
        }

        var sessionId = sessionIdFactory();
        await stateStore.WriteAsync(
            SessionStateDocument.Starting(sessionId, request),
            cancellationToken);

        using var worker = await workerControl.LaunchAsync(
            sessionId,
            request,
            cancellationToken);
        var startedAt = timeProvider.GetTimestamp();

        while (timeProvider.GetElapsedTime(startedAt) < StartupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await stateStore.ReadAsync(cancellationToken);

            if (state?.SessionId == sessionId && state.State == "running")
            {
                return state.ToStatus();
            }

            if (state?.SessionId == sessionId && state.State == "failed")
            {
                throw new LiveSessionException(
                    "session-start-failed",
                    state.Error ?? "The live transcription worker failed during startup.");
            }

            if (worker.HasExited)
            {
                throw new LiveSessionException(
                    "worker-start-failed",
                    "The live transcription worker exited before becoming ready.");
            }

            await Task.Delay(PollInterval, timeProvider, cancellationToken);
        }

        worker.Kill();
        throw new LiveSessionException(
            "session-start-timeout",
            "The live transcription worker did not become ready within 15 seconds.");
    }

    public async Task<LiveSessionStatus?> GetStatusAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var state = await stateStore.ReadAsync(cancellationToken);
        return Matches(state, sessionId) ? state!.ToStatus() : null;
    }

    public async Task<LiveSessionStatus?> StopAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var state = await stateStore.ReadAsync(cancellationToken);

        if (!Matches(state, sessionId) || state!.State != "running")
        {
            return null;
        }

        await workerControl.SignalStopAsync(state.SessionId, cancellationToken);

        if (!await workerControl.WaitForExitAsync(
            state.ProcessId,
            StopTimeout,
            cancellationToken))
        {
            throw new LiveSessionException(
                "session-stop-timeout",
                "The live transcription worker did not stop within five seconds.");
        }

        var finalState = await stateStore.ReadAsync(cancellationToken);
        return Matches(finalState, state.SessionId) ? finalState!.ToStatus() : null;
    }

    private static bool Matches(SessionStateDocument? state, string? sessionId) =>
        state is not null &&
        (sessionId is null || string.Equals(sessionId, state.SessionId, StringComparison.Ordinal));
}

internal interface ISessionStateStore
{
    Task WriteAsync(SessionStateDocument state, CancellationToken cancellationToken);

    Task<SessionStateDocument?> ReadAsync(CancellationToken cancellationToken);
}

internal interface IWorkerProcess : IDisposable
{
    int ProcessId { get; }

    bool HasExited { get; }

    void Kill();
}

internal interface IDetachedWorkerControl
{
    bool IsActive { get; }

    ValueTask<IAsyncDisposable> AcquireCommandLockAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<IWorkerProcess> LaunchAsync(
        string sessionId,
        StartSessionRequest request,
        CancellationToken cancellationToken);

    Task SignalStopAsync(string sessionId, CancellationToken cancellationToken);

    Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class WindowsDetachedWorkerControl : IDetachedWorkerControl
{
    public bool IsActive
    {
        get
        {
            try
            {
                using var activeLock = SessionProcessCoordination.TryAcquireActiveLock();
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }
    }

    public async ValueTask<IAsyncDisposable> AcquireCommandLockAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        await SessionProcessCoordination.AcquireCommandLockAsync(timeout, cancellationToken);

    public Task<IWorkerProcess> LaunchAsync(
        string sessionId,
        StartSessionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processPath = Environment.ProcessPath ??
            throw new LiveSessionException(
                "worker-start-failed",
                "The current executable path could not be resolved.");
        var startInfo = new ProcessStartInfo(processPath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        if (string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        }

        startInfo.ArgumentList.Add("worker");
        startInfo.ArgumentList.Add("--session");
        startInfo.ArgumentList.Add(sessionId);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(request.OutputPath);
        startInfo.ArgumentList.Add("--microphone");
        startInfo.ArgumentList.Add(request.MicrophoneId);
        startInfo.ArgumentList.Add("--playback");
        startInfo.ArgumentList.Add(request.PlaybackId);
        var process = Process.Start(startInfo) ??
            throw new LiveSessionException(
                "worker-start-failed",
                "The live transcription worker could not be started.");
        return Task.FromResult<IWorkerProcess>(new WorkerProcess(process));
    }

    public Task SignalStopAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var stopEvent = EventWaitHandle.OpenExisting(
                SessionProcessCoordination.StopEvent(sessionId));
            stopEvent.Set();
            return Task.CompletedTask;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            throw new LiveSessionException(
                "session-worker-missing",
                "The live transcription worker is no longer available.");
        }
    }

    public async Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutCancellation.Token);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private sealed class WorkerProcess(Process process) : IWorkerProcess
    {
        public int ProcessId => process.Id;

        public bool HasExited => process.HasExited;

        public void Kill() => process.Kill(entireProcessTree: true);

        public void Dispose() => process.Dispose();
    }
}

internal static class ProductionSessionWorker
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        if (!TryParse(arguments, out var sessionId, out var request))
        {
            return 1;
        }

        var stateStore = new SessionStateStore();
        FileStream activeLock;

        try
        {
            activeLock = SessionProcessCoordination.TryAcquireActiveLock();
        }
        catch (IOException)
        {
            await stateStore.WriteAsync(
                SessionStateDocument.Failed(sessionId, request, "A live transcription session is already active."),
                CancellationToken.None);
            return 2;
        }

        await using (activeLock)
        {
        using var stopEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            SessionProcessCoordination.StopEvent(sessionId));
        InProcessLiveSessionController? controller = null;

        try
        {
            controller = new InProcessLiveSessionController(
                new WindowsAudioCaptureFactory(),
                new AzureSpeechRecognizerFactory(),
                () => sessionId);
            var status = await controller.StartAsync(request, CancellationToken.None);
            await stateStore.WriteAsync(
                SessionStateDocument.FromStatus(status, Environment.ProcessId),
                CancellationToken.None);

            stopEvent.WaitOne();
            status = await controller.StopAsync(sessionId, CancellationToken.None) ?? status;
            await stateStore.WriteAsync(
                SessionStateDocument.FromStatus(status, Environment.ProcessId),
                CancellationToken.None);
            return 0;
        }
        catch (Exception exception)
        {
            if (controller is not null)
            {
                try
                {
                    await controller.StopAsync(sessionId, CancellationToken.None);
                }
                catch
                {
                }
            }

            await stateStore.WriteAsync(
                SessionStateDocument.Failed(sessionId, request, exception.Message),
                CancellationToken.None);
            return 3;
        }
        }
    }

    private static bool TryParse(
        string[] arguments,
        out string sessionId,
        out StartSessionRequest request)
    {
        sessionId = string.Empty;
        request = null!;

        if (arguments is not
            ["worker", "--session", var parsedSessionId, "--output", var outputPath,
             "--microphone", var microphoneId, "--playback", var playbackId])
        {
            return false;
        }

        sessionId = parsedSessionId;
        request = new StartSessionRequest(outputPath, microphoneId, playbackId);
        return true;
    }
}

internal sealed record SessionStateDocument(
    string SessionId,
    string State,
    string OutputPath,
    string MicrophoneId,
    string PlaybackId,
    int ProcessId,
    string? Error)
{
    public static SessionStateDocument Starting(string sessionId, StartSessionRequest request) =>
        new(sessionId, "starting", request.OutputPath, request.MicrophoneId, request.PlaybackId, 0, null);

    public static SessionStateDocument FromStatus(LiveSessionStatus status, int processId) =>
        new(
            status.SessionId,
            status.State,
            status.OutputPath,
            status.MicrophoneId,
            status.PlaybackId,
            processId,
            null);

    public static SessionStateDocument Failed(
        string sessionId,
        StartSessionRequest request,
        string error) => new(
            sessionId,
            "failed",
            request.OutputPath,
            request.MicrophoneId,
            request.PlaybackId,
            Environment.ProcessId,
            error);

    public LiveSessionStatus ToStatus() =>
        new(SessionId, State, OutputPath, MicrophoneId, PlaybackId);
}

internal sealed class SessionStateStore : ISessionStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string statePath;

    public SessionStateStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveTranscripts");
        Directory.CreateDirectory(directory);
        statePath = Path.Combine(directory, "session.json");
    }

    public async Task WriteAsync(SessionStateDocument state, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{statePath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(state, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        File.Move(temporaryPath, statePath, overwrite: true);
    }

    public async Task<SessionStateDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath))
        {
            return null;
        }

        await using var stream = new FileStream(
            statePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<SessionStateDocument>(
            stream,
            JsonOptions,
            cancellationToken);
    }
}

internal static class SessionProcessCoordination
{
    private static readonly string UserHash = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(
            WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName)))[..16];
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveTranscripts");

    public static async Task<FileStream> AcquireCommandLockAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DirectoryPath);
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < timeout)
        {
            try
            {
                return OpenExclusive("command.lock");
            }
            catch (IOException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }

        throw new LiveSessionException(
            "session-control-busy",
            "Another live transcription command is in progress.");
    }

    public static FileStream TryAcquireActiveLock()
    {
        Directory.CreateDirectory(DirectoryPath);
        return OpenExclusive("active.lock");
    }

    public static string StopEvent(string sessionId) =>
        $"Local\\LiveTranscripts.Stop.{UserHash}.{sessionId}";

    private static FileStream OpenExclusive(string fileName) => new(
        Path.Combine(DirectoryPath, fileName),
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None);
}