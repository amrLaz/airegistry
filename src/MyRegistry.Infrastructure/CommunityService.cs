using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record CommentDto(Guid Id, string UserId, string Body, DateTime At);
public sealed record CommentReportResult(bool Found, bool Own, bool Hidden);
public sealed record ReportResult(bool Accepted, bool Quarantined);
public sealed record MyPublication(Guid Id, string Namespace, string Name, ArtifactType Type, string Version, VersionStatus Status, bool Quarantined, DateTime CreatedAt);

public class CommunityService(AppDbContext db, MarketplaceCache marketplace, NotificationService notifications, WebhookService webhooks)
{
    public async Task<ReportResult?> ReportAsync(Guid versionId, ReportReason reason, string? details, string userId, CancellationToken ct = default)
    {
        var v = await db.Versions.FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null) return null;

        var now = DateTime.UtcNow;
        db.Reports.Add(new Report { VersionId = versionId, UserId = userId, Reason = reason.ToString(), Details = details, At = now });
        var family = await db.Artifacts.AsNoTracking().FirstAsync(a => a.Id == v.ArtifactId, ct);
        await notifications.AddAsync(await notifications.EmailsWithRoleAtLeastAsync(Role.Moderator, ct), userId, NotificationKind.ReportFiled,
            NotificationRules.Subject(family.Namespace, family.Name, v.Version), reason.ToString(), NotificationRules.ArtifactLink(versionId), ct);
        await webhooks.EnqueueAsync(new(WebhookEvents.NameOf(NotificationKind.ReportFiled), NotificationRules.Subject(family.Namespace, family.Name, v.Version), reason.ToString(), NotificationRules.ArtifactLink(versionId), userId, DateTime.UtcNow), ct);
        await db.SaveChangesAsync(ct);

        var times = (await db.Reports.Where(r => r.VersionId == versionId).Select(r => new { r.UserId, r.At }).ToListAsync(ct)).Select(r => (r.UserId, r.At)).ToList();
        if (!v.Quarantined && QuarantinePolicy.ShouldQuarantine(times, now))
        {
            v.Quarantined = true;
            marketplace.Invalidate();
            db.Audit.Add(new AuditEntry { UserId = "system", Action = "quarantine", Entity = "ArtifactVersion", EntityId = versionId.ToString(), Details = "auto: ≥5 reports/1h" });
            await db.SaveChangesAsync(ct);
        }
        return new(true, v.Quarantined);
    }

    public async Task<bool> ReleaseQuarantineAsync(Guid versionId, string userId, CancellationToken ct = default)
    {
        var v = await db.Versions.FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null) return false;
        v.Quarantined = false;
        marketplace.Invalidate();
        db.Audit.Add(new AuditEntry { UserId = userId, Action = "quarantine-release", Entity = "ArtifactVersion", EntityId = versionId.ToString() });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<CommentDto>?> CommentsAsync(Guid versionId, CancellationToken ct = default)
    {
        var artifactId = await db.Versions.Where(v => v.Id == versionId).Select(v => (Guid?)v.ArtifactId).FirstOrDefaultAsync(ct);
        if (artifactId is null) return null;
        var list = await db.Comments.Where(c => c.ArtifactId == artifactId && !c.Hidden).ToListAsync(ct);
        return list.OrderBy(c => c.At).Select(c => new CommentDto(c.Id, c.UserId, c.Body, c.At)).ToList();
    }

    public async Task<CommentDto?> AddCommentAsync(Guid versionId, string userId, string body, CancellationToken ct = default)
    {
        var target = await db.Versions.AsNoTracking().Include(v => v.Artifact).FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (target is null || string.IsNullOrWhiteSpace(body)) return null;
        var artifactId = target.ArtifactId;
        var c = new Comment { ArtifactId = artifactId, UserId = userId, Body = body.Trim()[..Math.Min(body.Trim().Length, 2000)] };
        db.Comments.Add(c);
        var owners = await db.Versions.AsNoTracking().Where(v => v.ArtifactId == artifactId).Select(v => v.CreatedBy).Distinct().ToListAsync(ct);
        await notifications.AddAsync(owners, userId, NotificationKind.CommentAdded,
            NotificationRules.Subject(target.Artifact.Namespace, target.Artifact.Name, target.Version), null, NotificationRules.ArtifactLink(versionId), ct);
        await webhooks.EnqueueAsync(new(WebhookEvents.NameOf(NotificationKind.CommentAdded), NotificationRules.Subject(target.Artifact.Namespace, target.Artifact.Name, target.Version), null, NotificationRules.ArtifactLink(versionId), userId, DateTime.UtcNow), ct);
        await db.SaveChangesAsync(ct);
        return new(c.Id, c.UserId, c.Body, c.At);
    }

    public async Task<IReadOnlyList<MyPublication>> MyPublicationsAsync(string userId, CancellationToken ct = default)
    {
        var list = await db.Versions.AsNoTracking().Include(v => v.Artifact).Where(v => v.CreatedBy == userId).ToListAsync(ct);
        return list.OrderByDescending(v => v.CreatedAt)
            .Select(v => new MyPublication(v.Id, v.Artifact.Namespace, v.Artifact.Name, v.Artifact.Type, v.Version, v.Status, v.Quarantined, v.CreatedAt)).ToList();
    }

    /// <summary>Retire de la publication (→ Deprecated). Réservé à l'auteur, ou à un modérateur (vérifié par l'appelant).</summary>
    public async Task<bool?> UnpublishAsync(Guid versionId, string userId, bool isModerator, CancellationToken ct = default)
    {
        var v = await db.Versions.FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null) return null;
        if (!isModerator && v.CreatedBy != userId) return false;
        db.StatusHistory.Add(new StatusHistory { VersionId = versionId, From = v.Status, To = VersionStatus.Deprecated, UserId = userId, Note = "unpublish" });
        v.Status = VersionStatus.Deprecated;
        marketplace.Invalidate();
        db.Audit.Add(new AuditEntry { UserId = userId, Action = "unpublish", Entity = "ArtifactVersion", EntityId = versionId.ToString() });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<string> SuggestVersionAsync(string ns, string name, ArtifactType type, string? requested, CancellationToken ct = default)
    {
        var known = await db.Versions.Where(v => v.Artifact.Namespace == ns && v.Artifact.Name == name && v.Artifact.Type == type)
            .Select(v => v.Version).ToListAsync(ct);
        return Application.Rules.VersionRules.Resolve(requested, known).Version;
    }

    /// <summary>Supprime un commentaire : 404 si inconnu, false si les droits manquent.</summary>
    public async Task<bool?> DeleteCommentAsync(Guid versionId, Guid commentId, CurrentUser user, CancellationToken ct = default)
    {
        var c = await CommentOfAsync(versionId, commentId, ct);
        if (c is null) return null;
        if (!CommentPolicy.CanDelete(c.UserId, user.Email, user.Role)) return false;
        db.CommentReports.RemoveRange(db.CommentReports.Where(r => r.CommentId == commentId));
        db.Comments.Remove(c);
        db.Audit.Add(new AuditEntry { UserId = user.Email, Action = "comment-delete", Entity = "Comment", EntityId = commentId.ToString(), Details = c.UserId });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Signale un commentaire ; masqué automatiquement à partir de 3 signalants distincts. Un signalant ne compte qu'une fois.</summary>
    public async Task<CommentReportResult> ReportCommentAsync(Guid versionId, Guid commentId, string userId, string? reason, CancellationToken ct = default)
    {
        var c = await CommentOfAsync(versionId, commentId, ct);
        if (c is null) return new(false, false, false);
        if (!CommentPolicy.CanReport(c.UserId, userId)) return new(true, true, c.Hidden);
        if (!await db.CommentReports.AnyAsync(r => r.CommentId == commentId && r.UserId == userId, ct))
            db.CommentReports.Add(new CommentReport { CommentId = commentId, UserId = userId, Reason = reason?.Trim() is { Length: > 0 } t ? t[..Math.Min(t.Length, 500)] : null });
        await db.SaveChangesAsync(ct);
        var reporters = await db.CommentReports.Where(r => r.CommentId == commentId).Select(r => r.UserId).ToListAsync(ct);
        if (!c.Hidden && CommentPolicy.ShouldAutoHide(reporters))
        {
            c.Hidden = true;
            db.Audit.Add(new AuditEntry { UserId = "system", Action = "comment-hide", Entity = "Comment", EntityId = commentId.ToString(), Details = "auto: ≥3 reports" });
            await db.SaveChangesAsync(ct);
        }
        return new(true, false, c.Hidden);
    }

    private async Task<Comment?> CommentOfAsync(Guid versionId, Guid commentId, CancellationToken ct)
    {
        var artifactId = await db.Versions.Where(v => v.Id == versionId).Select(v => (Guid?)v.ArtifactId).FirstOrDefaultAsync(ct);
        return artifactId is null ? null : await db.Comments.FirstOrDefaultAsync(x => x.Id == commentId && x.ArtifactId == artifactId, ct);
    }
}
