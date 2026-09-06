namespace LiveTranscripts;

internal sealed record StartSessionRequest(
    string OutputPath,
    string MicrophoneId,
    string PlaybackId,
    bool Append = false);

internal sealed record LiveSessionStatus(
    string SessionId,
    string State,
    string OutputPath,
    string MicrophoneId,
    string PlaybackId,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string YouRecognitionState = "running",
    string MeetingRecognitionState = "running",
    bool YouSpeechLost = false,
    bool MeetingSpeechLost = false,
    string? StopReason = null,
    DateTimeOffset? StartedAtUtc = null,
    TimeSpan ElapsedDuration = default);

internal static class LiveSessionStopReasons
{
    public const string Requested = "requested";
    public const string StartupFailure = "startup-failure";
    public const string StopTimeout = "stop-timeout";
    public const string DurationLimit = "duration-limit";
    public const string DeviceFailure = "device-failure";
    public const string AzureFailure = "azure-failure";
    public const string WriteFailure = "write-failure";
    public const string WorkerFailure = "worker-failure";
}

internal interface ILiveSessionController
{
    Task<LiveSessionStatus> StartAsync(
        StartSessionRequest request,
        CancellationToken cancellationToken);

    Task<LiveSessionStatus?> GetStatusAsync(
        string? sessionId,
        CancellationToken cancellationToken);

    Task<LiveSessionStatus?> StopAsync(
        string? sessionId,
        CancellationToken cancellationToken);
}

internal sealed class LiveSessionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed class UnavailableLiveSessionController : ILiveSessionController
{
    public Task<LiveSessionStatus> StartAsync(
        StartSessionRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException(
            "Live session control is not configured.");

    public Task<LiveSessionStatus?> GetStatusAsync(
        string? sessionId,
        CancellationToken cancellationToken) => throw new NotSupportedException(
            "Live session control is not configured.");

    public Task<LiveSessionStatus?> StopAsync(
        string? sessionId,
        CancellationToken cancellationToken) => throw new NotSupportedException(
            "Live session control is not configured.");
}