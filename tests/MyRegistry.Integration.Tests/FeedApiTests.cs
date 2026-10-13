using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class FeedApiTests(DbFixture db) : IDisposable
{
    private readonly List<string> _dirs = [];
    private HttpClient Admin() => db.Factory.CreateClient();
    private static string U(string p) => p + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');

    public void Dispose() { foreach (var d in _dirs) if (Directory.Exists(d)) { foreach (var f in new DirectoryInfo(d).GetFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal; Directory.Delete(d, true); } }

    private string NewRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mr-feed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir); _dirs.Add(dir);
        Git(dir, "init", "-q", "-b", "main");
        return dir;
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@t" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        return o;
    }

    private static void Write(string repo, string rel, string content)
    {
        var full = Path.Combine(repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void Commit(string repo, string msg = "c") { Git(repo, "add", "-A"); Git(repo, "commit", "-q", "-m", msg); }
    private static string Skill(string name, string? version = null, string body = "# x") =>
        $"---\nname: {name}\ndescription: Skill {name}\n{(version is null ? "" : $"version: {version}\n")}---\n{body}";

    private async Task<Guid> CreateFeed(string repo, string? name = null, string? ns = null)
    {
        var res = await Admin().PostAsJsonAsync("/api/feeds", new { name = name ?? U("feed"), kind = "Local", url = repo, branch = "main", intervalMinutes = 60, namespaceOverride = ns });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> Sync(Guid id, bool force = false) =>
        await (await Admin().PostAsync($"/api/feeds/{id}/sync{(force ? "?force=true" : "")}", null)).Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> Catalog(string q) =>
        await Admin().GetFromJsonAsync<JsonElement>($"/api/admin/versions?q={q}&pageSize=100");

    [Fact]
    public async Task Sync_imports_discovered_artifacts_as_external_toreview_in_the_provider_namespace()
    {
        var repo = NewRepo(); var a = U("alpha"); var b = U("bravo");
        Write(repo, $"skills/{a}/SKILL.md", Skill(a));
        Write(repo, $"skills/{a}/ref/notes.md", "n");
        Write(repo, $"agents/{b}.agent.md", $"---\nname: {b}\ndescription: Reviewer\n---\n# {b}");
        Commit(repo);
        var feed = await CreateFeed(repo);

        var r = await Sync(feed);
        Assert.True(r.GetProperty("success").GetBoolean());
        Assert.Equal(2, r.GetProperty("imported").GetInt32());

        var list = (await Catalog(a)).GetProperty("items").EnumerateArray().ToList();
        var v = Assert.Single(list);
        Assert.Equal("myregistry-local", v.GetProperty("namespace").GetString());
        Assert.Equal("ToReview", v.GetProperty("status").GetString());
        Assert.StartsWith("feed:", v.GetProperty("createdBy").GetString());

        var detail = await Admin().GetFromJsonAsync<JsonElement>($"/api/catalog/{v.GetProperty("id").GetGuid()}");
        Assert.Equal(2, detail.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task Resync_without_new_commit_does_nothing_and_a_new_commit_creates_a_new_version()
    {
        var repo = NewRepo(); var a = U("delta");
        Write(repo, $"skills/{a}/SKILL.md", Skill(a, "1.0.0"));
        Commit(repo);
        var feed = await CreateFeed(repo);
        Assert.Equal(1, (await Sync(feed)).GetProperty("imported").GetInt32());

        var again = await Sync(feed);
        Assert.Equal(0, again.GetProperty("imported").GetInt32());
        Assert.Contains("Aucun nouveau commit", string.Join(" ", again.GetProperty("messages").EnumerateArray().Select(x => x.GetString())));

        Write(repo, $"skills/{a}/SKILL.md", Skill(a, "1.1.0", "# changé"));
        Commit(repo);
        var third = await Sync(feed);
        Assert.Equal(1, third.GetProperty("updated").GetInt32());
        var versions = (await Catalog(a)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("version").GetString()).OrderBy(x => x).ToArray();
        Assert.Equal(["1.0.0", "1.1.0"], versions);

        // Nouveau commit sans changement de contenu de cet artefact : rien de nouveau
        Write(repo, "README.md", "autre");
        Commit(repo);
        var fourth = await Sync(feed);
        Assert.Equal(0, fourth.GetProperty("updated").GetInt32());
        Assert.Equal(1, fourth.GetProperty("unchanged").GetInt32());
    }

    [Fact]
    public async Task Version_falls_back_to_0_0_N_and_only_grows_when_content_changes()
    {
        var repo = NewRepo(); var a = U("auto");
        Write(repo, $"skills/{a}/SKILL.md", Skill(a));
        Commit(repo);
        var feed = await CreateFeed(repo);
        await Sync(feed);
        Write(repo, $"skills/{a}/SKILL.md", Skill(a, body: "# v2"));
        Commit(repo);
        await Sync(feed);
        var versions = (await Catalog(a)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("version").GetString()).OrderBy(x => x).ToArray();
        Assert.Equal(["0.0.1", "0.0.2"], versions);
    }

    [Fact]
    public async Task Secret_in_an_external_artifact_means_quarantine_not_refusal()
    {
        var repo = NewRepo(); var a = U("leak");
        Write(repo, $"skills/{a}/SKILL.md", Skill(a, body: "password = \"hunter2hunter2\""));
        Commit(repo);
        var r = await Sync(await CreateFeed(repo));
        Assert.Equal(1, r.GetProperty("imported").GetInt32());
        Assert.Equal(1, r.GetProperty("quarantined").GetInt32());
        Assert.True((await Catalog(a)).GetProperty("items")[0].GetProperty("quarantined").GetBoolean());
    }

    [Fact]
    public async Task Invalid_artifacts_are_skipped_with_a_message_and_do_not_block_others()
    {
        var repo = NewRepo(); var good = U("good");
        Write(repo, $"skills/{good}/SKILL.md", Skill(good));
        Write(repo, "skills/broken/SKILL.md", "pas de frontmatter");
        Commit(repo);
        var r = await Sync(await CreateFeed(repo));
        Assert.Equal(1, r.GetProperty("imported").GetInt32());
        Assert.True(r.GetProperty("skipped").GetInt32() >= 1);
        Assert.Contains("missing-frontmatter", string.Join(" ", r.GetProperty("messages").EnumerateArray().Select(x => x.GetString())));
    }

    [Fact]
    public async Task Artifact_removed_from_the_repo_is_deprecated_and_restored_when_it_comes_back()
    {
        var repo = NewRepo(); var a = U("gone"); var keep = U("keep");
        Write(repo, $"skills/{a}/SKILL.md", Skill(a));
        Write(repo, $"skills/{keep}/SKILL.md", Skill(keep));
        Commit(repo);
        var feed = await CreateFeed(repo);
        await Sync(feed);
        var id = (await Catalog(a)).GetProperty("items")[0].GetProperty("id").GetGuid();
        await Admin().PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" });

        Directory.Delete(Path.Combine(repo, "skills", a), true);
        Commit(repo);
        var r = await Sync(feed);
        Assert.Equal(1, r.GetProperty("deprecated").GetInt32());
        Assert.Equal("Deprecated", (await Admin().GetFromJsonAsync<JsonElement>($"/api/catalog/{id}")).GetProperty("status").GetString());

        Write(repo, $"skills/{a}/SKILL.md", Skill(a));
        Commit(repo);
        await Sync(feed);
        Assert.Equal("Approved", (await Admin().GetFromJsonAsync<JsonElement>($"/api/catalog/{id}")).GetProperty("status").GetString());
        Assert.Equal("Approved", (await Admin().GetFromJsonAsync<JsonElement>($"/api/catalog/{id}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unreachable_repo_fails_then_pauses_after_three_consecutive_failures_and_can_be_reactivated()
    {
        var missing = Path.Combine(Path.GetTempPath(), "mr-nope-" + Guid.NewGuid().ToString("N")[..8]);
        var feed = await CreateFeed(missing);
        for (var i = 1; i <= 2; i++)
        {
            var r = await Sync(feed);
            Assert.False(r.GetProperty("success").GetBoolean());
            Assert.Equal("Failed", (await Admin().GetFromJsonAsync<JsonElement>($"/api/feeds/{feed}")).GetProperty("status").GetString());
        }
        var third = await Sync(feed);
        Assert.Contains("pause", string.Join(" ", third.GetProperty("messages").EnumerateArray().Select(x => x.GetString())));
        var f = await Admin().GetFromJsonAsync<JsonElement>($"/api/feeds/{feed}");
        Assert.Equal("Paused", f.GetProperty("status").GetString());
        Assert.False(f.GetProperty("active").GetBoolean());

        var repo = NewRepo();
        Write(repo, $"skills/{U("x")}/SKILL.md", Skill("x"));
        Commit(repo);
        var fix = await Admin().PutAsJsonAsync($"/api/feeds/{feed}", new { name = f.GetProperty("name").GetString(), kind = "Local", url = repo, branch = "main", intervalMinutes = 60, active = true });
        Assert.Equal(HttpStatusCode.OK, fix.StatusCode);
        Assert.True((await Sync(feed)).GetProperty("success").GetBoolean());
        Assert.Equal(0, (await Admin().GetFromJsonAsync<JsonElement>($"/api/feeds/{feed}")).GetProperty("consecutiveFailures").GetInt32());
    }

    [Fact]
    public async Task Delete_cascades_to_imported_artifacts_and_system_feed_is_protected()
    {
        var repo = NewRepo(); var a = U("casc");
        Write(repo, $"skills/{a}/SKILL.md", Skill(a));
        Commit(repo);
        var feed = await CreateFeed(repo);
        await Sync(feed);
        Assert.Equal(1, (await Catalog(a)).GetProperty("total").GetInt32());

        var del = await Admin().DeleteAsync($"/api/feeds/{feed}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(1, (await del.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removedArtifacts").GetInt32());
        Assert.Equal(0, (await Catalog(a)).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/feeds/{feed}")).StatusCode);

        var sysRepo = NewRepo();
        var sys = await Admin().PostAsJsonAsync("/api/feeds", new { name = U("system"), kind = "Local", url = sysRepo, isSystem = true });
        if (sys.StatusCode == HttpStatusCode.Created)
        {
            var sysId = (await sys.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await Admin().DeleteAsync($"/api/feeds/{sysId}")).StatusCode);
            using var scope = db.Factory.Services.CreateScope(); // nettoyage : l'API refuse de supprimer un feed système
            var ctx = scope.ServiceProvider.GetRequiredService<MyRegistry.Infrastructure.AppDbContext>();
            ctx.Feeds.RemoveRange(ctx.Feeds.Where(f => f.Id == sysId));
            await ctx.SaveChangesAsync();
        }
        else Assert.Contains("système", await sys.Content.ReadAsStringAsync()); // un feed système existe déjà (autre test)
    }

    [Fact]
    public async Task Validation_test_connection_and_permissions()
    {
        var repo = NewRepo();
        Write(repo, "a/SKILL.md", Skill("a"));
        Commit(repo);
        var ok = await (await Admin().PostAsJsonAsync("/api/feeds/test", new { name = "t1", kind = "Local", url = repo })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Equal(40, ok.GetProperty("head").GetString()!.Length);

        var bad = await (await Admin().PostAsJsonAsync("/api/feeds/test", new { name = "t2", kind = "Local", url = Path.Combine(Path.GetTempPath(), "inexistant-" + U("z")) })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(bad.GetProperty("ok").GetBoolean());

        foreach (var invalid in new object[]
        {
            new { name = "x", kind = "GitHub", url = "https://gitlab.com/o/r" },
            new { name = "ok-name", kind = "Local", url = "relative" },
            new { name = "ok-name", kind = "GitHub", url = "https://github.com/o/r", patRef = "PATH" },
        })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Admin().PostAsJsonAsync("/api/feeds", invalid)).StatusCode);

        var reader = db.Factory.CreateClient();
        reader.DefaultRequestHeaders.Add("X-Dev-User", $"{U("rd")}@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/feeds")).StatusCode);
    }

    [Fact]
    public async Task Provider_kinds_get_their_default_namespace_suffix_and_interval()
    {
        var gh = await (await Admin().PostAsJsonAsync("/api/feeds", new { name = U("gh"), kind = "GitHub", url = "https://github.com/org/repo" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("myregistry-gh", gh.GetProperty("effectiveNamespace").GetString());
        Assert.Equal(1440, gh.GetProperty("intervalMinutes").GetInt32());
        var az = await (await Admin().PostAsJsonAsync("/api/feeds", new { name = U("az"), kind = "AzureDevOps", url = "https://dev.azure.com/o/p/_git/r" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("myregistry-azd", az.GetProperty("effectiveNamespace").GetString());
        Assert.Equal(60, az.GetProperty("intervalMinutes").GetInt32());
    }

    [Fact]
    public async Task Name_clash_with_an_internal_artifact_is_skipped_and_reports_are_listed()
    {
        var repo = NewRepo(); var a = U("clash");
        Write(repo, $"skills/{a}/SKILL.md", Skill(a));
        Commit(repo);
        // Un artefact interne occupe déjà le slug dans le namespace du feed (forcé par namespaceOverride)
        var pubOk = await Admin().PostAsJsonAsync("/api/import", new { type = "Skill", @namespace = "shared-ns", name = a, fileName = "SKILL.md", rawContent = Skill(a) });
        Assert.Equal(HttpStatusCode.Created, pubOk.StatusCode);
        var feed = await CreateFeed(repo, ns: "shared-ns");
        var r = await Sync(feed);
        Assert.Equal(0, r.GetProperty("imported").GetInt32());
        Assert.Contains("autre source", string.Join(" ", r.GetProperty("messages").EnumerateArray().Select(x => x.GetString())));
        var reports = await Admin().GetFromJsonAsync<JsonElement>($"/api/feeds/{feed}/reports");
        Assert.Equal(1, reports.GetArrayLength());
    }
}
