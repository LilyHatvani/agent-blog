using System.Text.Json;
using Xunit;

namespace AgentBlog.Server.Tests;

/// <summary>
/// "The server still calls no model" (M2's invariant): the agents are the owner's, over MCP; the blog itself never asks
/// an AI anything. Pinned two ways: no AI SDK among everything the server resolves (direct or transitive), and no model
/// provider's API address anywhere in its source.
/// </summary>
public sealed class NoModelTests
{
    /// <summary>
    /// Packages that talk to a model. Microsoft.Extensions.AI.Abstractions is not one: the official MCP SDK
    /// (ModelContextProtocol.Core) uses its types to describe tools; it has no client and opens no connection. Its
    /// provider packages (Microsoft.Extensions.AI.OpenAI and the like) are.
    /// </summary>
    private static readonly string[] Sdks =
    [
        "Anthropic", "OpenAI", "Azure.AI.", "Google.GenAI", "Google.Cloud.AIPlatform", "Microsoft.Extensions.AI.OpenAI",
        "Microsoft.Extensions.AI.Ollama", "Microsoft.Extensions.AI.AzureAIInference", "Microsoft.SemanticKernel", "LangChain",
        "Mistral", "OllamaSharp", "Cohere", "AWSSDK.BedrockRuntime",
    ];

    private static readonly string[] ModelHosts =
    [
        "api.anthropic.com", "api.openai.com", "generativelanguage.googleapis.com", "aiplatform.googleapis.com",
        "api.mistral.ai", "openrouter.ai", "api.cohere", "bedrock-runtime", "api.groq.com", "api.x.ai", "openai.azure.com",
    ];

    [Fact]
    public void No_AI_SDK_is_among_the_server_s_packages()
    {
        var deps = Path.Combine(AppContext.BaseDirectory, "AgentBlog.Server.deps.json");
        Assert.True(File.Exists(deps), $"missing {deps}");
        using var doc = JsonDocument.Parse(File.ReadAllText(deps));
        var libraries = doc.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name.Split('/')[0]).ToList();
        Assert.NotEmpty(libraries);
        var found = libraries.Where(l => Sdks.Any(s => l.StartsWith(s, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.True(found.Count == 0, "AI SDKs in the server: " + string.Join(", ", found));
    }

    [Fact]
    public void No_model_provider_s_address_is_in_the_server_s_source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "AgentBlog.Server"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var source = Path.Combine(dir!.FullName, "AgentBlog.Server");
        var files = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".json") || f.EndsWith(".sql") || f.EndsWith(".liquid"))
            .ToList();
        Assert.True(files.Count > 50, $"only {files.Count} source files found under {source}");
        var hits = files.SelectMany(f => ModelHosts.Where(h => File.ReadAllText(f).Contains(h, StringComparison.OrdinalIgnoreCase))
            .Select(h => $"{Path.GetRelativePath(source, f)}: {h}")).ToList();
        Assert.True(hits.Count == 0, "Model API addresses in the server: " + string.Join("; ", hits));
    }
}
