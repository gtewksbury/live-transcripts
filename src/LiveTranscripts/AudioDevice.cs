namespace LiveTranscripts;

internal sealed record AudioDevice(
    string Id,
    string Name,
    bool IsDefaultCommunications);

internal sealed record AudioDeviceInventory(
    IReadOnlyList<AudioDevice> Microphones,
    IReadOnlyList<AudioDevice> Playback);