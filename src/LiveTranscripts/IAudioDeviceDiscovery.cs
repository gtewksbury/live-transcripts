namespace LiveTranscripts;

internal interface IAudioDeviceDiscovery
{
    AudioDeviceInventory Discover();
}