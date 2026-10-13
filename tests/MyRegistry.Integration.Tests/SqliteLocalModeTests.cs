using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Integration.Tests;

/// <summary>Mode local SQLite : ne dépend d'aucune base externe.</summary>
public class SqliteLocalModeTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"mr-sqlite-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_file) + "*")) File.Delete(f);
    }

    private AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_file}").Options);

    [Fact]
    public async Task Fresh_database_is_created_with_every_table_and_seeded()
    {
        await using var db = Ctx();
        await Seeder.MigrateAndSeedAsync(db);
        Assert.True(await db.Users.CountAsync() >= 0);          // la table users existe
        Assert.Equal("MyRegistry", (await db.Settings.FirstAsync(s => s.Key == "title")).Value);
    }

    [Fact]
    public async Task A_database_created_by_an_older_version_without_the_users_table_is_recreated_instead_of_crashing()
    {
        // Simule la base d'un ancien lancement : une seule table « settings », comme avant l'ajout des utilisateurs.
        await using (var c = new SqliteConnection($"Data Source={_file}"))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE settings (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL); INSERT INTO settings VALUES ('title','Ancien');";
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = Ctx();
        await Seeder.MigrateAndSeedAsync(db);
        db.Users.Add(new AppUser { Email = "a@b.c", DisplayName = "a", Role = Role.Admin });
        await db.SaveChangesAsync();                                // « no such table: users » avant la correction
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal("MyRegistry", (await db.Settings.FirstAsync(s => s.Key == "title")).Value);   // base recréée et réinitialisée
    }

    [Fact]
    public async Task An_up_to_date_database_keeps_its_data_across_restarts()
    {
        await using (var db = Ctx())
        {
            await Seeder.MigrateAndSeedAsync(db);
            db.Users.Add(new AppUser { Email = "keep@x.io", DisplayName = "k", Role = Role.Reader });
            await db.SaveChangesAsync();
        }
        await using (var db = Ctx())
        {
            await Seeder.MigrateAndSeedAsync(db);
            Assert.Equal(1, await db.Users.CountAsync(u => u.Email == "keep@x.io"));            // pas de recréation inutile
        }
    }

    [Fact]
    public void Model_hash_is_stable_and_not_empty()
    {
        using var db = Ctx();
        Assert.Equal(SqliteSchema.ModelHash(db), SqliteSchema.ModelHash(db));
        Assert.Equal(64, SqliteSchema.ModelHash(db).Length);
    }
}
