using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MyRegistry.Application;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class CliAuthTests(DbFixture db)
{
    private HttpClient Admin() => db.Factory.CreateClient();
    private HttpClient NoRedirect() => db.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    private HttpClient As(string email)
    {
        var c = db.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", email);
        return c;
    }
    private HttpClient Bearer(string token, string? impersonate = null)
    {
        var c = db.Factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (impersonate is not null) c.DefaultRequestHeaders.Add("X-Dev-User", impersonate);
        return c;
    }
    private static string U(string p) => p + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');
    private async Task SetRole(string email, string role) =>
        (await Admin().PutAsJsonAsync($"/api/admin/users/{email}", new { role, active = true })).EnsureSuccessStatusCode();

    private static string TokenFrom(HttpResponseMessage r)
    {
        var q = System.Web.HttpUtility.ParseQueryString(r.Headers.Location!.Query);
        return q["token"]!;
    }

    [Fact]
    public async Task Browser_flow_redirects_the_token_and_state_to_the_local_callback()
    {
        var res = await NoRedirect().GetAsync("/cli/auth?callback=http://localhost:54123/cb&state=abcdEFGH1234");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("localhost:54123", res.Headers.Location!.Authority);
        Assert.Equal("/cb", res.Headers.Location.AbsolutePath);
        Assert.Equal("abcdEFGH1234", System.Web.HttpUtility.ParseQueryString(res.Headers.Location.Query)["state"]);

        var me = await Bearer(TokenFrom(res)).GetFromJsonAsync<JsonElement>("/api/me");
        Assert.True(me.GetProperty("authenticated").GetBoolean());
        Assert.Equal("dev@example.com", me.GetProperty("email").GetString());
    }

    [Theory]
    [InlineData("http://evil.example.com:5000/cb", "abcdEFGH1234")]
    [InlineData("https://localhost:5000/cb", "abcdEFGH1234")]
    [InlineData("http://localhost:5000/cb", "short")]
    [InlineData("", "abcdEFGH1234")]
    public async Task Unsafe_callbacks_and_states_are_refused(string callback, string state) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await NoRedirect().GetAsync($"/cli/auth?callback={Uri.EscapeDataString(callback)}&state={state}")).StatusCode);

    [Fact]
    public async Task A_token_takes_precedence_over_impersonation_and_an_unknown_token_never_falls_back_to_the_default_user()
    {
        var reader = $"{U("rd")}@example.com";
        await SetRole(reader, "Reader");
        var issued = await IssueFor(reader, "interactive", TimeSpan.FromMinutes(1));
        var me = await Bearer(issued, impersonate: "dev@example.com").GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal(reader, me.GetProperty("email").GetString());

        var bad = await Bearer("mrt_doesnotexist").GetFromJsonAsync<JsonElement>("/api/me");
        Assert.False(bad.GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer("mrt_doesnotexist").PostAsJsonAsync("/api/import", new { type = "Skill" })).StatusCode);
    }

    private async Task<string> IssueFor(string email, string kind, TimeSpan ttl)
    {
        using var scope = db.Factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<CliTokenService>().IssueAsync(email, kind, ttl, "test")).Token;
    }

    [Fact]
    public async Task Expired_and_deactivated_tokens_are_rejected()
    {
        var u = $"{U("ex")}@example.com";
        await SetRole(u, "Publisher");
        var expired = await IssueFor(u, "interactive", TimeSpan.FromSeconds(-1));
        Assert.False((await Bearer(expired).GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("authenticated").GetBoolean());

        var live = await IssueFor(u, "interactive", TimeSpan.FromMinutes(1));
        Assert.True((await Bearer(live).GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("authenticated").GetBoolean());
        (await Admin().PutAsJsonAsync($"/api/admin/users/{u}", new { role = "Publisher", active = false })).EnsureSuccessStatusCode();
        Assert.False((await Bearer(live).GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Ci_tokens_are_shown_once_listed_without_value_revocable_and_cannot_mint_others()
    {
        var created = await (await Admin().PostAsJsonAsync("/api/cli/tokens", new { name = "pipeline", days = 7 })).Content.ReadFromJsonAsync<JsonElement>();
        var token = created.GetProperty("token").GetString()!; var id = created.GetProperty("id").GetGuid();
        Assert.StartsWith("mrt_", token);

        var list = await Admin().GetStringAsync("/api/cli/tokens");
        Assert.Contains(id.ToString(), list);
        Assert.DoesNotContain(token, list);

        Assert.True((await Bearer(token).GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await Bearer(token).PostAsJsonAsync("/api/cli/tokens", new { name = "child" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Admin().DeleteAsync($"/api/cli/tokens/{id}")).StatusCode);
        Assert.False((await Bearer(token).GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await As($"{U("o")}@example.com").DeleteAsync($"/api/cli/tokens/{id}")).StatusCode); // simple Reader
    }

    [Fact]
    public async Task Edit_source_is_for_the_author_or_an_admin_and_includes_pending_versions()
    {
        var author = $"{U("au")}@example.com"; var other = $"{U("ot")}@example.com"; var name = "edit-" + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');
        await SetRole(author, "Publisher"); await SetRole(other, "Publisher");
        (await As(author).PostAsJsonAsync("/api/import", new
        {
            type = "Skill", @namespace = "myregistry", name, fileName = "SKILL.md", description = "d",
            rawContent = $"---\nname: {name}\ndescription: d\n---\n# {name}", additionalFiles = new Dictionary<string, string> { ["README.md"] = "# r" },
        })).EnsureSuccessStatusCode();

        var src = await As(author).GetFromJsonAsync<JsonElement>($"/api/cli/edit/source?ns=myregistry&name={name}");
        Assert.Equal("Pending", src.GetProperty("status").GetString());
        Assert.Equal(["README.md", "SKILL.md"], src.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToArray());

        Assert.Equal(HttpStatusCode.OK, (await Admin().GetAsync($"/api/cli/edit/source?ns=myregistry&name={name}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(other).GetAsync($"/api/cli/edit/source?ns=myregistry&name={name}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync("/api/cli/edit/source?ns=myregistry&name=inexistant-zzz")).StatusCode);

        var id = src.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await As(author).GetAsync($"/api/cli/edit/archive/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(other).GetAsync($"/api/cli/edit/archive/{id}")).StatusCode);
    }
}
