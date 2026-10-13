using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class SnapshotApiTests(DbFixture db) : IAsyncLifetime
{
    private HttpClient Admin() => db.Factory.CreateClient();
    private Dictionary<string, string> _original = new();
    private static string U(string p) => p + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');

    public async Task InitializeAsync() => _original = (await Admin().GetFromJsonAsync<Dictionary<string, string>>("/api/admin/settings"))!;

    public async Task DisposeAsync() =>
        await Admin().PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string>
        {
            ["title"] = _original["title"], ["default_namespace"] = _original["default_namespace"],
            ["public_url"] = _original.GetValueOrDefault("public_url", ""), ["marketplace_repo"] = _original.GetValueOrDefault("marketplace_repo", ""),
        });

    private async Task Configure(string repo, string publicUrl = "https://registry.example.io") =>
        (await Admin().PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string>
        { ["public_url"] = publicUrl, ["marketplace_repo"] = repo })).EnsureSuccessStatusCode();

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    private async Task<Guid> ApprovedPlugin(string pack)
    {
        var c = Admin();
        var skill = U("skl");
        var s = (await (await c.PostAsJsonAsync("/api/import", new { type = "Skill", @namespace = "myregistry", name = skill, fileName = "SKILL.md", description = "d",
            rawContent = $"---\nname: {skill}\ndescription: d\n---\n# x" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
        await c.PutAsJsonAsync($"/api/catalog/{s}/status", new { status = "Approved" });
        var d = (await (await c.PostAsJsonAsync("/api/plugins/drafts", new { @namespace = "myregistry", name = pack, description = "Pack",
            components = new[] { new { type = "Skill", slug = $"myregistry/{skill}", version = "1.0.0" } } })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var v = (await (await c.PostAsync($"/api/plugins/drafts/{d}/publish", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
        await c.PutAsJsonAsync($"/api/catalog/{v}/status", new { status = "Approved" });
        return v;
    }

    [Fact]
    public async Task Snapshot_writes_the_manifest_for_the_four_agents_and_commits_in_a_local_git_repo()
    {
        var repo = Path.Combine(Path.GetTempPath(), "mr-snap-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var pack = U("pack");
            await ApprovedPlugin(pack);
            await Configure($"file://{repo}");

            var res = await Admin().PostAsync("/api/admin/marketplace/snapshot", null);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var r = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(r.GetProperty("published").GetBoolean());
            Assert.Equal(64, r.GetProperty("digest").GetString()!.Length);

            var contents = new[] { ".github/plugin/marketplace.json", ".claude-plugin/marketplace.json", ".agents/plugins/marketplace.json", ".cursor-plugin/marketplace.json" }
                .Select(p => File.ReadAllText(Path.Combine(repo, p))).ToList();
            Assert.All(contents, c => Assert.Equal(contents[0], c));
            Assert.Contains($"myregistry-{pack}", contents[0]);
            Assert.Contains("https://registry.example.io/api/plugins/myregistry/", contents[0]);
            Assert.Contains("chore(marketplace): snapshot", Git(repo, "log", "-1", "--format=%s"));
            Assert.Equal("1", Git(repo, "rev-list", "--count", "HEAD").Trim());
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, true); }
    }

    [Fact]
    public async Task Unchanged_digest_pushes_nothing_and_a_new_plugin_creates_a_new_commit()
    {
        var repo = Path.Combine(Path.GetTempPath(), "mr-snap-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await ApprovedPlugin(U("pack"));
            await Configure(repo);
            await Admin().PostAsync("/api/admin/marketplace/snapshot", null);
            var again = await (await Admin().PostAsync("/api/admin/marketplace/snapshot", null)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(again.GetProperty("published").GetBoolean());
            Assert.Equal("1", Git(repo, "rev-list", "--count", "HEAD").Trim());

            await ApprovedPlugin(U("pack"));
            var third = await (await Admin().PostAsync("/api/admin/marketplace/snapshot", null)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(third.GetProperty("published").GetBoolean());
            Assert.Equal("2", Git(repo, "rev-list", "--count", "HEAD").Trim());

            var status = await Admin().GetFromJsonAsync<JsonElement>("/api/admin/marketplace/snapshot");
            Assert.True(status.GetProperty("configured").GetBoolean());
            Assert.Equal(third.GetProperty("digest").GetString(), status.GetProperty("lastDigest").GetString());
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, true); }
    }

    [Fact]
    public async Task Snapshot_requires_a_repo_and_a_public_url()
    {
        await Admin().PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string> { ["marketplace_repo"] = "", ["public_url"] = "" });
        var noRepo = await Admin().PostAsync("/api/admin/marketplace/snapshot", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noRepo.StatusCode);
        Assert.Contains("snapshot-repo", await noRepo.Content.ReadAsStringAsync());

        await Admin().PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string> { ["marketplace_repo"] = Path.Combine(Path.GetTempPath(), "mr-x") });
        var noUrl = await Admin().PostAsync("/api/admin/marketplace/snapshot", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noUrl.StatusCode);
        Assert.Contains("snapshot-public-url", await noUrl.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Remote_or_relative_repo_locations_are_refused_in_settings()
    {
        foreach (var bad in new[] { "https://github.com/org/repo", "relative/dir", "/" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity,
                (await Admin().PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string> { ["marketplace_repo"] = bad })).StatusCode);
    }

    [Fact]
    public async Task Internal_snapshot_keys_cannot_be_set_by_users() =>
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await Admin().PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string> { ["marketplace_snapshot_digest"] = "x" })).StatusCode);

    [Fact]
    public async Task Snapshot_is_admin_only_and_audited_and_registry_info_exposes_the_repo_url()
    {
        var repo = Path.Combine(Path.GetTempPath(), "mr-snap-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var mod = db.Factory.CreateClient();
            mod.DefaultRequestHeaders.Add("X-Dev-User", $"{U("m")}@example.com");
            Assert.Equal(HttpStatusCode.Forbidden, (await mod.PostAsync("/api/admin/marketplace/snapshot", null)).StatusCode);

            await ApprovedPlugin(U("pack"));
            await Configure(repo);
            await Admin().PostAsync("/api/admin/marketplace/snapshot", null);
            var audit = await Admin().GetFromJsonAsync<JsonElement>("/api/audit?action=marketplace-snapshot&pageSize=5");
            Assert.True(audit.GetProperty("total").GetInt32() >= 1);
            var info = await Admin().GetFromJsonAsync<JsonElement>("/api/registry-info");
            Assert.Equal(new Uri(repo).AbsoluteUri, info.GetProperty("marketplaceRepo").GetString());
        }
        finally { if (Directory.Exists(repo)) Directory.Delete(repo, true); }
    }
}
