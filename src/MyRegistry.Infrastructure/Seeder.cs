using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public static class Seeder
{
    public static async Task MigrateAndSeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        var existing = await db.Settings.Select(s => s.Key).ToListAsync(ct);
        foreach (var (k, v) in SettingDefaults.Initial.Where(kv => !existing.Contains(kv.Key)))
            db.Settings.Add(new Setting { Key = k, Value = v });
        await db.SaveChangesAsync(ct);
    }
}
