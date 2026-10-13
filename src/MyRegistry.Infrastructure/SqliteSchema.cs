using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace MyRegistry.Infrastructure;

/// <summary>
/// Mode local SQLite : le schéma est créé par <c>EnsureCreated</c>, qui ne complète JAMAIS une base existante. Sans garde-fou, une base créée
/// avant l'ajout d'une table provoque « no such table ». On mémorise donc une empreinte du modèle et on recrée la base (jetable) si elle change.
/// </summary>
public static class SqliteSchema
{
    public static string ModelHash(DbContext db)
    {
        var parts = db.Model.GetEntityTypes().OrderBy(e => e.GetTableName(), StringComparer.Ordinal)
            .Select(e => e.GetTableName() + ":" + string.Join(",", e.GetProperties().Select(p => p.GetColumnName()).OrderBy(c => c, StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)))).ToLowerInvariant();
    }

    /// <summary>Retourne true si la base a été recréée parce que son schéma ne correspondait plus au modèle.</summary>
    public static async Task<bool> EnsureCurrentAsync(DbContext db, CancellationToken ct = default)
    {
        var expected = ModelHash(db);
        var recreated = false;
        if (await db.Database.CanConnectAsync(ct) && await StoredHashAsync(db, ct) != expected)
        {
            await db.Database.EnsureDeletedAsync(ct);
            recreated = true;
        }
        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS local_schema (hash TEXT NOT NULL)", ct);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM local_schema", ct);
        await db.Database.ExecuteSqlRawAsync($"INSERT INTO local_schema (hash) VALUES ('{expected}')", ct); // empreinte hexadécimale calculée localement : pas d'entrée utilisateur
        return recreated;
    }

    private static async Task<string?> StoredHashAsync(DbContext db, CancellationToken ct)
    {
        try
        {
            return await db.Database.SqlQueryRaw<string>("SELECT hash AS Value FROM local_schema LIMIT 1").FirstOrDefaultAsync(ct);
        }
        catch (Exception) { return null; } // table absente : base créée avant ce garde-fou → à recréer
    }
}
