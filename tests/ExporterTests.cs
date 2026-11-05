using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Xunit;

public sealed class ExporterTests
{
    [Fact]
    public async Task Exporter_WhenIngestionRejected_ReportsSafeFailureWithoutPayload()
    {
        var originalError = Console.Error;
        using var errors = new StringWriter();
        Console.SetError(errors);
        try
        {
            using var diagnostics = new SafeExporterDiagnostics();
            using var wire = new WireCapture(HttpStatusCode.Forbidden);
            using var factory = new DemoFactory(wire: wire);
            factory.UseKestrel(8080);
            using var client = factory.CreateClient();
            using var response = await client.PostAsync("/api/demo/success", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var source = Assert.Single(EventSource.GetSources(),
                source => source.Name == "OpenTelemetry-AzureMonitor-Exporter");
            Assert.True(source.IsEnabled(EventLevel.Warning, EventKeywords.All));
            Assert.True(factory.Services.GetRequiredService<TracerProvider>().ForceFlush(5000));
            Assert.True(factory.Services.GetRequiredService<LoggerProvider>().ForceFlush(5000));
            Assert.NotEmpty(wire.Destinations);
            var output = errors.ToString();
            Assert.Contains("Azure Monitor ingestion returned HTTP 403", output);
            Assert.Contains("Ingestion may be failing", output);
            Assert.DoesNotContain("monitor.example.test", output);
            Assert.DoesNotContain("synthetic-test-token", output);
            Assert.DoesNotContain("InstrumentationKey", output);
            Assert.DoesNotContain("private-canary", output);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requests_WhenAzureExporterSerializes_PreserveSafeCorrelatedEnvelopes(bool hosted)
    {
        using var wire = new WireCapture();
        using var factory = new DemoFactory(wire: wire, hosted: hosted);
        factory.UseKestrel(8080);
        using var client = factory.CreateClient();
        var operationIds = new Dictionary<string, string>();
        foreach (var scenario in new[] { "success", "error", "dependency" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/demo/{scenario}");
            request.Headers.Add("Cookie", "user=private-canary");
            request.Headers.Add("baggage", "user=private-canary");
            request.Headers.Add("tracestate", "user=private-canary");
            using var response = await client.SendAsync(request);
            Assert.Equal(scenario == "error" ? 500 : 200, (int)response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            operationIds[scenario] = body.GetProperty("operationId").GetString()!;
        }

        Assert.True(factory.Services.GetRequiredService<TracerProvider>().ForceFlush(5000));
        Assert.True(factory.Services.GetRequiredService<LoggerProvider>().ForceFlush(5000));
        var envelopes = wire.Envelopes.ToArray();
        Assert.NotEmpty(wire.Destinations);
        Assert.NotEmpty(envelopes);
        Assert.All(wire.Destinations, destination => Assert.Equal("monitor.example.test", destination));
        Assert.All(wire.Authentication, scheme => Assert.Equal("Bearer", scheme));

        JsonElement Signal(string type, string operationId) => Assert.Single(envelopes, envelope =>
            envelope.GetProperty("data").GetProperty("baseType").GetString() == type &&
            envelope.GetProperty("tags").GetProperty("ai.operation.id").GetString() == operationId);

        var success = Signal("RequestData", operationIds["success"]);
        Assert.True(success.GetProperty("data").GetProperty("baseData").GetProperty("success").GetBoolean());
        var error = Signal("RequestData", operationIds["error"]);
        var errorData = error.GetProperty("data").GetProperty("baseData");
        Assert.Equal("500", errorData.GetProperty("responseCode").GetString());
        Assert.False(errorData.GetProperty("success").GetBoolean());
        var exception = Signal("ExceptionData", operationIds["error"]);
        Assert.Equal(errorData.GetProperty("id").GetString(),
            exception.GetProperty("tags").GetProperty("ai.operation.parentId").GetString());
        Assert.Equal(nameof(SyntheticDemoException), exception.GetProperty("data")
            .GetProperty("baseData").GetProperty("exceptions")[0].GetProperty("typeName").GetString());
        Signal("MessageData", operationIds["error"]);

        var dependency = Signal("RemoteDependencyData", operationIds["dependency"]);
        var dependencyParent = Assert.Single(envelopes, envelope =>
            envelope.GetProperty("data").GetProperty("baseType").GetString() == "RequestData" &&
            envelope.GetProperty("tags").GetProperty("ai.operation.id").GetString() == operationIds["dependency"] &&
            envelope.GetProperty("data").GetProperty("baseData").TryGetProperty("properties", out var properties) &&
            properties.TryGetProperty("demo.scenario", out var scenario) && scenario.GetString() == "dependency");
        Assert.Equal(dependencyParent.GetProperty("data").GetProperty("baseData").GetProperty("id").GetString(),
            dependency.GetProperty("tags").GetProperty("ai.operation.parentId").GetString());
        Signal("MessageData", operationIds["dependency"]);

        var payload = JsonSerializer.Serialize(envelopes);
        Assert.DoesNotContain("private-canary", payload);
        Assert.DoesNotContain("127.0.0.1", payload);
        Assert.DoesNotContain("localhost", payload);
        Assert.DoesNotContain("client.address", payload);
        Assert.DoesNotContain("url.full", payload);
        Assert.DoesNotContain("Authorization", payload);
        Assert.DoesNotContain("Cookie", payload);
        Assert.DoesNotContain("stacktrace", payload, StringComparison.OrdinalIgnoreCase);
        Assert.All(envelopes.Where(envelope =>
            envelope.GetProperty("data").GetProperty("baseType").GetString() == "RequestData"),
            envelope => Assert.StartsWith("http://sample.invalid/",
                envelope.GetProperty("data").GetProperty("baseData").GetProperty("url").GetString()));
    }
}

// Captures real Azure exporter envelopes without network access or an Azure identity.
internal sealed class WireCapture : HttpMessageHandler
{
    private readonly HttpClient client;
    private readonly HttpStatusCode responseStatus;
    private readonly string instrumentationKey = Guid.NewGuid().ToString();
    public ConcurrentBag<JsonElement> Envelopes { get; } = [];
    public ConcurrentBag<string?> Destinations { get; } = [];
    public ConcurrentBag<string?> Authentication { get; } = [];

    public WireCapture(HttpStatusCode responseStatus = HttpStatusCode.OK)
    {
        this.responseStatus = responseStatus;
        client = new HttpClient(this, disposeHandler: false);
    }

    public void Configure(AzureMonitorExporterOptions options)
    {
        Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
        options.ConnectionString =
            $"InstrumentationKey={instrumentationKey};IngestionEndpoint=https://monitor.example.test";
        options.Credential = new SyntheticCredential();
        options.Transport = new HttpClientTransport(client);
        options.DisableOfflineStorage = true;
        options.EnableLiveMetrics = false;
        options.EnableStandardMetrics = false;
        options.EnablePerformanceCounters = false;
        options.TracesPerSecond = null;
        options.SamplingRatio = 1;
        options.Retry.MaxRetries = 0;
        options.AddPolicy(new SafeIngestionFailurePolicy(), HttpPipelinePosition.PerCall);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Destinations.Add(request.RequestUri?.Host);
        Authentication.Add(request.Headers.Authorization?.Scheme);
        Assert.NotNull(request.Content);
        await using var input = await request.Content.ReadAsStreamAsync(cancellationToken);
        await using var decoded = request.Content.Headers.ContentEncoding.Contains("gzip")
            ? new GZipStream(input, CompressionMode.Decompress) : input;
        using var reader = new StreamReader(decoded, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        var count = 0;
        foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            Envelopes.Add(document.RootElement.Clone());
            count++;
        }
        return new HttpResponseMessage(responseStatus)
        {
            Content = JsonContent.Create(new
            {
                itemsReceived = count,
                itemsAccepted = responseStatus == HttpStatusCode.OK ? count : 0,
                errors = responseStatus == HttpStatusCode.OK ? Array.Empty<object>() :
                    [new { index = 0, statusCode = (int)responseStatus, message = "private-canary" }]
            })
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            client.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class SyntheticCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
