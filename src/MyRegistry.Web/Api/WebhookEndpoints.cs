using MyRegistry.Application;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Api;

public sealed record WebhookRequest(string? Name, string? Format, string? Url, string[]? Events, string? Secret, bool? Active);

public static class WebhookEndpoints
{
    public static void MapWebhookApi(this WebApplication app)
    {
        var hooks = app.MapGroup("/api/admin/webhooks").WithTags("Webhooks");

        hooks.MapGet("/", async (WebhookService svc) => Results.Ok(await svc.ListAsync())).Require(Role.Admin).WithName("Webhooks");

        hooks.MapPost("/", async (WebhookRequest body, WebhookService svc, IAuthProvider auth) =>
        {
            var r = await svc.CreateAsync(ToInput(body), auth.GetCurrentUser().Email);
            return r.Errors.Count > 0 ? Invalid(r) : Results.Created($"/api/admin/webhooks/{r.Webhook!.Id}", new { webhook = r.Webhook, secret = r.Secret });
        }).Require(Role.Admin).WithName("WebhookCreate");

        hooks.MapPut("/{id:guid}", async (Guid id, WebhookRequest body, WebhookService svc, IAuthProvider auth) =>
        {
            var r = await svc.UpdateAsync(id, ToInput(body), body.Active, auth.GetCurrentUser().Email);
            return r.NotFound ? Results.NotFound() : r.Errors.Count > 0 ? Invalid(r) : Results.Ok(r.Webhook);
        }).Require(Role.Admin).WithName("WebhookUpdate");

        hooks.MapPost("/{id:guid}/rotate-secret", async (Guid id, WebhookService svc, IAuthProvider auth) =>
            await svc.RotateSecretAsync(id, auth.GetCurrentUser().Email) is { } s ? Results.Ok(new { secret = s }) : Results.NotFound()).Require(Role.Admin).WithName("WebhookRotateSecret");

        hooks.MapDelete("/{id:guid}", async (Guid id, WebhookService svc, IAuthProvider auth) =>
            await svc.DeleteAsync(id, auth.GetCurrentUser().Email) ? Results.NoContent() : Results.NotFound()).Require(Role.Admin).WithName("WebhookDelete");

        hooks.MapPost("/{id:guid}/test", async (Guid id, WebhookService svc, IAuthProvider auth) =>
            await svc.TestAsync(id, auth.GetCurrentUser().Email) is { } d ? Results.Ok(d) : Results.NotFound()).Require(Role.Admin).WithName("WebhookTest");

        hooks.MapGet("/{id:guid}/deliveries", async (Guid id, int? take, WebhookService svc) =>
            await svc.DeliveriesAsync(id, take ?? 50) is { } l ? Results.Ok(l) : Results.NotFound()).Require(Role.Admin).WithName("WebhookDeliveries");
    }

    private static WebhookInput ToInput(WebhookRequest b) => new(b.Name ?? "", b.Format ?? "", b.Url ?? "", b.Events ?? [], b.Secret);

    private static IResult Invalid(WebhookResult r) =>
        Results.Problem(statusCode: 422, title: "Webhook invalide", extensions: new Dictionary<string, object?> { ["errors"] = r.Errors });
}
