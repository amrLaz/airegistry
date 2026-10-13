using System.Text;
using MyRegistry.Application;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Api;

public static class Endpoints
{
    // Hiérarchie des droits : docs/01 (SecurityValidator ⊇ Moderator ; Admin ⊇ tout).
    private static int Rank(Role r) => r switch { Role.Reader => 0, Role.Publisher => 1, Role.Moderator => 2, Role.SecurityValidator => 3, _ => 4 };

    private static RouteHandlerBuilder Require(this RouteHandlerBuilder b, Role min) =>
        b.AddEndpointFilter(async (ctx, next) =>
        {
            var user = ctx.HttpContext.RequestServices.GetRequiredService<IAuthProvider>().GetCurrentUser();
            return Rank(user.Role) >= Rank(min) ? await next(ctx) : Results.Problem(statusCode: 403, title: "Droits insuffisants", detail: $"Rôle requis : {min}.");
        });

    private static IResult Problem(ImportResult r) =>
        Results.Problem(statusCode: 422, title: "Publication refusée", extensions: new Dictionary<string, object?>
        {
            ["errors"] = r.Errors, ["warnings"] = r.Warnings, ["canonicalName"] = r.CanonicalName,
        });

    public static void MapApi(this WebApplication app)
    {
        var catalog = app.MapGroup("/api/catalog").WithTags("Catalogue");

        catalog.MapGet("/", async (CatalogService svc, IAuthProvider auth, ArtifactType? type, VersionStatus? status, string? tag, string? q,
                string? sort, int? page, int? pageSize) =>
            await svc.ListAsync(type, status, tag, q, sort, Rank(auth.GetCurrentUser().Role) >= Rank(Role.Moderator), page ?? 1, pageSize ?? 20))
            .WithName("CatalogList");

        catalog.MapGet("/{id:guid}", async (Guid id, CatalogService svc) =>
        {
            var v = await svc.GetAsync(id);
            return v is null ? Results.NotFound() : Results.Ok(new
            {
                v.Id, v.Artifact.Namespace, v.Artifact.Name, v.Artifact.Type, v.Version, v.Description, v.Status, v.Quarantined,
                Grade = v.QualityGrade, v.ContentHash, v.CreatedBy, v.CreatedAt, v.DownloadCount,
                Tags = v.Artifact.Tags.Select(t => t.Name),
                Files = v.Files.Select(f => new { f.Path, f.IsBinary, Size = f.Content.Length }),
                History = v.History.OrderBy(h => h.At).Select(h => new { h.From, h.To, h.UserId, h.At, h.Note }),
            });
        }).WithName("CatalogDetail");

        catalog.MapGet("/{id:guid}/download", async (Guid id, CatalogService svc, HttpContext ctx) =>
        {
            var v = await svc.GetAsync(id);
            if (v is null) return Results.NotFound();
            await svc.CountDownloadAsync(id);
            return Zip(v, ctx);
        }).WithName("CatalogDownload");

        catalog.MapGet("/by-hash/{hash}/download", async (string hash, CatalogService svc, HttpContext ctx) =>
        {
            var v = await svc.GetByHashAsync(hash.ToLowerInvariant());
            if (v is null) return Results.NotFound();
            await svc.CountDownloadAsync(v.Id);
            return Zip(v, ctx);
        }).WithName("CatalogDownloadByHash");

        catalog.MapPost("/{id:guid}/upvote", async (Guid id, CatalogService svc, IAuthProvider auth) =>
            await svc.VoteAsync(id, auth.GetCurrentUser().Email, true) is { } n ? Results.Ok(new { votes = n }) : Results.NotFound())
            .Require(Role.Reader).WithName("Upvote");

        catalog.MapDelete("/{id:guid}/upvote", async (Guid id, CatalogService svc, IAuthProvider auth) =>
            await svc.VoteAsync(id, auth.GetCurrentUser().Email, false) is { } n ? Results.Ok(new { votes = n }) : Results.NotFound())
            .Require(Role.Reader).WithName("RemoveUpvote");

        catalog.MapPut("/{id:guid}/status", async (Guid id, StatusRequest body, CatalogService svc, IAuthProvider auth) =>
            await svc.SetStatusAsync(id, body.Status, auth.GetCurrentUser().Email, body.Note) ? Results.NoContent() : Results.NotFound())
            .Require(Role.Moderator).WithName("SetStatus");

        catalog.MapPost("/{id:guid}/report", async (Guid id, ReportRequest body, CommunityService svc, IAuthProvider auth) =>
            await svc.ReportAsync(id, body.Reason, body.Details, auth.GetCurrentUser().Email) is { } r ? Results.Accepted(value: r) : Results.NotFound())
            .Require(Role.Reader).WithName("Report");

        catalog.MapPost("/{id:guid}/quarantine/release", async (Guid id, CommunityService svc, IAuthProvider auth) =>
            await svc.ReleaseQuarantineAsync(id, auth.GetCurrentUser().Email) ? Results.NoContent() : Results.NotFound())
            .Require(Role.Moderator).WithName("ReleaseQuarantine");

        catalog.MapGet("/{id:guid}/comments", async (Guid id, CommunityService svc) =>
            await svc.CommentsAsync(id) is { } l ? Results.Ok(l) : Results.NotFound()).WithName("Comments");

        catalog.MapPost("/{id:guid}/comments", async (Guid id, CommentRequest body, CommunityService svc, IAuthProvider auth) =>
            await svc.AddCommentAsync(id, auth.GetCurrentUser().Email, body.Body) is { } c ? Results.Created($"/api/catalog/{id}/comments", c)
                : Results.Problem(statusCode: 400, title: "Commentaire vide ou artefact introuvable"))
            .Require(Role.Reader).WithName("AddComment");

        catalog.MapPost("/{id:guid}/unpublish", async (Guid id, CommunityService svc, IAuthProvider auth) =>
        {
            var u = auth.GetCurrentUser();
            return await svc.UnpublishAsync(id, u.Email, Rank(u.Role) >= Rank(Role.Moderator)) switch
            {
                null => Results.NotFound(), false => Results.Problem(statusCode: 403, title: "Seul l'auteur ou un modérateur peut retirer cette version"),
                _ => Results.NoContent(),
            };
        }).Require(Role.Publisher).WithName("Unpublish");

        catalog.MapGet("/{id:guid}/export/cursorrules", async (Guid id, CatalogService svc) =>
        {
            var v = await svc.GetAsync(id);
            if (v is null) return Results.NotFound();
            return Results.Text(Exports.CursorRules(MainText(v)), "text/plain; charset=utf-8");
        }).WithName("ExportCursorRules");

        catalog.MapGet("/{id:guid}/export/claude-desktop", async (Guid id, CatalogService svc) =>
        {
            var v = await svc.GetAsync(id);
            if (v is null) return Results.NotFound();
            if (v.Artifact.Type != ArtifactType.McpServer) return Results.Problem(statusCode: 400, title: "Export réservé aux serveurs MCP");
            var json = Exports.ClaudeDesktop(v.Artifact.Name, MainText(v));
            return json is null ? Results.Problem(statusCode: 422, title: "Configuration MCP non convertible") : Results.Text(json, "application/json");
        }).WithName("ExportClaudeDesktop");

        catalog.MapGet("/{id:guid}/audit-prompt", async (Guid id, CatalogService svc) =>
        {
            var v = await svc.GetAsync(id);
            if (v is null) return Results.NotFound();
            var files = v.Files.Where(f => !f.IsBinary).OrderBy(f => f.Path, StringComparer.Ordinal)
                .Select(f => new ExportFile(f.Path, Encoding.UTF8.GetString(f.Content)));
            return Results.Text(Exports.AuditPrompt($"{v.Artifact.Namespace}/{v.Artifact.Name}", v.Artifact.Type, v.Version, files), "text/plain; charset=utf-8");
        }).WithName("AuditPrompt");

        var my = app.MapGroup("/api/my").WithTags("Mes publications");
        my.MapGet("/publications", async (CommunityService svc, IAuthProvider auth) => await svc.MyPublicationsAsync(auth.GetCurrentUser().Email))
            .Require(Role.Publisher).WithName("MyPublications");

        app.MapGet("/api/cli/publish/suggested-version", async (string ns, string name, ArtifactType type, string? requestedVersion, CommunityService svc) =>
            Results.Ok(new { version = await svc.SuggestVersionAsync(ns, name, type, requestedVersion) }))
            .WithTags("Publication").Require(Role.Publisher).WithName("SuggestedVersion");

        var artifacts = app.MapGroup("/api/artifacts").WithTags("Artefacts");
        artifacts.MapGet("/by-name/{ns}/{name}/raw", async (string ns, string name, string? version, CatalogService svc) =>
        {
            var v = await svc.GetByNameAsync(ns, name, version);
            if (v is null) return Results.NotFound();
            var main = v.Files.FirstOrDefault(f => f.Path == v.MainFile) ?? v.Files.First();
            return Results.Text(Encoding.UTF8.GetString(main.Content), "text/plain; charset=utf-8");
        }).WithName("RawByName");

        var pub = app.MapGroup("/api").WithTags("Publication");
        pub.MapPost("/validate", (ValidateRequest req) =>
        {
            try
            {
                var files = PayloadBuilder.Build(req.FileName, req.Content, req.AdditionalFiles, req.BinaryFiles);
                return Results.Ok(Validation.Run(req.Type, files));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            { return Results.Problem(statusCode: 400, title: "Charge invalide", detail: ex.Message); }
        }).Require(Role.Publisher).WithName("Validate");

        pub.MapPost("/import", async (ImportRequest req, PublishService svc, IAuthProvider auth) =>
        {
            var r = await svc.ImportAsync(req, auth.GetCurrentUser());
            return r.Success ? Results.Created($"/api/catalog/{r.VersionId}", r) : Problem(r);
        }).Require(Role.Publisher).WithName("Import");
    }

    private static string MainText(ArtifactVersion v)
    {
        var main = v.Files.FirstOrDefault(f => f.Path == v.MainFile) ?? v.Files.First();
        return Encoding.UTF8.GetString(main.Content);
    }

    private static IResult Zip(ArtifactVersion v, HttpContext ctx)
    {
        ctx.Response.Headers["X-Artifact-Sha256"] = v.ContentHash;
        ctx.Response.Headers.ETag = $"\"{v.ContentHash}\"";
        return Results.File(CatalogService.Zip(v), "application/zip", $"{v.Artifact.Name}-{v.Version}.zip");
    }
}

public sealed record StatusRequest(VersionStatus Status, string? Note);

public sealed record ReportRequest(ReportReason Reason, string? Details);
public sealed record CommentRequest(string Body);
