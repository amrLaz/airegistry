using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Api;

public sealed record RetentionRunRequest(bool Confirm);

public static class RetentionEndpoints
{
    public static void MapRetentionApi(this WebApplication app)
    {
        var r = app.MapGroup("/api/admin/retention").WithTags("Rétention");

        r.MapGet("/", async (RetentionService svc) => Results.Ok(await svc.PreviewAsync())).Require(Role.Admin).WithName("RetentionPreview");

        // Suppression irréversible : exige une confirmation explicite dans le corps.
        r.MapPost("/run", async (RetentionRunRequest? body, RetentionService svc, MyRegistry.Application.IAuthProvider auth) =>
            body is { Confirm: true }
                ? Results.Ok(await svc.RunAsync(auth.GetCurrentUser().Email))
                : Results.Problem(statusCode: 400, title: "Confirmation requise", detail: "Envoyez { \"confirm\": true } après avoir relu l'aperçu : la suppression est irréversible."))
            .Require(Role.Admin).WithName("RetentionRun");
    }
}
