using MyRegistry.Application;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record SnapshotStatus(bool Configured, string? Repo, string? LastDigest, DateTime? LastAt, string? LastCommit);
public sealed record SnapshotResult(bool Published, string Digest, string? Commit, IReadOnlyList<Issue> Errors);

/// <summary>Génère le manifeste de la marketplace et le pousse dans le dépôt Git configuré (ne fait rien si le digest est inchangé).</summary>
public class SnapshotService(PluginService plugins, SettingsService settings, IGitProvider git, AppDbContext db)
{
    public async Task<SnapshotStatus> StatusAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAllAsync(ct);
        var repo = s.GetValueOrDefault(SettingKeys.MarketplaceRepo);
        return new(!string.IsNullOrWhiteSpace(repo), repo, s.GetValueOrDefault(SettingKeys.SnapshotDigest),
            DateTime.TryParse(s.GetValueOrDefault(SettingKeys.SnapshotAt), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : null,
            s.GetValueOrDefault(SettingKeys.SnapshotCommit));
    }

    public async Task<SnapshotResult> PublishAsync(string actor, CancellationToken ct = default)
    {
        var s = await settings.GetAllAsync(ct);
        var repo = s.GetValueOrDefault(SettingKeys.MarketplaceRepo);
        if (RepoLocation.ToLocalPath(repo) is null)
            return new(false, "", null, [new("snapshot-repo", "Aucun dépôt de snapshot configuré (Configuration > Dépôt du snapshot).")]);
        var publicUrl = s.GetValueOrDefault(SettingKeys.PublicUrl);
        if (string.IsNullOrWhiteSpace(publicUrl))
            return new(false, "", null, [new("snapshot-public-url", "Configurez l'URL publique : les manifestes pointent vers les archives de téléchargement.")]);

        var manifest = (await plugins.MarketplaceAsync(publicUrl)).Body;
        var digest = MarketplaceSnapshot.Digest(manifest);
        if (digest == s.GetValueOrDefault(SettingKeys.SnapshotDigest)) return new(false, digest, s.GetValueOrDefault(SettingKeys.SnapshotCommit), []);

        GitCommitResult commit;
        try { commit = await git.CommitFilesAsync(repo!, MarketplaceSnapshot.Files(manifest), $"chore(marketplace): snapshot {digest[..12]}"); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        { return new(false, digest, null, [new("snapshot-git", ex.Message)]); }

        var errors = await settings.SetInternalAsync(new Dictionary<string, string>
        {
            [SettingKeys.SnapshotDigest] = digest, [SettingKeys.SnapshotAt] = DateTime.UtcNow.ToString("O"), [SettingKeys.SnapshotCommit] = commit.CommitSha ?? "",
        }, ct);
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "marketplace-snapshot", Entity = "Marketplace", EntityId = "snapshot", Details = $"{digest[..12]} commit={commit.CommitSha?[..Math.Min(7, commit.CommitSha.Length)]} changed={commit.Changed}" });
        await db.SaveChangesAsync(ct);
        return new(commit.Changed, digest, commit.CommitSha, errors);
    }
}
