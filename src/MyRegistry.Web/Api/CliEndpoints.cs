using Microsoft.AspNetCore.Authentication;
using MyRegistry.Application;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Api;

public sealed record CliTokenRequest(string? Name, int? Days);

public static class CliEndpoints
{
    public static void MapCliApi(this WebApplication app)
    {
        // Flux navigateur : le jeton (1 minute) n'est remis qu'à un serveur local ouvert par la CLI, avec le « state » reçu.
        app.MapGet("/cli/auth", async (string? callback, string? state, CliTokenService tokens, IAuthProvider auth, HttpContext ctx, MyRegistry.Web.Services.AuthModeOptions mode) =>
        {
            if (!CliAuthRules.IsValidCallback(callback)) return Results.Problem(statusCode: 400, title: "Callback invalide", detail: "Le callback doit être http://localhost:<port>/… (port ≥ 1024).");
            if (!CliAuthRules.IsValidState(state)) return Results.Problem(statusCode: 400, title: "State invalide", detail: "8 à 128 caractères : lettres, chiffres, _ et -.");
            var user = auth.GetCurrentUser();
            if (!user.IsAuthenticated)
            {
                // Connexion Entra ID puis retour sur cette même URL ; en développement il n'y a personne à connecter.
                if (!mode.Oidc || !string.IsNullOrEmpty(user.Email)) return Results.Problem(statusCode: string.IsNullOrEmpty(user.Email) ? 401 : 403, title: "Connexion requise");
                await ctx.ChallengeAsync(MyRegistry.Web.Services.OidcSetup.Scheme, new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = ctx.Request.Path + ctx.Request.QueryString });
                return Results.Empty;
            }
            var issued = await tokens.IssueAsync(user.Email, "interactive", CliAuthRules.InteractiveTtl, null);
            return Results.Redirect(CliAuthRules.RedirectUrl(callback!, issued.Token, state!));
        }).WithTags("CLI").WithName("CliAuth");

        var cli = app.MapGroup("/api/cli").WithTags("CLI");

        app.MapGet("/api/me", (IAuthProvider auth) =>
        {
            var u = auth.GetCurrentUser();
            return Results.Ok(new { email = u.Email, displayName = u.DisplayName, role = u.Role.ToString(), authenticated = u.IsAuthenticated });
        }).WithTags("CLI").WithName("Me");

        // Jetons longue durée pour la CI. Création interdite avec un jeton : un jeton ne peut pas en engendrer d'autres.
        cli.MapPost("/tokens", async (CliTokenRequest body, CliTokenService tokens, IAuthProvider auth, HttpContext ctx) =>
        {
            if (CliAuthRules.ParseBearer(ctx.Request.Headers.Authorization.FirstOrDefault()) is not null)
                return Results.Problem(statusCode: 403, title: "Création refusée", detail: "Créez les jetons depuis l'interface, pas avec un jeton.");
            var days = CliAuthRules.ClampCiDays(body.Days);
            var t = await tokens.IssueAsync(auth.GetCurrentUser().Email, "ci", TimeSpan.FromDays(days), body.Name);
            return Results.Created("/api/cli/tokens", new { id = t.Id, token = t.Token, expiresAt = t.ExpiresAt });
        }).Require(Role.Publisher).WithName("CliTokenCreate");

        cli.MapGet("/tokens", async (CliTokenService tokens, IAuthProvider auth) =>
            Results.Ok(await tokens.ListAsync(auth.GetCurrentUser().Email))).Require(Role.Publisher).WithName("CliTokens");

        cli.MapDelete("/tokens/{id:guid}", async (Guid id, CliTokenService tokens, IAuthProvider auth) =>
            await tokens.RevokeAsync(auth.GetCurrentUser().Email, id) ? Results.NoContent() : Results.NotFound()).Require(Role.Publisher).WithName("CliTokenRevoke");

        // `myregistry edit` : sources d'une version (tous statuts) pour l'auteur ou un Admin.
        cli.MapGet("/edit/source", async (string ns, string name, string? version, CatalogService svc, IAuthProvider auth) =>
        {
            var v = await svc.GetForEditAsync(ns, name, version);
            if (v is null) return Results.NotFound();
            if (!await svc.CanEditAsync(v.ArtifactId, auth.GetCurrentUser())) return Results.Problem(statusCode: 403, title: "Droits insuffisants", detail: "Seuls l'auteur et un Admin peuvent éditer.");
            return Results.Ok(new
            {
                v.Id, v.Artifact.Namespace, v.Artifact.Name, Type = v.Artifact.Type.ToString(), v.Version, v.Description, Status = v.Status.ToString(), mainFile = v.MainFile,
                tags = v.Artifact.Tags.Select(t => t.Name).OrderBy(n => n),
                files = v.Files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new { path = f.Path, isBinary = f.IsBinary, content = Convert.ToBase64String(f.Content) }),
            });
        }).Require(Role.Publisher).WithName("CliEditSource");

        cli.MapGet("/edit/archive/{id:guid}", async (Guid id, CatalogService svc, IAuthProvider auth) =>
        {
            var v = await svc.GetAsync(id);
            if (v is null) return Results.NotFound();
            if (!await svc.CanEditAsync(v.ArtifactId, auth.GetCurrentUser())) return Results.Problem(statusCode: 403, title: "Droits insuffisants");
            return Results.File(CatalogService.Zip(v), "application/zip", $"{v.Artifact.Name}-{v.Version}.zip");
        }).Require(Role.Publisher).WithName("CliEditArchive");
    }
}
