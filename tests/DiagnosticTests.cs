using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Xunit;

public sealed class DiagnosticTests
{
    [Fact]
    public async Task Setup_WhenExportOff_SeparatesLivenessFromUnverifiedDependency()
    {
        using var factory = new DemoFactory();
        using var client = factory.CreateClient();
        Assert.Equal("OK\n", await client.GetStringAsync("/health"));
        using var readiness = await client.GetAsync("/ready");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        var state = await client.GetFromJsonAsync<JsonElement>("/api/setup");
        Assert.Equal("configuration_missing", state.GetProperty("code").GetString());
        Assert.False(state.GetProperty("ingestionVerified").GetBoolean());
        Assert.Equal("./README.md#setup", state.GetProperty("setupUrl").GetString());
        Assert.Contains("id=\"setup\"", await client.GetStringAsync("/README.md"));
        Assert.Empty(factory.Spans.Items);
    }

    [Theory]
    [InlineData("managed-identity", "", "2099-01-01T00:00:00Z", "configuration_missing")]
    [InlineData("managed-identity", "private-canary", "2099-01-01T00:00:00Z", "configuration_invalid")]
    [InlineData("wrong-mode", "", "", "configuration_invalid")]
    public async Task Setup_WhenHostedSettingsIncomplete_BootsWithoutCloudExport(
        string mode, string connection, string deadline, string expected)
    {
        using var factory = new IncompleteSetupFactory(mode, connection, deadline);
        using var client = factory.CreateClient();
        Assert.Equal("OK\n", await client.GetStringAsync("/health"));
        var state = await client.GetFromJsonAsync<JsonElement>("/api/setup");
        Assert.Equal(expected, state.GetProperty("code").GetString());
        Assert.DoesNotContain("private-canary", state.GetRawText());
        using var readiness = await client.GetAsync("/ready");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
    }

    [Theory]
    [InlineData(403, "ingestion_denied")]
    [InlineData(401, "ingestion_denied")]
    [InlineData(206, "ingestion_partial")]
    [InlineData(429, "ingestion_limit")]
    [InlineData(500, "ingestion_failed")]
    public void Readiness_WhenTransportReportsFailure_DoesNotClaimDelivery(int status, string expected)
    {
        var diagnostics = new TelemetryDiagnostics();
        Assert.Equal("not_checked", diagnostics.Readiness(null));
        diagnostics.RecordIngestionResponse(status);
        Assert.Equal(expected, diagnostics.Readiness(null));
        diagnostics.RecordIngestionResponse(200);
        Assert.Equal("ingestion_accepted", diagnostics.Readiness(null));
        Assert.Equal("demo_expired", diagnostics.Readiness(DateTimeOffset.UtcNow.AddSeconds(-1)));
        diagnostics.RecordCredentialFailure("authentication_failed");
        Assert.Equal("identity_failed", diagnostics.Readiness(null));
        diagnostics.RecordTokenSuccess();
        Assert.Equal("ingestion_accepted", diagnostics.Readiness(null));
        diagnostics.RecordExporterEvent(1);
        Assert.Equal("export_warning", diagnostics.Readiness(null));
    }

    [Theory]
    [InlineData("private-canary", false)]
    [InlineData("InstrumentationKey=invalid;IngestionEndpoint=https://example.test", false)]
    [InlineData("InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=http://example.test", false)]
    [InlineData("InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://example.test", true)]
    public void Configuration_WhenMalformed_FailsClosedWithoutExporterSetup(string connection, bool valid)
    {
        Assert.Equal(valid, SampleTelemetry.ValidConnection(connection));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Credential_WhenAuthenticationFails_ReportsOnlyFixedCodeAndRethrows(bool asynchronous)
    {
        var diagnostics = new TelemetryDiagnostics();
        var credential = new DiagnosticCredential(new FailingCredential(), diagnostics);
        var context = new TokenRequestContext(["https://monitor.azure.com/.default"]);
        if (asynchronous)
        {
            await Assert.ThrowsAsync<AuthenticationFailedException>(async () =>
                await credential.GetTokenAsync(context, default));
        }
        else
        {
            Assert.Throws<AuthenticationFailedException>(() => credential.GetToken(context, default));
        }

        var snapshot = JsonSerializer.SerializeToElement(diagnostics.Snapshot(true));
        Assert.Equal(1, snapshot.GetProperty("tokenRequests").GetInt32());
        Assert.Equal(0, snapshot.GetProperty("tokenSuccesses").GetInt32());
        Assert.Equal("authentication_failed", snapshot.GetProperty("credentialFailure").GetString());
        Assert.DoesNotContain("private-canary", snapshot.GetRawText());
    }

    [Fact]
    public async Task Diagnostics_WhenRead_ReturnsOnlyAllowlistedFieldsAndNoTelemetry()
    {
        using var factory = new DemoFactory();
        using var client = factory.CreateClient();
        var result = await client.GetFromJsonAsync<JsonElement>("/api/diagnostics");
        Assert.Equal(new[]
        {
            "mode", "identityEndpointPresent", "msiEndpointPresent", "imdsEndpointPresent",
            "tokenRequests", "tokenSuccesses", "credentialFailure", "ingestionResponses",
            "lastIngestionStatus", "exporterEvents", "lastExporterEvent"
        }, result.EnumerateObject().Select(property => property.Name));
        Assert.Equal("off", result.GetProperty("mode").GetString());
        Assert.Empty(factory.Spans.Items);
    }

    private sealed class FailingCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new AuthenticationFailedException("private-canary credential and endpoint details");

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromException<AccessToken>(
                new AuthenticationFailedException("private-canary credential and endpoint details"));
    }

    private sealed class IncompleteSetupFactory(string mode, string connection, string deadline)
        : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("TELEMETRY_MODE", mode);
            builder.UseSetting("APPLICATIONINSIGHTS_CONNECTION_STRING", connection);
            builder.UseSetting("SAMPLE_EXPIRES_AT", deadline);
        }
    }
}
