using System.Text;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class InstallLayoutTests
{
    private static Dictionary<string, byte[]> F(params (string, string)[] f) => f.ToDictionary(x => x.Item1, x => Encoding.UTF8.GetBytes(x.Item2));
    private static string[] Paths(InstallPlan p) => p.Files.Select(f => f.Path).ToArray();

    [Theory]
    [InlineData("copilot", false, ".github/skills/pdf/SKILL.md")]
    [InlineData("copilot", true, ".agents/skills/pdf/SKILL.md")]
    [InlineData("claude", false, ".claude/skills/pdf/SKILL.md")]
    [InlineData("claude", true, ".claude/skills/pdf/SKILL.md")]
    [InlineData("codex", false, ".codex/skills/pdf/SKILL.md")]
    public void Skill_goes_under_the_agent_skills_folder(string target, bool global, string expected)
    {
        var p = InstallLayout.Plan(ArtifactType.Skill, "pdf", target, global, F(("SKILL.md", "x")));
        Assert.Null(p.Error);
        Assert.Equal([expected], Paths(p));
    }

    [Fact] public void Skill_keeps_every_file_of_the_folder() =>
        Assert.Equal([".claude/skills/pdf/SKILL.md", ".claude/skills/pdf/scripts/run.sh"], Paths(InstallLayout.Plan(ArtifactType.Skill, "pdf", "claude", false, F(("scripts/run.sh", "s"), ("SKILL.md", "x")))));

    [Theory]
    [InlineData("copilot", false, ".github/agents/rev.agent.md")]
    [InlineData("copilot", true, ".agents/agents/rev.agent.md")]
    [InlineData("claude", false, ".claude/agents/rev.agent.md")]
    public void Agent_is_renamed_after_the_artifact(string target, bool global, string expected) =>
        Assert.Equal([expected], Paths(InstallLayout.Plan(ArtifactType.Agent, "rev", target, global, F(("x.agent.md", "a")))));

    [Fact] public void Codex_agent_needs_the_cli_conversion() =>
        Assert.Contains("CLI", InstallLayout.Plan(ArtifactType.Agent, "rev", "codex", false, F(("AGENTS.md", "a"))).Error);

    [Fact] public void Prompt_is_copilot_local_only()
    {
        Assert.Equal([".github/prompts/p.prompt.md"], Paths(InstallLayout.Plan(ArtifactType.Prompt, "p", "copilot", false, F(("a.prompt.md", "p")))));
        Assert.NotNull(InstallLayout.Plan(ArtifactType.Prompt, "p", "claude", false, F(("a.prompt.md", "p"))).Error);
        Assert.NotNull(InstallLayout.Plan(ArtifactType.Prompt, "p", "copilot", true, F(("a.prompt.md", "p"))).Error);
    }

    [Fact] public void Hooks_are_copilot_or_claude_local_only()
    {
        Assert.Equal([".github/hooks/hooks.json"], Paths(InstallLayout.Plan(ArtifactType.Hook, "h", "copilot", false, F(("hooks.json", "{}")))));
        Assert.Equal([".claude/hooks/h/hooks.json"], Paths(InstallLayout.Plan(ArtifactType.Hook, "h", "claude", false, F(("hooks.json", "{}")))));
        Assert.NotNull(InstallLayout.Plan(ArtifactType.Hook, "h", "codex", false, F(("hooks.json", "{}"))).Error);
        Assert.NotNull(InstallLayout.Plan(ArtifactType.Hook, "h", "claude", true, F(("hooks.json", "{}"))).Error);
    }

    [Theory]
    [InlineData("copilot", ".vscode/mcp.json", "servers")]
    [InlineData("claude", ".mcp.json", "mcpServers")]
    [InlineData("codex", ".codex/mcp.json", "mcpServers")]
    public void Mcp_entry_is_written_under_the_agent_key(string target, string file, string key)
    {
        var p = InstallLayout.Plan(ArtifactType.McpServer, "git", target, false, F(("mcp.json", "{\"mcpServers\":{\"x\":{\"command\":\"npx\",\"args\":[\"-y\",\"srv\"]}}}")));
        Assert.Null(p.Error);
        Assert.Equal([file], Paths(p));
        var json = Encoding.UTF8.GetString(p.Files[0].Data);
        Assert.Contains($"\"{key}\"", json);
        Assert.Contains("\"git\"", json);
        Assert.Contains("npx", json);
    }

    [Fact] public void Global_mcp_requires_the_cli_merge()
    {
        Assert.Contains("CLI", InstallLayout.Plan(ArtifactType.McpServer, "g", "claude", true, F(("mcp.json", "{\"command\":\"x\"}"))).Error);
        Assert.NotNull(InstallLayout.Plan(ArtifactType.McpServer, "g", "copilot", true, F(("mcp.json", "{\"command\":\"x\"}"))).Error);
    }

    [Fact] public void Plugins_and_unknown_targets_are_refused()
    {
        Assert.NotNull(InstallLayout.Plan(ArtifactType.Plugin, "p", "claude", false, F()).Error);
        Assert.Contains("Cible inconnue", InstallLayout.Plan(ArtifactType.Skill, "p", "vim", false, F(("SKILL.md", "x"))).Error);
    }
}
