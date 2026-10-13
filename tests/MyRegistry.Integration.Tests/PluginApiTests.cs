using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class PluginApiTests(DbFixture db)
{
    private HttpClient Admin() => db.Factory.CreateClient();
    private HttpClient As(string email)
    {
        var c = db.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", email);
        return c;
    }
    private static string U(string p) => p + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');

    private async Task<Guid> PublishApproved(string type, string name, string? version = null)
    {
        var c = Admin();
        object body = type == "Skill"
            ? new { type, @namespace = "myregistry", name, version, fileName = "SKILL.md", description = "d", rawContent = $"---\nname: {name}\ndescription: d\n---\n# {name}" }
            : new { type, @namespace = "myregistry", name, version, fileName = $"{name}.agent.md", description = "d", rawContent = $"---\nname: {name}\ndescription: d\n---\n# {name}" };
        var res = await c.PostAsJsonAsync("/api/import", body);
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
        (await c.PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" })).EnsureSuccessStatusCode();
        return id;
    }

    private static object Draft(string name, params (string type, string slug, string version)[] comps) => new
    {
        @namespace = "myregistry", name, description = "Un pack de test", version = (string?)null,
        components = comps.Select(c => new { type = c.type, slug = c.slug, version = c.version }).ToArray(),
    };

    private async Task<Guid> CreateDraft(HttpClient c, object draft)
    {
        var res = await c.PostAsJsonAsync("/api/plugins/drafts", draft);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Draft_publish_approve_then_listed_in_marketplace_and_downloadable()
    {
        var skill = U("skl"); var agent = U("agt"); var pack = U("pack");
        await PublishApproved("Skill", skill);
        await PublishApproved("Agent", agent);
        var c = Admin();
        var draftId = await CreateDraft(c, Draft(pack, ("Skill", $"myregistry/{skill}", "1.0.0"), ("Agent", $"myregistry/{agent}", "1.0.0")));

        var pub = await c.PostAsync($"/api/plugins/drafts/{draftId}/publish", null);
        Assert.Equal(HttpStatusCode.Created, pub.StatusCode);
        var imp = await pub.Content.ReadFromJsonAsync<JsonElement>();
        var versionId = imp.GetProperty("versionId").GetGuid();
        Assert.Equal("1.0.0", imp.GetProperty("version").GetString());

        // Pending : absent de la marketplace
        Assert.DoesNotContain(pack, await c.GetStringAsync("/marketplace.json"));

        await c.PutAsJsonAsync($"/api/catalog/{versionId}/status", new { status = "Approved" }); // invalide le cache
        var market = await c.GetFromJsonAsync<JsonElement>("/marketplace.json");
        var entry = market.GetProperty("plugins").EnumerateArray().First(p => p.GetProperty("name").GetString() == $"myregistry-{pack}");
        Assert.Equal("archive", entry.GetProperty("source").GetProperty("source").GetString());
        var url = entry.GetProperty("source").GetProperty("url").GetString()!;
        Assert.EndsWith($"/api/plugins/myregistry/{pack}/download?version=1.0.0", url);

        var dl = await c.GetAsync(new Uri(url).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        var bytes = await dl.Content.ReadAsByteArrayAsync();
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), dl.Headers.GetValues("X-Artifact-Sha256").Single());
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains($"skills/{skill}/SKILL.md", names);
        Assert.Contains($"agents/{agent}.md", names);
        Assert.Contains(".claude-plugin/plugin.json", names);
        Assert.Contains(".github/plugin/plugin.json", names);
    }

    [Fact]
    public async Task Download_is_deterministic()
    {
        var skill = U("skl"); var pack = U("pack");
        await PublishApproved("Skill", skill);
        var c = Admin();
        var id = await CreateDraft(c, Draft(pack, ("Skill", $"myregistry/{skill}", "1.0.0")));
        var v = (await (await c.PostAsync($"/api/plugins/drafts/{id}/publish", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
        await c.PutAsJsonAsync($"/api/catalog/{v}/status", new { status = "Approved" });
        var a = await c.GetAsync($"/api/plugins/myregistry/{pack}/download");
        var b = await c.GetAsync($"/api/plugins/myregistry/{pack}/download");
        Assert.Equal(a.Headers.GetValues("X-Artifact-Sha256").Single(), b.Headers.GetValues("X-Artifact-Sha256").Single());
    }

    [Fact]
    public async Task Version_ranges_unapproved_missing_and_nested_components_are_refused()
    {
        var approved = U("ok"); var pending = U("pend");
        await PublishApproved("Skill", approved);
        var c = Admin();
        // Pending (non approuvé)
        await c.PostAsJsonAsync("/api/import", new { type = "Skill", @namespace = "myregistry", name = pending, fileName = "SKILL.md", rawContent = $"---\nname: {pending}\ndescription: d\n---\n# x" });

        async Task<string> Try(params (string type, string slug, string version)[] comps)
        {
            var id = await CreateDraft(c, Draft(U("p"), comps));
            var r = await c.PostAsync($"/api/plugins/drafts/{id}/publish", null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            return await r.Content.ReadAsStringAsync();
        }

        Assert.Contains("component-version", await Try(("Skill", $"myregistry/{approved}", "^1.0.0")));
        Assert.Contains("component-not-approved", await Try(("Skill", $"myregistry/{pending}", "1.0.0")));
        Assert.Contains("component-unresolved", await Try(("Skill", "myregistry/inexistant", "1.0.0")));
        Assert.Contains("component-unresolved", await Try(("Skill", $"myregistry/{approved}", "9.9.9")));
        Assert.Contains("component-type", await Try(("Plugin", "myregistry/autre", "1.0.0")));
        Assert.Contains("no-components", await Try());
    }

    [Fact]
    public async Task Update_badge_appears_when_a_newer_component_version_is_approved()
    {
        var skill = U("skl"); var pack = U("pack");
        await PublishApproved("Skill", skill, "1.0.0");
        var c = Admin();
        var id = await CreateDraft(c, Draft(pack, ("Skill", $"myregistry/{skill}", "1.0.0")));
        var before = await c.GetFromJsonAsync<JsonElement>($"/api/plugins/drafts/{id}");
        Assert.Equal(0, before.GetProperty("updates").GetArrayLength());

        var v = (await (await c.PostAsync($"/api/plugins/drafts/{id}/publish", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
        await PublishApproved("Skill", skill, "1.1.0");

        var draft = await c.GetFromJsonAsync<JsonElement>($"/api/plugins/drafts/{id}");
        Assert.Equal("1.1.0", draft.GetProperty("updates")[0].GetProperty("latest").GetString());
        var published = await c.GetFromJsonAsync<JsonElement>($"/api/plugins/versions/{v}/updates");
        Assert.Equal("1.0.0", published[0].GetProperty("pinned").GetString());
    }

    [Fact]
    public async Task Deprecating_the_plugin_removes_it_from_the_marketplace_immediately()
    {
        var skill = U("skl"); var pack = U("pack");
        await PublishApproved("Skill", skill);
        var c = Admin();
        var id = await CreateDraft(c, Draft(pack, ("Skill", $"myregistry/{skill}", "1.0.0")));
        var v = (await (await c.PostAsync($"/api/plugins/drafts/{id}/publish", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
        await c.PutAsJsonAsync($"/api/catalog/{v}/status", new { status = "Approved" });
        Assert.Contains(pack, await c.GetStringAsync("/marketplace.json"));
        await c.PostAsync($"/api/catalog/{v}/unpublish", null);
        Assert.DoesNotContain(pack, await c.GetStringAsync("/marketplace.json")); // cache invalidé, pas d'attente des 5 min
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/plugins/myregistry/{pack}/download")).StatusCode);
    }

    [Fact]
    public async Task Marketplace_supports_conditional_requests()
    {
        var c = Admin();
        var first = await c.GetAsync("/marketplace.json");
        var etag = first.Headers.ETag!.Tag;
        Assert.Contains("max-age=300", first.Headers.CacheControl!.ToString());
        var req = new HttpRequestMessage(HttpMethod.Get, "/marketplace.json");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Drafts_are_private_to_their_owner_but_admin_can_see()
    {
        var owner = $"{U("owner")}@example.com"; var other = $"{U("other")}@example.com";
        foreach (var e in new[] { owner, other }) (await Admin().PutAsJsonAsync($"/api/admin/users/{e}", new { role = "Publisher" })).EnsureSuccessStatusCode();
        var id = await CreateDraft(As(owner), Draft(U("mine")));
        Assert.Equal(HttpStatusCode.Forbidden, (await As(other).GetAsync($"/api/plugins/drafts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(other).DeleteAsync($"/api/plugins/drafts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin().GetAsync($"/api/plugins/drafts/{id}")).StatusCode);
        var mine = await As(owner).GetFromJsonAsync<JsonElement>("/api/plugins/drafts");
        Assert.Contains(mine.EnumerateArray(), d => d.GetProperty("id").GetGuid() == id);
        Assert.DoesNotContain((await As(other).GetFromJsonAsync<JsonElement>("/api/plugins/drafts")).EnumerateArray(), d => d.GetProperty("id").GetGuid() == id);
        Assert.Equal(HttpStatusCode.NoContent, (await As(owner).DeleteAsync($"/api/plugins/drafts/{id}")).StatusCode);
    }

    [Fact]
    public async Task Draft_zip_export_then_reimport_creates_an_equivalent_draft()
    {
        var skill = U("skl");
        await PublishApproved("Skill", skill);
        var c = Admin();
        var id = await CreateDraft(c, Draft(U("exp"), ("Skill", $"myregistry/{skill}", "1.0.0")));
        var zip = await c.GetByteArrayAsync($"/api/plugins/drafts/{id}/export");
        var res = await c.PostAsync("/api/plugins/drafts/import", new ByteArrayContent(zip));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var copy = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(id, copy.GetProperty("id").GetGuid());
        Assert.Equal($"myregistry/{skill}", copy.GetProperty("components")[0].GetProperty("slug").GetString());
        Assert.Equal("1.0.0", copy.GetProperty("components")[0].GetProperty("version").GetString());
    }

    [Fact]
    public async Task Import_rejects_foreign_archives()
    {
        var c = Admin();
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) { using var w = new StreamWriter(z.CreateEntry("evil.txt").Open()); w.Write("x"); }
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsync("/api/plugins/drafts/import", new ByteArrayContent(ms.ToArray()))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsync("/api/plugins/drafts/import", new ByteArrayContent([1, 2, 3]))).StatusCode);
    }

    [Fact]
    public async Task Component_search_only_lists_approved_non_plugin_artifacts()
    {
        var approved = U("search"); var pending = U("search");
        await PublishApproved("Skill", approved);
        var c = Admin();
        await c.PostAsJsonAsync("/api/import", new { type = "Skill", @namespace = "myregistry", name = pending, fileName = "SKILL.md", rawContent = $"---\nname: {pending}\ndescription: d\n---\n# x" });
        var list = await c.GetFromJsonAsync<JsonElement>("/api/plugins/components?q=search");
        var slugs = list.EnumerateArray().Select(x => x.GetProperty("slug").GetString()).ToList();
        Assert.Contains($"myregistry/{approved}", slugs);
        Assert.DoesNotContain($"myregistry/{pending}", slugs);
    }

    [Fact]
    public async Task Reader_cannot_use_the_editor()
    {
        var reader = $"{U("rd")}@example.com";
        Assert.Equal(HttpStatusCode.Forbidden, (await As(reader).GetAsync("/api/plugins/drafts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(reader).PostAsJsonAsync("/api/plugins/drafts", Draft("x"))).StatusCode);
    }
}
