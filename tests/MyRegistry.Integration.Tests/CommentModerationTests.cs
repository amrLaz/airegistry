using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class CommentModerationTests(DbFixture db)
{
    private HttpClient Admin() => db.Factory.CreateClient();
    private HttpClient As(string email)
    {
        var c = db.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", email);
        return c;
    }
    private static string U(string p) => p + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');

    private async Task<Guid> PublishApproved(string name)
    {
        var admin = Admin();
        var res = await admin.PostAsJsonAsync("/api/import", new
        {
            type = "Skill", @namespace = "myregistry", name, fileName = "SKILL.md", description = "d",
            rawContent = $"---\nname: {name}\ndescription: d\n---\n# {name}",
        });
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
        (await admin.PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" })).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task<Guid> Comment(HttpClient c, Guid id, string body)
    {
        var res = await c.PostAsJsonAsync($"/api/catalog/{id}/comments", new { body });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<int> Count(HttpClient c, Guid id) =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/catalog/{id}/comments")).GetArrayLength();

    [Fact]
    public async Task Author_and_moderator_can_delete_but_other_readers_cannot()
    {
        var id = await PublishApproved(U("cm"));
        var author = As($"{U("a")}@example.com"); var other = As($"{U("o")}@example.com");
        var mine = await Comment(author, id, "à moi");
        var theirs = await Comment(other, id, "à eux");

        Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync($"/api/catalog/{id}/comments/{mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await author.DeleteAsync($"/api/catalog/{id}/comments/{mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await author.DeleteAsync($"/api/catalog/{id}/comments/{mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin().DeleteAsync($"/api/catalog/{id}/comments/{theirs}")).StatusCode);
        Assert.Equal(0, await Count(Admin(), id));
    }

    [Fact]
    public async Task Three_distinct_reporters_hide_the_comment_and_duplicates_do_not_count()
    {
        var id = await PublishApproved(U("cr"));
        var author = As($"{U("a")}@example.com");
        var cid = await Comment(author, id, "contenu douteux");
        var r1 = As($"{U("r")}@example.com"); var r2 = As($"{U("r")}@example.com"); var r3 = As($"{U("r")}@example.com");

        Assert.Equal(HttpStatusCode.BadRequest, (await author.PostAsJsonAsync($"/api/catalog/{id}/comments/{cid}/report", new { reason = "x" })).StatusCode);

        for (var i = 0; i < 3; i++) Assert.False((await (await r1.PostAsJsonAsync($"/api/catalog/{id}/comments/{cid}/report", new { reason = "spam" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hidden").GetBoolean());
        Assert.False((await (await r2.PostAsync($"/api/catalog/{id}/comments/{cid}/report", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hidden").GetBoolean());
        Assert.Equal(1, await Count(Admin(), id));

        Assert.True((await (await r3.PostAsync($"/api/catalog/{id}/comments/{cid}/report", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hidden").GetBoolean());
        Assert.Equal(0, await Count(Admin(), id));
    }

    [Fact]
    public async Task Unknown_comment_is_a_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().PostAsync($"/api/catalog/{await PublishApproved(U("cn"))}/comments/{Guid.NewGuid()}/report", null)).StatusCode);
}
