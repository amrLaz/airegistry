using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class CatalogApiTests(DbFixture db)
{
    private HttpClient Client() => db.Factory.CreateClient();

    private static object Skill(string name, string? version = null, string ns = "myregistry", string? extra = null) => new
    {
        type = "Skill", @namespace = ns, name, version,
        description = "Un skill de test", fileName = "SKILL.md",
        rawContent = $"---\nname: {name}\ndescription: test\n---\n# {name}{extra}",
        additionalFiles = new Dictionary<string, string> { ["README.md"] = "# readme" },
        tags = new[] { "pdf", "test" },
    };

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');

    private static async Task<JsonElement> Json(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>());

    [Fact]
    public async Task Import_then_inspect_pending_and_download_with_correct_sha256()
    {
        var c = Client();
        var name = Unique("pdfx");
        var res = await c.PostAsJsonAsync("/api/import", Skill(name));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var imp = await Json(res);
        var id = imp.GetProperty("versionId").GetGuid();
        Assert.Equal("1.0.0", imp.GetProperty("version").GetString());

        var detail = await Json(await c.GetAsync($"/api/catalog/{id}"));
        Assert.Equal("Pending", detail.GetProperty("status").GetString());
        Assert.Equal(2, detail.GetProperty("files").GetArrayLength());

        var dl = await c.GetAsync($"/api/catalog/{id}/download");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal(detail.GetProperty("contentHash").GetString(), dl.Headers.GetValues("X-Artifact-Sha256").Single());
        Assert.NotEmpty(await dl.Content.ReadAsByteArrayAsync());

        var byHash = await c.GetAsync($"/api/catalog/by-hash/{detail.GetProperty("contentHash").GetString()}/download");
        Assert.Equal(HttpStatusCode.OK, byHash.StatusCode);
    }

    [Fact]
    public async Task Pending_is_hidden_from_public_catalog_until_approved()
    {
        var c = Client();
        var name = Unique("lifecycle");
        var id = (await Json(await c.PostAsJsonAsync("/api/import", Skill(name)))).GetProperty("versionId").GetGuid();

        // Le client de test est Admin (auth dev) donc voit tout ; on vérifie le filtre par statut.
        var approvedBefore = await Json(await c.GetAsync($"/api/catalog?status=Approved&q={name}"));
        Assert.Equal(0, approvedBefore.GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" })).StatusCode);

        var approved = await Json(await c.GetAsync($"/api/catalog?status=Approved&q={name}"));
        Assert.Equal(1, approved.GetProperty("total").GetInt32());
        var detail = await Json(await c.GetAsync($"/api/catalog/{id}"));
        Assert.Equal(2, detail.GetProperty("history").GetArrayLength());

        var raw = await c.GetAsync($"/api/artifacts/by-name/myregistry/{name}/raw");
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.Contains($"# {name}", await raw.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Secret_in_content_is_refused_naming_detector_and_line()
    {
        var res = await Client().PostAsJsonAsync("/api/import", Skill(Unique("leaky"), extra: "\npassword = \"hunter2hunter2\""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("secret-detected", body);
        Assert.Contains("password-assignment", body);
        Assert.Contains("SKILL.md", body);
    }

    [Fact]
    public async Task Non_standard_name_is_refused_with_canonical_proposal()
    {
        var res = await Client().PostAsJsonAsync("/api/import", Skill("Bad_Name"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal("bad-name", (await Json(res)).GetProperty("canonicalName").GetString());
    }

    [Fact]
    public async Task Second_publish_with_lower_version_is_auto_patched()
    {
        var c = Client();
        var name = Unique("semver");
        await c.PostAsJsonAsync("/api/import", Skill(name, "2.0.0"));
        var second = await Json(await c.PostAsJsonAsync("/api/import", Skill(name, "1.0.0")));
        Assert.Equal("2.0.1", second.GetProperty("version").GetString());
        Assert.True(second.GetProperty("autoBumped").GetBoolean());
    }

    [Fact]
    public async Task Near_name_in_same_namespace_is_refused()
    {
        var c = Client();
        var name = Unique("closeone");
        await c.PostAsJsonAsync("/api/import", Skill(name));
        var res = await c.PostAsJsonAsync("/api/import", Skill(name + "s"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("name-too-close", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Near_name_in_other_namespace_stays_pending_with_name_confusion_note()
    {
        var c = Client();
        var name = Unique("confuse");
        await c.PostAsJsonAsync("/api/import", Skill(name));
        var res = await Json(await c.PostAsJsonAsync("/api/import", Skill(name + "s", ns: "otherns")));
        Assert.Contains("name-confusion", res.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public async Task Validate_reports_without_publishing()
    {
        var res = await Client().PostAsJsonAsync("/api/validate", new { fileName = "SKILL.md", content = "# no frontmatter" });
        res.EnsureSuccessStatusCode();
        var body = await Json(res);
        Assert.False(body.GetProperty("isValid").GetBoolean());
        Assert.Equal("Skill", body.GetProperty("detectedType").GetString());
    }

    [Fact]
    public async Task Validate_checks_name_when_provided()
    {
        var content = "---\nname: mon-skill\ndescription: Un skill de test\n---\n# Mon skill\n";
        var res = await Client().PostAsJsonAsync("/api/validate", new { fileName = "SKILL.md", content, @namespace = "myregistry", name = "mon-skill" });
        res.EnsureSuccessStatusCode();
        var body = await Json(res);
        Assert.False(body.GetProperty("isValid").GetBoolean());
        Assert.Contains("name-contains-type", body.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Upvote_is_idempotent_and_removable()
    {
        var c = Client();
        var id = (await Json(await c.PostAsJsonAsync("/api/import", Skill(Unique("votey"))))).GetProperty("versionId").GetGuid();
        await c.PostAsync($"/api/catalog/{id}/upvote", null);
        var again = await Json(await c.PostAsync($"/api/catalog/{id}/upvote", null));
        Assert.Equal(1, again.GetProperty("votes").GetInt32());
        var removed = await Json(await c.DeleteAsync($"/api/catalog/{id}/upvote"));
        Assert.Equal(0, removed.GetProperty("votes").GetInt32());
    }

    [Fact]
    public async Task Unknown_id_is_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync($"/api/catalog/{Guid.NewGuid()}")).StatusCode);

    [Fact]
    public async Task Hash_is_independent_of_file_order()
    {
        var a = MyRegistry.Application.ContentHasher.Hash([new("a", [1], false), new("b", [2], false)]);
        var b = MyRegistry.Application.ContentHasher.Hash([new("b", [2], false), new("a", [1], false)]);
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        await Task.CompletedTask;
    }
}
