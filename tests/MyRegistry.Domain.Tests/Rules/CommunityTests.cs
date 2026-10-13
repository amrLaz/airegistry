using System.Text.Json.Nodes;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class QuarantinePolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private static IEnumerable<(string, DateTime)> Reports(int n, Func<int, DateTime> at, Func<int, string>? user = null) =>
        Enumerable.Range(0, n).Select(i => (user?.Invoke(i) ?? $"u{i}", at(i)));

    [Fact] public void Five_distinct_users_within_an_hour_quarantine() =>
        Assert.True(QuarantinePolicy.ShouldQuarantine(Reports(5, i => Now.AddMinutes(-i * 10)), Now));

    [Fact] public void Four_reports_do_not() =>
        Assert.False(QuarantinePolicy.ShouldQuarantine(Reports(4, i => Now.AddMinutes(-i)), Now));

    [Fact] public void One_user_reporting_five_times_does_not() =>
        Assert.False(QuarantinePolicy.ShouldQuarantine(Reports(5, i => Now.AddMinutes(-i), _ => "same"), Now));

    [Fact] public void Old_reports_are_ignored() =>
        Assert.False(QuarantinePolicy.ShouldQuarantine(Reports(5, i => Now.AddMinutes(-61 - i)), Now));

    [Fact] public void Exactly_one_hour_old_still_counts() =>
        Assert.True(QuarantinePolicy.ShouldQuarantine(Reports(5, _ => Now.AddHours(-1)), Now));
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

public class DeletionPolicyTests
{
    private static VersionRef V(string v, int day = 1) => new(Guid.NewGuid(), v, new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc));

    [Fact] public void Only_version_cannot_be_deleted()
    {
        var a = V("1.0.0");
        Assert.Equal(DeletionVerdict.OnlyVersion, DeletionPolicy.Check(a.Id, [a]));
    }

    [Fact] public void Latest_semver_is_protected_even_if_published_earlier()
    {
        var old = V("1.0.0", 5); var high = V("2.0.0", 1);
        Assert.Equal(DeletionVerdict.LatestVersion, DeletionPolicy.Check(high.Id, [old, high]));
        Assert.Equal(DeletionVerdict.Allowed, DeletionPolicy.Check(old.Id, [old, high]));
    }

    [Fact] public void Semver_comparison_is_numeric_not_lexical()
    {
        var nine = V("1.9.0"); var ten = V("1.10.0");
        Assert.Equal(DeletionVerdict.LatestVersion, DeletionPolicy.Check(ten.Id, [nine, ten]));
        Assert.Equal(DeletionVerdict.Allowed, DeletionPolicy.Check(nine.Id, [nine, ten]));
    }

    [Fact] public void Non_semver_falls_back_to_most_recent()
    {
        var a = V("alpha", 1); var b = V("beta", 2);
        Assert.Equal(DeletionVerdict.LatestVersion, DeletionPolicy.Check(b.Id, [a, b]));
        Assert.Equal(DeletionVerdict.Allowed, DeletionPolicy.Check(a.Id, [a, b]));
    }

    [Fact] public void Unknown_id_is_not_found() =>
        Assert.Equal(DeletionVerdict.NotFound, DeletionPolicy.Check(Guid.NewGuid(), [V("1.0.0")]));
}
