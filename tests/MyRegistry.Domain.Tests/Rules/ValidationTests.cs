using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class ArtifactValidatorTests
{
    private static ValidationResult V(ArtifactType t, params (string, string)[] files) =>
        ArtifactValidator.Validate(t, files.ToDictionary(f => f.Item1, f => f.Item2));

    private const string GoodSkill = "---\nname: pdf\ndescription: Extract PDFs\n---\n# body";

    // Skill
    [Fact] public void Skill_valid_with_readme() =>
        Assert.True(V(ArtifactType.Skill, ("SKILL.md", GoodSkill), ("README.md", "x")).Errors.Count == 0);
    [Fact] public void Skill_missing_skill_md() =>
        Assert.Contains(V(ArtifactType.Skill, ("README.md", "x")).Errors, e => e.Code == "missing-skill-md");
    [Fact] public void Skill_missing_name() =>
        Assert.Contains(V(ArtifactType.Skill, ("SKILL.md", "---\ndescription: d\n---")).Errors, e => e.Code == "missing-name");
    [Fact] public void Skill_missing_description() =>
        Assert.Contains(V(ArtifactType.Skill, ("SKILL.md", "---\nname: n\n---")).Errors, e => e.Code == "missing-description");
    [Fact] public void Skill_without_frontmatter_is_an_error() =>
        Assert.Contains(V(ArtifactType.Skill, ("SKILL.md", "# x")).Errors, e => e.Code == "missing-frontmatter");
    [Fact] public void Skill_warnings()
    {
        var r = V(ArtifactType.Skill, ("SKILL.md", $"---\nname: {new string('a', 65)}\ndescription: {new string('d', 1025)}\n---"));
        Assert.Empty(r.Errors);
        Assert.Contains(r.Warnings, w => w.Code == "name-too-long");
        Assert.Contains(r.Warnings, w => w.Code == "description-too-long");
        Assert.Contains(r.Warnings, w => w.Code == "missing-readme");
    }

    // MCP
    [Fact] public void Mcp_stdio_valid() =>
        Assert.Empty(V(ArtifactType.McpServer, ("mcp.json", "{\"name\":\"a\",\"description\":\"d\",\"command\":\"node\",\"args\":[\"x\"]}")).Errors);
    [Fact] public void Mcp_http_valid() =>
        Assert.Empty(V(ArtifactType.McpServer, ("mcp.json", "{\"url\":\"https://x.io/mcp\"}")).Errors);
    [Fact] public void Mcp_docker_valid() =>
        Assert.Empty(V(ArtifactType.McpServer, ("mcp.json", "{\"docker\":\"img:1\"}")).Errors);
    [Fact] public void Mcp_invalid_json() =>
        Assert.Contains(V(ArtifactType.McpServer, ("mcp.json", "{oops")).Errors, e => e.Code == "invalid-json");
    [Fact] public void Mcp_without_launch_info() =>
        Assert.Contains(V(ArtifactType.McpServer, ("mcp.json", "{\"name\":\"a\"}")).Errors, e => e.Code == "mcp-no-launch");
    [Fact] public void Mcp_missing_file() =>
        Assert.Contains(V(ArtifactType.McpServer, ("x.txt", "")).Errors, e => e.Code == "missing-mcp-file");
    [Fact] public void Mcp_recommended_fields_warn()
    {
        var r = V(ArtifactType.McpServer, ("mcp.json", "{\"command\":\"node\"}"));
        Assert.Empty(r.Errors);
        Assert.Contains(r.Warnings, w => w.Code == "missing-name");
        Assert.Contains(r.Warnings, w => w.Code == "missing-description");
        Assert.Contains(r.Warnings, w => w.Code == "missing-args");
    }

    // Agent
    [Fact] public void Agent_valid() =>
        Assert.Empty(V(ArtifactType.Agent, ("review.agent.md", "---\nname: r\ndescription: d\nmodel: m\ntools: t\n---")).Errors);
    [Fact] public void Agent_agents_md_accepted_without_skill_md() =>
        Assert.Empty(V(ArtifactType.Agent, ("AGENTS.md", "---\nname: r\ndescription: d\n---")).Errors);
    [Fact] public void Agent_without_frontmatter_falls_back_with_warning()
    {
        var r = V(ArtifactType.Agent, ("a.agent.md", "# Reviewer\n\nReviews code."));
        Assert.Empty(r.Errors);
        Assert.Contains(r.Warnings, w => w.Code == "no-frontmatter");
    }
    [Fact] public void Agent_without_anything_fails() =>
        Assert.NotEmpty(V(ArtifactType.Agent, ("a.agent.md", "")).Errors);
    [Fact] public void Agent_missing_file() =>
        Assert.Contains(V(ArtifactType.Agent, ("x.md", "")).Errors, e => e.Code == "missing-agent-file");
    [Fact] public void Agent_model_tools_recommended() =>
        Assert.Contains(V(ArtifactType.Agent, ("a.agent.md", "---\nname: r\ndescription: d\n---")).Warnings, w => w.Code == "missing-model");

    // Prompt
    [Fact] public void Prompt_valid() =>
        Assert.Empty(V(ArtifactType.Prompt, ("x.prompt.md", "---\nagent: a\ndescription: d\n---")).Errors);
    [Fact] public void Prompt_missing_agent() =>
        Assert.Contains(V(ArtifactType.Prompt, ("x.prompt.md", "---\ndescription: d\n---")).Errors, e => e.Code == "missing-agent");
    [Fact] public void Prompt_missing_file() =>
        Assert.Contains(V(ArtifactType.Prompt, ("x.md", "")).Errors, e => e.Code == "missing-prompt-file");

    // Hook
    private const string Readme = "---\nname: n\ndescription: d\nhooks: [PreToolUse]\ntags: [a]\n---";
    [Fact] public void Hook_valid() =>
        Assert.Empty(V(ArtifactType.Hook, ("hooks.json", "{\"hooks\":{\"PreToolUse\":[{}]}}"), ("README.md", Readme)).Errors);
    [Fact] public void Hook_without_events() =>
        Assert.Contains(V(ArtifactType.Hook, ("hooks.json", "{\"hooks\":{}}"), ("README.md", Readme)).Errors, e => e.Code == "hooks-empty");
    [Fact] public void Hook_event_must_be_array() =>
        Assert.Contains(V(ArtifactType.Hook, ("hooks.json", "{\"hooks\":{\"X\":1}}"), ("README.md", Readme)).Errors, e => e.Code == "hooks-not-array");
    [Fact] public void Hook_missing_root_property() =>
        Assert.Contains(V(ArtifactType.Hook, ("hooks.json", "{}"), ("README.md", Readme)).Errors, e => e.Code == "missing-hooks-property");
    [Fact] public void Hook_readme_frontmatter_required() =>
        Assert.Contains(V(ArtifactType.Hook, ("hooks.json", "{\"hooks\":{\"A\":[]}}"), ("README.md", "---\nname: n\n---")).Errors, e => e.Code == "missing-description");
    [Fact] public void Hook_missing_readme() =>
        Assert.Contains(V(ArtifactType.Hook, ("hooks.json", "{\"hooks\":{\"A\":[]}}")).Errors, e => e.Code == "missing-readme");

    // Plugin
    [Fact] public void Plugin_valid() =>
        Assert.Empty(V(ArtifactType.Plugin, (".claude-plugin/plugin.json", "{\"name\":\"my-plugin\",\"description\":\"d\",\"version\":\"1.0.0\"}")).Errors);
    [Fact] public void Plugin_missing_manifest() =>
        Assert.Contains(V(ArtifactType.Plugin, ("x", "")).Errors, e => e.Code == "missing-plugin-manifest");
    [Fact] public void Plugin_invalid_name() =>
        Assert.Contains(V(ArtifactType.Plugin, (".github/plugin/plugin.json", "{\"name\":\"Bad Name\"}")).Errors, e => e.Code == "name-format");
    [Fact] public void Plugin_missing_description_and_version_warn()
    {
        var r = V(ArtifactType.Plugin, (".codex-plugin/plugin.json", "{\"name\":\"ok\"}"));
        Assert.Empty(r.Errors);
        Assert.Contains(r.Warnings, w => w.Code == "missing-description");
        Assert.Contains(r.Warnings, w => w.Code == "missing-version");
    }
    [Fact] public void Plugin_component_needs_exact_semver() =>
        Assert.Contains(V(ArtifactType.Plugin, (".claude-plugin/plugin.json",
            "{\"name\":\"ok\",\"x-myregistry-components\":[{\"type\":\"Skill\",\"slug\":\"myregistry/a\",\"version\":\"^1.0.0\"}]}")).Errors,
            e => e.Code == "component-version");
    [Fact] public void Plugin_component_ok() =>
        Assert.Empty(V(ArtifactType.Plugin, (".claude-plugin/plugin.json",
            "{\"name\":\"ok\",\"description\":\"d\",\"version\":\"1.0.0\",\"x-myregistry-components\":[{\"type\":\"Skill\",\"slug\":\"myregistry/a\",\"version\":\"1.2.3\"}]}")).Errors);
}

public class TypeDetectionTests
{
    [Theory]
    [InlineData("SKILL.md", ArtifactType.Skill)]
    [InlineData("hooks.json", ArtifactType.Hook)]
    [InlineData("x.agent.md", ArtifactType.Agent)]
    [InlineData("AGENTS.md", ArtifactType.Agent)]
    [InlineData("x.prompt.md", ArtifactType.Prompt)]
    [InlineData("mcp.json", ArtifactType.McpServer)]
    [InlineData("package.json", ArtifactType.McpServer)]
    [InlineData(".claude-plugin/plugin.json", ArtifactType.Plugin)]
    public void Detects_type_from_file(string path, ArtifactType expected) =>
        Assert.Equal(expected, ArtifactTypeDetector.Detect([path, "README.md"]));

    [Fact] public void Unknown_returns_null() => Assert.Null(ArtifactTypeDetector.Detect(["foo.txt"]));
    [Fact] public void Plugin_wins_over_contained_skill() =>
        Assert.Equal(ArtifactType.Plugin, ArtifactTypeDetector.Detect(["SKILL.md", ".claude-plugin/plugin.json"]));
}

public class McpTransportTests
{
    [Theory]
    [InlineData("{\"transport\":\"sse\"}", McpTransport.Http)]
    [InlineData("{\"transport\":\"streamable-http\"}", McpTransport.Http)]
    [InlineData("{\"url\":\"https://x\"}", McpTransport.Http)]
    [InlineData("{\"docker\":\"img\"}", McpTransport.Docker)]
    [InlineData("{\"image\":\"img\"}", McpTransport.Docker)]
    [InlineData("{\"command\":\"npx\",\"args\":[\"-y\",\"p\"]}", McpTransport.Npx)]
    [InlineData("{\"command\":\"uvx\",\"args\":[\"p\"]}", McpTransport.Uvx)]
    [InlineData("{\"command\":\"uv\",\"args\":[\"run\"]}", McpTransport.Uvx)]
    [InlineData("{\"command\":\"node\",\"args\":[\"s.js\"]}", McpTransport.Stdio)]
    [InlineData("{\"url\":\"https://x\",\"docker\":\"img\"}", McpTransport.Http)] // priorité
    [InlineData("{\"mcpServers\":{\"s\":{\"command\":\"npx\"}}}", McpTransport.Npx)]
    public void Detects_transport(string json, McpTransport expected) =>
        Assert.Equal(expected, McpTransportDetector.Detect(json));
}

public class VersionRulesTests
{
    [Fact] public void New_family_accepts_proposed() =>
        Assert.Equal("1.0.0", VersionRules.Resolve("1.0.0", []).Version);
    [Fact] public void Higher_semver_accepted() =>
        Assert.Equal("1.1.0", VersionRules.Resolve("1.1.0", ["1.0.0"]).Version);
    [Fact] public void Lower_semver_is_auto_patched_from_latest()
    {
        var r = VersionRules.Resolve("0.9.0", ["1.0.0", "1.0.3"]);
        Assert.Equal("1.0.4", r.Version);
        Assert.True(r.AutoBumped);
    }
    [Fact] public void Equal_semver_is_auto_patched() =>
        Assert.Equal("1.0.1", VersionRules.Resolve("1.0.0", ["1.0.0"]).Version);
    [Fact] public void Numeric_not_lexical_comparison() =>
        Assert.Equal("1.10.0", VersionRules.Resolve("1.10.0", ["1.9.0"]).Version);
    [Fact] public void Missing_proposal_bumps_latest() =>
        Assert.Equal("2.0.1", VersionRules.Resolve(null, ["2.0.0"]).Version);
    [Fact] public void Missing_proposal_new_family_starts_at_1_0_0() =>
        Assert.Equal("1.0.0", VersionRules.Resolve(null, []).Version);
    [Fact] public void Non_semver_duplicate_is_refused() =>
        Assert.NotNull(VersionRules.Resolve("v-alpha", ["v-alpha"]).Error);
    [Fact] public void Non_semver_new_is_accepted() =>
        Assert.Null(VersionRules.Resolve("v-beta", ["v-alpha"]).Error);
    [Theory]
    [InlineData("1.0.0", "1.0.0-rc1", 1)]
    [InlineData("1.2.3", "1.10.0", -1)]
    [InlineData("2.0.0", "2.0.0", 0)]
    public void Compare(string a, string b, int expected) => Assert.Equal(expected, Math.Sign(VersionRules.Compare(a, b)));
}

public class FeedVersionTests
{
    private static Dictionary<string, string> Fm(params (string, string)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact] public void Cascade_version_first() =>
        Assert.Equal("2.0.0", FeedVersion.Resolve(Fm(("version", "2.0.0"), ("semver", "3.0.0")), null, false));
    [Fact] public void Cascade_semver_then_release() =>
        Assert.Equal("3.0.0", FeedVersion.Resolve(Fm(("semver", "3.0.0"), ("release", "4.0.0")), null, false));
    [Fact] public void Release_used_last() =>
        Assert.Equal("4.0.0", FeedVersion.Resolve(Fm(("release", "4.0.0")), null, false));
    [Fact] public void Fallback_starts_at_0_0_1() =>
        Assert.Equal("0.0.1", FeedVersion.Resolve(Fm(), null, true));
    [Fact] public void Fallback_increments_only_when_content_changed()
    {
        Assert.Equal("0.0.4", FeedVersion.Resolve(Fm(), "0.0.3", true));
        Assert.Equal("0.0.3", FeedVersion.Resolve(Fm(), "0.0.3", false));
    }
}

public class QualityScoreTests
{
    [Theory]
    [InlineData(0, QualityGrade.A)]
    [InlineData(1, QualityGrade.B)]
    [InlineData(2, QualityGrade.B)]
    [InlineData(3, QualityGrade.C)]
    [InlineData(9, QualityGrade.C)]
    public void Grades_by_warning_count(int warnings, QualityGrade expected) =>
        Assert.Equal(expected, QualityScore.Grade(warnings));
}
