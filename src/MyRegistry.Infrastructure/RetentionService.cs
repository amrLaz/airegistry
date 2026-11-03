using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record RetentionItem(Guid Id, string Artifact, string Version, DateTime CreatedAt, string Reason);
public sealed record RetentionPreview(bool Enabled, int KeepVersions, int MinAgeDays, IReadOnlyList<RetentionItem> Items);
public sealed record RetentionRunResult(int Deleted, IReadOnlyList<RetentionItem> Items);

public class RetentionService(AppDbContext db, SettingsService settings, MarketplaceCache marketplace)
{
    private async Task<(int Keep, int Age)> ConfigAsync(CancellationToken ct)
    {
        var s = await settings.GetAllAsync(ct);
        return (int.TryParse(s.GetValueOrDefault(SettingKeys.RetentionKeepVersions), out var k) ? k : 0,
                int.TryParse(s.GetValueOrDefault(SettingKeys.RetentionMinAgeDays), out var a) ? a : 90);
    }

    /// <summary>Ce qui serait supprimé maintenant, sans rien supprimer.</summary>
    public async Task<RetentionPreview> PreviewAsync(DateTime? now = null, CancellationToken ct = default)
    {
        var (keep, age) = await ConfigAsync(ct);
        var versions = await db.Versions.AsNoTracking().Include(v => v.Artifact).ToListAsync(ct);
        var pins = (await db.PluginComponents.AsNoTracking().Select(c => new { c.Slug, c.ExactVersion }).ToListAsync(ct)).Select(c => (c.Slug, c.ExactVersion)).ToHashSet();
        var reported = (await db.Reports.AsNoTracking().Where(r => r.Status == "Open").Select(r => r.VersionId).ToListAsync(ct)).ToHashSet();
        var candidates = versions.Select(v => new RetentionCandidate(v.Id, v.ArtifactId, v.Version, v.Status, v.CreatedAt,
            pins.Contains(($"{v.Artifact.Namespace}/{v.Artifact.Name}", v.Version)), v.PrState == "Open", reported.Contains(v.Id))).ToList();
        var byId = versions.ToDictionary(v => v.Id);
        var items = RetentionPolicy.Select(candidates, keep, age, now ?? DateTime.UtcNow)
            .Select(d => new RetentionItem(d.Id, $"{byId[d.Id].Artifact.Namespace}/{byId[d.Id].Artifact.Name}", d.Version, byId[d.Id].CreatedAt, d.Reason))
            .OrderBy(i => i.Artifact, StringComparer.Ordinal).ThenBy(i => i.CreatedAt).ToList();
        return new(keep > 0, keep, age, items);
    }

    /// <summary>Supprime les versions de l'aperçu courant (recalculé au moment de l'appel). Chaque suppression est auditée.</summary>
    public async Task<RetentionRunResult> RunAsync(string actor, DateTime? now = null, CancellationToken ct = default)
    {
        var preview = await PreviewAsync(now, ct);
        if (preview.Items.Count == 0) return new(0, []);
        var ids = preview.Items.Select(i => i.Id).ToList();
        var doomed = await db.Versions.Where(v => ids.Contains(v.Id)).ToListAsync(ct);
        foreach (var i in preview.Items)
            db.Audit.Add(new AuditEntry { UserId = actor, Action = "retention-delete", Entity = "ArtifactVersion", EntityId = i.Id.ToString(), Details = $"{i.Artifact}@{i.Version} : {i.Reason}" });
        db.Versions.RemoveRange(doomed);
        marketplace.Invalidate();
        await db.SaveChangesAsync(ct);
        return new(doomed.Count, preview.Items);
    }
}

/// <summary>Applique la politique de rétention chaque nuit, uniquement si elle est activée (versions conservées &gt; 0).</summary>
public sealed class RetentionJob : IBackgroundJob
{
    public string Name => "retention";
    public TimeSpan Interval => TimeSpan.FromHours(24);

    public async Task RunAsync(IServiceProvider sp, CancellationToken ct) =>
        await sp.GetRequiredService<RetentionService>().RunAsync("system", ct: ct);
}
