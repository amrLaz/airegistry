using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MyRegistry.Application;

namespace MyRegistry.Web.Services;

public static class OidcSetup
{
    public const string Scheme = OpenIdConnectDefaults.AuthenticationScheme;

    /// <summary>Configure Entra ID. Échoue au démarrage, avec un message précis, si la configuration est incomplète : mieux vaut ne pas démarrer qu'exposer une instance sans login.</summary>
    public static OidcOptions Add(WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var authority = OidcRules.Authority(cfg["OIDC_AUTHORITY"] ?? cfg["OIDC_TENANT"]);
        var clientId = cfg["OIDC_CLIENT_ID"]; var secret = cfg["OIDC_CLIENT_SECRET"];
        var missing = new List<string>();
        if (authority is null) missing.Add("MYREGISTRY_OIDC_TENANT (identifiant de tenant) ou MYREGISTRY_OIDC_AUTHORITY (URL https)");
        if (string.IsNullOrWhiteSpace(clientId)) missing.Add("MYREGISTRY_OIDC_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(secret)) missing.Add("MYREGISTRY_OIDC_CLIENT_SECRET");
        if (missing.Count > 0) throw new InvalidOperationException("Authentification OIDC activée (MYREGISTRY_AUTH_MODE=oidc) mais configuration incomplète : " + string.Join(", ", missing) + ".");

        var options = new OidcOptions { BootstrapAdmins = OidcRules.ParseList(cfg["BOOTSTRAP_ADMINS"]), AllowedDomains = OidcRules.ParseList(cfg["OIDC_ALLOWED_DOMAINS"]) };

        // Derrière l'ingress Azure Container Apps : sans ces en-têtes, l'URI de redirection serait en http et Entra ID la refuserait.
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            o.KnownIPNetworks.Clear(); o.KnownProxies.Clear();
        });

        builder.Services.AddAuthentication(o =>
            {
                o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = Scheme;
            })
            .AddCookie(o =>
            {
                o.Cookie.Name = "myregistry.auth";
                o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Lax; o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = true;
                o.Events.OnRedirectToLogin = ctx => Respond(ctx, 401);
                o.Events.OnRedirectToAccessDenied = ctx => Respond(ctx, 403);
            })
            .AddOpenIdConnect(Scheme, o =>
            {
                o.Authority = authority; o.ClientId = clientId; o.ClientSecret = secret;
                o.ResponseType = OpenIdConnectResponseType.Code; o.UsePkce = true;
                o.Scope.Clear(); o.Scope.Add("openid"); o.Scope.Add("profile"); o.Scope.Add("email");
                o.CallbackPath = "/signin-oidc"; o.SignedOutCallbackPath = "/signout-callback-oidc";
                o.SaveTokens = false; o.GetClaimsFromUserInfoEndpoint = false;
                o.MapInboundClaims = false; o.TokenValidationParameters.NameClaimType = "name";
                o.Events.OnTokenValidated = ctx =>
                {
                    var claims = ctx.Principal!.Claims.Select(c => (c.Type, c.Value));
                    var email = OidcRules.EmailFrom(claims);
                    if (email is null) ctx.Fail("Le compte ne fournit pas d'adresse e-mail exploitable.");
                    else if (!OidcRules.IsDomainAllowed(email, options.AllowedDomains.ToList())) ctx.Fail("Domaine de messagerie non autorisé.");
                    return Task.CompletedTask;
                };
                o.Events.OnRemoteFailure = ctx =>
                {
                    ctx.Response.Redirect("/auth/error");
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                };
            });
        return options;
    }

    private static Task Respond(RedirectContext<CookieAuthenticationOptions> ctx, int status)
    {
        if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = status;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    }

    /// <summary>Chemins ouverts sans connexion : santé, marketplace et surface npm (lus par les outils), API (gérée par rôle), documentation, ressources statiques.</summary>
    public static bool IsPublicPath(PathString path)
    {
        string[] prefixes = ["/api", "/auth", "/signin-oidc", "/signout-callback-oidc", "/health", "/marketplace.json", "/skills", "/scalar", "/openapi",
            "/_framework", "/_content", "/_blazor", "/cli/auth"];
        return prefixes.Any(p => path.StartsWithSegments(p)) || Path.HasExtension(path.Value);
    }
}
