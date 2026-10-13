using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record CommentDto(Guid Id, string UserId, string Body, DateTime At);
public sealed record ReportResult(bool Accepted, bool Quarantined);
public sealed record MyPublication(Guid Id, string Namespace, string Name, ArtifactType Type, string Version, VersionStatus Status, bool Quarantined, DateTime CreatedAt);

public class CommunityService(AppDbContext db)
{
    public async Task<ReportResult?> ReportAsync(Guid versionId, ReportReason reason, string? details, string userId, CancellationToken ct = default)
    {
        var v = await db.Versions.FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null) return null;

        var now = DateTime.UtcNow;
        db.Reports.Add(new Report { VersionId = versionId, UserId = userId, Reason = reason.ToString(), Details = details, At = now });
        await db.SaveChangesAsync(ct);

        var times = await db.Reports.Where(r => r.VersionId == versionId).Select(r => r.At).ToListAsync(ct);
        if (!v.Quarantined && QuarantinePolicy.ShouldQuarantine(times, now))
        {
            v.Quarantined = true;
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
        var artifactId = await db.Versions.Where(v => v.Id == versionId).Select(v => (Guid?)v.ArtifactId).FirstOrDefaultAsync(ct);
        if (artifactId is null || string.IsNullOrWhiteSpace(body)) return null;
        var c = new Comment { ArtifactId = artifactId.Value, UserId = userId, Body = body.Trim()[..Math.Min(body.Trim().Length, 2000)] };
        db.Comments.Add(c);
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
}
