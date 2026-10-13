using System.Text;
using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public interface IGitProviderResolver
{
    /// <summary>Fournisseur d'un type de feed. Lève <see cref="NotSupportedException"/> si le fournisseur n'est pas encore disponible.</summary>
    IGitProvider For(FeedKind kind);
}

public sealed class GitProviderResolver(LocalGitProvider local, GitHubProvider gitHub, AzureDevOpsProvider azdo) : IGitProviderResolver
{
    public IGitProvider For(FeedKind kind) => kind switch
    {
        FeedKind.Local => local,
        FeedKind.GitHub => gitHub,
        FeedKind.AzureDevOps => azdo,
        _ => throw new NotSupportedException($"Fournisseur inconnu : {kind}."),
    };
}

public sealed record FeedDto(Guid Id, string Name, string Kind, string Url, string Branch, string? PatRef, int IntervalMinutes, bool Active, bool IsSystem,
    string? NamespaceOverride, string EffectiveNamespace, string Status, DateTime? LastSyncAt, string? LastCommit, int ConsecutiveFailures);
public sealed record FeedReportDto(Guid Id, DateTime StartedAt, DateTime? EndedAt, bool Success, string? Commit, int Imported, int Updated, int Unchanged,
    int Skipped, int Deprecated, int Quarantined, IReadOnlyList<string> Messages);
public sealed record FeedResult(FeedDto? Feed, IReadOnlyList<Issue> Errors, bool NotFound, bool Conflict = false);

public class FeedService(AppDbContext db, IGitProviderResolver providers, SettingsService settings, MarketplaceCache marketplace)
{
    // ---------- CRUD ----------
    private async Task<string> RegistryNamespaceAsync(CancellationToken ct) => await settings.GetAsync(SettingKeys.DefaultNamespace, "myregistry", ct);

    private async Task<FeedDto> ToDtoAsync(Feed f, CancellationToken ct)
    {
        var kind = Enum.Parse<FeedKind>(f.Kind);
        return new(f.Id, f.Name, f.Kind, f.Url, f.Branch, f.PatRef, f.IntervalMinutes, f.Active, f.IsSystem, f.NamespaceOverride,
            f.NamespaceOverride ?? FeedRules.DefaultNamespace(await RegistryNamespaceAsync(ct), kind), f.Status.ToString(), f.LastSyncAt, f.LastCommit, f.ConsecutiveFailures);
    }

    public async Task<IReadOnlyList<FeedDto>> ListAsync(CancellationToken ct = default)
    {
        var feeds = (await db.Feeds.AsNoTracking().ToListAsync(ct)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        var list = new List<FeedDto>();
        foreach (var f in feeds) list.Add(await ToDtoAsync(f, ct));
        return list;
    }

    public async Task<FeedDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.Feeds.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct) is { } f ? await ToDtoAsync(f, ct) : null;

    public async Task<FeedResult> CreateAsync(FeedRules.FeedInput input, bool isSystem, string actor, CancellationToken ct = default)
    {
        var errors = FeedRules.Validate(input).ToList();
        if (isSystem && await db.Feeds.AnyAsync(f => f.IsSystem, ct)) errors.Add(new("system", "Un feed système existe déjà."));
        if (errors.Count > 0) return new(null, errors, false);

        var f = new Feed
        {
            Name = input.Name.Trim(), Kind = input.Kind.ToString(), Url = input.Url.Trim(), Branch = string.IsNullOrWhiteSpace(input.Branch) ? "main" : input.Branch.Trim(),
            PatRef = string.IsNullOrWhiteSpace(input.PatRef) ? null : input.PatRef.Trim(), IntervalMinutes = input.IntervalMinutes ?? FeedRules.DefaultInterval(input.Kind),
            NamespaceOverride = string.IsNullOrWhiteSpace(input.NamespaceOverride) ? null : input.NamespaceOverride.Trim(), IsSystem = isSystem,
        };
        db.Feeds.Add(f);
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "feed-create", Entity = "Feed", EntityId = f.Id.ToString(), Details = $"{f.Kind} {f.Url}" });
        await db.SaveChangesAsync(ct);
        return new(await ToDtoAsync(f, ct), [], false);
    }

    public async Task<FeedResult> UpdateAsync(Guid id, FeedRules.FeedInput input, bool? active, string actor, CancellationToken ct = default)
    {
        var f = await db.Feeds.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return new(null, [], true);
        var errors = FeedRules.Validate(input);
        if (errors.Count > 0) return new(null, errors, false);
        if (input.Kind.ToString() != f.Kind) return new(null, [new("kind", "Le type d'un feed ne peut pas être modifié : créez-en un autre.")], false);
        if (!string.Equals(input.NamespaceOverride ?? "", f.NamespaceOverride ?? "", StringComparison.Ordinal))
            return new(null, [new("namespace", "Le namespace se modifie via l'aperçu puis l'application (PUT /namespace).")], false);

        f.Name = input.Name.Trim(); f.Url = input.Url.Trim(); f.Branch = string.IsNullOrWhiteSpace(input.Branch) ? "main" : input.Branch.Trim();
        f.PatRef = string.IsNullOrWhiteSpace(input.PatRef) ? null : input.PatRef.Trim();
        f.IntervalMinutes = input.IntervalMinutes ?? f.IntervalMinutes;
        if (active is not null)
        {
            f.Active = active.Value;
            if (active.Value) { f.ConsecutiveFailures = 0; if (f.Status == FeedStatus.Paused) f.Status = FeedStatus.Idle; } // réactivation manuelle
        }
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "feed-update", Entity = "Feed", EntityId = id.ToString(), Details = f.Url });
        await db.SaveChangesAsync(ct);
        return new(await ToDtoAsync(f, ct), [], false);
    }

    /// <summary>Supprime le feed et, en cascade, ses artefacts importés. Le feed système est protégé.</summary>
    public async Task<(bool Found, bool Deleted, int RemovedVersions, int RemovedArtifacts)> DeleteAsync(Guid id, string actor, CancellationToken ct = default)
    {
        var f = await db.Feeds.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return (false, false, 0, 0);
        if (f.IsSystem) return (true, false, 0, 0);

        var versions = await db.Versions.Where(v => v.FeedId == id).ToListAsync(ct);
        var versionIds = versions.Select(v => v.Id).ToList();
        var artifactIds = versions.Select(v => v.ArtifactId).Distinct().ToList();

        db.PluginComponents.RemoveRange(db.PluginComponents.Where(c => versionIds.Contains(c.PluginVersionId)));
        db.Reports.RemoveRange(db.Reports.Where(r => versionIds.Contains(r.VersionId)));
        db.Versions.RemoveRange(versions);
        await db.SaveChangesAsync(ct);

        var orphans = await db.Artifacts.Where(a => artifactIds.Contains(a.Id) && !a.Versions.Any()).ToListAsync(ct);
        var orphanIds = orphans.Select(a => a.Id).ToList();
        db.Governances.RemoveRange(db.Governances.Where(g => orphanIds.Contains(g.ArtifactId)));
        db.Comments.RemoveRange(db.Comments.Where(c => orphanIds.Contains(c.ArtifactId)));
        db.Artifacts.RemoveRange(orphans);
        db.FeedReports.RemoveRange(db.FeedReports.Where(r => r.FeedId == id));
        db.Feeds.Remove(f);
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "feed-delete", Entity = "Feed", EntityId = id.ToString(), Details = $"{f.Name}: {versions.Count} version(s), {orphans.Count} artefact(s) supprimés" });
        await db.SaveChangesAsync(ct);
        marketplace.Invalidate();
        return (true, true, versions.Count, orphans.Count);
    }

    // ---------- Changement de namespace (aperçu puis application) ----------
    private async Task<(Feed? Feed, string OldNs, List<(ArtifactType Type, string Name)> Owned)> NamespaceContextAsync(Guid id, CancellationToken ct)
    {
        var f = await db.Feeds.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return (null, "", []);
        var oldNs = f.NamespaceOverride ?? FeedRules.DefaultNamespace(await RegistryNamespaceAsync(ct), Enum.Parse<FeedKind>(f.Kind));
        // Artefacts qui appartiennent exclusivement à ce feed
        var owned = (await db.Artifacts.AsNoTracking().Include(a => a.Versions).Where(a => a.Versions.Any(v => v.FeedId == id)).ToListAsync(ct))
            .Where(a => a.Versions.All(v => v.FeedId == id)).Select(a => (a.Type, a.Name)).ToList();
        return (f, oldNs, owned);
    }

    public async Task<RenamePlan?> PreviewNamespaceAsync(Guid id, string newNamespace, CancellationToken ct = default)
    {
        var (f, oldNs, owned) = await NamespaceContextAsync(id, ct);
        if (f is null) return null;
        var existing = (await db.Artifacts.AsNoTracking().Select(a => new { a.Type, a.Namespace, a.Name }).ToListAsync(ct)).Select(a => (a.Type, $"{a.Namespace}/{a.Name}"));
        return NamespaceRename.Plan(oldNs, newNamespace.Trim(), owned, existing);
    }

    /// <summary>Applique le changement : renomme les artefacts du feed et conserve les anciens slugs en alias pendant 547 jours.</summary>
    public async Task<(RenamePlan? Plan, bool Applied)> ApplyNamespaceAsync(Guid id, string newNamespace, string actor, CancellationToken ct = default)
    {
        var plan = await PreviewNamespaceAsync(id, newNamespace, ct);
        if (plan is null || !plan.CanApply) return (plan, false);
        var feed = await db.Feeds.FirstAsync(x => x.Id == id, ct);
        var now = DateTime.UtcNow;

        foreach (var item in plan.Items)
        {
            var a = await db.Artifacts.FirstAsync(x => x.Namespace == plan.OldNamespace && x.Name == item.Name && x.Type == item.Type, ct);
            a.Namespace = plan.NewNamespace;
            db.Aliases.Add(new NamespaceAlias { OldSlug = item.OldSlug, NewSlug = item.NewSlug, ExpiresAt = AliasRules.ExpiresAt(now) });
        }
        // Les alias existants qui pointaient vers l'ancien slug suivent le renommage (pas de chaîne cassée).
        foreach (var alias in await db.Aliases.Where(x => plan.Items.Select(i => i.OldSlug).Contains(x.NewSlug)).ToListAsync(ct)) alias.NewSlug = alias.NewSlug.Replace(plan.OldNamespace + "/", plan.NewNamespace + "/");
        feed.NamespaceOverride = plan.NewNamespace;
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "feed-namespace", Entity = "Feed", EntityId = id.ToString(), Details = $"{plan.OldNamespace} → {plan.NewNamespace} ({plan.Items.Count} artefact(s))" });
        await db.SaveChangesAsync(ct);
        marketplace.Invalidate();
        return (plan, true);
    }

    // ---------- Test de connexion ----------
    public async Task<(bool Ok, string Message, string? Head, int? Files)> TestConnectionAsync(FeedRules.FeedInput input, CancellationToken ct = default)
    {
        var errors = FeedRules.Validate(input);
        if (errors.Count > 0) return (false, string.Join(" ", errors.Select(e => e.Message)), null, null);
        try
        {
            var snap = await providers.For(input.Kind).ReadRepoAsync(input.Url, input.Branch, TokenFor(input.PatRef), ct);
            return (true, "Connexion réussie.", snap.HeadCommit, snap.Files.Count);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or IOException or HttpRequestException or TaskCanceledException)
        { return (false, ex.Message, null, null); }
    }

    private static string? TokenFor(string? patRef) => string.IsNullOrWhiteSpace(patRef) ? null : Environment.GetEnvironmentVariable(patRef);

    // ---------- Rapports ----------
    public async Task<IReadOnlyList<FeedReportDto>> ReportsAsync(Guid feedId, int take = 20, CancellationToken ct = default) =>
        (await db.FeedReports.AsNoTracking().Where(r => r.FeedId == feedId).ToListAsync(ct)).OrderByDescending(r => r.StartedAt).Take(take)
            .Select(r => new FeedReportDto(r.Id, r.StartedAt, r.EndedAt, r.Success, r.Commit, r.Imported, r.Updated, r.Unchanged, r.Skipped, r.Deprecated, r.Quarantined,
                r.Messages.Split('\n', StringSplitOptions.RemoveEmptyEntries))).ToList();

    // ---------- Synchronisation ----------
    public async Task<FeedReportDto?> SyncAsync(Guid feedId, string actor, bool force = false, CancellationToken ct = default)
    {
        var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Id == feedId, ct);
        if (feed is null) return null;
        var kind = Enum.Parse<FeedKind>(feed.Kind);
        var report = new FeedSyncReport { FeedId = feed.Id };
        var messages = new List<string>();

        RepoSnapshot snapshot;
        try { snapshot = await providers.For(kind).ReadRepoAsync(feed.Url, feed.Branch, TokenFor(feed.PatRef), ct); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or IOException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            feed.ConsecutiveFailures++;
            feed.LastSyncAt = DateTime.UtcNow;
            feed.Status = FeedStatus.Failed;
            if (FeedRules.ShouldPause(feed.ConsecutiveFailures)) { feed.Active = false; feed.Status = FeedStatus.Paused; messages.Add($"Feed mis en pause après {feed.ConsecutiveFailures} échecs consécutifs."); }
            messages.Insert(0, "Échec de lecture : " + ex.Message);
            return await FinishAsync(report, feed, success: false, null, messages, actor, ct);
        }

        report.Commit = snapshot.HeadCommit;
        foreach (var s in snapshot.Skipped) { report.Skipped++; messages.Add($"Ignoré (taille ou nombre de fichiers) : {s}"); }

        if (!force && snapshot.HeadCommit == feed.LastCommit)
        {
            feed.ConsecutiveFailures = 0; feed.LastSyncAt = DateTime.UtcNow; feed.Status = FeedStatus.Ok;
            messages.Add("Aucun nouveau commit.");
            return await FinishAsync(report, feed, success: true, snapshot.HeadCommit, messages, actor, ct);
        }

        var ns = feed.NamespaceOverride ?? FeedRules.DefaultNamespace(await RegistryNamespaceAsync(ct), kind);
        var repoName = feed.Url.TrimEnd('/').Split('/', '\\').Last().Replace(".git", "");
        var files = feed.IsSystem ? snapshot.Files.Where(f => !f.Key.StartsWith("artifacts/", StringComparison.Ordinal)).ToDictionary(f => f.Key, f => f.Value) : snapshot.Files; // artifacts/ = publications internes
        var discovered = RepoDiscovery.Discover(files, NameRules.Canonicalize(repoName) is { Length: > 1 } rn ? rn : "depot");
        var present = new HashSet<(ArtifactType, string, string)>();

        foreach (var d in discovered)
        {
            var name = NameRules.Canonicalize(d.RawName);
            var check = NameRules.Check(ns, name, d.Type, creation: false); // la règle « nom contenant son type » ne vaut que pour une création interne
            if (!check.IsValid) { report.Skipped++; messages.Add($"Ignoré {d.GitPath} ({d.RawName}) : {string.Join(" ", check.Errors.Select(e => e.Message))}"); continue; }
            present.Add((d.Type, name, d.GitPath));
            await ImportOneAsync(feed, ns, name, d, snapshot.HeadCommit, report, messages, ct);
        }

        await DeprecateAbsentAsync(feed, present, report, messages, ct);

        feed.ConsecutiveFailures = 0; feed.LastCommit = snapshot.HeadCommit; feed.LastSyncAt = DateTime.UtcNow; feed.Status = FeedStatus.Ok;
        marketplace.Invalidate();
        return await FinishAsync(report, feed, success: true, snapshot.HeadCommit, messages, actor, ct);
    }

    private async Task ImportOneAsync(Feed feed, string ns, string name, DiscoveredArtifact d, string commit, FeedSyncReport report, List<string> messages, CancellationToken ct)
    {
        var files = d.Files.Select(f => new FilePayload(f.Key, f.Value, f.Value.Contains((byte)0))).ToList();
        var hash = ContentHasher.Hash(files);

        var artifact = await db.Artifacts.Include(a => a.Versions).FirstOrDefaultAsync(a => a.Namespace == ns && a.Name == name && a.Type == d.Type, ct);
        var latest = artifact?.Versions.Where(v => v.FeedId == feed.Id && v.GitPath == d.GitPath).OrderByDescending(v => v.CreatedAt).FirstOrDefault();

        if (artifact is not null && latest is null && artifact.Versions.Any(v => v.FeedId != feed.Id))
        {
            report.Skipped++; messages.Add($"Ignoré {d.GitPath} : {ns}/{name} existe déjà et provient d'une autre source.");
            return;
        }

        if (latest is not null && latest.ContentHash == hash)
        {
            report.Unchanged++;
            if (latest.Status == VersionStatus.Deprecated && await LastNoteAsync(latest.Id, ct) == "absent-from-repo")
            {
                var before = (await db.StatusHistory.Where(h => h.VersionId == latest.Id && h.Note == "absent-from-repo").ToListAsync(ct)).OrderByDescending(h => h.At).First().From ?? VersionStatus.ToReview;
                db.StatusHistory.Add(new StatusHistory { VersionId = latest.Id, From = latest.Status, To = before, UserId = $"feed:{feed.Name}", Note = "back-in-repo" });
                latest.Status = before;
                messages.Add($"Retour dans le dépôt : {ns}/{name} repasse en {before}.");
            }
            return;
        }

        // Validation : un secret met en quarantaine (import externe), toute autre erreur de structure fait ignorer l'élément.
        var validation = Validation.Run(d.Type, files);
        var structural = validation.Errors.Where(e => e.Code != "secret-detected").ToList();
        if (structural.Count > 0)
        {
            report.Skipped++; messages.Add($"Ignoré {d.GitPath} : {string.Join(" ; ", structural.Select(e => $"{e.Code} {e.Message}"))}");
            return;
        }
        var quarantine = validation.Errors.Any(e => e.Code == "secret-detected");
        if (d.Type == ArtifactType.Plugin && !await ComponentsResolvedAsync(d, ct)) { quarantine = true; messages.Add($"Quarantaine {d.GitPath} : composants du plugin introuvables dans le registre."); }

        var known = artifact?.Versions.Select(v => v.Version).ToList() ?? [];
        var fm = Frontmatter.Parse(Encoding.UTF8.GetString(d.Files[d.MainFile])) ?? new Dictionary<string, string>();
        var proposed = FeedVersion.Resolve(fm, latest?.Version, contentChanged: true);
        var decision = VersionRules.Resolve(proposed, known);
        if (decision.Error is not null) { report.Skipped++; messages.Add($"Ignoré {d.GitPath} : {decision.Error}"); return; }

        if (artifact is null) { artifact = new Artifact { Namespace = ns, Name = name, Type = d.Type }; db.Artifacts.Add(artifact); }
        var v = new ArtifactVersion
        {
            Artifact = artifact, Version = decision.Version, Description = fm.GetValueOrDefault("description"), ContentHash = hash, QualityGrade = validation.Grade,
            MainFile = d.MainFile, Origin = Origin.External, FeedId = feed.Id, GitPath = d.GitPath, CommitSha = commit, CreatedBy = $"feed:{feed.Name}",
            Status = VersionStatus.ToReview, Quarantined = quarantine,
            Files = files.Select(f => new VersionFile { Path = f.Path, Content = f.Content, IsBinary = f.IsBinary }).ToList(),
        };
        v.History.Add(new StatusHistory { To = VersionStatus.ToReview, UserId = $"feed:{feed.Name}", Note = quarantine ? "feed-import:quarantine" : "feed-import" });
        db.Versions.Add(v);
        if (latest is null) report.Imported++; else report.Updated++;
        if (quarantine) { report.Quarantined++; if (validation.Errors.Any(e => e.Code == "secret-detected")) messages.Add($"Quarantaine {d.GitPath} : secret détecté ({string.Join(", ", validation.Errors.Where(e => e.Code == "secret-detected").Select(e => $"{e.File}:{e.Line}"))})."); }
    }

    private async Task<bool> ComponentsResolvedAsync(DiscoveredArtifact d, CancellationToken ct)
    {
        var manifestPath = PluginRules.ManifestPaths.FirstOrDefault(d.Files.ContainsKey);
        if (manifestPath is null) return true;
        IReadOnlyList<ComponentRef> refs;
        try { refs = PluginRules.ReadManifest(Encoding.UTF8.GetString(d.Files[manifestPath])); } catch (System.Text.Json.JsonException) { return false; }
        foreach (var r in refs)
        {
            var p = r.Slug.Split('/');
            if (p.Length != 2) return false;
            if (!await db.Versions.AnyAsync(v => v.Artifact.Namespace == p[0] && v.Artifact.Name == p[1] && v.Artifact.Type == r.Type && v.Version == r.Version, ct)) return false;
        }
        return true;
    }

    private async Task<string?> LastNoteAsync(Guid versionId, CancellationToken ct) =>
        (await db.StatusHistory.Where(h => h.VersionId == versionId).ToListAsync(ct)).OrderByDescending(h => h.At).FirstOrDefault()?.Note;

    /// <summary>Un artefact du feed qui n'est plus dans le dépôt passe en Deprecated ; il sera restauré s'il revient.</summary>
    private async Task DeprecateAbsentAsync(Feed feed, HashSet<(ArtifactType, string, string)> present, FeedSyncReport report, List<string> messages, CancellationToken ct)
    {
        var current = await db.Versions.Include(v => v.Artifact).Where(v => v.FeedId == feed.Id).ToListAsync(ct);
        foreach (var group in current.GroupBy(v => (v.ArtifactId, v.GitPath)))
        {
            var latest = group.OrderByDescending(v => v.CreatedAt).First();
            if (latest.Status == VersionStatus.Deprecated || present.Contains((latest.Artifact.Type, latest.Artifact.Name, latest.GitPath ?? ""))) continue;
            db.StatusHistory.Add(new StatusHistory { VersionId = latest.Id, From = latest.Status, To = VersionStatus.Deprecated, UserId = $"feed:{feed.Name}", Note = "absent-from-repo" });
            latest.Status = VersionStatus.Deprecated;
            report.Deprecated++;
            messages.Add($"Déprécié : {latest.Artifact.Namespace}/{latest.Artifact.Name} (absent du dépôt).");
        }
    }

    private async Task<FeedReportDto> FinishAsync(FeedSyncReport report, Feed feed, bool success, string? commit, List<string> messages, string actor, CancellationToken ct)
    {
        report.EndedAt = DateTime.UtcNow; report.Success = success; report.Commit ??= commit; report.Messages = string.Join('\n', messages);
        db.FeedReports.Add(report);
        db.Audit.Add(new AuditEntry
        {
            UserId = actor, Action = "feed-sync", Entity = "Feed", EntityId = feed.Id.ToString(),
            Details = $"{feed.Name}: ok={success} +{report.Imported} ~{report.Updated} ={report.Unchanged} -{report.Deprecated} skip={report.Skipped} quarantine={report.Quarantined}",
        });
        await db.SaveChangesAsync(ct);
        return new(report.Id, report.StartedAt, report.EndedAt, report.Success, report.Commit, report.Imported, report.Updated, report.Unchanged, report.Skipped, report.Deprecated, report.Quarantined, messages);
    }
}
