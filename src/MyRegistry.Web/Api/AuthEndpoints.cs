using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MyRegistry.Application;
using MyRegistry.Web.Services;

namespace MyRegistry.Web.Api;

public static class AuthEndpoints
{
    public static void MapAuthApi(this WebApplication app)
    {
        var auth = app.MapGroup("/auth").WithTags("Authentification");

        // En mode développement il n'y a rien à ouvrir : on revient simplement à la page demandée.
        auth.MapGet("/login", async (string? returnUrl, HttpContext ctx, AuthModeOptions mode) =>
        {
            var target = OidcRules.SafeReturnUrl(returnUrl);
            if (!mode.Oidc || ctx.User.Identity?.IsAuthenticated == true) return Results.LocalRedirect(target);
            await ctx.ChallengeAsync(OidcSetup.Scheme, new AuthenticationProperties { RedirectUri = target });
            return Results.Empty;
        }).WithName("Login");

        auth.MapGet("/logout", async (HttpContext ctx, AuthModeOptions mode) =>
        {
            if (!mode.Oidc) return Results.LocalRedirect("/");
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignOutAsync(OidcSetup.Scheme, new AuthenticationProperties { RedirectUri = "/" });
            return Results.Empty;
        }).WithName("Logout");

        auth.MapGet("/error", () => Results.Content(
            "<!doctype html><meta charset=utf-8><title>Connexion refusée</title><body style=\"font-family:sans-serif;max-width:40rem;margin:4rem auto\"><h1>Connexion refusée</h1><p>La connexion Entra ID a échoué ou ce compte n'est pas autorisé (adresse e-mail absente ou domaine non autorisé).</p><p><a href=\"/auth/login\">Réessayer</a></p></body>",
            "text/html; charset=utf-8", statusCode: 403)).WithName("AuthError");
    }
}
