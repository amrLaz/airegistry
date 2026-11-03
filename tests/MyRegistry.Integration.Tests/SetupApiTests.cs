using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class SetupApiTests(DbFixture db)
{
    private HttpClient Admin() => db.Factory.CreateClient();
    private HttpClient As(string email)
    {
        var c = db.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", email);
        return c;
    }

    [Fact]
    public async Task Setup_endpoints_are_admin_only()
    {
        var reader = As($"rd{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/setup/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/setup/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/setup/identity", new { title = "x", @namespace = "ab" })).StatusCode);
    }

    [Fact]
    public async Task Status_is_consistent_with_the_rule_and_identity_round_trips_through_the_settings()
    {
        var s = await Admin().GetFromJsonAsync<JsonElement>("/api/setup/status");
        Assert.Equal(!s.GetProperty("completed").GetBoolean() && s.GetProperty("artifacts").GetInt32() == 0, s.GetProperty("needed").GetBoolean());

        // On réécrit les valeurs courantes (la base est partagée entre tests) en ajoutant un courriel de contact.
        var res = await Admin().PutAsJsonAsync("/api/setup/identity", new { title = s.GetProperty("title").GetString(), @namespace = s.GetProperty("defaultNamespace").GetString(), contactEmail = "registre@example.com", publicUrl = "" });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal("registre@example.com", (await Admin().GetFromJsonAsync<JsonElement>("/api/setup/status")).GetProperty("contactEmail").GetString());
    }

    [Theory]
    [InlineData("", "myregistry", "", "title")]
    [InlineData("Mon registre", "Pas Valide", "", "default_namespace")]
    [InlineData("Mon registre", "myregistry", "pas-un-mail", "contact_email")]
    [InlineData("Mon registre", "myregistry", "", "public_url")]
    public async Task Invalid_identity_is_a_422_naming_the_field(string title, string ns, string mail, string field)
    {
        var res = await Admin().PutAsJsonAsync("/api/setup/identity", new { title, @namespace = ns, contactEmail = mail, publicUrl = field == "public_url" ? "ftp://x" : "" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var errors = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString());
        Assert.Contains(field, errors);
    }

    [Fact]
    public async Task Completing_sets_the_flag_once_and_the_flag_cannot_be_forged_through_settings()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await Admin().PostAsync("/api/setup/complete", null)).StatusCode);
        var s = await Admin().GetFromJsonAsync<JsonElement>("/api/setup/status");
        Assert.True(s.GetProperty("completed").GetBoolean());
        Assert.False(s.GetProperty("needed").GetBoolean());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Admin().PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string> { ["setup_completed"] = "false" })).StatusCode);
    }
}
