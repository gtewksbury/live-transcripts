using System.Text.Json;

namespace LiveTranscripts.Tests;

[TestClass]
public sealed class DevicesCommandTests
{
    [TestMethod]
    public async Task DevicesReturnsMicrophonesAndPlaybackDevicesAsJson()
    {
        var discovery = new ControlledAudioDeviceDiscovery(
            microphones:
            [
                new AudioDevice("microphone-1", "Desk microphone", true),
                new AudioDevice("microphone-2", "Webcam microphone", false),
            ],
            playback:
            [
                new AudioDevice("playback-1", "Headset", true),
            ]);
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(discovery);

        var exitCode = await application.RunAsync(
            ["devices"],
            standardOutput,
            standardError);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(string.Empty, standardError.ToString());

        using var result = JsonDocument.Parse(standardOutput.ToString());
        Assert.IsTrue(result.RootElement.GetProperty("success").GetBoolean());
        AssertDevices(
            result.RootElement.GetProperty("microphones"),
            ("microphone-1", "Desk microphone", true),
            ("microphone-2", "Webcam microphone", false));
        AssertDevices(
            result.RootElement.GetProperty("playback"),
            ("playback-1", "Headset", true));
    }

    [TestMethod]
    public async Task DevicesReportsDiscoveryFailureAsJsonAndStandardError()
    {
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(new FailingAudioDeviceDiscovery());

        var exitCode = await application.RunAsync(
            ["devices"],
            standardOutput,
            standardError);

        Assert.AreEqual(2, exitCode);
        Assert.AreEqual(
            "{\"success\":false,\"error\":{\"code\":\"device-discovery-failed\",\"message\":\"Unable to enumerate Windows audio devices.\"}}",
            standardOutput.ToString().TrimEnd());
        StringAssert.Contains(standardError.ToString(), "Audio device discovery failed: access denied");
    }

    [TestMethod]
    public async Task UnknownCommandReportsArgumentFailureWithoutDiscoveringDevices()
    {
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(new UnexpectedAudioDeviceDiscovery());

        var exitCode = await application.RunAsync(
            ["unknown"],
            standardOutput,
            standardError);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(
            "{\"success\":false,\"error\":{\"code\":\"invalid-arguments\",\"message\":\"Expected a devices, start, status, or stop command.\"}}",
            standardOutput.ToString().TrimEnd());
        Assert.AreEqual(
            $"Usage: LiveTranscripts <devices|start|status|stop>{Environment.NewLine}",
            standardError.ToString());
    }

    [TestMethod]
    public async Task DevicesReturnsEmptyCollectionsWhenNoEndpointsAreAvailable()
    {
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(
            new ControlledAudioDeviceDiscovery([], []));

        var exitCode = await application.RunAsync(
            ["devices"],
            standardOutput,
            standardError);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(
            $"{{\"success\":true,\"microphones\":[],\"playback\":[]}}{Environment.NewLine}",
            standardOutput.ToString());
        Assert.AreEqual(string.Empty, standardError.ToString());
    }

    private static void AssertDevices(
        JsonElement actual,
        params (string Id, string Name, bool IsDefaultCommunications)[] expected)
    {
        Assert.AreEqual(expected.Length, actual.GetArrayLength());

        for (var index = 0; index < expected.Length; index++)
        {
            var device = actual[index];
            Assert.AreEqual(expected[index].Id, device.GetProperty("id").GetString());
            Assert.AreEqual(expected[index].Name, device.GetProperty("name").GetString());
            Assert.AreEqual(
                expected[index].IsDefaultCommunications,
                device.GetProperty("isDefaultCommunications").GetBoolean());
        }
    }

    private sealed class ControlledAudioDeviceDiscovery(
        IReadOnlyList<AudioDevice> microphones,
        IReadOnlyList<AudioDevice> playback) : IAudioDeviceDiscovery
    {
        public AudioDeviceInventory Discover() => new(microphones, playback);
    }

    private sealed class FailingAudioDeviceDiscovery : IAudioDeviceDiscovery
    {
        public AudioDeviceInventory Discover() => throw new InvalidOperationException("access denied");
    }

    private sealed class UnexpectedAudioDeviceDiscovery : IAudioDeviceDiscovery
    {
        public AudioDeviceInventory Discover() => throw new AssertFailedException("Device discovery was not expected.");
    }
}