using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class DemoTests
{
    [Fact]
    public async Task Requests_WhenExecuted_ExportCorrelatedRedactedSignals()
    {
        using var factory = new DemoFactory();
        factory.UseKestrel(8080);
        using var client = factory.CreateClient();

        using var success = await Send(client, "success");
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var successBody = await success.Content.ReadFromJsonAsync<JsonElement>();
        var successId = successBody.GetProperty("operationId").GetString();
        Assert.Equal(32, successId!.Length);

        var started = Stopwatch.StartNew();
        using var slow = await Send(client, "slow");
        Assert.Equal(HttpStatusCode.OK, slow.StatusCode);
        var slowBody = await slow.Content.ReadFromJsonAsync<JsonElement>();
        Assert.InRange(slowBody.GetProperty("durationMs").GetDouble(), 550, 2000);
        Assert.InRange(started.Elapsed.TotalMilliseconds, 550, 3000);

        using var error = await Send(client, "error");
        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        var errorText = await error.Content.ReadAsStringAsync();
        Assert.DoesNotContain("stack", errorText, StringComparison.OrdinalIgnoreCase);
        var errorBody = JsonDocument.Parse(errorText).RootElement;
        Assert.True(errorBody.GetProperty("synthetic").GetBoolean());
        var errorId = errorBody.GetProperty("operationId").GetString();

        using var dependency = await Send(client, "dependency");
        Assert.Equal(HttpStatusCode.OK, dependency.StatusCode);
        var dependencyBody = await dependency.Content.ReadFromJsonAsync<JsonElement>();
        var dependencyId = dependencyBody.GetProperty("operationId").GetString();
        using var health = await client.GetAsync("/health");
        Assert.Equal("OK\n", await health.Content.ReadAsStringAsync());
        using var missing = await client.GetAsync("/not-found");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var unknown = await Send(client, "unknown");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
        var spans = factory.Spans.Items.ToArray();
        var successSpan = Assert.Single(spans, item => item.Kind == ActivityKind.Server && item.TraceId.ToHexString() == successId);
        Assert.Equal("POST demo/success", successSpan.DisplayName);
        var errorSpan = Assert.Single(spans, item => item.Kind == ActivityKind.Server && item.TraceId.ToHexString() == errorId);
        Assert.Equal(ActivityStatusCode.Error, errorSpan.Status);
        var exception = Assert.Single(errorSpan.Events);
        Assert.Equal("exception", exception.Name);
        Assert.Equal(nameof(SyntheticDemoException), exception.Tags.Single(tag => tag.Key == "exception.type").Value);
        Assert.DoesNotContain(exception.Tags, tag => tag.Key == "exception.stacktrace");
        var dependencySpans = spans.Where(item => item.TraceId.ToHexString() == dependencyId).ToArray();
        var parent = Assert.Single(dependencySpans, item => item.GetTagItem("demo.scenario") is "dependency");
        var call = Assert.Single(dependencySpans, item => item.Kind == ActivityKind.Client);
        Assert.Equal(parent.SpanId, call.ParentSpanId);
        Assert.Equal("GET loopback catalog", call.DisplayName);
        var catalog = Assert.Single(dependencySpans, item => item.Kind == ActivityKind.Server && item != parent);
        Assert.Equal(call.SpanId, catalog.ParentSpanId);

        Assert.DoesNotContain(spans, item => item.DisplayName.Contains("health") || item.DisplayName.Contains("unknown"));
        var serialized = JsonSerializer.Serialize(spans.Select(item => new
        {
            item.DisplayName,
            tags = item.TagObjects.ToArray(),
            baggage = item.Baggage.ToArray(),
            item.TraceStateString
        }));
        Assert.DoesNotContain("private-canary", serialized);
        Assert.DoesNotContain("Authorization", serialized);
        Assert.DoesNotContain("client.address", serialized);
        Assert.DoesNotContain("url.full", serialized);
        Assert.All(spans, span => Assert.Empty(span.Baggage));
        Assert.Contains(factory.Logs.Items, item => item.TraceId == errorId);
        Assert.Contains(factory.Logs.Items, item => item.TraceId == dependencyId);
        Assert.All(factory.Logs.Items, item => Assert.Equal("Demo", item.CategoryName));
    }

    [Fact]
    public async Task Requests_WhenAllowanceExceeded_ReturnExplicit429()
    {
        using var factory = new DemoFactory();
        factory.UseKestrel(8080);
        using var client = factory.CreateClient();
        for (var index = 0; index < 12; index++)
        {
            using var response = await Send(client, "success");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var rejected = await Send(client, "success");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("60", rejected.Headers.GetValues("Retry-After").Single());
        Assert.Contains("rate_limited", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Requests_WhenWindowExpired_Return410WithoutDemoSignals()
    {
        using var factory = new DemoFactory(DateTimeOffset.UtcNow.AddMinutes(-1));
        factory.UseKestrel(8080);
        using var client = factory.CreateClient();
        using var response = await Send(client, "success");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Contains("demo_expired", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Requests_WhenBusyOrCancelled_ReleaseConcurrencySlot()
    {
        using var factory = new DemoFactory();
        factory.UseKestrel(8080);
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        var first = client.PostAsync("/api/demo/slow", null, cancellation.Token);
        var second = client.PostAsync("/api/demo/slow", null);
        await Task.Delay(150);
        using var rejected = await Send(client, "success");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Contains("busy", await rejected.Content.ReadAsStringAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        using var secondResult = await second;
        Assert.Equal(HttpStatusCode.OK, secondResult.StatusCode);
        using var next = await Send(client, "success");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Requests_WhenPayloadOrQueryProvided_RejectInput()
    {
        using var factory = new DemoFactory();
        factory.UseKestrel(8080);
        using var client = factory.CreateClient();
        using var query = await client.PostAsync("/api/demo/success?private-canary=secret", null);
        using var body = await client.PostAsync("/api/demo/success", new StringContent("private-canary"));
        Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, body.StatusCode);
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
        Assert.DoesNotContain(factory.Spans.Items.SelectMany(span => span.TagObjects), tag => tag.Value?.ToString()?.Contains("private-canary") == true);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string scenario)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/demo/{scenario}");
        request.Headers.Add("Authorization", "Bearer private-canary");
        request.Headers.Add("Cookie", "session=private-canary");
        request.Headers.Add("tracestate", "private-canary=secret");
        request.Headers.Add("baggage", "user=private-canary");
        return client.SendAsync(request);
    }
}

internal sealed class DemoFactory(DateTimeOffset? expiresAt = null, WireCapture? wire = null, bool hosted = false) : WebApplicationFactory<Program>
{
    public Collector<Activity> Spans { get; } = new();
    public LogCollector Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("TELEMETRY_MODE", hosted ? "managed-identity" : "off");
        if (hosted)
        {
            builder.UseSetting("SAMPLE_EXPIRES_AT", "2099-01-01T00:00:00Z");
            builder.UseSetting("APPLICATIONINSIGHTS_CONNECTION_STRING",
                "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://monitor.example.test");
        }
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DemoWindow>();
            services.AddSingleton(new DemoWindow(expiresAt));
            services.ConfigureOpenTelemetryTracerProvider((_, tracing) =>
                tracing.AddProcessor(new SimpleActivityExportProcessor(Spans)));
            if (wire is not null && !hosted)
            {
                services.AddOpenTelemetry().WithTracing(tracing =>
                    tracing.AddAzureMonitorTraceExporter(wire.Configure));
            }
            if (wire is not null && hosted)
            {
                services.Configure<Azure.Monitor.OpenTelemetry.Exporter.AzureMonitorExporterOptions>(wire.Configure);
            }
        });
        builder.ConfigureLogging(logging => logging.AddOpenTelemetry(options =>
        {
            options.AddProcessor(new SimpleLogRecordExportProcessor(Logs));
            if (wire is not null && !hosted)
            {
                options.AddAzureMonitorLogExporter(wire.Configure);
            }
        }));
    }
}

internal sealed class Collector<T> : BaseExporter<T> where T : class
{
    public ConcurrentBag<T> Items { get; } = [];
    public override ExportResult Export(in Batch<T> batch)
    {
        foreach (var item in batch)
        {
            Items.Add(item);
        }
        return ExportResult.Success;
    }
}

internal sealed class LogCollector : BaseExporter<LogRecord>
{
    public ConcurrentBag<(string TraceId, string? CategoryName)> Items { get; } = [];

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        foreach (var item in batch)
        {
            Items.Add((item.TraceId.ToHexString(), item.CategoryName));
        }
        return ExportResult.Success;
    }
}
