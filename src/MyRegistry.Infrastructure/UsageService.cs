using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record UsageTop(string Name, string Type, int Count);
public sealed record UsagePoint(DateTime Day, int Count);
public sealed record UsageSummary(int Days, int Total, IReadOnlyDictionary<string, int> BySource, IReadOnlyDictionary<string, int> ByType, IReadOnlyList<UsageTop> Top, IReadOnlyList<UsagePoint> Daily);

public class UsageService(AppDbContext db)
{
    /// <summary>Incrémente le compteur du jour. Mise à jour atomique ; la première écriture du jour insère, et si deux requêtes s'y croisent la perdante réessaie en mise à jour.</summary>
    public async Task RecordAsync(Guid versionId, string source, DateTime? now = null, CancellationToken ct = default)
    {
        if (!UsageSources.All.Contains(source)) source = UsageSources.Api;
        var day = UsageRules.DayOf(now ?? DateTime.UtcNow);
        Task<int> Bump() => db.UsageDaily.Where(u => u.Day == day && u.VersionId == versionId && u.Source == source)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Count, u => u.Count + 1), ct);
        if (await Bump() > 0) return;
        var row = new UsageDaily { Day = day, VersionId = versionId, Source = source, Count = 1 };
        db.UsageDaily.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            await Bump();
        }
    }

    public async Task<UsageSummary> SummaryAsync(int? days, DateTime? now = null, CancellationToken ct = default)
    {
        var n = UsageRules.ClampDays(days);
        var clock = now ?? DateTime.UtcNow;
        var start = UsageRules.WindowStart(clock, n);
        var end = UsageRules.DayOf(clock);
        var rows = await db.UsageDaily.AsNoTracking().Where(u => u.Day >= start && u.Day <= end).ToListAsync(ct);
        var ids = rows.Select(r => r.VersionId).Distinct().ToList();
        var versions = await db.Versions.AsNoTracking().Include(v => v.Artifact).Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);

        var bySource = UsageSources.All.ToDictionary(s => s, s => rows.Where(r => r.Source == s).Sum(r => r.Count));
        var known = rows.Where(r => versions.ContainsKey(r.VersionId)).ToList();
        var byType = Enum.GetValues<ArtifactType>().ToDictionary(t => t.ToString(), t => known.Where(r => versions[r.VersionId].Artifact.Type == t).Sum(r => r.Count));
        var top = known.GroupBy(r => versions[r.VersionId].ArtifactId)
            .Select(g => (A: versions[g.First().VersionId].Artifact, Count: g.Sum(r => r.Count)))
            .OrderByDescending(x => x.Count).ThenBy(x => x.A.Name, StringComparer.Ordinal).Take(10)
            .Select(x => new UsageTop($"{x.A.Namespace}/{x.A.Name}", x.A.Type.ToString(), x.Count)).ToList();
        var perDay = rows.GroupBy(r => r.Day).ToDictionary(g => DateTime.SpecifyKind(g.Key, DateTimeKind.Utc), g => g.Sum(r => r.Count));
        return new(n, rows.Sum(r => r.Count), bySource, byType, top, UsageRules.FillDays(perDay, clock, n).Select(p => new UsagePoint(p.Day, p.Count)).ToList());
    }
}
