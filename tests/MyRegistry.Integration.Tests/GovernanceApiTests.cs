using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class GovernanceApiTests(DbFixture db)
{
    private HttpClient Admin() => db.Factory.CreateClient();
    private HttpClient As(string email)
    {
        var c = db.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", email);
        return c;
    }
    private static string U(string p) => p + Guid.NewGuid().ToString("N")[..6];

    private async Task<string> MakeUser(string role)
    {
        var email = $"{U(role.ToLower())}@example.com";
        (await Admin().PutAsJsonAsync($"/api/admin/users/{email}", new { role })).EnsureSuccessStatusCode();
        return email;
    }

    private static async Task<Guid> Publish(HttpClient c, string name)
    {
        var res = await c.PostAsJsonAsync("/api/import", new
        {
            type = "Skill", @namespace = "myregistry", name, fileName = "SKILL.md", description = "d",
            rawContent = $"---\nname: {name}\ndescription: d\n---\n# {name}",
        });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
    }

    private static object Contract(string owner, string[]? caps = null, bool review = false) =>
        new { ownerId = owner, risk = "High", dataClass = "Confidential", reviewEveryDays = 90, capabilities = caps ?? ["Network Access"], markReviewed = review };

    [Fact]
    public async Task Undefined_contract_defaults_to_author_as_owner()
    {
        var author = await MakeUser("Publisher");
        var id = await Publish(As(author), U("gov"));
        var g = await As(author).GetFromJsonAsync<JsonElement>($"/api/catalog/{id}/governance");
        Assert.False(g.GetProperty("defined").GetBoolean());
        Assert.Equal(author, g.GetProperty("owner").GetString());
        Assert.True(g.GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task Author_can_save_and_capabilities_are_normalized()
    {
        var author = await MakeUser("Publisher");
        var c = As(author);
        var id = await Publish(c, U("gov"));
        var res = await c.PostAsJsonAsync($"/api/catalog/{id}/governance", Contract(author, ["Network Access", "network-access", "File Write"]));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var g = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("High", g.GetProperty("risk").GetString());
        Assert.Equal("Confidential", g.GetProperty("dataClass").GetString());
        Assert.Equal(["file-write", "network-access"], g.GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.True((await c.GetFromJsonAsync<JsonElement>($"/api/catalog/{id}/governance")).GetProperty("defined").GetBoolean());
    }

    [Fact]
    public async Task Another_publisher_cannot_edit_but_security_validator_and_admin_can()
    {
        var author = await MakeUser("Publisher");
        var id = await Publish(As(author), U("gov"));
        var other = await MakeUser("Publisher");
        Assert.Equal(HttpStatusCode.Forbidden, (await As(other).PostAsJsonAsync($"/api/catalog/{id}/governance", Contract(author))).StatusCode);
        Assert.False((await As(other).GetFromJsonAsync<JsonElement>($"/api/catalog/{id}/governance")).GetProperty("canEdit").GetBoolean());

        var sv = await MakeUser("SecurityValidator");
        Assert.Equal(HttpStatusCode.OK, (await As(sv).PostAsJsonAsync($"/api/catalog/{id}/governance", Contract(author))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin().PostAsJsonAsync($"/api/catalog/{id}/governance", Contract(author))).StatusCode);
        var mod = await MakeUser("Moderator");
        Assert.Equal(HttpStatusCode.Forbidden, (await As(mod).PostAsJsonAsync($"/api/catalog/{id}/governance", Contract(author))).StatusCode);
    }

    [Fact]
    public async Task Invalid_contract_is_422_with_errors()
    {
        var c = Admin();
        var id = await Publish(c, U("gov"));
        var res = await c.PostAsJsonAsync($"/api/catalog/{id}/governance", new { ownerId = "pas-un-mail", risk = "Low", dataClass = "Public", reviewEveryDays = 9999 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("owner", body);
        Assert.Contains("review-every-days", body);
    }

    [Fact]
    public async Task Mark_reviewed_sets_last_reviewed_and_is_audited()
    {
        var c = Admin();
        var id = await Publish(c, U("gov"));
        var g = await (await c.PostAsJsonAsync($"/api/catalog/{id}/governance", Contract("owner@example.com", review: true))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(JsonValueKind.Null, g.GetProperty("lastReviewed").ValueKind);
        Assert.False(g.GetProperty("reviewDue").GetBoolean());
        var audit = await c.GetFromJsonAsync<JsonElement>("/api/audit?action=governance-reviewed&pageSize=200");
        Assert.True(audit.GetProperty("total").GetInt32() >= 1);
    }

    [Fact]
    public async Task Capabilities_inventory_is_the_union_with_counts()
    {
        var c = Admin();
        var cap = U("cap-");
        var a = await Publish(c, U("inv"));
        var b = await Publish(c, U("inv"));
        await c.PostAsJsonAsync($"/api/catalog/{a}/governance", Contract("o@example.com", [cap]));
        await c.PostAsJsonAsync($"/api/catalog/{b}/governance", Contract("o@example.com", [cap, "autre"]));
        var inv = await c.GetFromJsonAsync<JsonElement>("/api/catalog/capabilities-inventory");
        var row = inv.EnumerateArray().First(r => r.GetProperty("capability").GetString() == cap);
        Assert.Equal(2, row.GetProperty("artifacts").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await As(await MakeUser("Reader")).GetAsync("/api/catalog/capabilities-inventory")).StatusCode);
    }

    [Fact]
    public async Task Settings_are_admin_only_validated_audited_and_exposed_by_registry_info()
    {
        var c = Admin();
        Assert.Equal(HttpStatusCode.Forbidden, (await As(await MakeUser("Moderator")).GetAsync("/api/admin/settings")).StatusCode);

        var bad = await c.PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string> { ["default_namespace"] = "Bad_NS", ["title"] = "" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        var original = await c.GetFromJsonAsync<Dictionary<string, string>>("/api/admin/settings");
        try
        {
            var ok = await c.PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string>
            { ["title"] = "Acme Registry", ["default_namespace"] = "acme", ["public_url"] = "https://registry.acme.io" });
            Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
            var info = await c.GetFromJsonAsync<JsonElement>("/api/registry-info");
            Assert.Equal("Acme Registry", info.GetProperty("title").GetString());
            Assert.Equal("acme", info.GetProperty("defaultNamespace").GetString());
            Assert.Equal("https://registry.acme.io", info.GetProperty("publicUrl").GetString());
            var audit = await c.GetFromJsonAsync<JsonElement>("/api/audit?action=settings-update&pageSize=5");
            Assert.True(audit.GetProperty("total").GetInt32() >= 1);
        }
        finally
        {
            await c.PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string>
            { ["title"] = original!["title"], ["default_namespace"] = original["default_namespace"], ["public_url"] = original.GetValueOrDefault("public_url", "") });
        }
    }
}
