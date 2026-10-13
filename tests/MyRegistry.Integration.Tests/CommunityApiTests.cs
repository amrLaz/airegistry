using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class CommunityApiTests(DbFixture db)
{
    private HttpClient Client() => db.Factory.CreateClient();
    private static string Unique(string p) => p + "-" + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');

    private static async Task<Guid> Publish(HttpClient c, string name, string type = "Skill", string? file = null, string? content = null)
    {
        object body = type == "McpServer"
            ? new { type, @namespace = "myregistry", name, fileName = "mcp.json", description = "d",
                rawContent = content ?? "{\"name\":\"a\",\"description\":\"d\",\"command\":\"npx\",\"args\":[\"-y\",\"pkg\"]}" }
            : new { type, @namespace = "myregistry", name, fileName = file ?? "SKILL.md", description = "d",
                rawContent = content ?? $"---\nname: {name}\ndescription: d\n---\n# {name}\nCorps" };
        var res = await c.PostAsJsonAsync("/api/import", body);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
    }

    [Fact]
    public async Task Five_reports_quarantine_the_version_and_moderator_can_release()
    {
        var c = Client();
        var id = await Publish(c, Unique("flagged"));
        await c.PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" });

        JsonElement last = default;
        for (var i = 0; i < 5; i++)
        {
            var r = await c.PostAsJsonAsync($"/api/catalog/{id}/report", new { reason = "DataLeak", details = $"r{i}" });
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            last = await r.Content.ReadFromJsonAsync<JsonElement>();
        }
        Assert.True(last.GetProperty("quarantined").GetBoolean());
        Assert.True((await c.GetFromJsonAsync<JsonElement>($"/api/catalog/{id}")).GetProperty("quarantined").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/catalog/{id}/quarantine/release", null)).StatusCode);
        Assert.False((await c.GetFromJsonAsync<JsonElement>($"/api/catalog/{id}")).GetProperty("quarantined").GetBoolean());
    }

    [Fact]
    public async Task Fewer_than_five_reports_do_not_quarantine()
    {
        var c = Client();
        var id = await Publish(c, Unique("mildly"));
        for (var i = 0; i < 4; i++) await c.PostAsJsonAsync($"/api/catalog/{id}/report", new { reason = "BrokenArtifact" });
        Assert.False((await c.GetFromJsonAsync<JsonElement>($"/api/catalog/{id}")).GetProperty("quarantined").GetBoolean());
    }

    [Fact]
    public async Task Comments_can_be_added_and_listed_and_empty_ones_are_refused()
    {
        var c = Client();
        var id = await Publish(c, Unique("chatty"));
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync($"/api/catalog/{id}/comments", new { body = "Très utile" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/catalog/{id}/comments", new { body = "   " })).StatusCode);
        var list = await c.GetFromJsonAsync<JsonElement>($"/api/catalog/{id}/comments");
        Assert.Equal("Très utile", list[0].GetProperty("body").GetString());
    }

    [Fact]
    public async Task My_publications_lists_mine_and_unpublish_deprecates()
    {
        var c = Client();
        var name = Unique("mine");
        var id = await Publish(c, name);
        await c.PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" });

        var mine = await c.GetFromJsonAsync<JsonElement>("/api/my/publications");
        Assert.Contains(mine.EnumerateArray(), m => m.GetProperty("id").GetGuid() == id);

        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/api/catalog/{id}/unpublish", null)).StatusCode);
        Assert.Equal("Deprecated", (await c.GetFromJsonAsync<JsonElement>($"/api/catalog/{id}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Exports_cursorrules_claude_desktop_and_audit_prompt()
    {
        var c = Client();
        var skill = await Publish(c, Unique("exp"));
        var rules = await c.GetStringAsync($"/api/catalog/{skill}/export/cursorrules");
        Assert.StartsWith("# exp-", rules);
        Assert.DoesNotContain("description:", rules);

        var audit = await c.GetStringAsync($"/api/catalog/{skill}/audit-prompt");
        Assert.Contains("Injection de prompt", audit);
        Assert.Contains("SKILL.md", audit);

        var mcp = await Publish(c, Unique("srv"), "McpServer");
        var cfg = await c.GetFromJsonAsync<JsonElement>($"/api/catalog/{mcp}/export/claude-desktop");
        Assert.Equal("npx", cfg.GetProperty("mcpServers").EnumerateObject().First().Value.GetProperty("command").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/api/catalog/{skill}/export/claude-desktop")).StatusCode);
    }

    [Fact]
    public async Task Suggested_version_follows_the_family()
    {
        var c = Client();
        var name = Unique("sug");
        var fresh = await c.GetFromJsonAsync<JsonElement>($"/api/cli/publish/suggested-version?ns=myregistry&name={name}&type=Skill");
        Assert.Equal("1.0.0", fresh.GetProperty("version").GetString());
        await Publish(c, name);
        var next = await c.GetFromJsonAsync<JsonElement>($"/api/cli/publish/suggested-version?ns=myregistry&name={name}&type=Skill");
        Assert.Equal("1.0.1", next.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Report_on_unknown_version_is_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync($"/api/catalog/{Guid.NewGuid()}/report", new { reason = "DataLeak" })).StatusCode);

    [Fact]
    public async Task By_name_resolves_latest_approved_version_only()
    {
        var c = Client();
        var name = Unique("byname");
        var id = await Publish(c, name);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/catalog/by-name/myregistry/{name}")).StatusCode); // Pending : invisible
        await c.PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" });
        var d = await c.GetFromJsonAsync<JsonElement>($"/api/catalog/by-name/myregistry/{name}");
        Assert.Equal(id, d.GetProperty("id").GetGuid());
        Assert.Equal("Skill", d.GetProperty("type").GetString());
    }
}
