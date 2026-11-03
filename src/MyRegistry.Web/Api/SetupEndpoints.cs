using MyRegistry.Application;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Api;

public sealed record SetupIdentityRequest(string Title, string Namespace, string? ContactEmail, string? PublicUrl);

public static class SetupEndpoints
{
    public static void MapSetupApi(this WebApplication app)
    {
        var setup = app.MapGroup("/api/setup").WithTags("Assistant de premier lancement");

        setup.MapGet("/status", async (SetupService svc, IAuthProvider auth) =>
            Results.Ok(await svc.StatusAsync(auth.GetCurrentUser().Role))).Require(Role.Admin).WithName("SetupStatus");

        setup.MapPut("/identity", async (SetupIdentityRequest body, SetupService svc, IAuthProvider auth) =>
        {
            var errors = await svc.ApplyIdentityAsync(body.Title ?? "", body.Namespace ?? "", body.ContactEmail, body.PublicUrl, auth.GetCurrentUser().Email);
            return errors.Count == 0 ? Results.NoContent() : Results.Problem(statusCode: 422, title: "Réglages invalides", extensions: new Dictionary<string, object?> { ["errors"] = errors });
        }).Require(Role.Admin).WithName("SetupIdentity");

        setup.MapPost("/complete", async (SetupService svc, IAuthProvider auth) =>
        {
            await svc.CompleteAsync(auth.GetCurrentUser().Email);
            return Results.NoContent();
        }).Require(Role.Admin).WithName("SetupComplete");
    }
}
