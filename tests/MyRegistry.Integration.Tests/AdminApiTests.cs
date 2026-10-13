using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class AdminApiTests(DbFixture db)
{
    private HttpClient Admin() => db.Factory.CreateClient(); // utilisateur dev par défaut : Admin
    private HttpClient As(string email)
    {
        var c = db.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", email);
        return c;
    }
    private static string U(string p) => p + Guid.NewGuid().ToString("N")[..6];

    private static async Task<Guid> Publish(HttpClient c, string name, string? version = null)
    {
        var res = await c.PostAsJsonAsync("/api/import", new
        {
            type = "Skill", @namespace = "myregistry", name, version, fileName = "SKILL.md", description = "d",
            rawContent = $"---\nname: {name}\ndescription: d\n---\n# {name}",
        });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
    }

    private async Task SetRole(string email, string role, bool active = true) =>
        (await Admin().PutAsJsonAsync($"/api/admin/users/{email}", new { role, active })).EnsureSuccessStatusCode();

    [Fact]
    public async Task New_impersonated_user_is_reader_and_cannot_publish_or_administer()
    {
        var email = $"{U("reader")}@example.com";
        var c = As(email);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/import", new { type = "Skill" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/catalog")).StatusCode); // consultation anonyme
    }

    [Fact]
    public async Task Admin_can_grant_a_role_which_takes_effect_immediately()
    {
        var email = $"{U("pub")}@example.com";
        Assert.Equal(HttpStatusCode.Forbidden, (await As(email).GetAsync("/api/my/publications")).StatusCode);
        await SetRole(email, "Publisher");
        Assert.Equal(HttpStatusCode.OK, (await As(email).GetAsync("/api/my/publications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(email).GetAsync("/api/admin/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Moderator_can_moderate_but_not_manage_users()
    {
        var email = $"{U("mod")}@example.com";
        await SetRole(email, "Moderator");
        var mod = As(email);
        Assert.Equal(HttpStatusCode.OK, (await mod.GetAsync("/api/admin/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mod.GetAsync("/api/admin/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mod.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Inactive_user_loses_all_privileges()
    {
        var email = $"{U("gone")}@example.com";
        await SetRole(email, "Admin");
        Assert.Equal(HttpStatusCode.OK, (await As(email).GetAsync("/api/admin/users")).StatusCode);
        await SetRole(email, "Admin", active: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(email).GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Last_active_admin_cannot_be_demoted()
    {
        // Avec deux admins on peut en rétrograder un ; on vérifie la protection sur un annuaire où il ne reste qu'un admin.
        var users = await Admin().GetFromJsonAsync<JsonElement>("/api/admin/users");
        var admins = users.EnumerateArray().Where(u => u.GetProperty("role").GetString() == "Admin" && u.GetProperty("active").GetBoolean())
            .Select(u => u.GetProperty("email").GetString()!).ToList();
        foreach (var extra in admins.Skip(1).Where(e => e != "dev@example.com")) await SetRole(extra, "Reader");
        var res = await Admin().PutAsJsonAsync("/api/admin/users/dev@example.com", new { role = "Reader", active = true });
        if (res.StatusCode == HttpStatusCode.OK) { await SetRole("dev@example.com", "Admin"); Assert.Fail("Le dernier admin a pu être rétrogradé."); }
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        // restaure les autres admins pour ne pas perturber les autres tests
        foreach (var extra in admins.Where(e => e != "dev@example.com")) await SetRole(extra, "Admin");
    }

    [Fact]
    public async Task Role_changes_are_audited()
    {
        var email = $"{U("audited")}@example.com";
        await SetRole(email, "Publisher");
        var audit = await Admin().GetFromJsonAsync<JsonElement>("/api/audit?action=user-update&pageSize=200");
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), a => a.GetProperty("entityId").GetString() == email.ToLowerInvariant());
    }

    [Fact]
    public async Task Reject_requires_a_reason_and_deprecates_with_history()
    {
        var c = Admin();
        var id = await Publish(c, U("rej"));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/catalog/{id}/reject", new { reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync($"/api/catalog/{id}/reject", new { reason = "Doublon" })).StatusCode);
        var d = await c.GetFromJsonAsync<JsonElement>($"/api/catalog/{id}");
        Assert.Equal("Deprecated", d.GetProperty("status").GetString());
        Assert.Contains("Doublon", d.GetProperty("history").EnumerateArray().Last().GetProperty("note").GetString());
    }

    [Fact]
    public async Task Delete_protects_latest_and_only_version()
    {
        var c = Admin();
        var name = U("del");
        var v1 = await Publish(c, name, "1.0.0");
        var res = await c.DeleteAsync($"/api/catalog/{v1}");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("seule version", await res.Content.ReadAsStringAsync());

        var v2 = await Publish(c, name, "2.0.0");
        var latest = await c.DeleteAsync($"/api/catalog/{v2}");
        Assert.Equal(HttpStatusCode.Conflict, latest.StatusCode);
        Assert.Contains("dernière version", await latest.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/catalog/{v1}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/catalog/{v1}")).StatusCode);
    }

    [Fact]
    public async Task Moderation_list_filters_by_status_and_search()
    {
        var c = Admin();
        var name = U("modlist");
        var id = await Publish(c, name);
        var pending = await c.GetFromJsonAsync<JsonElement>($"/api/admin/versions?status=Pending&q={name}");
        Assert.Equal(1, pending.GetProperty("total").GetInt32());
        await c.PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" });
        Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>($"/api/admin/versions?status=Pending&q={name}")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await c.GetFromJsonAsync<JsonElement>($"/api/admin/versions?status=Approved&q={name}")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Reports_can_be_listed_and_resolved()
    {
        var c = Admin();
        var id = await Publish(c, U("repd"));
        await As($"{U("rep")}@example.com").PostAsJsonAsync($"/api/catalog/{id}/report", new { reason = "BrokenArtifact", details = "cassé" });
        var open = await c.GetFromJsonAsync<JsonElement>("/api/admin/reports?status=Open");
        var row = open.EnumerateArray().First(r => r.GetProperty("versionId").GetGuid() == id);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync($"/api/admin/reports/{row.GetProperty("id").GetGuid()}/resolve", new { status = "Resolved" })).StatusCode);
        var again = await c.GetFromJsonAsync<JsonElement>("/api/admin/reports?status=Open");
        Assert.DoesNotContain(again.EnumerateArray(), r => r.GetProperty("versionId").GetGuid() == id);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/admin/reports/{row.GetProperty("id").GetGuid()}/resolve", new { status = "Nope" })).StatusCode);
    }

    [Fact]
    public async Task Dashboard_counts_and_recent_activity()
    {
        var c = Admin();
        await Publish(c, U("dash"));
        var d = await c.GetFromJsonAsync<JsonElement>("/api/admin/dashboard");
        Assert.True(d.GetProperty("versions").GetInt32() >= 1);
        Assert.True(d.GetProperty("byStatus").GetProperty("Pending").GetInt32() >= 1);
        Assert.True(d.GetProperty("byType").GetProperty("Skill").GetInt32() >= 1);
        Assert.True(d.GetProperty("recent").GetArrayLength() > 0);
    }
}
