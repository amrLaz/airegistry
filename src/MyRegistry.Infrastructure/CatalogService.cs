using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record CatalogItem(Guid Id, string Namespace, string Name, ArtifactType Type, string Version, string? Description,
    VersionStatus Status, bool Quarantined, string Grade, string ContentHash, int Votes, int Downloads, string[] Tags, DateTime CreatedAt);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public class CatalogService(AppDbContext db, MarketplaceCache marketplace)
{
    public async Task<PagedResult<CatalogItem>> ListAsync(ArtifactType? type, VersionStatus? status, string? tag, string? q, string? sort,
        bool includeNonPublic, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Une entrée par famille : dernière version visible (approuvée, hors quarantaine sauf modération).
        var versions = db.Versions.AsNoTracking().Include(v => v.Artifact).ThenInclude(a => a.Tags).AsQueryable();
        if (!includeNonPublic) versions = versions.Where(v => v.Status == VersionStatus.Approved && !v.Quarantined);
        else if (status is not null) versions = versions.Where(v => v.Status == status);
        if (type is not null) versions = versions.Where(v => v.Artifact.Type == type);
        if (!string.IsNullOrWhiteSpace(tag)) versions = versions.Where(v => v.Artifact.Tags.Any(t => t.Name == tag));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            versions = versions.Where(v => v.Artifact.Name.ToLower().Contains(term) || v.Artifact.Namespace.ToLower().Contains(term)
                || (v.Description != null && v.Description.ToLower().Contains(term)) || v.CreatedBy.ToLower().Contains(term));
        }

        var all = await versions.ToListAsync(ct);
        var latest = all.GroupBy(v => v.ArtifactId)
            .Select(g => g.OrderByDescending(v => v.CreatedAt).First()).ToList();
        var votes = await db.Votes.GroupBy(v => v.ArtifactId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

        var items = latest.Select(v => new CatalogItem(v.Id, v.Artifact.Namespace, v.Artifact.Name, v.Artifact.Type, v.Version, v.Description,
            v.Status, v.Quarantined, v.QualityGrade, v.ContentHash, votes.GetValueOrDefault(v.ArtifactId), v.DownloadCount,
            v.Artifact.Tags.Select(t => t.Name).OrderBy(n => n).ToArray(), v.CreatedAt));

        items = sort == "popular" ? items.OrderByDescending(i => i.Votes).ThenByDescending(i => i.CreatedAt) : items.OrderByDescending(i => i.CreatedAt);
        var list = items.ToList();
        return new(list.Skip((page - 1) * pageSize).Take(pageSize).ToList(), list.Count, page, pageSize);
    }

    public Task<ArtifactVersion?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Versions.Include(v => v.Artifact).ThenInclude(a => a.Tags).Include(v => v.Files).Include(v => v.History)
            .AsSplitQuery().FirstOrDefaultAsync(v => v.Id == id, ct);

    public Task<ArtifactVersion?> GetByHashAsync(string hash, CancellationToken ct = default) =>
        db.Versions.Include(v => v.Artifact).Include(v => v.Files).FirstOrDefaultAsync(v => v.ContentHash == hash, ct);

    public async Task<ArtifactVersion?> GetByNameAsync(string ns, string name, string? version, CancellationToken ct = default)
    {
        var q = db.Versions.Include(v => v.Artifact).Include(v => v.Files)
            .Where(v => v.Artifact.Namespace == ns && v.Artifact.Name == name && v.Status == VersionStatus.Approved && !v.Quarantined);
        if (version is not null) q = q.Where(v => v.Version == version);
        return (await q.ToListAsync(ct)).OrderByDescending(v => v.CreatedAt).FirstOrDefault();
    }

    public async Task CountDownloadAsync(Guid versionId, CancellationToken ct = default) =>
        await db.Versions.Where(v => v.Id == versionId).ExecuteUpdateAsync(s => s.SetProperty(v => v.DownloadCount, v => v.DownloadCount + 1), ct);

    public static byte[] Zip(ArtifactVersion v)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var f in v.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry(f.Path);
                using var s = entry.Open();
                s.Write(f.Content);
            }
        return ms.ToArray();
    }

    public async Task<bool> SetStatusAsync(Guid id, VersionStatus to, string userId, string? note, CancellationToken ct = default)
    {
        var v = await db.Versions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return false;
        db.StatusHistory.Add(new StatusHistory { VersionId = id, From = v.Status, To = to, UserId = userId, Note = note });
        v.Status = to;
        marketplace.Invalidate();
        db.Audit.Add(new AuditEntry { UserId = userId, Action = "status", Entity = "ArtifactVersion", EntityId = id.ToString(), Details = to.ToString() });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public Task<int> VoteCountAsync(Guid artifactId, CancellationToken ct = default) =>
        db.Votes.CountAsync(v => v.ArtifactId == artifactId, ct);

    /// <summary>Bascule le vote de l'utilisateur ; renvoie le total ou null si la version n'existe pas.</summary>
    public async Task<int?> ToggleVoteAsync(Guid versionId, string userId, CancellationToken ct = default)
    {
        var artifactId = await db.Versions.Where(v => v.Id == versionId).Select(v => (Guid?)v.ArtifactId).FirstOrDefaultAsync(ct);
        if (artifactId is null) return null;
        var has = await db.Votes.AnyAsync(v => v.ArtifactId == artifactId && v.UserId == userId, ct);
        return await VoteAsync(versionId, userId, !has, ct);
    }

    public async Task<int?> VoteAsync(Guid versionId, string userId, bool add, CancellationToken ct = default)
    {
        var artifactId = await db.Versions.Where(v => v.Id == versionId).Select(v => (Guid?)v.ArtifactId).FirstOrDefaultAsync(ct);
        if (artifactId is null) return null;
        var existing = await db.Votes.FindAsync([artifactId.Value, userId], ct);
        if (add && existing is null) db.Votes.Add(new Vote { ArtifactId = artifactId.Value, UserId = userId });
        if (!add && existing is not null) db.Votes.Remove(existing);
        await db.SaveChangesAsync(ct);
        return await db.Votes.CountAsync(v => v.ArtifactId == artifactId, ct);
    }
}
