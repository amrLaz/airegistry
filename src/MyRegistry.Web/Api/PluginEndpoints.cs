using MyRegistry.Application;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Api;

public static class PluginEndpoints
{
    public static void MapPluginApi(this WebApplication app)
    {
        app.MapGet("/marketplace.json", async (PluginService svc, HttpContext ctx) =>
        {
            var r = await svc.MarketplaceAsync($"{ctx.Request.Scheme}://{ctx.Request.Host}");
            ctx.Response.Headers.ETag = r.ETag;
            ctx.Response.Headers.CacheControl = "public, max-age=300";
            if (ctx.Request.Headers.IfNoneMatch == r.ETag) return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Text(r.Body, "application/json");
        }).WithTags("Marketplace").WithName("Marketplace");

        var plugins = app.MapGroup("/api/plugins").WithTags("Plugins");

        plugins.MapGet("/{ns}/{name}/download", async (string ns, string name, string? version, PluginService svc, HttpContext ctx) =>
        {
            var b = await svc.BundleAsync(ns, name, version);
            if (b is null) return Results.NotFound();
            ctx.Response.Headers["X-Artifact-Sha256"] = b.Sha256;
            ctx.Response.Headers.ETag = $"\"{b.Sha256}\"";
            return Results.File(b.Zip, "application/zip", b.FileName);
        }).WithName("PluginDownload");

        plugins.MapGet("/versions/{id:guid}/updates", async (Guid id, PluginService svc) => await svc.UpdatesForVersionAsync(id)).WithName("PluginUpdates");

        plugins.MapGet("/components", async (PluginService svc, ArtifactType? type, string? q) => await svc.SearchComponentsAsync(type, q))
            .Require(Role.Publisher).WithName("PluginComponents");

        var drafts = plugins.MapGroup("/drafts");
        drafts.MapGet("/", async (PluginService svc, IAuthProvider auth) => await svc.ListDraftsAsync(auth.GetCurrentUser().Email)).Require(Role.Publisher).WithName("PluginDrafts");

        drafts.MapPost("/", async (DraftRequest body, PluginService svc, IAuthProvider auth) =>
            Draft(await svc.SaveDraftAsync(null, body.ToInput(), auth.GetCurrentUser()), created: true)).Require(Role.Publisher).WithName("CreatePluginDraft");

        drafts.MapGet("/{id:guid}", async (Guid id, PluginService svc, IAuthProvider auth) =>
            Draft(await svc.GetDraftAsync(id, auth.GetCurrentUser()))).Require(Role.Publisher).WithName("GetPluginDraft");

        drafts.MapPut("/{id:guid}", async (Guid id, DraftRequest body, PluginService svc, IAuthProvider auth) =>
            Draft(await svc.SaveDraftAsync(id, body.ToInput(), auth.GetCurrentUser()))).Require(Role.Publisher).WithName("UpdatePluginDraft");

        drafts.MapDelete("/{id:guid}", async (Guid id, PluginService svc, IAuthProvider auth) =>
            await svc.DeleteDraftAsync(id, auth.GetCurrentUser()) switch { null => Results.NotFound(), false => Forbidden(), _ => Results.NoContent() })
            .Require(Role.Publisher).WithName("DeletePluginDraft");

        drafts.MapPost("/{id:guid}/publish", async (Guid id, PluginService svc, IAuthProvider auth) =>
        {
            var r = await svc.PublishDraftAsync(id, auth.GetCurrentUser());
            if (r.NotFound) return Results.NotFound();
            if (r.Forbidden) return Forbidden();
            return r.Errors.Count > 0
                ? Results.Problem(statusCode: 422, title: "Publication refusée", extensions: new Dictionary<string, object?> { ["errors"] = r.Errors, ["canonicalName"] = r.Import?.CanonicalName })
                : Results.Created($"/api/catalog/{r.Import!.VersionId}", r.Import);
        }).Require(Role.Publisher).WithName("PublishPluginDraft");

        drafts.MapGet("/{id:guid}/export", async (Guid id, PluginService svc, IAuthProvider auth) =>
        {
            var r = await svc.GetDraftAsync(id, auth.GetCurrentUser());
            if (r.NotFound) return Results.NotFound();
            if (r.Forbidden) return Forbidden();
            return Results.File(svc.ExportDraft(r.Draft!), "application/zip", $"{r.Draft!.Name}-draft.zip");
        }).Require(Role.Publisher).WithName("ExportPluginDraft");

        drafts.MapPost("/import", async (HttpRequest req, PluginService svc, IAuthProvider auth) =>
        {
            if (req.ContentLength is > 2_000_000) return Results.Problem(statusCode: 413, title: "Archive trop volumineuse");
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            if (ms.Length > 2_000_000) return Results.Problem(statusCode: 413, title: "Archive trop volumineuse");
            return Draft(await svc.ImportDraftAsync(ms.ToArray(), auth.GetCurrentUser()), created: true);
        }).Require(Role.Publisher).WithName("ImportPluginDraft");
    }

    private static IResult Forbidden() => Results.Problem(statusCode: 403, title: "Ce brouillon appartient à un autre utilisateur");

    private static IResult Draft(DraftResult r, bool created = false)
    {
        if (r.NotFound) return Results.NotFound();
        if (r.Forbidden) return Forbidden();
        if (r.Errors.Count > 0) return Results.Problem(statusCode: 422, title: "Brouillon invalide", extensions: new Dictionary<string, object?> { ["errors"] = r.Errors });
        return created ? Results.Created($"/api/plugins/drafts/{r.Draft!.Id}", r.Draft) : Results.Ok(r.Draft);
    }
}

public sealed record DraftRequest(string Namespace, string Name, string? Description, string? Version, List<ComponentRef>? Components)
{
    public DraftInput ToInput() => new(Namespace ?? "", Name ?? "", Description, Version, Components ?? []);
}
