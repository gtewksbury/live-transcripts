using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveTranscripts.Tests;

[TestClass]
public sealed class ApiEndpointTests
{
    [TestMethod]
    public async Task HealthReportsThatTheApiIsAvailable()
    {
        var runner = new ControlledLiveTranscriptsCommandRunner(
            new LiveTranscriptsCommandResult(0, "{\"success\":true}", string.Empty));
        await using var application = CreateApplication(runner);
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(
            "{\"status\":\"healthy\"}",
            await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task DevicesReturnsSuccessfulCliJson()
    {
        const string cliJson = "{\"success\":true,\"microphones\":[],\"playback\":[]}";
        var runner = new ControlledLiveTranscriptsCommandRunner(
            new LiveTranscriptsCommandResult(0, cliJson, string.Empty));
        await using var application = CreateApplication(runner);
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/devices");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(cliJson, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "devices" }, runner.SingleInvocation.ToArray());
    }

    [TestMethod]
    public async Task DevicesReturnsBadRequestForCliFailure()
    {
        const string cliJson =
            "{\"success\":false,\"error\":{\"code\":\"device-discovery-failed\",\"message\":\"Unavailable.\"}}";
        var runner = new ControlledLiveTranscriptsCommandRunner(
            new LiveTranscriptsCommandResult(2, cliJson, "diagnostic"));
        await using var application = CreateApplication(runner);
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/devices");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(cliJson, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task DevicesReturnsServerErrorForInvalidCliOutput()
    {
        var runner = new ControlledLiveTranscriptsCommandRunner(
            new LiveTranscriptsCommandResult(0, "not-json", "diagnostic"));
        await using var application = CreateApplication(runner);
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/devices");

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.IsFalse(body.RootElement.GetProperty("success").GetBoolean());
        Assert.AreEqual(
            "invalid-cli-output",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task DevicesReturnsServerErrorWhenCliCannotBeInvoked()
    {
        var runner = new ControlledLiveTranscriptsCommandRunner(
            new InvalidOperationException("sensitive process detail"));
        await using var application = CreateApplication(runner);
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/devices");

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        var responseJson = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sensitive process detail", responseJson);
        using var body = JsonDocument.Parse(responseJson);
        Assert.AreEqual(
            "cli-invocation-failed",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task StartInvokesCliWithAbsoluteTranscriptPath()
    {
        const string cliJson = "{\"success\":true,\"sessionId\":\"session-1\",\"state\":\"running\"}";
        var transcriptPath = Path.Combine(Path.GetTempPath(), "meeting.md");
        var runner = new ControlledLiveTranscriptsCommandRunner(
            new LiveTranscriptsCommandResult(0, cliJson, string.Empty));
        await using var application = CreateApplication(runner);
        using var client = application.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/start",
            new { transcriptPath });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(cliJson, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(
            new[] { "start", transcriptPath },
            runner.SingleInvocation.ToArray());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("meeting.md")]
    public async Task StartRejectsInvalidTranscriptPath(string transcriptPath)
    {
        var runner = new ControlledLiveTranscriptsCommandRunner(
            new LiveTranscriptsCommandResult(0, "{}", string.Empty));
        await using var application = CreateApplication(runner);
        using var client = application.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/start",
            new { transcriptPath });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(
            "invalid-transcript-path",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.AreEqual(0, runner.InvocationCount);
    }

    [TestMethod]
    public async Task StatusInvokesCliStatusCommand()
    {
        const string cliJson = "{\"success\":true,\"sessionId\":\"session-1\",\"state\":\"running\"}";
        var runner = new ControlledLiveTranscriptsCommandRunner(
            new LiveTranscriptsCommandResult(0, cliJson, string.Empty));
        await using var application = CreateApplication(runner);
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/status");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(cliJson, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "status" }, runner.SingleInvocation.ToArray());
    }

    [TestMethod]
    public async Task StopInvokesCliStopCommand()
    {
        const string cliJson = "{\"success\":true,\"sessionId\":\"session-1\",\"state\":\"stopped\"}";
        var runner = new ControlledLiveTranscriptsCommandRunner(
            new LiveTranscriptsCommandResult(0, cliJson, string.Empty));
        await using var application = CreateApplication(runner);
        using var client = application.CreateClient();

        using var response = await client.PostAsync("/stop", null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(cliJson, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "stop" }, runner.SingleInvocation.ToArray());
    }

    [TestMethod]
    public async Task GracefulShutdownInvokesCliStopCommand()
    {
        var runner = new ControlledLiveTranscriptsCommandRunner(
            new LiveTranscriptsCommandResult(0, "{\"success\":true}", string.Empty));
        var application = CreateApplication(runner);
        using var client = application.CreateClient();
        using var response = await client.GetAsync("/health");

        await application.DisposeAsync();

        Assert.AreEqual(1, runner.InvocationCount);
        CollectionAssert.AreEqual(new[] { "stop" }, runner.SingleInvocation.ToArray());
    }

    [TestMethod]
    public async Task MissingCliExecutableReturnsSanitizedServerError()
    {
        var missingExecutable = Path.Combine(
            Path.GetTempPath(),
            $"missing-live-transcripts-{Guid.NewGuid():N}.exe");
        await using var application = new WebApplicationFactory<ApiProgram>()
            .WithWebHostBuilder(builder => builder.UseSetting(
                "LiveTranscripts:ExecutablePath",
                missingExecutable));
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/devices");

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        var responseJson = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(missingExecutable, responseJson);
        using var body = JsonDocument.Parse(responseJson);
        Assert.AreEqual(
            "cli-invocation-failed",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static WebApplicationFactory<ApiProgram> CreateApplication(
        ILiveTranscriptsCommandRunner runner) =>
        new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILiveTranscriptsCommandRunner>();
                services.AddSingleton(runner);
            }));

    private sealed class ControlledLiveTranscriptsCommandRunner : ILiveTranscriptsCommandRunner
    {
        private readonly List<IReadOnlyList<string>> invocations = [];
        private readonly LiveTranscriptsCommandResult? result;
        private readonly Exception? exception;

        public ControlledLiveTranscriptsCommandRunner(LiveTranscriptsCommandResult result)
        {
            this.result = result;
        }

        public ControlledLiveTranscriptsCommandRunner(Exception exception)
        {
            this.exception = exception;
        }

        public IReadOnlyList<string> SingleInvocation => invocations.Single();

        public int InvocationCount => invocations.Count;

        public Task<LiveTranscriptsCommandResult> RunAsync(IReadOnlyList<string> arguments)
        {
            invocations.Add(arguments);
            return exception is null
                ? Task.FromResult(result!)
                : Task.FromException<LiveTranscriptsCommandResult>(exception);
        }
    }
}