using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace LiveTranscripts;

internal sealed class WindowsAudioDeviceDiscovery : IAudioDeviceDiscovery
{
    public AudioDeviceInventory Discover()
    {
        using var enumerator = new MMDeviceEnumerator();

        return new AudioDeviceInventory(
            DiscoverEndpoints(enumerator, DataFlow.Capture),
            DiscoverEndpoints(enumerator, DataFlow.Render));
    }

    private static IReadOnlyList<AudioDevice> DiscoverEndpoints(
        MMDeviceEnumerator enumerator,
        DataFlow dataFlow)
    {
        var defaultEndpointId = GetCommunicationsDefaultId(enumerator, dataFlow);
        var devices = new List<AudioDevice>();

        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active))
        {
            using (endpoint)
            {
                devices.Add(new AudioDevice(
                    endpoint.ID,
                    endpoint.FriendlyName,
                    endpoint.ID == defaultEndpointId));
            }
        }

        return devices;
    }

    private static string? GetCommunicationsDefaultId(
        MMDeviceEnumerator enumerator,
        DataFlow dataFlow)
    {
        try
        {
            using var endpoint = enumerator.GetDefaultAudioEndpoint(dataFlow, Role.Communications);
            return endpoint.ID;
        }
        catch (COMException)
        {
            return null;
        }
    }
}