using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

/// <summary>
/// Publication interne par pull request sur le dépôt de gouvernance (feed système Azure DevOps) :
/// création à la publication, fusion squash à l'approbation, fermeture au rejet (docs/06, phase 5).
/// Sans feed système, la publication reste directe (stockage en base) comme avant.
/// </summary>
public class PullRequestWorkflow(AppDbContext db, IGitProviderResolver providers)
{
    public sealed record Target(Feed Feed, IGitHostProvider Host, string? Token);

    public async Task<Target?> SystemTargetAsync(CancellationToken ct = default)
    {
        var feed = await db.Feeds.AsNoTracking().FirstOrDefaultAsync(f => f.IsSystem && f.Kind == nameof(FeedKind.AzureDevOps), ct);
        if (feed is null) return null;
        var token = string.IsNullOrWhiteSpace(feed.PatRef) ? null : Environment.GetEnvironmentVariable(feed.PatRef);
        return providers.For(FeedKind.AzureDevOps) is IGitHostProvider host ? new(feed, host, token) : null;
    }

    /// <summary>Ouvre la PR d'une nouvelle version. Lève <see cref="InvalidOperationException"/> si elle ne peut pas être créée (la publication échoue alors).</summary>
    public async Task<PullRequestInfo> OpenAsync(Target t, string ns, string name, string version, Guid versionId, IEnumerable<FilePayload> files, string author, CancellationToken ct = default)
    {
        var folder = GitUrls.ArtifactFolder(ns, name);
        var map = files.ToDictionary(f => $"{folder}/{f.Path}", f => f.Content, StringComparer.Ordinal);
        return await t.Host.CreatePullRequestAsync(t.Feed.Url, new NewPullRequest(
            t.Feed.Branch, GitUrls.PublicationBranch(ns, name, version, versionId), map,
            $"feat({ns}/{name}): publish {version}", $"Publication interne de {ns}/{name}@{version} par {author} (MyRegistry)."), t.Token, ct);
    }

    /// <summary>Applique l'effet Git d'un changement de statut. Retourne un message d'erreur si l'opération Git échoue (le statut ne doit alors pas changer).</summary>
    public async Task<string?> OnTransitionAsync(ArtifactVersion v, VersionStatus to, CancellationToken ct = default)
    {
        if (v.PrId is null || v.PrState != "Open") return null;
        var target = await SystemTargetAsync(ct);
        if (target is null) return null;
        try
        {
            if (to == VersionStatus.Approved) { await target.Host.CompletePullRequestAsync(target.Feed.Url, v.PrId.Value, target.Token, ct); v.PrState = "Merged"; }
            else if (to == VersionStatus.Deprecated) { await target.Host.AbandonPullRequestAsync(target.Feed.Url, v.PrId.Value, target.Token, ct); v.PrState = "Abandoned"; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        { return $"Opération Git impossible sur la pull request : {ex.Message}"; }
        return null;
    }
}
