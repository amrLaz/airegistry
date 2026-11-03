using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public sealed record CliTokenDto(Guid Id, string Kind, string? Name, DateTime CreatedAt, DateTime ExpiresAt, DateTime? LastUsedAt, bool Active);
public sealed record IssuedToken(Guid Id, string Token, DateTime ExpiresAt);

public class CliTokenService(AppDbContext db) : ICliTokenResolver
{
    /// <summary>Émet un jeton et ne renvoie sa valeur qu'une fois : seul son hachage est conservé.</summary>
    public async Task<IssuedToken> IssueAsync(string email, string kind, TimeSpan ttl, string? name, CancellationToken ct = default)
    {
        var token = CliAuthRules.NewToken();
        var row = new CliToken
        {
            TokenHash = CliAuthRules.Hash(token), UserEmail = email.Trim().ToLowerInvariant(), Kind = kind,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 80)], ExpiresAt = DateTime.UtcNow + ttl,
        };
        db.CliTokens.Add(row);
        db.Audit.Add(new AuditEntry { UserId = row.UserEmail, Action = "cli-token-issue", Entity = "CliToken", EntityId = row.Id.ToString(), Details = $"{kind} {row.Name}".Trim() });
        await db.SaveChangesAsync(ct);
        return new(row.Id, token, row.ExpiresAt);
    }

    public string? ResolveEmail(string token)
    {
        var hash = CliAuthRules.Hash(token);
        var row = db.CliTokens.FirstOrDefault(t => t.TokenHash == hash);
        var now = DateTime.UtcNow;
        if (row is null || !CliAuthRules.IsUsable(row.ExpiresAt, row.RevokedAt, now)) return null;
        if (row.LastUsedAt is null || now - row.LastUsedAt > TimeSpan.FromMinutes(5)) { row.LastUsedAt = now; db.SaveChanges(); }
        return row.UserEmail;
    }

    public async Task<IReadOnlyList<CliTokenDto>> ListAsync(string email, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow; var key = email.Trim().ToLowerInvariant();
        var rows = await db.CliTokens.AsNoTracking().Where(t => t.UserEmail == key && t.Kind == "ci").ToListAsync(ct);
        return rows.OrderByDescending(t => t.CreatedAt)
            .Select(t => new CliTokenDto(t.Id, t.Kind, t.Name, t.CreatedAt, t.ExpiresAt, t.LastUsedAt, CliAuthRules.IsUsable(t.ExpiresAt, t.RevokedAt, now))).ToList();
    }

    public async Task<bool> RevokeAsync(string email, Guid id, CancellationToken ct = default)
    {
        var key = email.Trim().ToLowerInvariant();
        var row = await db.CliTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserEmail == key, ct);
        if (row is null) return false;
        row.RevokedAt ??= DateTime.UtcNow;
        db.Audit.Add(new AuditEntry { UserId = key, Action = "cli-token-revoke", Entity = "CliToken", EntityId = id.ToString() });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Purge des jetons expirés depuis plus de 7 jours (appelée par un job de fond).</summary>
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var limit = DateTime.UtcNow.AddDays(-7);
        return await db.CliTokens.Where(t => t.ExpiresAt < limit).ExecuteDeleteAsync(ct);
    }
}
