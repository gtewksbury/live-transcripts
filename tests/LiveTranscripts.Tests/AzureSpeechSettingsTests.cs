namespace LiveTranscripts.Tests;

[TestClass]
public sealed class AzureSpeechSettingsTests
{
    [TestMethod]
    public async Task LoadsKeyAndRegionFromAppSettings()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"appsettings-{Guid.NewGuid():N}.json");

        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                """
                {
                  "AzureSpeech": {
                    "Key": "speech-key",
                    "Region": "eastus"
                  }
                }
                """);

            var settings = AzureSpeechSettings.Load(settingsPath);

            Assert.AreEqual("speech-key", settings.Key);
            Assert.AreEqual("eastus", settings.Region);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [TestMethod]
    public async Task MissingKeyFailsWithoutExposingOtherConfiguration()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"appsettings-{Guid.NewGuid():N}.json");

        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                """
                {
                  "AzureSpeech": {
                    "Key": "",
                    "Region": "eastus"
                  }
                }
                """);

            var exception = Assert.ThrowsExactly<LiveSessionException>(
                () => AzureSpeechSettings.Load(settingsPath));

            Assert.AreEqual("speech-configuration-missing", exception.Code);
            Assert.AreEqual("AzureSpeech:Key is required in appsettings.json.", exception.Message);
            Assert.DoesNotContain("eastus", exception.Message);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [TestMethod]
    public async Task EndpointUrlInRegionFailsWithActionableMessage()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"appsettings-{Guid.NewGuid():N}.json");

        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                """
                {
                  "AzureSpeech": {
                    "Key": "speech-key",
                    "Region": "https://example.cognitiveservices.azure.com/"
                  }
                }
                """);

            var exception = Assert.ThrowsExactly<LiveSessionException>(
                () => AzureSpeechSettings.Load(settingsPath));

            Assert.AreEqual("speech-configuration-invalid", exception.Code);
            Assert.AreEqual(
                "AzureSpeech:Region must be an Azure region name such as 'eastus', not an endpoint URL.",
                exception.Message);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }
}