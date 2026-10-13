using MyRegistry.Application;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Api;

public static class SecurityEndpoints
{
    public static void MapSecurityApi(this WebApplication app)
    {
        var sec = app.MapGroup("/api/catalog").WithTags("Sécurité");

        sec.MapGet("/{id:guid}/security", async (Guid id, SecurityService svc, IAuthProvider auth) =>
            await svc.SummaryAsync(id, auth.GetCurrentUser()) is { } s ? Results.Ok(s) : Results.NotFound()).Require(Role.Reader).WithName("SecuritySummary");

        sec.MapPost("/{id:guid}/security/scan", async (Guid id, SecurityService svc, IAuthProvider auth) =>
        {
            var u = auth.GetCurrentUser();
            return await svc.ScanVersionAsync(id, "manual", u.Email, u) is { } s ? Results.Ok(s) : Results.NotFound();
        }).Require(Role.Moderator).WithName("RescanVersion");

        sec.MapPost("/{id:guid}/security/waivers", async (Guid id, WaiverRequest body, SecurityService svc, IAuthProvider auth) =>
        {
            var r = await svc.AddWaiverAsync(id, body.Severity, body.Reason ?? "", body.ExpiresAt, auth.GetCurrentUser());
            if (r.NotFound) return Results.NotFound();
            if (r.Forbidden) return Results.Problem(statusCode: 403, title: body.Severity == FindingSeverity.Critical ? "Une dérogation Critical est réservée aux SecurityValidator et Admin" : "Droits insuffisants pour accorder une dérogation");
            return r.Errors.Count > 0
                ? Results.Problem(statusCode: 422, title: "Dérogation invalide", extensions: new Dictionary<string, object?> { ["errors"] = r.Errors })
                : Results.Created($"/api/catalog/{id}/security", r.Waiver);
        }).Require(Role.Moderator).WithName("AddWaiver");

        sec.MapGet("/{id:guid}/security/fix-prompt", async (Guid id, SecurityService svc) =>
            await svc.FixPromptAsync(id) is { } p ? Results.Text(p, "text/plain; charset=utf-8") : Results.NotFound()).Require(Role.Reader).WithName("FixPrompt");

        app.MapDelete("/api/security/waivers/{id:guid}", async (Guid id, SecurityService svc, IAuthProvider auth) =>
        {
            var r = await svc.RevokeWaiverAsync(id, auth.GetCurrentUser());
            if (r.NotFound) return Results.NotFound();
            if (r.Forbidden) return Results.Problem(statusCode: 403, title: "Droits insuffisants pour révoquer cette dérogation");
            return r.Errors.Count > 0 ? Results.Problem(statusCode: 409, title: r.Errors[0].Message) : Results.Ok(r.Waiver);
        }).Require(Role.Moderator).WithTags("Sécurité").WithName("RevokeWaiver");

        var admin = app.MapGroup("/api/admin/security").WithTags("Sécurité");
        admin.MapGet("/rules", () => StaticScanner.Catalog.Select(r => new { r.Id, Severity = r.Severity.ToString(), r.Description })).Require(Role.Moderator).WithName("SecurityRules");
        admin.MapGet("/blocked", async (SecurityService svc) => (await svc.BlockedAsync()).Select(b => new { b.VersionId, b.Artifact, b.Version, b.Verdict })).Require(Role.Moderator).WithName("BlockedVersions");

        admin.MapGet("/baseline", async (SecurityService svc) => await svc.GetBaselineAsync()).Require(Role.SecurityValidator).WithName("GetBaseline");
        admin.MapPut("/baseline", async (BaselineRequest body, SecurityService svc, IAuthProvider auth) =>
        {
            var r = await svc.SaveBaselineAsync(body.Yaml ?? "", body.ConfirmHighSeverity ?? false, auth.GetCurrentUser().Email);
            return r.Errors.Count > 0
                ? Results.Problem(statusCode: 422, title: "Baseline invalide", extensions: new Dictionary<string, object?> { ["errors"] = r.Errors })
                : Results.Ok(r.Baseline);
        }).Require(Role.SecurityValidator).WithName("PutBaseline");

        admin.MapPost("/backfill", async (SecurityService svc, IAuthProvider auth) => await svc.BackfillAsync(auth.GetCurrentUser().Email)).Require(Role.Admin).WithName("Backfill");

        app.MapGet("/api/admin/security-scans/backfill/{runId:guid}/report.csv", async (Guid runId, SecurityService svc) =>
            await svc.BackfillCsvAsync(runId) is { } csv ? Results.Text(csv, "text/csv; charset=utf-8") : Results.NotFound())
            .Require(Role.Admin).WithTags("Sécurité").WithName("BackfillCsv");
    }
}

public sealed record WaiverRequest(FindingSeverity Severity, string? Reason, DateTime? ExpiresAt);
public sealed record BaselineRequest(string? Yaml, bool? ConfirmHighSeverity);
