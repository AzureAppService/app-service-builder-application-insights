using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Xunit;

public sealed class DiagnosticTests
{
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
}
