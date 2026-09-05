using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RelayLab.Core;

public static class LocalHosting
{
    public static void RequireLocal(IHostEnvironment environment)
    {
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            throw new InvalidOperationException("M1 requires the Development environment. Authenticated deployment is not implemented.");
    }

    public static void ConfigureWeb(WebApplicationBuilder builder)
    {
        RequireLocal(builder.Environment);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = EventContract.BodyLimit);
        builder.Services.AddProblemDetails();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.UnmappedMemberHandling = EventContract.Json.UnmappedMemberHandling;
            options.SerializerOptions.PropertyNameCaseInsensitive = false;
        });
        // Never log SQL parameter values, request payloads or destination URLs.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    }

    public static string Connection(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name) ?? throw new InvalidOperationException($"Configure ConnectionStrings:{name}.");

    public static void UseSafeErrors(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception error) when (error is SqlException or DbUpdateException or TimeoutException)
            {
                await Problem(503, "persistence_unavailable", "Persistence is unavailable. Retry with the same idempotency key.").ExecuteAsync(context);
            }
            catch (BadHttpRequestException error)
            {
                await Problem(error.StatusCode, error.StatusCode == 413 ? "body_too_large" : "invalid_request", "The request is invalid.").ExecuteAsync(context);
            }
            catch (Exception)
            {
                app.Logger.LogError("Unhandled request failure; details suppressed to protect configuration and payloads.");
                if (!context.Response.HasStarted)
                    await Problem(500, "internal_error", "The request could not be completed.").ExecuteAsync(context);
                else context.Abort();
            }
        });
    }

    public static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });

    public static async Task<(EventRequest? Value, IResult? Error)> ReadEventAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType())
            return (null, Problem(415, "unsupported_media_type", "Use application/json."));
        if (request.ContentLength > EventContract.BodyLimit)
            return (null, Problem(413, "body_too_large", "The request body limit is 4096 bytes."));
        try
        {
            // Read at most limit+1, including chunked requests and non-Kestrel test hosts.
            using var body = new MemoryStream();
            var buffer = new byte[EventContract.BodyLimit + 1];
            while (body.Length <= EventContract.BodyLimit)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length - (int)body.Length), ct);
                if (read == 0) break;
                body.Write(buffer, 0, read);
            }
            if (body.Length > EventContract.BodyLimit)
                return (null, Problem(413, "body_too_large", "The request body limit is 4096 bytes."));
            return (JsonSerializer.Deserialize<EventRequest>(body.ToArray(), EventContract.Json), null);
        }
        catch (JsonException)
        {
            return (null, Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["Supply valid JSON with only the documented properties."] },
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_request" }));
        }
    }

    public static IResult Validation(Dictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, extensions: new Dictionary<string, object?> { ["code"] = "invalid_request" });
}
