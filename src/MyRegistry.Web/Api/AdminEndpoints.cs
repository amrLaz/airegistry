using MyRegistry.Application;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Api;

public static class AdminEndpoints
{
    public static void MapAdminApi(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin").WithTags("Administration");

        admin.MapGet("/users", async (AdminService svc) => await svc.UsersAsync()).Require(Role.Admin).WithName("Users");
        admin.MapPut("/users/{email}", async (string email, UserRequest body, AdminService svc, IAuthProvider auth) =>
        {
            var (user, error) = await svc.UpsertUserAsync(email, body.Role, body.Active ?? true, body.DisplayName, auth.GetCurrentUser().Email);
            return error is null ? Results.Ok(user) : Results.Problem(statusCode: 409, title: error);
        }).Require(Role.Admin).WithName("UpsertUser");

        admin.MapGet("/dashboard", async (AdminService svc) => await svc.DashboardAsync()).Require(Role.Moderator).WithName("Dashboard");

        admin.MapGet("/versions", async (AdminService svc, VersionStatus? status, bool? quarantined, string? q, int? page, int? pageSize) =>
            await svc.VersionsAsync(status, quarantined, q, page ?? 1, pageSize ?? 50)).Require(Role.Moderator).WithName("ModerationVersions");

        admin.MapGet("/reports", async (AdminService svc, string? status) => await svc.ReportsAsync(status)).Require(Role.Moderator).WithName("Reports");
        admin.MapPost("/reports/{id:guid}/resolve", async (Guid id, ResolveRequest body, AdminService svc, IAuthProvider auth) =>
        {
            if (body.Status is not ("Resolved" or "Dismissed")) return Results.Problem(statusCode: 400, title: "Statut attendu : Resolved ou Dismissed");
            return await svc.ResolveReportAsync(id, body.Status, auth.GetCurrentUser().Email) ? Results.NoContent() : Results.NotFound();
        }).Require(Role.Moderator).WithName("ResolveReport");

        app.MapGet("/api/audit", async (AdminService svc, string? action, string? user, int? page, int? pageSize) =>
            await svc.AuditAsync(action, user, page ?? 1, pageSize ?? 50)).Require(Role.Admin).WithTags("Administration").WithName("Audit");

        admin.MapGet("/settings", async (SettingsService svc) => await svc.GetAllAsync()).Require(Role.Admin).WithName("Settings");
        admin.MapPut("/settings", async (Dictionary<string, string> body, SettingsService svc, IAuthProvider auth, MyRegistry.Web.Services.SettingsCache cache) =>
        {
            var errors = await svc.UpdateAsync(body, auth.GetCurrentUser().Email);
            cache.Invalidate();
            return errors.Count == 0 ? Results.NoContent() : Results.Problem(statusCode: 422, title: "Configuration invalide", extensions: new Dictionary<string, object?> { ["errors"] = errors });
        }).Require(Role.Admin).WithName("UpdateSettings");

        var catalog = app.MapGroup("/api/catalog").WithTags("Catalogue");
        catalog.MapGet("/{id:guid}/governance", async (Guid id, GovernanceService svc, IAuthProvider auth) =>
            await svc.GetAsync(id, auth.GetCurrentUser()) is { } g ? Results.Ok(g) : Results.NotFound()).WithName("GetGovernance");

        catalog.MapPost("/{id:guid}/governance", async (Guid id, GovernanceInput body, GovernanceService svc, IAuthProvider auth) =>
        {
            var r = await svc.SaveAsync(id, body, auth.GetCurrentUser());
            if (r.NotFound) return Results.NotFound();
            if (r.Forbidden) return Results.Problem(statusCode: 403, title: "Seuls l'auteur, un SecurityValidator ou un Admin peuvent modifier le contrat de gouvernance");
            return r.Errors.Count > 0
                ? Results.Problem(statusCode: 422, title: "Contrat de gouvernance invalide", extensions: new Dictionary<string, object?> { ["errors"] = r.Errors })
                : Results.Ok(r.Contract);
        }).Require(Role.Publisher).WithName("SaveGovernance");

        catalog.MapGet("/capabilities-inventory", async (GovernanceService svc) =>
            (await svc.CapabilitiesInventoryAsync()).Select(c => new { capability = c.Capability, artifacts = c.Artifacts }))
            .Require(Role.Reader).WithName("CapabilitiesInventory");

        catalog.MapPost("/{id:guid}/reject", async (Guid id, RejectRequest body, AdminService svc, IAuthProvider auth) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason)) return Results.Problem(statusCode: 400, title: "Un motif de rejet est obligatoire");
            return await svc.RejectAsync(id, body.Reason.Trim(), auth.GetCurrentUser().Email) ? Results.NoContent() : Results.NotFound();
        }).Require(Role.Moderator).WithName("Reject");

        catalog.MapDelete("/{id:guid}", async (Guid id, AdminService svc, IAuthProvider auth) =>
            await svc.DeleteVersionAsync(id, auth.GetCurrentUser().Email) switch
            {
                DeletionVerdict.Allowed => Results.NoContent(),
                DeletionVerdict.NotFound => Results.NotFound(),
                DeletionVerdict.OnlyVersion => Results.Problem(statusCode: 409, title: "La seule version restante ne peut pas être supprimée"),
                _ => Results.Problem(statusCode: 409, title: "La dernière version est protégée"),
            }).Require(Role.Moderator).WithName("DeleteVersion");
    }
}

public sealed record UserRequest(Role Role, bool? Active, string? DisplayName);
public sealed record ResolveRequest(string Status);
public sealed record RejectRequest(string Reason);
