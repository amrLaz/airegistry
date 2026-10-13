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
}
