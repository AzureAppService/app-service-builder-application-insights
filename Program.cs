using System.Diagnostics;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8080");
var mode = builder.Configuration["TELEMETRY_MODE"];
var setupCode = mode is null or "off" ? "configuration_missing" :
    mode != "managed-identity" ? "configuration_invalid" :
    string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]) ? "configuration_missing" :
    !SampleTelemetry.ValidConnection(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]!) ? "configuration_invalid" : null;
var hosted = mode == "managed-identity" && setupCode is null;

DateTimeOffset? deadline = null;
if (hosted)
{
    if (!DateTimeOffset.TryParseExact(
        builder.Configuration["SAMPLE_EXPIRES_AT"], "yyyy-MM-dd'T'HH:mm:ss'Z'",
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires))
    {
        setupCode = string.IsNullOrWhiteSpace(builder.Configuration["SAMPLE_EXPIRES_AT"]) ? "configuration_missing" : "configuration_invalid";
        hosted = false;
    }
    else
    {
        deadline = expires;
    }
}

SampleTelemetry.Configure(builder, hosted);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new DemoWindow(deadline));
builder.Services.AddHttpClient("catalog", client =>
{
    client.BaseAddress = new Uri("http://127.0.0.1:8080");
    client.Timeout = TimeSpan.FromSeconds(2);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseProxy = false,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("demo", _ => RateLimitPartition.GetFixedWindowLimiter("demo", _ => new()
    {
        PermitLimit = 12,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true
    }));
    options.OnRejected = async (context, cancellation) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { code = "rate_limited", message = "Demo allowance reached. Wait one minute." },
            cancellation);
    };
});

var app = builder.Build();
var telemetryDiagnostics = app.Services.GetRequiredService<TelemetryDiagnostics>();
using var exportDiagnostics = hosted ? new SafeExporterDiagnostics(telemetryDiagnostics) : null;
app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Text("OK\n"));
app.MapGet("/api/config", () => Results.Json(new
{
    mode = hosted ? "Managed identity configured; ingestion must be verified in Azure." : "Azure export is off; this run does not verify ingestion.",
    expiresAt = deadline
}));
app.MapGet("/api/diagnostics", () => Results.Json(telemetryDiagnostics.Snapshot(hosted)));
app.MapGet("/api/setup", () => Results.Json(new
{
    code = setupCode ?? telemetryDiagnostics.Readiness(deadline),
    setupUrl = "./README.md#setup",
    ingestionVerified = false
}));
app.MapGet("/ready", () =>
{
    var code = setupCode ?? telemetryDiagnostics.Readiness(deadline);
    return Results.Json(new { code, ingestionVerified = false },
        statusCode: code == "ingestion_accepted" ? 200 : 503);
});
app.MapGet("/README.md", () =>
{
    var text = File.ReadAllText(
        Path.Combine(app.Environment.ContentRootPath, "README.md"));
    return Results.Content(SetupPage.Render(text), "text/html");
});
app.MapGet("/internal/catalog", (HttpContext context) =>
    context.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address)
        ? Results.Json(new { item = "Synthetic notebook", available = true })
        : Results.StatusCode(StatusCodes.Status403Forbidden));

var concurrency = new SemaphoreSlim(2, 2);
app.Lifetime.ApplicationStopped.Register(concurrency.Dispose);
app.MapPost("/api/demo/{scenario}", async (
    string scenario, HttpContext context, IHttpClientFactory clients,
    ILoggerFactory logs, DemoWindow window, TimeProvider clock) =>
{
    if (scenario is not ("success" or "slow" or "error" or "dependency"))
    {
        return Results.NotFound(new { code = "unknown_scenario", message = "Choose a listed demonstration." });
    }
    if (context.Request.QueryString.HasValue || context.Request.ContentLength is > 0 ||
        context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        return Results.BadRequest(new { code = "unexpected_input", message = "Demo requests take no query or body." });
    }
    if (window.ExpiresAt is { } expiresAt && clock.GetUtcNow() >= expiresAt)
    {
        return Results.Json(new { code = "demo_expired", message = "The test window has ended." }, statusCode: 410);
    }
    if (!await concurrency.WaitAsync(0, context.RequestAborted))
    {
        context.Response.Headers.RetryAfter = "1";
        return Results.Json(new { code = "busy", message = "Two demonstrations are already running." }, statusCode: 429);
    }

    try
    {
        var started = Stopwatch.GetTimestamp();
        var activity = Activity.Current;
        activity?.SetTag("demo.scenario", scenario);
        var operationId = activity?.TraceId.ToHexString();
        var logger = logs.CreateLogger("Demo");
        var status = 200;
        var message = "A synthetic request completed successfully.";

        switch (scenario)
        {
            case "slow":
                await Task.Delay(TimeSpan.FromMilliseconds(600), context.RequestAborted);
                message = "A fixed 600 ms asynchronous delay completed.";
                break;
            case "error":
                try
                {
                    throw new SyntheticDemoException();
                }
                catch (SyntheticDemoException exception)
                {
                    activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                    {
                        { "exception.type", nameof(SyntheticDemoException) },
                        { "exception.message", exception.Message }
                    }));
                    activity?.SetStatus(ActivityStatusCode.Error, "Intentional synthetic error");
                    status = 500;
                    message = "Intentional synthetic error; the process is still healthy.";
                }
                break;
            case "dependency":
                try
                {
                    using var response = await clients.CreateClient("catalog").GetAsync(
                        "/internal/catalog", HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                    response.EnsureSuccessStatusCode();
                    message = "One real HTTP call to this app's fixed loopback catalog completed.";
                }
                catch (HttpRequestException)
                {
                    status = 502;
                    message = "The fixed loopback catalog call failed.";
                    activity?.SetStatus(ActivityStatusCode.Error, "Loopback dependency failed");
                }
                catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
                {
                    status = 504;
                    message = "The loopback catalog exceeded its two-second timeout.";
                    activity?.SetStatus(ActivityStatusCode.Error, "Loopback dependency timed out");
                }
                break;
        }

        logger.LogInformation("Synthetic {Scenario} demonstration returned {StatusCode}.", scenario, status);
        return Results.Json(new
        {
            scenario,
            synthetic = true,
            message,
            operationId,
            durationMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1),
            dependency = scenario == "dependency" ? "Real HTTP, same process, loopback catalog" : null
        }, statusCode: status);
    }
    finally
    {
        concurrency.Release();
    }
}).RequireRateLimiting("demo");

app.Run();

public sealed record DemoWindow(DateTimeOffset? ExpiresAt);
public sealed class SyntheticDemoException() : Exception("Intentional synthetic demonstration failure.");
public partial class Program;
