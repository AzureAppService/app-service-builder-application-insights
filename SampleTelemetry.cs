using System.Diagnostics;
using System.Diagnostics.Tracing;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

public static class SampleTelemetry
{
    public static void Configure(WebApplicationBuilder builder, bool hosted)
    {
        var diagnostics = new TelemetryDiagnostics();
        builder.Services.AddSingleton(diagnostics);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Logging.AddFilter((category, level) => category == "Demo" && level >= LogLevel.Information);

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("builder-appinsights-demo", serviceInstanceId: "demo"))
            .WithTracing(tracing => tracing
                .SetSampler(new AlwaysOnSampler())
                .AddProcessor(new SafeDemoProcessor()));

        if (hosted)
        {
            Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
            var connection = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
            if (string.IsNullOrWhiteSpace(connection))
            {
                throw new InvalidOperationException("Managed-identity mode requires APPLICATIONINSIGHTS_CONNECTION_STRING.");
            }
            telemetry.UseAzureMonitor(options =>
            {
                options.ConnectionString = connection;
                options.Credential = new DiagnosticCredential(
                    new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned), diagnostics);
                options.EnableLiveMetrics = false;
                options.EnableStandardMetrics = false;
                options.EnablePerformanceCounters = false;
                options.DisableOfflineStorage = true;
                options.TracesPerSecond = null;
                options.SamplingRatio = 1;
            });
            builder.Services.Configure<AzureMonitorExporterOptions>(options =>
                options.AddPolicy(new SafeIngestionFailurePolicy(diagnostics), HttpPipelinePosition.PerCall));
            // This request/exception tutorial does not export potentially identifying runtime metrics.
            telemetry.WithMetrics(metrics => metrics.AddView("*", MetricStreamConfiguration.Drop));
        }
        else
        {
            telemetry.WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());
            builder.Logging.AddOpenTelemetry();
        }

        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            options.Filter = context =>
                (HttpMethods.IsPost(context.Request.Method) &&
                 context.Request.Path.Value is "/api/demo/success" or "/api/demo/slow" or
                     "/api/demo/error" or "/api/demo/dependency") ||
                (context.Request.Path == "/internal/catalog" &&
                 context.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address));
            options.RecordException = false;
        });
        builder.Services.Configure<HttpClientTraceInstrumentationOptions>(options =>
        {
            options.FilterHttpRequestMessage = request => request.RequestUri is
            { Host: "127.0.0.1", Port: 8080, AbsolutePath: "/internal/catalog", Query: "" };
            options.RecordException = false;
        });
    }
}

public sealed class SafeDemoProcessor : BaseProcessor<Activity>
{
    private static readonly HashSet<string> AllowedTags =
    [
        "http.request.method", "http.response.status_code", "http.route",
        "http.method", "http.status_code", "demo.scenario"
    ];

    public override void OnStart(Activity activity)
    {
        activity.TraceStateString = null;
        foreach (var item in activity.Baggage.ToArray())
        {
            activity.SetBaggage(item.Key, null);
        }

    }

    public override void OnEnd(Activity activity)
    {
        foreach (var tag in activity.TagObjects.ToArray())
        {
            if (!AllowedTags.Contains(tag.Key))
            {
                activity.SetTag(tag.Key, null);
            }
        }
        // Keep a well-formed synthetic request URL without exporting the caller's host.
        if (activity.Kind == ActivityKind.Server)
        {
            activity.SetTag("url.scheme", "http");
            activity.SetTag("server.address", "sample.invalid");
            activity.SetTag("url.path", activity.GetTagItem("http.route") ?? "/internal/catalog");
        }
        // Route parameters and caller-provided hosts never become operation names or URLs.
        activity.DisplayName = activity.Kind == ActivityKind.Client ? "GET loopback catalog" :
            activity.GetTagItem("demo.scenario") is string scenario ? $"POST demo/{scenario}" : "GET internal/catalog";
        activity.TraceStateString = null;
    }
}

public sealed class SafeExporterDiagnostics : EventListener
{
    private readonly TelemetryDiagnostics? diagnostics;

    public SafeExporterDiagnostics(TelemetryDiagnostics? diagnostics = null)
    {
        this.diagnostics = diagnostics;
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "OpenTelemetry-AzureMonitor-Exporter")
        {
            EnableEvents(eventSource, EventLevel.Warning);
        }

    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        diagnostics?.RecordExporterEvent(eventData.EventId);
        // SDK payloads can contain destination/credential details; report only the fixed event ID.
        Console.Error.WriteLine(
            $"Azure Monitor exporter event {eventData.EventId} ({eventData.Level}). " +
            "Ingestion may be failing; verify managed identity, destination and RBAC. SDK payload omitted.");
    }
}

public sealed class SafeIngestionFailurePolicy(TelemetryDiagnostics? diagnostics = null) : HttpPipelineSynchronousPolicy
{
    public override void OnReceivedResponse(HttpMessage message)
    {
        if (message.HasResponse)
        {
            diagnostics?.RecordIngestionResponse(message.Response.Status);
        }
        if (message.HasResponse && message.Response.Status != 200)
        {
            Console.Error.WriteLine(
                $"Azure Monitor ingestion returned HTTP {message.Response.Status}. " +
                "Ingestion may be failing or incomplete; verify managed identity, destination and RBAC. Response payload omitted.");
        }
    }
}

public sealed class TelemetryDiagnostics
{
    private int tokenRequests;
    private int tokenSuccesses;
    private string? credentialFailure;
    private int ingestionResponses;
    private int lastIngestionStatus;
    private int exporterEvents;
    private int lastExporterEvent;

    public void RecordTokenRequest() => Interlocked.Increment(ref tokenRequests);
    public void RecordTokenSuccess() => Interlocked.Increment(ref tokenSuccesses);
    public void RecordCredentialFailure(string code) => Volatile.Write(ref credentialFailure, code);

    public void RecordIngestionResponse(int status)
    {
        Interlocked.Increment(ref ingestionResponses);
        Volatile.Write(ref lastIngestionStatus, status);
    }

    public void RecordExporterEvent(int eventId)
    {
        Interlocked.Increment(ref exporterEvents);
        Volatile.Write(ref lastExporterEvent, eventId);
    }

    public object Snapshot(bool hosted) => new
    {
        mode = hosted ? "managed-identity" : "off",
        identityEndpointPresent = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT")),
        msiEndpointPresent = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSI_ENDPOINT")),
        imdsEndpointPresent = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IMDS_ENDPOINT")),
        tokenRequests = Volatile.Read(ref tokenRequests),
        tokenSuccesses = Volatile.Read(ref tokenSuccesses),
        credentialFailure = Volatile.Read(ref credentialFailure),
        ingestionResponses = Volatile.Read(ref ingestionResponses),
        lastIngestionStatus = Volatile.Read(ref lastIngestionStatus),
        exporterEvents = Volatile.Read(ref exporterEvents),
        lastExporterEvent = Volatile.Read(ref lastExporterEvent)
    };
}

public sealed class DiagnosticCredential(TokenCredential credential, TelemetryDiagnostics diagnostics) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        diagnostics.RecordTokenRequest();
        try
        {
            var token = credential.GetToken(requestContext, cancellationToken);
            diagnostics.RecordTokenSuccess();
            return token;
        }
        catch (CredentialUnavailableException)
        {
            diagnostics.RecordCredentialFailure("credential_unavailable");
            throw;
        }
        catch (AuthenticationFailedException)
        {
            diagnostics.RecordCredentialFailure("authentication_failed");
            throw;
        }
        catch (RequestFailedException)
        {
            diagnostics.RecordCredentialFailure("credential_request_failed");
            throw;
        }
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        diagnostics.RecordTokenRequest();
        try
        {
            var token = await credential.GetTokenAsync(requestContext, cancellationToken);
            diagnostics.RecordTokenSuccess();
            return token;
        }
        catch (CredentialUnavailableException)
        {
            diagnostics.RecordCredentialFailure("credential_unavailable");
            throw;
        }
        catch (AuthenticationFailedException)
        {
            diagnostics.RecordCredentialFailure("authentication_failed");
            throw;
        }
        catch (RequestFailedException)
        {
            diagnostics.RecordCredentialFailure("credential_request_failed");
            throw;
        }
    }
}
