using MyRegistry.Application;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Web.Api;

public static class NotificationEndpoints
{
    public static void MapNotificationApi(this WebApplication app)
    {
        var n = app.MapGroup("/api/notifications").WithTags("Notifications");

        n.MapGet("/", async (bool? includeArchived, int? take, NotificationService svc, IAuthProvider auth) =>
            Results.Ok(await svc.ListAsync(auth.GetCurrentUser().Email, includeArchived ?? false, take ?? 50))).Require(Role.Reader).WithName("Notifications");

        n.MapGet("/unread-count", async (NotificationService svc, IAuthProvider auth) =>
            Results.Ok(new { count = await svc.UnreadCountAsync(auth.GetCurrentUser().Email) })).Require(Role.Reader).WithName("NotificationsUnreadCount");

        n.MapPatch("/{id:guid}/read", async (Guid id, NotificationService svc, IAuthProvider auth) =>
            await svc.MarkReadAsync(auth.GetCurrentUser().Email, id) ? Results.NoContent() : Results.NotFound()).Require(Role.Reader).WithName("NotificationRead");

        n.MapPatch("/{id:guid}/archive", async (Guid id, NotificationService svc, IAuthProvider auth) =>
            await svc.ArchiveAsync(auth.GetCurrentUser().Email, id) ? Results.NoContent() : Results.NotFound()).Require(Role.Reader).WithName("NotificationArchive");

        n.MapDelete("/{id:guid}", async (Guid id, NotificationService svc, IAuthProvider auth) =>
            await svc.DeleteAsync(auth.GetCurrentUser().Email, id) ? Results.NoContent() : Results.NotFound()).Require(Role.Reader).WithName("NotificationDelete");

        n.MapPost("/mark-all-read", async (NotificationService svc, IAuthProvider auth) =>
            Results.Ok(new { count = await svc.MarkAllReadAsync(auth.GetCurrentUser().Email) })).Require(Role.Reader).WithName("NotificationsMarkAllRead");

        n.MapPost("/archive-all", async (NotificationService svc, IAuthProvider auth) =>
            Results.Ok(new { count = await svc.ArchiveAllAsync(auth.GetCurrentUser().Email) })).Require(Role.Reader).WithName("NotificationsArchiveAll");

        n.MapPost("/delete-all", async (NotificationService svc, IAuthProvider auth) =>
            Results.Ok(new { count = await svc.DeleteAllAsync(auth.GetCurrentUser().Email) })).Require(Role.Reader).WithName("NotificationsDeleteAll");

        n.MapGet("/preferences", async (NotificationService svc, IAuthProvider auth) =>
            Results.Ok(await svc.PreferencesAsync(auth.GetCurrentUser().Email))).Require(Role.Reader).WithName("NotificationPreferences");

        n.MapPatch("/preferences", async (Dictionary<NotificationKind, bool> patch, NotificationService svc, IAuthProvider auth) =>
            Results.Ok(await svc.SetPreferencesAsync(auth.GetCurrentUser().Email, patch))).Require(Role.Reader).WithName("NotificationPreferencesUpdate");
    }
}
