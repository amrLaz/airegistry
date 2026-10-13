using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record DraftInput(string Namespace, string Name, string? Description, string? Version, IReadOnlyList<ComponentRef> Components);
public sealed record DraftDto(Guid Id, string Namespace, string Name, string? Description, string? Version, IReadOnlyList<ComponentRef> Components,
    Guid? PublishedVersionId, DateTime UpdatedAt, IReadOnlyList<ComponentUpdate> Updates);
public sealed record ComponentChoice(ArtifactType Type, string Slug, string? Description, IReadOnlyList<string> Versions);
public sealed record DraftResult(DraftDto? Draft, IReadOnlyList<Issue> Errors, bool Forbidden, bool NotFound);
public sealed record PublishPluginResult(ImportResult? Import, IReadOnlyList<Issue> Errors, bool Forbidden, bool NotFound);
public sealed record Bundle(byte[] Zip, string Sha256, string FileName);
public sealed record MarketplaceResult(string Body, string ETag);

public class PluginService(AppDbContext db, PublishService publisher, SettingsService settings, MarketplaceCache cache)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static IReadOnlyList<ComponentRef> ParseComponents(string json) => JsonSerializer.Deserialize<List<ComponentRef>>(json, Json) ?? [];

    // ---------- Résolution ----------
    private async Task<Dictionary<string, IReadOnlyList<string>>> ApprovedVersionsAsync(IEnumerable<string> slugs, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var slug in slugs.Distinct())
        {
            var parts = slug.Split('/');
            if (parts.Length != 2) continue;
            var versions = await db.Versions.AsNoTracking()
                .Where(v => v.Artifact.Namespace == parts[0] && v.Artifact.Name == parts[1] && v.Status == VersionStatus.Approved && !v.Quarantined)
                .Select(v => v.Version).ToListAsync(ct);
            result[slug] = versions;
        }
        return result;
    }

    private async Task<DraftDto> ToDtoAsync(PluginDraft d, CancellationToken ct)
    {
        var comps = ParseComponents(d.ComponentsJson);
        var updates = PluginUpdates.Find(comps, await ApprovedVersionsAsync(comps.Select(c => c.Slug), ct));
        return new(d.Id, d.Namespace, d.Name, d.Description, d.Version, comps, d.PublishedVersionId, d.UpdatedAt, updates);
    }

    public async Task<IReadOnlyList<ComponentChoice>> SearchComponentsAsync(ArtifactType? type, string? q, CancellationToken ct = default)
    {
        var query = db.Versions.AsNoTracking().Include(v => v.Artifact)
            .Where(v => v.Status == VersionStatus.Approved && !v.Quarantined && v.Artifact.Type != ArtifactType.Plugin);
        if (type is not null) query = query.Where(v => v.Artifact.Type == type);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim().ToLower();
            query = query.Where(v => v.Artifact.Name.ToLower().Contains(t) || v.Artifact.Namespace.ToLower().Contains(t) || (v.Description != null && v.Description.ToLower().Contains(t)));
        }
        return (await query.ToListAsync(ct)).GroupBy(v => v.ArtifactId)
            .Select(g => new ComponentChoice(g.First().Artifact.Type, $"{g.First().Artifact.Namespace}/{g.First().Artifact.Name}", g.OrderByDescending(v => v.CreatedAt).First().Description,
                g.Select(v => v.Version).OrderBy(v => v, Comparer<string>.Create((a, b) => VersionRules.IsSemVer(a) && VersionRules.IsSemVer(b) ? VersionRules.Compare(b, a) : string.CompareOrdinal(b, a))).ToList()))
            .OrderBy(c => c.Slug, StringComparer.Ordinal).Take(100).ToList();
    }

    // ---------- Brouillons ----------
    public async Task<IReadOnlyList<DraftDto>> ListDraftsAsync(string owner, CancellationToken ct = default)
    {
        var drafts = (await db.PluginDrafts.AsNoTracking().Where(d => d.OwnerId == owner).ToListAsync(ct)).OrderByDescending(d => d.UpdatedAt).ToList();
        var list = new List<DraftDto>();
        foreach (var d in drafts) list.Add(await ToDtoAsync(d, ct));
        return list;
    }

    public async Task<DraftResult> GetDraftAsync(Guid id, CurrentUser user, CancellationToken ct = default)
    {
        var d = await db.PluginDrafts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return new(null, [], false, true);
        if (!CanAccess(user, d)) return new(null, [], true, false);
        return new(await ToDtoAsync(d, ct), [], false, false);
    }

    private static bool CanAccess(CurrentUser u, PluginDraft d) => u.IsAuthenticated && (u.Role == Role.Admin || string.Equals(u.Email, d.OwnerId, StringComparison.OrdinalIgnoreCase));

    public async Task<DraftResult> SaveDraftAsync(Guid? id, DraftInput input, CurrentUser user, CancellationToken ct = default)
    {
        var errors = new List<Issue>();
        // Au brouillon on tolère un nom/une composition incomplets ; seuls les formats dangereux ou incohérents sont refusés.
        if (input.Name.Length > 48 || input.Namespace.Length > 32) errors.Add(new("name-length", "Namespace (32) ou nom (48) trop long."));
        if (input.Components.Count > PluginRules.MaxComponents) errors.Add(new("too-many-components", $"Au plus {PluginRules.MaxComponents} composants."));
        if (errors.Count > 0) return new(null, errors, false, false);

        PluginDraft? d = null;
        if (id is not null)
        {
            d = await db.PluginDrafts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return new(null, [], false, true);
            if (!CanAccess(user, d)) return new(null, [], true, false);
        }
        else { d = new PluginDraft { OwnerId = user.Email }; db.PluginDrafts.Add(d); }

        d.Namespace = input.Namespace.Trim(); d.Name = input.Name.Trim(); d.Description = input.Description?.Trim(); d.Version = string.IsNullOrWhiteSpace(input.Version) ? null : input.Version.Trim();
        d.ComponentsJson = JsonSerializer.Serialize(input.Components, Json);
        d.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new(await ToDtoAsync(d, ct), [], false, false);
    }

    public async Task<bool?> DeleteDraftAsync(Guid id, CurrentUser user, CancellationToken ct = default)
    {
        var d = await db.PluginDrafts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return null;
        if (!CanAccess(user, d)) return false;
        db.PluginDrafts.Remove(d);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---------- Publication ----------
    public async Task<PublishPluginResult> PublishDraftAsync(Guid id, CurrentUser user, CancellationToken ct = default)
    {
        var d = await db.PluginDrafts.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return new(null, [], false, true);
        if (!CanAccess(user, d)) return new(null, [], true, false);

        var refs = ParseComponents(d.ComponentsJson);
        var errors = PluginRules.ValidateRefs($"{d.Namespace}/{d.Name}", refs).ToList();
        if (errors.Count > 0) return new(null, errors, false, false);

        // Chaque composant doit exister, au type annoncé, à la version exacte, approuvé et hors quarantaine.
        foreach (var r in refs)
        {
            var parts = r.Slug.Split('/');
            var v = await db.Versions.AsNoTracking().Include(x => x.Artifact)
                .FirstOrDefaultAsync(x => x.Artifact.Namespace == parts[0] && x.Artifact.Name == parts[1] && x.Artifact.Type == r.Type && x.Version == r.Version, ct);
            if (v is null) errors.Add(new("component-unresolved", $"{r.Slug}@{r.Version} ({r.Type}) est introuvable."));
            else if (v.Status != VersionStatus.Approved || v.Quarantined) errors.Add(new("component-not-approved", $"{r.Slug}@{r.Version} n'est pas approuvé ou est en quarantaine."));
        }
        if (errors.Count > 0) return new(null, errors, false, false);

        var known = await db.Versions.Where(v => v.Artifact.Namespace == d.Namespace && v.Artifact.Name == d.Name && v.Artifact.Type == ArtifactType.Plugin).Select(v => v.Version).ToListAsync(ct);
        var decision = VersionRules.Resolve(d.Version, known);
        if (decision.Error is not null) return new(null, [new("version-duplicate", decision.Error)], false, false);

        var manifest = PluginRules.BuildManifest(d.Name, d.Description, decision.Version, refs);
        var import = await publisher.ImportAsync(new ImportRequest(ArtifactType.Plugin, d.Namespace, d.Name, decision.Version, d.Description,
            ".claude-plugin/plugin.json", manifest, null,
            new Dictionary<string, string> { [".github/plugin/plugin.json"] = manifest, [".codex-plugin/plugin.json"] = manifest }), user, ct);
        if (!import.Success) return new(import, import.Errors, false, false);

        foreach (var r in refs)
            db.PluginComponents.Add(new PluginComponent { PluginVersionId = import.VersionId!.Value, Type = r.Type, Slug = r.Slug, ExactVersion = r.Version, Resolved = true });
        d.PublishedVersionId = import.VersionId;
        d.Version = decision.Version;
        await db.SaveChangesAsync(ct);
        return new(import, [], false, false);
    }

    /// <summary>Composants pour lesquels une version plus récente approuvée existe (badge sur une version publiée).</summary>
    public async Task<IReadOnlyList<ComponentUpdate>> UpdatesForVersionAsync(Guid versionId, CancellationToken ct = default)
    {
        var comps = await db.PluginComponents.AsNoTracking().Where(c => c.PluginVersionId == versionId).ToListAsync(ct);
        var refs = comps.Select(c => new ComponentRef(c.Type, c.Slug, c.ExactVersion)).ToList();
        return PluginUpdates.Find(refs, await ApprovedVersionsAsync(refs.Select(r => r.Slug), ct));
    }

    // ---------- Export / réimport ZIP du brouillon ----------
    private const string DraftEntry = "draft.json";

    public byte[] ExportDraft(DraftDto d)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var e = zip.CreateEntry(DraftEntry);
            e.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(JsonSerializer.Serialize(new { d.Namespace, d.Name, d.Description, d.Version, d.Components }, new JsonSerializerOptions(Json) { WriteIndented = true }));
        }
        return ms.ToArray();
    }

    public async Task<DraftResult> ImportDraftAsync(byte[] zipBytes, CurrentUser user, CancellationToken ct = default)
    {
        DraftInput input;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            if (zip.Entries.Count != 1 || zip.Entries[0].FullName != DraftEntry) return new(null, [new("import-format", "L'archive doit contenir uniquement draft.json.")], false, false);
            if (zip.Entries[0].Length > 1_000_000) return new(null, [new("import-size", "draft.json trop volumineux.")], false, false);
            using var r = new StreamReader(zip.Entries[0].Open());
            var node = JsonSerializer.Deserialize<JsonElement>(await r.ReadToEndAsync(ct), Json);
            input = new(node.GetProperty("namespace").GetString() ?? "", node.GetProperty("name").GetString() ?? "",
                node.TryGetProperty("description", out var de) ? de.GetString() : null, node.TryGetProperty("version", out var ve) ? ve.GetString() : null,
                node.GetProperty("components").Deserialize<List<ComponentRef>>(Json) ?? []);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return new(null, [new("import-format", "Archive ou contenu invalide.")], false, false); }
        return await SaveDraftAsync(null, input, user, ct);
    }

    // ---------- Archive installable ----------
    public async Task<Bundle?> BundleAsync(string ns, string name, string? version, CancellationToken ct = default)
    {
        var q = db.Versions.AsNoTracking().Include(v => v.Files).Include(v => v.Artifact)
            .Where(v => v.Artifact.Namespace == ns && v.Artifact.Name == name && v.Artifact.Type == ArtifactType.Plugin && v.Status == VersionStatus.Approved && !v.Quarantined);
        if (version is not null) q = q.Where(v => v.Version == version);
        var pv = (await q.ToListAsync(ct)).OrderByDescending(v => v.CreatedAt).FirstOrDefault();
        if (pv is null) return null;
        var manifestFile = pv.Files.FirstOrDefault(f => PluginRules.ManifestPaths.Contains(f.Path));
        if (manifestFile is null) return null;
        var manifest = Encoding.UTF8.GetString(manifestFile.Content);

        var parts = new List<(ComponentRef, IReadOnlyDictionary<string, byte[]>)>();
        foreach (var r in PluginRules.ReadManifest(manifest))
        {
            var p = r.Slug.Split('/');
            var cv = await db.Versions.AsNoTracking().Include(x => x.Files).Include(x => x.Artifact)
                .FirstOrDefaultAsync(x => x.Artifact.Namespace == p[0] && x.Artifact.Name == p[1] && x.Artifact.Type == r.Type && x.Version == r.Version, ct);
            if (cv is null) return null; // composant supprimé : l'archive ne peut plus être produite fidèlement
            parts.Add((r, cv.Files.ToDictionary(f => f.Path, f => f.Content)));
        }
        var files = PluginRules.BuildBundle(manifest, parts);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (path, data) in files)
            {
                var e = zip.CreateEntry(path);
                e.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero); // archive déterministe
                using var s = e.Open();
                s.Write(data);
            }
        var bytes = ms.ToArray();
        return new(bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), $"{ns}-{name}-{pv.Version}.zip");
    }

    // ---------- Marketplace ----------
    public async Task<MarketplaceResult> MarketplaceAsync(string fallbackBaseUrl, CancellationToken ct = default)
    {
        var s = await settings.GetAllAsync(ct);
        var baseUrl = s.GetValueOrDefault(SettingKeys.PublicUrl) is { Length: > 0 } pu ? pu : fallbackBaseUrl;
        var key = baseUrl;
        if (cache.TryGet(key, out var hit)) return new(hit.Body, hit.ETag);

        var versions = await db.Versions.AsNoTracking().Include(v => v.Artifact).ThenInclude(a => a.Tags)
            .Where(v => v.Artifact.Type == ArtifactType.Plugin && v.Status == VersionStatus.Approved && !v.Quarantined).ToListAsync(ct);
        var latest = versions.GroupBy(v => v.ArtifactId).Select(g => g.OrderByDescending(v => v.CreatedAt).First())
            .Select(v => new MarketplaceManifest.Entry(v.Artifact.Namespace, v.Artifact.Name, v.Version, v.Description, v.Artifact.Tags.Select(t => t.Name).OrderBy(t => t, StringComparer.Ordinal).ToArray()));

        var body = MarketplaceManifest.Build(s.GetValueOrDefault(SettingKeys.Title) ?? "MyRegistry", baseUrl, latest);
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant()[..32] + "\"";
        cache.Set(key, body, etag);
        return new(body, etag);
    }
}
