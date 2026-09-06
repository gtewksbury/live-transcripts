namespace LiveTranscripts;

internal sealed record StartSessionRequest(
    string OutputPath,
    string MicrophoneId,
    string PlaybackId);

internal sealed record LiveSessionStatus(
    string SessionId,
    string State,
    string OutputPath,
    string MicrophoneId,
    string PlaybackId);

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