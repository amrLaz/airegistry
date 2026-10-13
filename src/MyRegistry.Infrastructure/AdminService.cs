using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record UserDto(string Email, string DisplayName, Role Role, bool Active, DateTime? LastLogin);
public sealed record AuditDto(Guid Id, DateTime At, string UserId, string Action, string Entity, string EntityId, string? Details);
public sealed record VersionRow(Guid Id, string Namespace, string Name, ArtifactType Type, string Version, VersionStatus Status,
    bool Quarantined, string CreatedBy, DateTime CreatedAt, int OpenReports);
public sealed record ReportRow(Guid Id, Guid VersionId, string Artifact, string Version, string UserId, string Reason, string? Details, string Status, DateTime At);
public sealed record Dashboard(int Artifacts, int Versions, Dictionary<string, int> ByStatus, Dictionary<string, int> ByType,
    int Quarantined, int OpenReports, int Users, IReadOnlyList<AuditDto> Recent, int ReviewsDue);

public class AdminService(AppDbContext db)
{
    // ---- Utilisateurs ----
    public async Task<IReadOnlyList<UserDto>> UsersAsync(CancellationToken ct = default) =>
        (await db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(ct))
            .Select(u => new UserDto(u.Email, u.DisplayName, u.Role, u.Active, u.LastLogin)).ToList();

    /// <summary>Crée ou met à jour un utilisateur. Refuse de retirer le dernier Admin actif.</summary>
    public async Task<(UserDto? User, string? Error)> UpsertUserAsync(string email, Role role, bool active, string? displayName, string actor, CancellationToken ct = default)
    {
        var key = email.Trim().ToLowerInvariant();
        if (!key.Contains('@')) return (null, "Adresse e-mail invalide.");
        var u = await db.Users.FirstOrDefaultAsync(x => x.Email == key, ct);
        var wasAdmin = u is { Role: Role.Admin, Active: true };
        if (wasAdmin && (role != Role.Admin || !active))
        {
            var otherAdmins = await db.Users.CountAsync(x => x.Role == Role.Admin && x.Active && x.Email != key, ct);
            if (otherAdmins == 0) return (null, "Impossible de retirer le dernier administrateur actif.");
        }
        var before = u is null ? "new" : $"{u.Role}/{(u.Active ? "active" : "inactive")}";
        if (u is null) { u = new AppUser { Email = key }; db.Users.Add(u); }
        u.Role = role; u.Active = active;
        u.DisplayName = string.IsNullOrWhiteSpace(displayName) ? (u.DisplayName.Length > 0 ? u.DisplayName : key.Split('@')[0]) : displayName.Trim();
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "user-update", Entity = "User", EntityId = key, Details = $"{before} → {role}/{(active ? "active" : "inactive")}" });
        await db.SaveChangesAsync(ct);
        return (new UserDto(u.Email, u.DisplayName, u.Role, u.Active, u.LastLogin), null);
    }

    // ---- Journal d'audit ----
    public async Task<PagedResult<AuditDto>> AuditAsync(string? action, string? user, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        var q = db.Audit.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(user)) q = q.Where(a => a.UserId == user);
        var total = await q.CountAsync(ct);
        var items = (await q.ToListAsync(ct)).OrderByDescending(a => a.At).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new AuditDto(a.Id, a.At, a.UserId, a.Action, a.Entity, a.EntityId, a.Details)).ToList();
        return new(items, total, page, pageSize);
    }

    // ---- Tableau de bord ----
    public async Task<Dashboard> DashboardAsync(CancellationToken ct = default)
    {
        var versions = await db.Versions.AsNoTracking().Include(v => v.Artifact).ToListAsync(ct);
        var recent = (await db.Audit.AsNoTracking().ToListAsync(ct)).OrderByDescending(a => a.At).Take(10)
            .Select(a => new AuditDto(a.Id, a.At, a.UserId, a.Action, a.Entity, a.EntityId, a.Details)).ToList();
        return new(
            await db.Artifacts.CountAsync(ct), versions.Count,
            versions.GroupBy(v => v.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            versions.GroupBy(v => v.Artifact.Type.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            versions.Count(v => v.Quarantined),
            await db.Reports.CountAsync(r => r.Status == "Open", ct),
            await db.Users.CountAsync(ct), recent,
            (await db.Governances.AsNoTracking().ToListAsync(ct)).Count(g => GovernancePolicy.ReviewDue(g.ReviewEveryDays, g.LastReviewed, g.UpdatedAt, DateTime.UtcNow)));
    }

    // ---- Modération ----
    public async Task<PagedResult<VersionRow>> VersionsAsync(VersionStatus? status, bool? quarantined, string? q, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.Versions.AsNoTracking().Include(v => v.Artifact).AsQueryable();
        if (status is not null) query = query.Where(v => v.Status == status);
        if (quarantined is not null) query = query.Where(v => v.Quarantined == quarantined);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim().ToLower();
            query = query.Where(v => v.Artifact.Name.ToLower().Contains(t) || v.Artifact.Namespace.ToLower().Contains(t) || v.CreatedBy.ToLower().Contains(t));
        }
        var list = (await query.ToListAsync(ct)).OrderByDescending(v => v.CreatedAt).ToList();
        var open = await db.Reports.Where(r => r.Status == "Open").GroupBy(r => r.VersionId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var rows = list.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(v => new VersionRow(v.Id, v.Artifact.Namespace, v.Artifact.Name, v.Artifact.Type, v.Version, v.Status, v.Quarantined, v.CreatedBy, v.CreatedAt, open.GetValueOrDefault(v.Id))).ToList();
        return new(rows, list.Count, page, pageSize);
    }

    public async Task<bool> RejectAsync(Guid id, string reason, string actor, CancellationToken ct = default)
    {
        var v = await db.Versions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return false;
        db.StatusHistory.Add(new StatusHistory { VersionId = id, From = v.Status, To = VersionStatus.Deprecated, UserId = actor, Note = "rejected: " + reason });
        v.Status = VersionStatus.Deprecated;
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "reject", Entity = "ArtifactVersion", EntityId = id.ToString(), Details = reason });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<DeletionVerdict> DeleteVersionAsync(Guid id, string actor, CancellationToken ct = default)
    {
        var v = await db.Versions.Include(x => x.Artifact).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return DeletionVerdict.NotFound;
        var family = await db.Versions.Where(x => x.ArtifactId == v.ArtifactId).Select(x => new { x.Id, x.Version, x.CreatedAt }).ToListAsync(ct);
        var verdict = DeletionPolicy.Check(id, family.Select(f => new VersionRef(f.Id, f.Version, f.CreatedAt)).ToList());
        if (verdict != DeletionVerdict.Allowed) return verdict;
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "delete-version", Entity = "ArtifactVersion", EntityId = id.ToString(), Details = $"{v.Artifact.Namespace}/{v.Artifact.Name}@{v.Version}" });
        db.Versions.Remove(v);
        await db.SaveChangesAsync(ct);
        return DeletionVerdict.Allowed;
    }

    // ---- Signalements ----
    public async Task<IReadOnlyList<ReportRow>> ReportsAsync(string? status, CancellationToken ct = default)
    {
        var q = db.Reports.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
        var reports = (await q.ToListAsync(ct)).OrderByDescending(r => r.At).Take(200).ToList();
        var ids = reports.Select(r => r.VersionId).Distinct().ToList();
        var versions = await db.Versions.AsNoTracking().Include(v => v.Artifact).Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        return reports.Select(r =>
        {
            versions.TryGetValue(r.VersionId, out var v);
            return new ReportRow(r.Id, r.VersionId, v is null ? "?" : $"{v.Artifact.Namespace}/{v.Artifact.Name}", v?.Version ?? "?", r.UserId, r.Reason, r.Details, r.Status, r.At);
        }).ToList();
    }

    public async Task<bool> ResolveReportAsync(Guid id, string status, string actor, CancellationToken ct = default)
    {
        var r = await db.Reports.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return false;
        r.Status = status;
        db.Audit.Add(new AuditEntry { UserId = actor, Action = "report-" + status.ToLowerInvariant(), Entity = "Report", EntityId = id.ToString() });
        await db.SaveChangesAsync(ct);
        return true;
    }
}
