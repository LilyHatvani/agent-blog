using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentBlog.Server.Tests;

public sealed class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Healthz_answers_ok()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Health>(TestContext.Current.CancellationToken);
        Assert.Equal("ok", body?.Status);
    }

    [Fact]
    public async Task Version_names_the_build()
    {
        using var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<VersionBody>("/version", TestContext.Current.CancellationToken);

        Assert.StartsWith("0.1.", body?.Version);
    }

    private sealed record Health(string Status);

    private sealed record VersionBody(string Version);
}
