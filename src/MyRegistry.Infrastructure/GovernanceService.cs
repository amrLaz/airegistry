using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record GovernanceDto(string? Owner, RiskLevel Risk, DataClass DataClass, int? ReviewEveryDays, DateTime? LastReviewed,
    IReadOnlyList<string> Capabilities, bool ReviewDue, bool Defined, string Author, bool CanEdit);

public sealed record SaveGovernanceResult(GovernanceDto? Contract, IReadOnlyList<Issue> Errors, bool Forbidden, bool NotFound);

public class GovernanceService(AppDbContext db)
{
    private async Task<(Guid ArtifactId, string Author, DateTime FirstAt)?> FamilyAsync(Guid versionId, CancellationToken ct)
    {
        var artifactId = await db.Versions.Where(v => v.Id == versionId).Select(v => (Guid?)v.ArtifactId).FirstOrDefaultAsync(ct);
        if (artifactId is null) return null;
        var first = (await db.Versions.Where(v => v.ArtifactId == artifactId).Select(v => new { v.CreatedBy, v.CreatedAt }).ToListAsync(ct)).OrderBy(v => v.CreatedAt).First();
        return (artifactId.Value, first.CreatedBy, first.CreatedAt);
    }

    private static GovernanceDto ToDto(Governance? g, string author, DateTime createdAt, CurrentUser user, DateTime now) =>
        g is null
            ? new(author, RiskLevel.Low, DataClass.Internal, null, null, [], false, false, author, GovernancePolicy.CanEdit(user, author))
            : new(g.OwnerId, g.Risk, g.DataClass, g.ReviewEveryDays, g.LastReviewed, g.Capabilities,
                GovernancePolicy.ReviewDue(g.ReviewEveryDays, g.LastReviewed, g.UpdatedAt, now), true, author, GovernancePolicy.CanEdit(user, author));

    public async Task<GovernanceDto?> GetAsync(Guid versionId, CurrentUser user, CancellationToken ct = default)
    {
        if (await FamilyAsync(versionId, ct) is not { } f) return null;
        var g = await db.Governances.AsNoTracking().FirstOrDefaultAsync(x => x.ArtifactId == f.ArtifactId, ct);
        return ToDto(g, f.Author, f.FirstAt, user, DateTime.UtcNow);
    }

    public async Task<SaveGovernanceResult> SaveAsync(Guid versionId, GovernanceInput input, CurrentUser user, CancellationToken ct = default)
    {
        if (await FamilyAsync(versionId, ct) is not { } f) return new(null, [], false, true);
        if (!GovernancePolicy.CanEdit(user, f.Author)) return new(null, [], true, false);

        var (caps, errors) = GovernancePolicy.Validate(input);
        if (errors.Count > 0) return new(null, errors, false, false);

        var g = await db.Governances.FirstOrDefaultAsync(x => x.ArtifactId == f.ArtifactId, ct);
        if (g is null) { g = new Governance { ArtifactId = f.ArtifactId }; db.Governances.Add(g); }
        g.OwnerId = input.OwnerId!.Trim().ToLowerInvariant();
        g.Risk = input.Risk; g.DataClass = input.DataClass; g.ReviewEveryDays = input.ReviewEveryDays;
        g.Capabilities = caps.ToList();
        if (input.MarkReviewed) g.LastReviewed = DateTime.UtcNow;
        g.UpdatedAt = DateTime.UtcNow; g.UpdatedBy = user.Email;

        db.Audit.Add(new AuditEntry
        {
            UserId = user.Email, Action = input.MarkReviewed ? "governance-reviewed" : "governance-update", Entity = "Artifact", EntityId = f.ArtifactId.ToString(),
            Details = $"owner={g.OwnerId} risk={g.Risk} data={g.DataClass} review={g.ReviewEveryDays}",
        });
        await db.SaveChangesAsync(ct);
        return new(ToDto(g, f.Author, f.FirstAt, user, DateTime.UtcNow), [], false, false);
    }

    public async Task<IReadOnlyList<(string Capability, int Artifacts)>> CapabilitiesInventoryAsync(CancellationToken ct = default) =>
        (await db.Governances.AsNoTracking().Select(g => g.Capabilities).ToListAsync(ct))
            .SelectMany(c => c).GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count())).ToList();

    public async Task<int> ReviewsDueCountAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return (await db.Governances.AsNoTracking().ToListAsync(ct)).Count(g => GovernancePolicy.ReviewDue(g.ReviewEveryDays, g.LastReviewed, g.UpdatedAt, now));
    }
}

public class SettingsService(AppDbContext db, MarketplaceCache marketplace)
{
    public async Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
        await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);

    public async Task<string> GetAsync(string key, string fallback, CancellationToken ct = default) =>
        (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value ?? fallback;

    public async Task<IReadOnlyList<Issue>> UpdateAsync(IReadOnlyDictionary<string, string> values, string actor, CancellationToken ct = default)
    {
        var clean = values.ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
        var errors = SettingsRules.Validate(clean);
        if (errors.Count > 0) return errors;
        var existing = await db.Settings.ToDictionaryAsync(s => s.Key, ct);
        var changes = new List<string>();
        foreach (var (k, v) in clean)
        {
            if (existing.TryGetValue(k, out var s)) { if (s.Value != v) { changes.Add($"{k}: {s.Value} → {v}"); s.Value = v; } }
            else { db.Settings.Add(new Setting { Key = k, Value = v }); changes.Add($"{k} = {v}"); }
        }
        if (changes.Count > 0)
            db.Audit.Add(new AuditEntry { UserId = actor, Action = "settings-update", Entity = "Settings", EntityId = "global", Details = string.Join("; ", changes) });
        await db.SaveChangesAsync(ct);
        marketplace.Invalidate();
        return [];
    }
}
