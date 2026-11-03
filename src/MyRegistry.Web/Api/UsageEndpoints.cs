using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Api;

public static class UsageEndpoints
{
    public static void MapUsageApi(this WebApplication app) =>
        app.MapGet("/api/admin/usage", async (int? days, UsageService svc) => Results.Ok(await svc.SummaryAsync(days)))
            .Require(Role.Moderator).WithTags("Usage").WithName("Usage");
}
