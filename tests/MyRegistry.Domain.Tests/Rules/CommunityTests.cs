using System.Text.Json.Nodes;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class QuarantinePolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact] public void Five_reports_within_an_hour_quarantine() =>
        Assert.True(QuarantinePolicy.ShouldQuarantine(Enumerable.Range(0, 5).Select(i => Now.AddMinutes(-i * 10)), Now));

    [Fact] public void Four_reports_do_not() =>
        Assert.False(QuarantinePolicy.ShouldQuarantine(Enumerable.Range(0, 4).Select(i => Now.AddMinutes(-i)), Now));

    [Fact] public void Old_reports_are_ignored() =>
        Assert.False(QuarantinePolicy.ShouldQuarantine(Enumerable.Range(0, 5).Select(i => Now.AddMinutes(-61 - i)), Now));

    [Fact] public void Exactly_one_hour_old_still_counts() =>
        Assert.True(QuarantinePolicy.ShouldQuarantine(Enumerable.Range(0, 5).Select(_ => Now.AddHours(-1)), Now));
}

public class ExportsTests
{
    [Fact] public void CursorRules_strips_frontmatter() =>
        Assert.Equal("# Titre\ncorps\n", Exports.CursorRules("---\nname: x\ndescription: y\n---\n# Titre\ncorps"));

    [Fact] public void CursorRules_keeps_content_without_frontmatter() =>
        Assert.Equal("# A\n", Exports.CursorRules("# A"));

    [Fact] public void ClaudeDesktop_wraps_stdio_server()
    {
        var json = Exports.ClaudeDesktop("my-srv", "{\"name\":\"n\",\"command\":\"npx\",\"args\":[\"-y\",\"p\"],\"env\":{\"K\":\"v\"}}");
        var s = JsonNode.Parse(json!)!["mcpServers"]!["my-srv"]!;
        Assert.Equal("npx", (string?)s["command"]);
        Assert.Equal("-y", (string?)s["args"]![0]);
        Assert.Equal("v", (string?)s["env"]!["K"]);
        Assert.Null(s["name"]);
    }

    [Fact] public void ClaudeDesktop_supports_mcpServers_wrapper_and_url() =>
        Assert.Contains("https://x", Exports.ClaudeDesktop("a", "{\"mcpServers\":{\"s\":{\"url\":\"https://x\"}}}"));

    [Theory]
    [InlineData("{oops")]
    [InlineData("{\"name\":\"only\"}")]
    [InlineData("[1]")]
    public void ClaudeDesktop_returns_null_when_not_convertible(string json) =>
        Assert.Null(Exports.ClaudeDesktop("a", json));

    [Fact] public void AuditPrompt_embeds_files_and_criteria()
    {
        var p = Exports.AuditPrompt("myregistry/pdf", ArtifactType.Skill, "1.0.0", [new("SKILL.md", "# hello")]);
        Assert.Contains("myregistry/pdf", p);
        Assert.Contains("Injection de prompt", p);
        Assert.Contains("----- SKILL.md -----", p);
        Assert.Contains("# hello", p);
    }
}
