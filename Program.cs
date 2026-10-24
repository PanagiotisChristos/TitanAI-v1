using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("ask", limiterOptions =>
    {
        limiterOptions.PermitLimit = 10;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
        limiterOptions.AutoReplenishment = true;
    });
    options.OnRejected = (context, cancellationToken) =>
    {
        var problem = new ProblemDetails
        {
            Title = "Too many questions",
            Detail = "Please wait a minute before asking more questions.",
            Status = StatusCodes.Status429TooManyRequests
        };

        return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(problem, cancellationToken));
    };
});
builder.Services.AddHttpClient("Groq", client =>
{
    client.BaseAddress = new Uri("https://api.groq.com/openai/v1/");
    client.Timeout = TimeSpan.FromSeconds(90);
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.MapOpenApi();
app.MapGet("/api/status", () => TypedResults.Ok(
    new StatusResponse(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GROQ_API_KEY")))))
    .WithSummary("Check whether the assistant is configured.");

app.MapPost("/api/ask", async Task<Results<Ok<AskResponse>, BadRequest<ApiErrorResponse>, ProblemHttpResult>> (
    AskRequest request,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var question = request.Question?.Trim();
    if (string.IsNullOrWhiteSpace(question))
    {
        return TypedResults.BadRequest(new ApiErrorResponse("Enter a question first."));
    }

    if (question.Length > 1000)
    {
        return TypedResults.BadRequest(new ApiErrorResponse("Questions must be 1,000 characters or fewer."));
    }

    var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return TypedResults.Problem(
            title: "Assistant is not configured",
            detail: "Set GROQ_API_KEY in the environment and restart the app.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var httpClient = httpClientFactory.CreateClient("Groq");
    var model = Environment.GetEnvironmentVariable("GROQ_MODEL") ?? "openai/gpt-oss-20b";
    using var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    message.Content = JsonContent.Create(new
    {
        model,
        messages = new[]
        {
            new
            {
                role = "user",
                content = $"Answer the user's question clearly and concisely. If you are unsure or the question depends on current information, say so.\n\nQuestion: {question}"
            }
        }
    });

    try
    {
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string? serviceError = null;
            try
            {
                var errorPayload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
                serviceError = errorPayload?["error"]?["message"]?.GetValue<string>();
            }
            catch (System.Text.Json.JsonException)
            {
                // The service may return a non-JSON error response.
            }

            var detail = string.IsNullOrWhiteSpace(serviceError)
                ? $"Groq returned HTTP {(int)response.StatusCode}. Check your API key, model, and available usage, then try again."
                : $"Groq returned HTTP {(int)response.StatusCode}: {serviceError}";
            return TypedResults.Problem(
                title: response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? "Groq rate limit reached"
                    : "The AI service could not answer",
                detail: detail,
                statusCode: StatusCodes.Status502BadGateway);
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
        var answer = payload?["choices"]?.AsArray()
            .FirstOrDefault()?["message"]?["content"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(answer))
        {
            return TypedResults.Problem(
                title: "No answer was returned",
                detail: "Try asking the question another way.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        return TypedResults.Ok(new AskResponse(answer.Trim()));
    }
    catch (HttpRequestException)
    {
        return TypedResults.Problem(
            title: "Could not reach the AI service",
            detail: "Check your internet connection and try again.",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return TypedResults.Problem(
            title: "The AI service took too long to respond",
            detail: "Try again in a moment.",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
})
    .WithName("AskWithGroq")
    .WithSummary("Answer a question using a Groq-hosted AI model.")
    .RequireRateLimiting("ask");

app.Run();

/// <summary>A question submitted to the assistant.</summary>
public sealed record AskRequest(string? Question);

/// <summary>An answer returned by the assistant.</summary>
public sealed record AskResponse(string Answer);

/// <summary>Assistant configuration status.</summary>
public sealed record StatusResponse(bool Configured);

/// <summary>A user-facing API error.</summary>
public sealed record ApiErrorResponse(string Message);
