using MyRegistry.Application;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Api;

public static class FeedEndpoints
{
    public static void MapFeedApi(this WebApplication app)
    {
        var feeds = app.MapGroup("/api/feeds").WithTags("Feeds");

        feeds.MapGet("/", async (FeedService svc) => await svc.ListAsync()).Require(Role.Admin).WithName("Feeds");
        feeds.MapGet("/{id:guid}", async (Guid id, FeedService svc) => await svc.GetAsync(id) is { } f ? Results.Ok(f) : Results.NotFound()).Require(Role.Admin).WithName("GetFeed");

        feeds.MapPost("/", async (FeedRequest body, FeedService svc, IAuthProvider auth) =>
            Result(await svc.CreateAsync(body.ToInput(), body.IsSystem ?? false, auth.GetCurrentUser().Email), created: true)).Require(Role.Admin).WithName("CreateFeed");

        feeds.MapPut("/{id:guid}", async (Guid id, FeedRequest body, FeedService svc, IAuthProvider auth) =>
            Result(await svc.UpdateAsync(id, body.ToInput(), body.Active, auth.GetCurrentUser().Email))).Require(Role.Admin).WithName("UpdateFeed");

        feeds.MapDelete("/{id:guid}", async (Guid id, FeedService svc, IAuthProvider auth) =>
        {
            var r = await svc.DeleteAsync(id, auth.GetCurrentUser().Email);
            if (!r.Found) return Results.NotFound();
            return r.Deleted ? Results.Ok(new { removedVersions = r.RemovedVersions, removedArtifacts = r.RemovedArtifacts })
                : Results.Problem(statusCode: 409, title: "Le feed système ne peut pas être supprimé");
        }).Require(Role.Admin).WithName("DeleteFeed");

        feeds.MapPost("/test", async (FeedRequest body, FeedService svc) =>
        {
            var (ok, message, head, files) = await svc.TestConnectionAsync(body.ToInput());
            return Results.Ok(new { ok, message, head, files });
        }).Require(Role.Admin).WithName("TestFeed");

        feeds.MapPost("/{id:guid}/sync", async (Guid id, FeedService svc, IAuthProvider auth, bool? force) =>
            await svc.SyncAsync(id, auth.GetCurrentUser().Email, force ?? false) is { } r ? Results.Ok(r) : Results.NotFound()).Require(Role.Admin).WithName("SyncFeed");

        feeds.MapGet("/{id:guid}/reports", async (Guid id, FeedService svc) => await svc.ReportsAsync(id)).Require(Role.Admin).WithName("FeedReports");
    }

    private static IResult Result(FeedResult r, bool created = false)
    {
        if (r.NotFound) return Results.NotFound();
        if (r.Errors.Count > 0) return Results.Problem(statusCode: 422, title: "Feed invalide", extensions: new Dictionary<string, object?> { ["errors"] = r.Errors });
        return created ? Results.Created($"/api/feeds/{r.Feed!.Id}", r.Feed) : Results.Ok(r.Feed);
    }
}

public sealed record FeedRequest(string Name, FeedKind Kind, string Url, string? Branch, string? PatRef, int? IntervalMinutes, string? NamespaceOverride, bool? IsSystem, bool? Active)
{
    public FeedRules.FeedInput ToInput() => new(Name ?? "", Kind, Url ?? "", Branch, PatRef, IntervalMinutes, NamespaceOverride);
}
