using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record FindingDto(Guid Id, string RuleId, string Severity, string File, int Line, string Message, bool ExcludedByBaseline, bool Waived, bool Blocking);
public sealed record WaiverDto(Guid Id, string AcceptedSeverity, string By, DateTime At, DateTime? ExpiresAt, string Reason, DateTime? RevokedAt, bool Active);
public sealed record ScanDto(Guid Id, string Mode, string ToolVersion, DateTime StartedAt, string Status, string? MaxSeverity, int FilesScanned, int FilesTotal, bool Incomplete, string Verdict);
public sealed record SecuritySummary(ScanDto? Scan, string ContentHash, IReadOnlyList<FindingDto> Findings, int BlockingCount, string Verdict,
    IReadOnlyList<WaiverDto> Waivers, bool CanApprove, bool CanWaive, bool ScanStale);
public sealed record ApprovalGate(bool Allowed, string? Reason);
public sealed record WaiverResult(WaiverDto? Waiver, IReadOnlyList<Issue> Errors, bool Forbidden, bool NotFound);
public sealed record BaselineDto(string Yaml, string Sha256, IReadOnlyList<string> ExcludedRules, string? UpdatedBy, DateTime? At);
public sealed record BaselineResult(BaselineDto? Baseline, IReadOnlyList<Issue> Errors);
public sealed record BackfillResult(Guid RunId, int Scanned, int Blocked, int Failed, int MovedToReview);

public class SecurityService(AppDbContext db, IScanner scanner, SettingsService settings, MarketplaceCache marketplace)
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        !string.Equals(await settings.GetAsync(SettingKeys.SecurityScanEnabled, "true", ct), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Analyse avant publication (pas encore de version en base).</summary>
    public Task<ScanOutcome> ScanFilesAsync(IReadOnlyList<FilePayload> files, ArtifactType type, CancellationToken ct = default) => scanner.ScanAsync(files, type, ct);

    // ---------- Baseline ----------
    private async Task<HashSet<string>> ExcludedRulesAsync(CancellationToken ct)
    {
        var b = await db.Baselines.AsNoTracking().FirstOrDefaultAsync(x => x.Active, ct);
        return b is null ? [] : b.ExcludedRules.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    }

    public async Task<BaselineDto> GetBaselineAsync(CancellationToken ct = default)
    {
        var b = await db.Baselines.AsNoTracking().FirstOrDefaultAsync(x => x.Active, ct);
        return b is null ? new("version: 2\nexcluded_rules: []\n", "", [], null, null)
            : new(b.Yaml, b.Sha256, b.ExcludedRules.Split(',', StringSplitOptions.RemoveEmptyEntries), b.UpdatedBy, b.At);
    }

    /// <summary>Enregistre la baseline. Les règles High/Critical exigent une confirmation explicite (case « confirmHighSeverity »).</summary>
    public async Task<BaselineResult> SaveBaselineAsync(string yaml, bool confirmHighSeverity, string actor, CancellationToken ct = default)
    {
        var parse = BaselineYaml.Parse(yaml);
        var errors = parse.Errors.ToList();
        if (errors.Count == 0 && parse.HighSeverityRules.Count > 0 && !confirmHighSeverity)
            errors.Add(new("confirm-required", $"Exclure une règle High/Critical ({string.Join(", ", parse.HighSeverityRules.Select(r => r.Id))}) exige une confirmation supplémentaire."));
        if (errors.Count > 0) return new(null, errors);

        foreach (var old in await db.Baselines.Where(x => x.Active).ToListAsync(ct)) old.Active = false;
        var row = new ScanBaseline
        {
            Yaml = yaml, Sha256 = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(yaml))), ExcludedRules = string.Join(',', parse.Rules.Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal)),
            UpdatedBy = actor, Active = true,
        };
        db.Baselines.Add(row);
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "security-baseline", Entity = "Security", EntityId = row.Sha256[..12], Details = $"{parse.Rules.Count} règle(s) exclue(s) : {row.ExcludedRules}" });
        await db.SaveChangesAsync(ct);
        await ReevaluateApprovedAsync(actor, ct); // un retrait d'exclusion peut re-bloquer des versions approuvées
        return new(await GetBaselineAsync(ct), []);
    }

    // ---------- Analyses ----------
    private async Task<SecurityScan> RecordAsync(ArtifactVersion v, ScanOutcome o, string mode, Guid? runId, CancellationToken ct)
    {
        var excluded = await ExcludedRulesAsync(ct);
        var waivers = await WaiversAsync(v.Id, ct);
        var blocking = SecurityPolicy.EffectiveBlocking(o.Findings, excluded, waivers, v.ContentHash, DateTime.UtcNow);
        var scan = new SecurityScan
        {
            VersionId = v.Id, ContentHash = v.ContentHash, Mode = mode, ToolVersion = o.ToolVersion, EndedAt = DateTime.UtcNow, Status = o.Status.ToString(),
            MaxSeverity = SecurityPolicy.Max(o.Findings) is { } m ? (int)m : null, FilesScanned = o.FilesScanned, FilesTotal = o.FilesTotal, Incomplete = o.Incomplete,
            Verdict = o.Status == ScanStatus.Succeeded ? SecurityPolicy.Verdict(o.Findings, blocking) : "Unavailable", RunId = runId,
            Findings = o.Findings.Select(f => new SecurityFinding { RuleId = f.RuleId, Severity = (int)f.Severity, File = f.File, Line = f.Line, Message = f.Message, BlocksPublish = SecurityPolicy.Blocks(f.Severity) }).ToList(),
        };
        db.Scans.Add(scan);
        await db.SaveChangesAsync(ct);
        return scan;
    }

    private static IReadOnlyList<FilePayload> Payloads(ArtifactVersion v) => v.Files.Select(f => new FilePayload(f.Path, f.Content, f.IsBinary)).ToList();

    /// <summary>Enregistre l'analyse faite à la publication (même résultat que celui qui a autorisé la publication).</summary>
    public async Task<SecurityScan> RecordDirectScanAsync(ArtifactVersion v, ScanOutcome outcome, CancellationToken ct = default) =>
        await RecordAsync(v, outcome, "direct", null, ct);

    public async Task<SecuritySummary?> ScanVersionAsync(Guid versionId, string mode, string actor, CurrentUser? viewer = null, Guid? runId = null, CancellationToken ct = default)
    {
        var v = await db.Versions.Include(x => x.Files).Include(x => x.Artifact).FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null) return null;
        var outcome = await scanner.ScanAsync(Payloads(v), v.Artifact.Type, ct);
        await RecordAsync(v, outcome, mode, runId, ct);
        if (outcome.Status == ScanStatus.Succeeded) await ApplyLateFindingAsync(v, actor, ct);
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "security-scan", Entity = "ArtifactVersion", EntityId = versionId.ToString(), Details = $"{mode} {outcome.Status} {outcome.Findings.Count} constat(s)" });
        await db.SaveChangesAsync(ct);
        return await SummaryAsync(versionId, viewer, ct);
    }

    /// <summary>Un constat High/Critical tardif sur une version approuvée la repasse en ToReview (docs/05).</summary>
    private async Task ApplyLateFindingAsync(ArtifactVersion v, string actor, CancellationToken ct)
    {
        if (v.Status != VersionStatus.Approved) return;
        var (_, blocking) = await EvaluateAsync(v, ct);
        if (blocking.Count == 0) return;
        db.StatusHistory.Add(new StatusHistory { VersionId = v.Id, From = v.Status, To = VersionStatus.ToReview, UserId = actor, Note = "late-finding" });
        v.Status = VersionStatus.ToReview;
        marketplace.Invalidate();
    }

    private async Task<List<WaiverView>> WaiversAsync(Guid versionId, CancellationToken ct) =>
        (await db.Waivers.AsNoTracking().Where(w => w.VersionId == versionId).ToListAsync(ct)).Select(w => new WaiverView((FindingSeverity)w.AcceptedSeverity, w.ContentHash, w.ExpiresAt, w.RevokedAt)).ToList();

    private async Task<SecurityScan?> LatestSuccessfulScanAsync(Guid versionId, string hash, CancellationToken ct) =>
        (await db.Scans.AsNoTracking().Include(s => s.Findings).Where(s => s.VersionId == versionId && s.ContentHash == hash && s.Status == nameof(ScanStatus.Succeeded)).ToListAsync(ct))
            .OrderByDescending(s => s.StartedAt).FirstOrDefault();

    private async Task<(SecurityScan? Scan, IReadOnlyList<ScanFinding> Blocking)> EvaluateAsync(ArtifactVersion v, CancellationToken ct)
    {
        var scan = await LatestSuccessfulScanAsync(v.Id, v.ContentHash, ct);
        if (scan is null) return (null, []);
        var findings = scan.Findings.Select(f => new ScanFinding(f.RuleId, (FindingSeverity)f.Severity, f.File, f.Line, f.Message)).ToList();
        return (scan, SecurityPolicy.EffectiveBlocking(findings, await ExcludedRulesAsync(ct), await WaiversAsync(v.Id, ct), v.ContentHash, DateTime.UtcNow));
    }

    /// <summary>
    /// Porte d'approbation : sans analyse réussie pour cette version et cette empreinte, on la lance ; indisponible → refus ;
    /// constats High/Critical non exclus ni dérogés → refus, même pour un Admin (aucun contournement).
    /// </summary>
    public async Task<ApprovalGate> EvaluateApprovalAsync(Guid versionId, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct)) return new(true, null);
        var v = await db.Versions.Include(x => x.Files).Include(x => x.Artifact).FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null) return new(false, "Version introuvable.");
        var (scan, blocking) = await EvaluateAsync(v, ct);
        if (scan is null)
        {
            var outcome = await scanner.ScanAsync(Payloads(v), v.Artifact.Type, ct);
            await RecordAsync(v, outcome, "approval", null, ct);
            if (outcome.Status != ScanStatus.Succeeded) return new(false, "Analyse de sécurité indisponible : approbation impossible pour l'instant.");
            (_, blocking) = await EvaluateAsync(v, ct);
        }
        return blocking.Count == 0 ? new(true, null)
            : new(false, $"{blocking.Count} constat(s) de sécurité High/Critical bloquent l'approbation : {string.Join(", ", blocking.Select(b => b.RuleId).Distinct())}. Corrigez ou demandez une dérogation.");
    }

    public async Task<SecuritySummary?> SummaryAsync(Guid versionId, CurrentUser? viewer, CancellationToken ct = default)
    {
        var v = await db.Versions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null) return null;
        var scan = await LatestSuccessfulScanAsync(v.Id, v.ContentHash, ct);
        var excluded = await ExcludedRulesAsync(ct);
        var waivers = await db.Waivers.AsNoTracking().Where(w => w.VersionId == versionId).ToListAsync(ct);
        var views = waivers.Select(w => new WaiverView((FindingSeverity)w.AcceptedSeverity, w.ContentHash, w.ExpiresAt, w.RevokedAt)).ToList();
        var now = DateTime.UtcNow;

        var findings = (scan?.Findings ?? []).Select(f =>
        {
            var sev = (FindingSeverity)f.Severity;
            var excl = excluded.Contains(f.RuleId);
            var waived = SecurityPolicy.Blocks(sev) && !excl && views.Any(w => SecurityPolicy.Covers(w, v.ContentHash, sev, now));
            return new FindingDto(f.Id, f.RuleId, sev.ToString(), f.File, f.Line, f.Message, excl, waived, SecurityPolicy.Blocks(sev) && !excl && !waived);
        }).OrderByDescending(f => Enum.Parse<FindingSeverity>(f.Severity)).ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).ToList();

        var blocking = findings.Count(f => f.Blocking);
        var anyScan = (await db.Scans.AsNoTracking().Where(s => s.VersionId == versionId).ToListAsync(ct)).OrderByDescending(s => s.StartedAt).FirstOrDefault();
        var shown = scan ?? anyScan;
        return new(shown is null ? null : new ScanDto(shown.Id, shown.Mode, shown.ToolVersion, shown.StartedAt, shown.Status, shown.MaxSeverity is { } m ? ((FindingSeverity)m).ToString() : null, shown.FilesScanned, shown.FilesTotal, shown.Incomplete, shown.Verdict),
            v.ContentHash, findings, blocking, scan is null ? "Unknown" : blocking > 0 ? "Blocked" : findings.Count > 0 ? "Findings" : "Clean",
            waivers.OrderByDescending(w => w.At).Select(w => new WaiverDto(w.Id, ((FindingSeverity)w.AcceptedSeverity).ToString(), w.By, w.At, w.ExpiresAt, w.Reason, w.RevokedAt, SecurityPolicy.Covers(new((FindingSeverity)w.AcceptedSeverity, w.ContentHash, w.ExpiresAt, w.RevokedAt), v.ContentHash, FindingSeverity.High, now))).ToList(),
            scan is not null && blocking == 0, viewer is { IsAuthenticated: true } u && SecurityPolicy.CanWaive(u.Role, FindingSeverity.High), scan is null);
    }

    // ---------- Dérogations ----------
    public async Task<WaiverResult> AddWaiverAsync(Guid versionId, FindingSeverity accepted, string reason, DateTime? expiresAt, CurrentUser user, CancellationToken ct = default)
    {
        var v = await db.Versions.FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null) return new(null, [], false, true);
        if (!user.IsAuthenticated || !SecurityPolicy.CanWaive(user.Role, accepted)) return new(null, [], true, false);

        var errors = new List<Issue>();
        if (accepted is not (FindingSeverity.High or FindingSeverity.Critical)) errors.Add(new("severity", "Une dérogation porte sur la gravité High ou Critical."));
        if (string.IsNullOrWhiteSpace(reason)) errors.Add(new("reason", "Le motif est obligatoire."));
        if (expiresAt is not null && expiresAt <= DateTime.UtcNow) errors.Add(new("expires", "La date d'expiration doit être dans le futur."));
        if (await LatestSuccessfulScanAsync(versionId, v.ContentHash, ct) is null) errors.Add(new("no-scan", "Aucune analyse réussie pour ce contenu : lancez l'analyse d'abord."));
        if (errors.Count > 0) return new(null, errors, false, false);

        var w = new SecurityWaiver { VersionId = versionId, ContentHash = v.ContentHash, AcceptedSeverity = (int)accepted, By = user.Email, Reason = reason.Trim(), ExpiresAt = expiresAt };
        db.Waivers.Add(w);
        db.StatusHistory.Add(new StatusHistory { VersionId = versionId, From = v.Status, To = v.Status, UserId = user.Email, Note = $"waiver:{accepted}:{w.Reason}" });
        db.Audit.Add(new AuditEntry { UserId = user.Email, Action = "security-waiver", Entity = "ArtifactVersion", EntityId = versionId.ToString(), Details = $"{accepted} jusqu'à {expiresAt?.ToString("yyyy-MM-dd") ?? "indéfini"} : {w.Reason}" });
        await db.SaveChangesAsync(ct);
        return new(new(w.Id, accepted.ToString(), w.By, w.At, w.ExpiresAt, w.Reason, null, true), [], false, false);
    }

    /// <summary>Révocation : la version approuvée repasse en ToReview si des constats bloquants redeviennent actifs.</summary>
    public async Task<WaiverResult> RevokeWaiverAsync(Guid waiverId, CurrentUser user, CancellationToken ct = default)
    {
        var w = await db.Waivers.FirstOrDefaultAsync(x => x.Id == waiverId, ct);
        if (w is null) return new(null, [], false, true);
        if (!user.IsAuthenticated || !SecurityPolicy.CanWaive(user.Role, (FindingSeverity)w.AcceptedSeverity)) return new(null, [], true, false);
        if (w.RevokedAt is not null) return new(null, [new("revoked", "Dérogation déjà révoquée.")], false, false);

        w.RevokedAt = DateTime.UtcNow; w.RevokedBy = user.Email;
        var v = await db.Versions.FirstAsync(x => x.Id == w.VersionId, ct);
        db.StatusHistory.Add(new StatusHistory { VersionId = v.Id, From = v.Status, To = v.Status, UserId = user.Email, Note = $"waiver-revoked:{(FindingSeverity)w.AcceptedSeverity}" });
        await db.SaveChangesAsync(ct);

        if (v.Status == VersionStatus.Approved && (await EvaluateAsync(v, ct)).Blocking.Count > 0)
        {
            db.StatusHistory.Add(new StatusHistory { VersionId = v.Id, From = v.Status, To = VersionStatus.ToReview, UserId = user.Email, Note = "waiver-revoked" });
            v.Status = VersionStatus.ToReview;
            marketplace.Invalidate();
        }
        db.Audit.Add(new AuditEntry { UserId = user.Email, Action = "security-waiver-revoke", Entity = "ArtifactVersion", EntityId = v.Id.ToString(), Details = $"{(FindingSeverity)w.AcceptedSeverity}" });
        await db.SaveChangesAsync(ct);
        return new(new(w.Id, ((FindingSeverity)w.AcceptedSeverity).ToString(), w.By, w.At, w.ExpiresAt, w.Reason, w.RevokedAt, false), [], false, false);
    }

    // ---------- Réévaluation et backfill ----------
    /// <summary>Réévalue, sans relancer d'analyse, les versions approuvées avec les analyses existantes (ex. après un changement de baseline).</summary>
    public async Task<int> ReevaluateApprovedAsync(string actor, CancellationToken ct = default)
    {
        var moved = 0;
        foreach (var v in await db.Versions.Where(x => x.Status == VersionStatus.Approved).ToListAsync(ct))
        {
            var (scan, blocking) = await EvaluateAsync(v, ct);
            if (scan is null || blocking.Count == 0) continue;
            db.StatusHistory.Add(new StatusHistory { VersionId = v.Id, From = v.Status, To = VersionStatus.ToReview, UserId = actor, Note = "late-finding" });
            v.Status = VersionStatus.ToReview;
            moved++;
        }
        if (moved > 0) { marketplace.Invalidate(); await db.SaveChangesAsync(ct); }
        return moved;
    }

    /// <summary>Analyse la dernière version de chaque artefact (hors dépréciées) et produit un rapport rattaché à <c>RunId</c>.</summary>
    public async Task<BackfillResult> BackfillAsync(string actor, CancellationToken ct = default)
    {
        var runId = Guid.NewGuid();
        var latest = (await db.Versions.AsNoTracking().Where(v => v.Status != VersionStatus.Deprecated).ToListAsync(ct))
            .GroupBy(v => v.ArtifactId).Select(g => g.OrderByDescending(v => v.CreatedAt).First().Id).ToList();
        int scanned = 0, blocked = 0, failed = 0, moved = 0;
        foreach (var id in latest)
        {
            var before = (await db.Versions.AsNoTracking().FirstAsync(v => v.Id == id, ct)).Status;
            var s = await ScanVersionAsync(id, "backfill", actor, null, runId, ct);
            if (s is null) continue;
            scanned++;
            if (s.Scan?.Status != nameof(ScanStatus.Succeeded)) failed++;
            else if (s.BlockingCount > 0) blocked++;
            if (before == VersionStatus.Approved && (await db.Versions.AsNoTracking().FirstAsync(v => v.Id == id, ct)).Status == VersionStatus.ToReview) moved++;
        }
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "security-backfill", Entity = "Security", EntityId = runId.ToString(), Details = $"{scanned} analysée(s), {blocked} bloquée(s), {failed} en échec, {moved} repassée(s) en ToReview" });
        await db.SaveChangesAsync(ct);
        return new(runId, scanned, blocked, failed, moved);
    }

    /// <summary>Rapport CSV d'un passage de backfill. Les cellules commençant par = + - @ sont neutralisées (injection de formules).</summary>
    public async Task<string?> BackfillCsvAsync(Guid runId, CancellationToken ct = default)
    {
        var scans = await db.Scans.AsNoTracking().Where(s => s.RunId == runId).Include(s => s.Findings).ToListAsync(ct);
        if (scans.Count == 0) return null;
        var versions = await db.Versions.AsNoTracking().Include(v => v.Artifact).Where(v => scans.Select(s => s.VersionId).Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var sb = new StringBuilder("artifact,version,status,scan_status,verdict,max_severity,blocking_findings,findings,rules\n");
        foreach (var s in scans.OrderBy(s => versions[s.VersionId].Artifact.Namespace, StringComparer.Ordinal).ThenBy(s => versions[s.VersionId].Artifact.Name, StringComparer.Ordinal))
        {
            var v = versions[s.VersionId];
            sb.AppendLine(string.Join(',', new[]
            {
                Csv($"{v.Artifact.Namespace}/{v.Artifact.Name}"), Csv(v.Version), Csv(v.Status.ToString()), Csv(s.Status), Csv(s.Verdict),
                Csv(s.MaxSeverity is { } m ? ((FindingSeverity)m).ToString() : ""), s.Findings.Count(f => f.BlocksPublish).ToString(), s.Findings.Count.ToString(),
                Csv(string.Join(' ', s.Findings.Select(f => f.RuleId).Distinct().OrderBy(x => x, StringComparer.Ordinal))),
            }));
        }
        return sb.ToString();
    }

    public static string Csv(string v)
    {
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public async Task<IReadOnlyList<(Guid VersionId, string Artifact, string Version, string Verdict)>> BlockedAsync(CancellationToken ct = default)
    {
        var result = new List<(Guid, string, string, string)>();
        foreach (var v in await db.Versions.AsNoTracking().Include(x => x.Artifact).Where(x => x.Status != VersionStatus.Deprecated).ToListAsync(ct))
        {
            var (scan, blocking) = await EvaluateAsync(v, ct);
            if (scan is not null && blocking.Count > 0) result.Add((v.Id, $"{v.Artifact.Namespace}/{v.Artifact.Name}", v.Version, $"{blocking.Count} bloquant(s)"));
        }
        return result;
    }

    public async Task<string?> FixPromptAsync(Guid versionId, CancellationToken ct = default)
    {
        var v = await db.Versions.AsNoTracking().Include(x => x.Artifact).FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null) return null;
        var (scan, blocking) = await EvaluateAsync(v, ct);
        var findings = scan is null ? [] : scan.Findings.Select(f => new ScanFinding(f.RuleId, (FindingSeverity)f.Severity, f.File, f.Line, f.Message)).ToList();
        return FixPrompt.Build($"{v.Artifact.Namespace}/{v.Artifact.Name}", v.Version, findings.Where(f => blocking.Any(b => b.RuleId == f.RuleId && b.File == f.File && b.Line == f.Line)).Concat(findings.Where(f => !SecurityPolicy.Blocks(f.Severity))));
    }
}
