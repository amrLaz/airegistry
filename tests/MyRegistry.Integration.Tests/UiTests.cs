using System.Net.Http.Json;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class UiTests(DbFixture db)
{
    [Fact]
    public async Task Catalog_page_renders_and_lists_an_imported_artifact()
    {
        var c = db.Factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/import", new
        {
            type = "Skill", @namespace = "myregistry", name = "ui-visible", fileName = "SKILL.md",
            rawContent = "---\nname: ui-visible\ndescription: d\n---\n# x", description = "Visible dans la page",
        });
        var id = (await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("versionId").GetGuid();

        var html = await c.GetStringAsync("/");
        Assert.Contains("MyRegistry", html);
        Assert.Contains("ui-visible", html);

        var detail = await c.GetStringAsync($"/artifact/{id}");
        Assert.Contains("myregistry add ui-visible", detail);
    }

    [Fact]
    public async Task Publish_page_renders() =>
        Assert.Contains("SKILL.md", await db.Factory.CreateClient().GetStringAsync("/publish"));

    [Fact]
    public async Task My_publications_page_lists_published_artifacts()
    {
        var c = db.Factory.CreateClient();
        await c.PostAsJsonAsync("/api/import", new
        {
            type = "Skill", @namespace = "myregistry", name = "ui-mine", fileName = "SKILL.md",
            rawContent = "---\nname: ui-mine\ndescription: d\n---\n# x",
        });
        Assert.Contains("ui-mine", await c.GetStringAsync("/my"));
    }

    [Fact]
    public async Task Detail_page_shows_content_comments_and_report_sections()
    {
        var c = db.Factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/import", new
        {
            type = "Skill", @namespace = "myregistry", name = "ui-detail", fileName = "SKILL.md",
            rawContent = "---\nname: ui-detail\ndescription: d\n---\n# Corps visible",
        });
        var id = (await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("versionId").GetGuid();
        await c.PostAsJsonAsync($"/api/catalog/{id}/comments", new { body = "Commentaire visible" });
        var html = await c.GetStringAsync($"/artifact/{id}");
        Assert.Contains("Corps visible", html);
        Assert.Contains("Commentaire visible", html);
        Assert.Contains("Signaler", html);
    }

    [Theory]
    [InlineData("/admin", "Tableau de bord")]
    [InlineData("/admin/moderation", "Modération")]
    [InlineData("/admin/reports", "Signalements")]
    [InlineData("/admin/users", "dev@example.com")]
    [InlineData("/admin/audit", "Journal d")]
    [InlineData("/admin/config", "Namespace par défaut")]
    public async Task Admin_pages_render_for_admin(string url, string expected) =>
        Assert.Contains(expected, System.Net.WebUtility.HtmlDecode(await db.Factory.CreateClient().GetStringAsync(url)));

    [Fact]
    public async Task Detail_page_shows_governance_contract_form_for_the_author()
    {
        var c = db.Factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/import", new
        {
            type = "Skill", @namespace = "myregistry", name = "ui-gov", fileName = "SKILL.md",
            rawContent = "---\nname: ui-gov\ndescription: d\n---\n# x",
        });
        var id = (await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("versionId").GetGuid();
        var html = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync($"/artifact/{id}"));
        Assert.Contains("Contrat de gouvernance", html);
        Assert.Contains("Niveau de risque", html);
    }
}
