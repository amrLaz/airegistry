using MyRegistry.Domain;

namespace MyRegistry.Application;

public sealed record RetentionCandidate(Guid Id, Guid ArtifactId, string Version, VersionStatus Status, DateTime CreatedAt, bool Pinned, bool OpenPullRequest, bool OpenReports);
public sealed record RetentionDecision(Guid Id, Guid ArtifactId, string Version, string Reason);

/// <summary>
/// Politique de rétention des versions (backlog : « un plugin auto-publié peut atteindre des centaines de versions »).
/// Prudente par construction : seules des versions <b>dépréciées</b> et anciennes sont supprimables ; tout le reste est conservé.
/// </summary>
public static class RetentionPolicy
{
    public const int MinAgeFloorDays = 7, MaxKeep = 1000, MaxAgeDays = 3650;

    /// <summary>Versions à supprimer. <paramref name="keepNewest"/> = 0 désactive la politique (rien n'est supprimé).</summary>
    public static IReadOnlyList<RetentionDecision> Select(IEnumerable<RetentionCandidate> candidates, int keepNewest, int minAgeDays, DateTime now)
    {
        if (keepNewest <= 0) return [];
        var minAge = TimeSpan.FromDays(Math.Max(minAgeDays, MinAgeFloorDays));
        var result = new List<RetentionDecision>();
        foreach (var family in candidates.GroupBy(c => c.ArtifactId))
        {
            var all = family.ToList();
            var latest = DeletionPolicy.Latest(all.Select(c => new VersionRef(c.Id, c.Version, c.CreatedAt)).ToList()).Id;
            var newest = all.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Version, StringComparer.Ordinal).Take(keepNewest).Select(c => c.Id).ToHashSet();
            foreach (var c in all)
            {
                if (c.Status != VersionStatus.Deprecated || c.Id == latest || newest.Contains(c.Id)) continue;   // jamais une version vivante, la dernière ou les N plus récentes
                if (c.Pinned || c.OpenPullRequest || c.OpenReports) continue;                                    // référencée par un plugin, PR ouverte ou signalement en cours
                if (now - c.CreatedAt < minAge) continue;
                result.Add(new(c.Id, c.ArtifactId, c.Version, $"dépréciée, hors des {keepNewest} plus récentes, âgée de {(int)(now - c.CreatedAt).TotalDays} jours"));
            }
        }
        return result;
    }
}
