using System.Text;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class RepoDiscoveryTests
{
    private static IReadOnlyDictionary<string, byte[]> Repo(params (string path, string content)[] f) =>
        f.ToDictionary(x => x.path, x => Encoding.UTF8.GetBytes(x.content));

    private const string SkillMd = "---\nname: pdf\ndescription: d\n---\n# x";

    [Fact] public void Skill_directory_is_one_artifact_with_all_its_files()
    {
        var r = RepoDiscovery.Discover(Repo(("skills/pdf/SKILL.md", SkillMd), ("skills/pdf/ref/a.md", "a"), ("README.md", "r")));
        var s = Assert.Single(r);
        Assert.Equal(ArtifactType.Skill, s.Type);
        Assert.Equal("pdf", s.RawName);
        Assert.Equal("skills/pdf", s.GitPath);
        Assert.Equal(["SKILL.md", "ref/a.md"], s.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("SKILL.md", s.MainFile);
    }

    [Fact] public void Skill_name_falls_back_to_the_directory_name() =>
        Assert.Equal("my-dir", RepoDiscovery.Discover(Repo(("skills/my-dir/SKILL.md", "---\ndescription: d\n---"))).Single().RawName);

    [Fact] public void Single_file_agents_and_prompts()
    {
        var r = RepoDiscovery.Discover(Repo((".github/agents/rev.agent.md", "---\nname: reviewer\n---"), ("prompts/summ.prompt.md", "---\nagent: a\n---")));
        Assert.Contains(r, a => a.Type == ArtifactType.Agent && a.RawName == "reviewer" && a.GitPath == ".github/agents");
        Assert.Contains(r, a => a.Type == ArtifactType.Prompt && a.RawName == "summ");
        Assert.All(r, a => Assert.Single(a.Files));
    }

    [Fact] public void Agents_md_is_named_after_its_directory() =>
        Assert.Equal("team", RepoDiscovery.Discover(Repo(("team/AGENTS.md", "# A"))).Single().RawName);

    [Fact] public void Hook_and_mcp_directories()
    {
        var r = RepoDiscovery.Discover(Repo(("hooks/guard/hooks.json", "{}"), ("hooks/guard/README.md", "---\nname: guard\n---"), ("mcp/db/mcp.json", "{\"name\":\"db-bridge\"}")));
        Assert.Contains(r, a => a.Type == ArtifactType.Hook && a.RawName == "guard" && a.Files.Count == 2);
        Assert.Contains(r, a => a.Type == ArtifactType.McpServer && a.RawName == "db-bridge");
    }

    [Fact] public void Plugin_claims_its_whole_subtree_so_inner_skills_are_not_separate_artifacts()
    {
        var r = RepoDiscovery.Discover(Repo(
            ("pack/.claude-plugin/plugin.json", "{\"name\":\"pack\"}"), ("pack/skills/inner/SKILL.md", SkillMd), ("pack/agents/x.agent.md", "# a"),
            ("outer/SKILL.md", SkillMd)));
        Assert.Equal(2, r.Count);
        var plugin = Assert.Single(r, a => a.Type == ArtifactType.Plugin);
        Assert.Equal("pack", plugin.GitPath);
        Assert.Contains(".claude-plugin/plugin.json", plugin.Files.Keys);
        Assert.Contains("skills/inner/SKILL.md", plugin.Files.Keys);
        Assert.Single(r, a => a.Type == ArtifactType.Skill && a.GitPath == "outer");
    }

    [Fact] public void Plugin_with_several_runtime_manifests_is_a_single_artifact()
    {
        var r = RepoDiscovery.Discover(Repo((".claude-plugin/plugin.json", "{\"name\":\"p\"}"), (".github/plugin/plugin.json", "{\"name\":\"p\"}")));
        Assert.Single(r);
    }

    [Fact] public void Git_and_node_modules_and_workflows_are_ignored() =>
        Assert.Empty(RepoDiscovery.Discover(Repo((".git/x/SKILL.md", SkillMd), ("node_modules/a/SKILL.md", SkillMd), (".github/workflows/ci.yml", "x"))));

    [Fact] public void Files_are_attributed_to_only_one_artifact()
    {
        var r = RepoDiscovery.Discover(Repo(("s/SKILL.md", SkillMd), ("s/helper.agent.md", "# a")));
        Assert.Single(r); // l'agent est dans le dossier du skill : pas d'artefact en double
    }

    [Fact] public void Repo_root_skill_uses_the_repo_name_when_unnamed()
    {
        var a = RepoDiscovery.Discover(Repo(("SKILL.md", "---\ndescription: d\n---")), "mon-depot").Single();
        Assert.Equal("mon-depot", a.RawName);
        Assert.Equal(".", a.GitPath);
    }

    [Fact] public void Output_is_sorted_and_deterministic()
    {
        var f = Repo(("b/SKILL.md", SkillMd), ("a/SKILL.md", SkillMd));
        Assert.Equal(RepoDiscovery.Discover(f).Select(x => x.GitPath), RepoDiscovery.Discover(f.Reverse().ToDictionary(x => x.Key, x => x.Value)).Select(x => x.GitPath));
    }

    [Fact] public void Empty_repo_yields_nothing() => Assert.Empty(RepoDiscovery.Discover(Repo()));
}

public class FeedRulesTests
{
    [Theory]
    [InlineData("myregistry", FeedKind.GitHub, "myregistry-gh")]
    [InlineData("myregistry", FeedKind.AzureDevOps, "myregistry-azd")]
    [InlineData("MyRegistry", FeedKind.Local, "myregistry-local")]
    public void Default_namespace_gets_a_provider_suffix(string ns, FeedKind kind, string expected) =>
        Assert.Equal(expected, FeedRules.DefaultNamespace(ns, kind));

    [Fact] public void Long_namespaces_are_truncated_to_fit_32_chars()
    {
        var ns = FeedRules.DefaultNamespace(new string('a', 40), FeedKind.AzureDevOps);
        Assert.True(ns.Length <= 32);
        Assert.EndsWith("-azd", ns);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(10, true)]
    public void Pause_after_three_consecutive_failures(int failures, bool pause) => Assert.Equal(pause, FeedRules.ShouldPause(failures));

    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact] public void Never_synced_active_feed_is_due() => Assert.True(FeedRules.IsDue(true, null, 60, Now));
    [Fact] public void Inactive_feed_is_never_due() => Assert.False(FeedRules.IsDue(false, null, 60, Now));
    [Fact] public void Due_only_after_the_interval() { Assert.False(FeedRules.IsDue(true, Now.AddMinutes(-59), 60, Now)); Assert.True(FeedRules.IsDue(true, Now.AddMinutes(-60), 60, Now)); }
}

public class FeedValidationTests
{
    private static FeedRules.FeedInput I(FeedKind k, string url, string name = "Mon feed", string? branch = null, string? pat = null, int? interval = null, string? ns = null) =>
        new(name, k, url, branch, pat, interval, ns);

    [Theory]
    [InlineData(FeedKind.GitHub, "https://github.com/org/repo")]
    [InlineData(FeedKind.GitHub, "https://github.com/org/repo.git")]
    [InlineData(FeedKind.AzureDevOps, "https://dev.azure.com/org/proj/_git/repo")]
    [InlineData(FeedKind.Local, "file:///srv/repos/x")]
    [InlineData(FeedKind.Local, "/srv/repos/x")]
    public void Valid_urls(FeedKind k, string url) => Assert.Empty(FeedRules.Validate(I(k, url)));

    [Theory]
    [InlineData(FeedKind.GitHub, "https://gitlab.com/org/repo")]
    [InlineData(FeedKind.GitHub, "http://github.com/org/repo")]
    [InlineData(FeedKind.GitHub, "https://github.com/org")]
    [InlineData(FeedKind.AzureDevOps, "https://dev.azure.com/org/proj")]
    [InlineData(FeedKind.AzureDevOps, "https://github.com/org/repo")]
    [InlineData(FeedKind.Local, "relative/path")]
    [InlineData(FeedKind.Local, "https://github.com/org/repo")]
    public void Invalid_urls(FeedKind k, string url) => Assert.Contains(FeedRules.Validate(I(k, url)), e => e.Code == "url");

    [Theory]
    [InlineData("MYREGISTRY_GIT_GITHUB_PAT", true)]
    [InlineData("MYREGISTRY_GIT_AZDO_PAT", true)]
    [InlineData("PATH", false)]
    [InlineData("AWS_SECRET_ACCESS_KEY", false)]
    [InlineData("MYREGISTRY_lower", false)]
    [InlineData("ghp_" + "abcdefghijklmnopqrstuvwxyz0123456789", false)] // un vrai jeton collé par erreur est refusé
    public void Pat_ref_is_an_env_var_name_never_a_token(string pat, bool ok) =>
        Assert.Equal(ok, FeedRules.Validate(I(FeedKind.GitHub, "https://github.com/o/r", pat: pat)).All(e => e.Code != "pat-ref"));

    [Fact] public void Name_and_interval_and_branch_and_namespace_bounds()
    {
        Assert.Contains(FeedRules.Validate(I(FeedKind.Local, "/srv/r", name: "x")), e => e.Code == "name");
        Assert.Contains(FeedRules.Validate(I(FeedKind.Local, "/srv/r", interval: 1)), e => e.Code == "interval");
        Assert.Contains(FeedRules.Validate(I(FeedKind.Local, "/srv/r", interval: 20000)), e => e.Code == "interval");
        Assert.Contains(FeedRules.Validate(I(FeedKind.Local, "/srv/r", branch: "a..b")), e => e.Code == "branch");
        Assert.Contains(FeedRules.Validate(I(FeedKind.Local, "/srv/r", branch: "-rf")), e => e.Code == "branch");
        Assert.Contains(FeedRules.Validate(I(FeedKind.Local, "/srv/r", ns: "Bad_NS")), e => e.Code == "namespace");
        Assert.Empty(FeedRules.Validate(I(FeedKind.Local, "/srv/r", branch: "release/1.0", interval: 60, ns: "acme-gh")));
    }

    [Fact] public void Default_intervals_follow_the_spec()
    {
        Assert.Equal(60, FeedRules.DefaultInterval(FeedKind.AzureDevOps));
        Assert.Equal(1440, FeedRules.DefaultInterval(FeedKind.GitHub));
    }
}

public class JobAndAliasRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact] public void Never_run_job_is_due() => Assert.True(JobSchedule.IsDue(null, TimeSpan.FromHours(6), Now));
    [Fact] public void Job_is_due_only_after_its_interval()
    {
        Assert.False(JobSchedule.IsDue(Now.AddHours(-5), TimeSpan.FromHours(6), Now));
        Assert.True(JobSchedule.IsDue(Now.AddHours(-6), TimeSpan.FromHours(6), Now));
    }
    [Fact] public void Last_run_key_is_stable() => Assert.Equal("job_last_feed-sync", JobSchedule.LastRunKey("feed-sync"));

    [Fact] public void Aliases_live_547_days()
    {
        Assert.Equal(TimeSpan.FromDays(547), AliasRules.Retention);
        var exp = AliasRules.ExpiresAt(Now);
        Assert.True(AliasRules.IsActive(exp, Now.AddDays(546)));
        Assert.False(AliasRules.IsActive(exp, Now.AddDays(547)));
    }
}

public class NamespaceRenameTests
{
    private static RenamePlan P(string newNs, (ArtifactType, string)[] feed, params (ArtifactType, string)[] existing) =>
        NamespaceRename.Plan("old-gh", newNs, feed, existing);

    [Fact] public void Lists_every_rename_with_old_and_new_slug()
    {
        var plan = P("acme", [(ArtifactType.Skill, "pdf"), (ArtifactType.Agent, "rev")]);
        Assert.True(plan.CanApply);
        Assert.Equal(["old-gh/pdf → acme/pdf", "old-gh/rev → acme/rev"], plan.Items.Select(i => $"{i.OldSlug} → {i.NewSlug}").ToArray());
    }

    [Fact] public void Conflict_with_an_existing_slug_of_the_same_type_blocks_apply()
    {
        var plan = P("acme", [(ArtifactType.Skill, "pdf")], (ArtifactType.Skill, "acme/pdf"));
        Assert.False(plan.CanApply);
        Assert.True(plan.Items[0].Conflict);
    }

    [Fact] public void Same_slug_of_another_type_is_not_a_conflict() =>
        Assert.True(P("acme", [(ArtifactType.Skill, "pdf")], (ArtifactType.Agent, "acme/pdf")).CanApply);

    [Theory]
    [InlineData("Bad_NS")]
    [InlineData("a")]
    [InlineData("old-gh")]
    public void Invalid_or_unchanged_namespace_is_refused(string ns) => Assert.False(P(ns, [(ArtifactType.Skill, "pdf")]).CanApply);
}
