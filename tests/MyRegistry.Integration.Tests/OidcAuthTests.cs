using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MyRegistry.Application;
using MyRegistry.Infrastructure;

namespace MyRegistry.Integration.Tests;

/// <summary>Instance en mode OIDC avec un fournisseur simulé : pas de réseau, métadonnées statiques. L'identité est injectée par un en-tête de test.</summary>
public sealed class OidcFixture : IAsyncLifetime
{
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"myregistry-oidc-{Guid.NewGuid():N}.db");

    public static WebApplicationFactory<Program> Build(string db, Action<IWebHostBuilder>? more = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("LOCAL_DB_PATH", db);
            b.UseSetting("AUTH_MODE", "oidc");
            b.UseSetting("OIDC_TENANT", "contoso.onmicrosoft.com");
            b.UseSetting("OIDC_CLIENT_ID", "client-123");
            b.UseSetting("OIDC_CLIENT_SECRET", "not-a-real-secret");
            b.UseSetting("BOOTSTRAP_ADMINS", "boss@up.coop");
            more?.Invoke(b);
            b.ConfigureServices(s =>
            {
                s.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                    o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration
                    {
                        Issuer = "https://login.test/tenant/v2.0", AuthorizationEndpoint = "https://login.test/tenant/authorize",
                        TokenEndpoint = "https://login.test/tenant/token", EndSessionEndpoint = "https://login.test/tenant/logout",
                    }));
                s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new TestIdentityFilter());
            });
        });

    public async Task InitializeAsync()
    {
        Factory = Build(_db);
        using var scope = Factory.Services.CreateScope();
        await Seeder.MigrateAndSeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public Task DisposeAsync() { Factory.Dispose(); try { File.Delete(_db); } catch { } return Task.CompletedTask; }

    private sealed class TestIdentityFilter : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, n) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-Claims", out var raw))
                {
                    var claims = raw.ToString().Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Select(p => new Claim(p[0], p[1]));
                    ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
                }
                await n();
            });
            next(app);
        };
    }
}

public class OidcAuthTests(OidcFixture fx) : IClassFixture<OidcFixture>
{
    private HttpClient Client(string? claims = null, bool follow = false)
    {
        var c = fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = follow });
        if (claims is not null) c.DefaultRequestHeaders.Add("X-Test-Claims", claims);
        return c;
    }
    private static string U(string p) => p + Guid.NewGuid().ToString("N")[..8];
    private static async Task<JsonElement> Me(HttpClient c) => await c.GetFromJsonAsync<JsonElement>("/api/me");

    [Fact]
    public async Task Startup_fails_with_a_precise_message_when_the_configuration_is_incomplete()
    {
        var path = Path.Combine(Path.GetTempPath(), $"myregistry-oidc-{Guid.NewGuid():N}.db");
        using var broken = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing"); b.UseSetting("LOCAL_DB_PATH", path); b.UseSetting("AUTH_MODE", "oidc"); b.UseSetting("OIDC_CLIENT_ID", "x");
        });
        var ex = Assert.ThrowsAny<Exception>(() => broken.CreateClient());
        var text = ex.ToString();
        Assert.Contains("MYREGISTRY_OIDC_CLIENT_SECRET", text);
        Assert.Contains("MYREGISTRY_OIDC_TENANT", text);
        Assert.DoesNotContain("MYREGISTRY_OIDC_CLIENT_ID,", text);
        try { File.Delete(path); } catch { }
    }

    [Fact]
    public async Task Anonymous_callers_are_unauthenticated_and_get_401_on_protected_api_routes()
    {
        var c = Client();
        Assert.False((await Me(c)).GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/import", new { type = "Skill" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/notifications")).StatusCode);
    }

    [Fact]
    public async Task Dev_impersonation_header_is_ignored_in_oidc_mode()
    {
        var c = Client(); c.DefaultRequestHeaders.Add("X-Dev-User", "boss@up.coop");
        Assert.False((await Me(c)).GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Login_redirects_to_entra_with_code_flow_pkce_and_the_openid_email_scopes()
    {
        var res = await Client().GetAsync("/auth/login?returnUrl=/catalog");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var loc = res.Headers.Location!;
        Assert.Equal("login.test", loc.Host);
        var q = System.Web.HttpUtility.ParseQueryString(loc.Query);
        Assert.Equal("client-123", q["client_id"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(q["code_challenge"]));
        Assert.False(string.IsNullOrEmpty(q["state"]));
        Assert.False(string.IsNullOrEmpty(q["nonce"]));
        Assert.Contains("openid", q["scope"]); Assert.Contains("email", q["scope"]);
        Assert.EndsWith("/signin-oidc", q["redirect_uri"]);
    }

    [Fact]
    public async Task The_ui_requires_a_session_but_health_marketplace_npm_and_public_api_do_not()
    {
        var c = Client();
        var home = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("login.test", home.Headers.Location!.Host);
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/notifications")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/marketplace.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/skills/@acme/rien")).StatusCode);
    }

    [Fact]
    public async Task An_authenticated_user_starts_as_reader_and_a_bootstrap_admin_as_admin()
    {
        var reader = $"{U("rd")}@up.coop";
        var me = await Me(Client($"email={reader}|name=Reader Test"));
        Assert.True(me.GetProperty("authenticated").GetBoolean());
        Assert.Equal(reader, me.GetProperty("email").GetString());
        Assert.Equal("Reader", me.GetProperty("role").GetString());
        Assert.Equal("Reader Test", me.GetProperty("displayName").GetString());

        Assert.Equal("Admin", (await Me(Client("preferred_username=Boss@UP.coop"))).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Roles_come_from_the_database_not_from_the_token_or_the_bootstrap_list_after_first_login()
    {
        var admin = Client("email=boss@up.coop");
        var user = $"{U("pub")}@up.coop";
        Assert.Equal("Reader", (await Me(Client($"email={user}"))).GetProperty("role").GetString());
        (await admin.PutAsJsonAsync($"/api/admin/users/{user}", new { role = "Publisher", active = true })).EnsureSuccessStatusCode();
        Assert.Equal("Publisher", (await Me(Client($"email={user}|roles=Admin"))).GetProperty("role").GetString());

        (await admin.PutAsJsonAsync($"/api/admin/users/{user}", new { role = "Publisher", active = false })).EnsureSuccessStatusCode();
        Assert.False((await Me(Client($"email={user}"))).GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await Client($"email={user}").PostAsJsonAsync("/api/import", new { type = "Skill" })).StatusCode);
    }

    [Fact]
    public async Task A_principal_without_a_usable_email_is_not_authenticated()
    {
        Assert.False((await Me(Client("sub=1234|oid=abcd|name=Sans mail"))).GetProperty("authenticated").GetBoolean());
        Assert.False((await Me(Client("email=pas-un-mail"))).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Cli_tokens_work_in_oidc_mode_and_unknown_tokens_do_not_fall_back_to_a_session()
    {
        var email = $"{U("cli")}@up.coop";
        using (var scope = fx.Factory.Services.CreateScope())
        {
            var dir = scope.ServiceProvider.GetRequiredService<IUserDirectory>(); dir.Resolve(email, MyRegistry.Domain.Role.Publisher);
        }
        string token;
        using (var scope = fx.Factory.Services.CreateScope())
            token = (await scope.ServiceProvider.GetRequiredService<CliTokenService>().IssueAsync(email, "ci", TimeSpan.FromDays(1), "t")).Token;

        var c = Client(); c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var me = await Me(c);
        Assert.Equal(email, me.GetProperty("email").GetString()); Assert.Equal("Publisher", me.GetProperty("role").GetString());

        var bad = Client("email=boss@up.coop"); bad.DefaultRequestHeaders.Authorization = new("Bearer", "mrt_nope");
        Assert.False((await Me(bad)).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Logout_redirects_to_the_identity_provider_end_session_endpoint()
    {
        var res = await Client("email=boss@up.coop").GetAsync("/auth/logout");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("login.test", res.Headers.Location!.Host);
        Assert.Contains("/logout", res.Headers.Location.AbsolutePath);
    }

    [Fact]
    public async Task The_cli_browser_flow_sends_an_anonymous_browser_to_login_then_redirects_the_token_when_signed_in()
    {
        var url = "/cli/auth?callback=http://localhost:54123/cb&state=abcdEFGH1234";
        var anon = await Client().GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, anon.StatusCode);
        Assert.Equal("login.test", anon.Headers.Location!.Host);

        var signed = await Client("email=boss@up.coop").GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, signed.StatusCode);
        Assert.Equal("localhost:54123", signed.Headers.Location!.Authority);
        Assert.Contains("token=mrt_", signed.Headers.Location.Query);
    }
}

public class OidcDomainRestrictionTests : IAsyncLifetime
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"myregistry-oidc-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program> _f = null!;

    public async Task InitializeAsync()
    {
        _f = OidcFixture.Build(_db, b => b.UseSetting("OIDC_ALLOWED_DOMAINS", "up.coop"));
        using var scope = _f.Services.CreateScope();
        await Seeder.MigrateAndSeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    public Task DisposeAsync() { _f.Dispose(); try { File.Delete(_db); } catch { } return Task.CompletedTask; }

    private async Task<bool> Authenticated(string email)
    {
        var c = _f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add("X-Test-Claims", $"email={email}");
        return (await c.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("authenticated").GetBoolean();
    }

    [Fact]
    public async Task Only_listed_domains_are_accepted()
    {
        Assert.True(await Authenticated("someone@up.coop"));
        Assert.False(await Authenticated("someone@evil-up.coop"));
        Assert.False(await Authenticated("someone@sub.up.coop"));
        Assert.False(await Authenticated("someone@gmail.com"));
    }
}
