using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record NotificationDto(Guid Id, NotificationKind Kind, string Subject, string? Detail, string? Link, DateTime CreatedAt, bool Read, bool Archived);

public class NotificationService(AppDbContext db)
{
    /// <summary>Ajoute les notifications au contexte sans sauvegarder : l'appelant les persiste avec sa propre transaction.</summary>
    public async Task AddAsync(IEnumerable<string?> candidates, string actor, NotificationKind kind, string subject, string? detail, string? link, CancellationToken ct = default)
    {
        var recipients = NotificationRules.Recipients(candidates, actor);
        if (recipients.Count == 0) return;
        var disabled = (await db.NotificationPreferences.AsNoTracking()
                .Where(p => p.Kind == kind && !p.Enabled && recipients.Contains(p.UserId)).Select(p => p.UserId).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in recipients.Where(r => !disabled.Contains(r)))
            db.Notifications.Add(new Notification { UserId = r, Kind = kind, Subject = subject, Detail = detail, Link = link });
    }

    /// <summary>Courriels des utilisateurs actifs ayant au moins ce rôle (modérateurs à prévenir).</summary>
    public async Task<IReadOnlyList<string>> EmailsWithRoleAtLeastAsync(Role min, CancellationToken ct = default)
    {
        var users = await db.Users.AsNoTracking().Where(u => u.Active).ToListAsync(ct);
        return users.Where(u => Rank(u.Role) >= Rank(min)).Select(u => u.Email).ToList();
    }

    private static int Rank(Role r) => r switch { Role.Reader => 0, Role.Publisher => 1, Role.Moderator => 2, Role.SecurityValidator => 3, _ => 4 };

    public async Task<IReadOnlyList<NotificationDto>> ListAsync(string user, bool includeArchived, int take = 50, CancellationToken ct = default)
    {
        var q = db.Notifications.AsNoTracking().Where(n => n.UserId == user);
        if (!includeArchived) q = q.Where(n => n.ArchivedAt == null);
        var list = await q.ToListAsync(ct);
        return list.OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(take, 1, 200))
            .Select(n => new NotificationDto(n.Id, n.Kind, n.Subject, n.Detail, n.Link, n.CreatedAt, n.ReadAt is not null, n.ArchivedAt is not null)).ToList();
    }

    public Task<int> UnreadCountAsync(string user, CancellationToken ct = default) =>
        db.Notifications.CountAsync(n => n.UserId == user && n.ReadAt == null && n.ArchivedAt == null, ct);

    public async Task<bool> MarkReadAsync(string user, Guid id, CancellationToken ct = default) =>
        await Mutate(user, id, n => n.ReadAt ??= DateTime.UtcNow, ct);

    public async Task<bool> ArchiveAsync(string user, Guid id, CancellationToken ct = default) =>
        await Mutate(user, id, n => { n.ArchivedAt ??= DateTime.UtcNow; n.ReadAt ??= DateTime.UtcNow; }, ct);

    public async Task<bool> DeleteAsync(string user, Guid id, CancellationToken ct = default)
    {
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user, ct);
        if (n is null) return false;
        db.Notifications.Remove(n);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> MarkAllReadAsync(string user, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var items = await db.Notifications.Where(n => n.UserId == user && n.ReadAt == null).ToListAsync(ct);
        foreach (var n in items) n.ReadAt = now;
        await db.SaveChangesAsync(ct);
        return items.Count;
    }

    public async Task<int> ArchiveAllAsync(string user, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var items = await db.Notifications.Where(n => n.UserId == user && n.ArchivedAt == null).ToListAsync(ct);
        foreach (var n in items) { n.ArchivedAt = now; n.ReadAt ??= now; }
        await db.SaveChangesAsync(ct);
        return items.Count;
    }

    public async Task<int> DeleteAllAsync(string user, CancellationToken ct = default)
    {
        var items = await db.Notifications.Where(n => n.UserId == user).ToListAsync(ct);
        db.Notifications.RemoveRange(items);
        await db.SaveChangesAsync(ct);
        return items.Count;
    }

    public async Task<IReadOnlyDictionary<NotificationKind, bool>> PreferencesAsync(string user, CancellationToken ct = default)
    {
        var stored = await db.NotificationPreferences.AsNoTracking().Where(p => p.UserId == user).ToListAsync(ct);
        return Enum.GetValues<NotificationKind>().ToDictionary(k => k, k => stored.FirstOrDefault(p => p.Kind == k)?.Enabled ?? true);
    }

    public async Task<IReadOnlyDictionary<NotificationKind, bool>> SetPreferencesAsync(string user, IReadOnlyDictionary<NotificationKind, bool> patch, CancellationToken ct = default)
    {
        var stored = await db.NotificationPreferences.Where(p => p.UserId == user).ToListAsync(ct);
        foreach (var (kind, enabled) in patch)
        {
            var row = stored.FirstOrDefault(p => p.Kind == kind);
            if (row is null) db.NotificationPreferences.Add(new NotificationPreference { UserId = user, Kind = kind, Enabled = enabled });
            else row.Enabled = enabled;
        }
        await db.SaveChangesAsync(ct);
        return await PreferencesAsync(user, ct);
    }

    private async Task<bool> Mutate(string user, Guid id, Action<Notification> change, CancellationToken ct)
    {
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == user, ct);
        if (n is null) return false;
        change(n);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
