using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace AgentBlog.Server.Tests;

public sealed class HealthTests(BlogApp app)
{
    [Fact]
    public async Task Healthz_answers_ok()
    {
        using var client = app.Factory.CreateClient();

        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Health>(TestContext.Current.CancellationToken);
        Assert.Equal("ok", body?.Status);
    }

    [Fact]
    public async Task Version_names_the_build()
    {
        using var client = app.Factory.CreateClient();

        var body = await client.GetFromJsonAsync<VersionBody>("/version", TestContext.Current.CancellationToken);

        Assert.StartsWith("0.1.", body?.Version);
    }

    private sealed record Health(string Status);

    private sealed record VersionBody(string Version);
}
