using MyRegistry.Application;

namespace MyRegistry.Domain.Tests.Rules;

public class McpRulesTests
{
    [Theory]
    [InlineData("2025-06-18", "2025-06-18")] [InlineData("2025-03-26", "2025-03-26")] [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2099-01-01", "2025-06-18")] [InlineData("", "2025-06-18")] [InlineData(null, "2025-06-18")]
    public void Protocol_version_negotiation(string? requested, string expected) => Assert.Equal(expected, McpRules.Negotiate(requested));

    [Theory]
    [InlineData(null, "registry.example.com", true)]
    [InlineData("", "registry.example.com", true)]
    [InlineData("https://registry.example.com", "registry.example.com", true)]
    [InlineData("https://REGISTRY.example.com:8443", "registry.example.com:8443", true)]
    [InlineData("http://localhost:5108", "localhost:5108", true)]
    [InlineData("https://evil.example.com", "registry.example.com", false)]
    [InlineData("https://registry.example.com.evil.com", "registry.example.com", false)]
    [InlineData("null", "registry.example.com", false)]
    [InlineData("pas une origine", "registry.example.com", false)]
    public void Origin_must_match_the_host_when_present(string? origin, string host, bool ok) => Assert.Equal(ok, McpRules.IsOriginAllowed(origin, host));

    [Theory] [InlineData(null, 10)] [InlineData(0, 1)] [InlineData(-4, 1)] [InlineData(7, 7)] [InlineData(50, 50)] [InlineData(5000, 50)]
    public void Limit_is_clamped(int? limit, int expected) => Assert.Equal(expected, McpRules.ClampLimit(limit));

    [Fact] public void Tools_are_read_only_with_unique_names_and_valid_schemas()
    {
        var tools = McpRules.ToolList();
        var names = tools.Select(t => (string)t!["name"]!).ToList();
        Assert.Equal(["search_artifacts", "get_artifact", "get_artifact_file", "install_instructions"], names);
        Assert.Equal(names.Count, names.Distinct().Count());
        foreach (var t in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)t!["description"]));
            Assert.Equal("object", (string?)t["inputSchema"]!["type"]);
            Assert.True((bool)t["annotations"]!["readOnlyHint"]!);
            var props = t["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key).ToHashSet();
            Assert.All(t["inputSchema"]!["required"]!.AsArray(), r => Assert.Contains((string)r!, props));
        }
    }
}
