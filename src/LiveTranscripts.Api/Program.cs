using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<ILiveTranscriptsCommandRunner, ProcessLiveTranscriptsCommandRunner>();
builder.Services.AddHostedService<StopLiveTranscriptionOnShutdown>();
var app = builder.Build();

app.MapOpenApi();
app.MapGet("/health", () => TypedResults.Ok(new HealthResponse("healthy")))
	.WithName("GetHealth")
	.WithSummary("Check whether the local API is available.");
app.MapGet("/devices", (
	ILiveTranscriptsCommandRunner runner,
	ILogger<ApiProgram> logger) =>
	RunCommandAsync(runner, ["devices"], logger))
	.WithName("GetAudioDevices")
	.WithSummary("List audio devices through the live-transcripts CLI.");
app.MapPost("/start", (
	StartTranscriptionRequest request,
	ILiveTranscriptsCommandRunner runner,
	ILogger<ApiProgram> logger) =>
	StartAsync(request, runner, logger))
	.WithName("StartTranscription")
	.WithSummary("Start a live transcription session.");
app.MapGet("/status", (
	ILiveTranscriptsCommandRunner runner,
	ILogger<ApiProgram> logger) =>
	RunCommandAsync(runner, ["status"], logger))
	.WithName("GetTranscriptionStatus")
	.WithSummary("Get the current transcription session status.");
app.MapPost("/stop", (
	ILiveTranscriptsCommandRunner runner,
	ILogger<ApiProgram> logger) =>
	RunCommandAsync(runner, ["stop"], logger))
	.WithName("StopTranscription")
	.WithSummary("Stop the current transcription session.");

app.Run();

static async Task<IResult> StartAsync(
	StartTranscriptionRequest request,
	ILiveTranscriptsCommandRunner runner,
	ILogger logger)
{
	if (string.IsNullOrWhiteSpace(request.TranscriptPath) ||
		!Path.IsPathFullyQualified(request.TranscriptPath))
	{
		return Results.Json(
			new ApiFailureResponse(
				false,
				new ApiErrorResponse(
					"invalid-transcript-path",
					"TranscriptPath must be a nonempty absolute path.")),
			statusCode: StatusCodes.Status400BadRequest);
	}

	return await RunCommandAsync(runner, ["start", request.TranscriptPath], logger);
}

static async Task<IResult> RunCommandAsync(
	ILiveTranscriptsCommandRunner runner,
	IReadOnlyList<string> arguments,
	ILogger logger)
{
	LiveTranscriptsCommandResult result;

	try
	{
		result = await runner.RunAsync(arguments);
	}
	catch (Exception exception)
	{
		logger.LogError(exception, "Unable to invoke the live-transcripts CLI.");
		return Results.Json(
			new ApiFailureResponse(
				false,
				new ApiErrorResponse(
					"cli-invocation-failed",
					"The live-transcripts CLI could not be invoked.")),
			statusCode: StatusCodes.Status500InternalServerError);
	}

	try
	{
		using var document = JsonDocument.Parse(result.StandardOutput);

		if (document.RootElement.ValueKind != JsonValueKind.Object)
		{
			throw new JsonException("The CLI response must be a JSON object.");
		}
	}
	catch (JsonException)
	{
		return Results.Json(
			new ApiFailureResponse(
				false,
				new ApiErrorResponse(
					"invalid-cli-output",
					"The live-transcripts CLI returned an invalid response.")),
			statusCode: StatusCodes.Status500InternalServerError);
	}

	return Results.Text(
		result.StandardOutput,
		contentType: "application/json",
		statusCode: result.ExitCode == 0
		    ? StatusCodes.Status200OK
		    : StatusCodes.Status400BadRequest);
}

public partial class ApiProgram;

/// <summary>Describes whether the API is available.</summary>
public sealed record HealthResponse(string Status);

/// <summary>Describes an API operation that could not be completed.</summary>
public sealed record ApiFailureResponse(bool Success, ApiErrorResponse Error);

/// <summary>Describes the reason an API operation failed.</summary>
public sealed record ApiErrorResponse(string Code, string Message);

/// <summary>Describes a request to start live transcription.</summary>
public sealed record StartTranscriptionRequest
{
	public required string TranscriptPath { get; init; }
}