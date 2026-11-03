using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class NotificationApiTests(DbFixture db)
{
    private HttpClient Admin() => db.Factory.CreateClient();
    private HttpClient As(string email)
    {
        var c = db.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", email);
        return c;
    }
    private static string U(string p) => p + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');

    private async Task SetRole(string email, string role) =>
        (await Admin().PutAsJsonAsync($"/api/admin/users/{email}", new { role, active = true })).EnsureSuccessStatusCode();

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

    private static async Task<JsonElement> List(HttpClient c, bool archived = false) =>
        await c.GetFromJsonAsync<JsonElement>($"/api/notifications?includeArchived={archived.ToString().ToLower()}");

    private static async Task<int> Unread(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/notifications/unread-count")).GetProperty("count").GetInt32();

    [Fact]
    public async Task Moderators_are_told_of_a_submission_and_the_author_of_the_decision()
    {
        var mod = $"{U("mod")}@example.com"; var author = $"{U("pub")}@example.com";
        await SetRole(mod, "Moderator"); await SetRole(author, "Publisher");

        var id = await Publish(As(author), U("notif"));
        var modList = await List(As(mod));
        Assert.Contains(modList.EnumerateArray(), n => n.GetProperty("kind").GetString() == "NewSubmission" && n.GetProperty("link").GetString() == $"/artifact/{id}");
        Assert.Empty((await List(As(author))).EnumerateArray().Where(n => n.GetProperty("kind").GetString() == "NewSubmission"));

        (await As(mod).PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" })).EnsureSuccessStatusCode();
        var authorList = await List(As(author));
        var n = Assert.Single(authorList.EnumerateArray(), x => x.GetProperty("kind").GetString() == "StatusChanged");
        Assert.Equal("Approved", n.GetProperty("detail").GetString());
        Assert.False(n.GetProperty("read").GetBoolean());
    }

    [Fact]
    public async Task Acting_on_your_own_artifact_does_not_notify_yourself()
    {
        var admin = Admin();
        var id = await Publish(admin, U("self"));
        (await admin.PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" })).EnsureSuccessStatusCode();
        Assert.DoesNotContain((await List(admin)).EnumerateArray(), n => n.GetProperty("link").GetString() == $"/artifact/{id}");
    }

    [Fact]
    public async Task Read_archive_delete_and_bulk_actions_are_scoped_to_the_user()
    {
        var mod = $"{U("mod")}@example.com"; var author = $"{U("pub")}@example.com"; var other = $"{U("oth")}@example.com";
        await SetRole(mod, "Moderator"); await SetRole(author, "Publisher");
        await Publish(As(author), U("a")); await Publish(As(author), U("b")); await Publish(As(author), U("c"));
        var c = As(mod);
        Assert.Equal(3, await Unread(c));
        var ids = (await List(c)).EnumerateArray().Select(n => n.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(HttpStatusCode.NotFound, (await As(other).PatchAsync($"/api/notifications/{ids[0]}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PatchAsync($"/api/notifications/{ids[0]}/read", null)).StatusCode);
        Assert.Equal(2, await Unread(c));

        Assert.Equal(HttpStatusCode.NoContent, (await c.PatchAsync($"/api/notifications/{ids[1]}/archive", null)).StatusCode);
        Assert.Equal(2, (await List(c)).GetArrayLength());
        Assert.Equal(3, (await List(c, archived: true)).GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/notifications/{ids[2]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/notifications/{ids[2]}")).StatusCode);

        (await c.PostAsync("/api/notifications/mark-all-read", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, await Unread(c));
        (await c.PostAsync("/api/notifications/delete-all", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await List(c, archived: true)).GetArrayLength());
    }

    [Fact]
    public async Task A_disabled_kind_is_not_delivered_and_preferences_round_trip()
    {
        var mod = $"{U("mod")}@example.com"; var author = $"{U("pub")}@example.com";
        await SetRole(mod, "Moderator"); await SetRole(author, "Publisher");
        var c = As(mod);

        var defaults = await c.GetFromJsonAsync<JsonElement>("/api/notifications/preferences");
        Assert.True(defaults.GetProperty("NewSubmission").GetBoolean());

        var patched = await (await c.PatchAsJsonAsync("/api/notifications/preferences", new Dictionary<string, bool> { ["NewSubmission"] = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(patched.GetProperty("NewSubmission").GetBoolean());
        Assert.True(patched.GetProperty("StatusChanged").GetBoolean());

        await Publish(As(author), U("muted"));
        Assert.Equal(0, await Unread(c));
    }
}
