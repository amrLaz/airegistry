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

    private static IResult Zip(ArtifactVersion v, HttpContext ctx)
    {
        ctx.Response.Headers["X-Artifact-Sha256"] = v.ContentHash;
        ctx.Response.Headers.ETag = $"\"{v.ContentHash}\"";
        return Results.File(CatalogService.Zip(v), "application/zip", $"{v.Artifact.Name}-{v.Version}.zip");
    }
}

public sealed record StatusRequest(VersionStatus Status, string? Note);
