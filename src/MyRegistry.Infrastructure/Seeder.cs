using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public static class Seeder
{
    public static async Task MigrateAndSeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        // PostgreSQL : migrations. SQLite (mode local sans Postgres) : schéma créé directement.
        if (db.Database.IsSqlite())
        {
            if (await SqliteSchema.EnsureCurrentAsync(db, ct))
                Console.WriteLine("[MyRegistry] Schéma SQLite obsolète : la base locale a été recréée (mode développement, données jetables).");
        }
        else await db.Database.MigrateAsync(ct);
        var existing = await db.Settings.Select(s => s.Key).ToListAsync(ct);
        foreach (var (k, v) in SettingDefaults.Initial.Where(kv => !existing.Contains(kv.Key)))
            db.Settings.Add(new Setting { Key = k, Value = v });
        await db.SaveChangesAsync(ct);
    }
}
