using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using MyRegistry.Infrastructure;
using Testcontainers.PostgreSql;

namespace MyRegistry.Integration.Tests;

/// <summary>
/// PostgreSQL réel. Si MYREGISTRY_TEST_DB (chaîne de connexion serveur) est défini, on l'utilise (CI avec service
/// container, ou poste avec Postgres local) ; sinon Testcontainers démarre un conteneur.
/// </summary>
public sealed class DbFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _admin = "";
    private readonly string _dbName = "t_" + Guid.NewGuid().ToString("N")[..12];

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var env = Environment.GetEnvironmentVariable("MYREGISTRY_TEST_DB");
        if (env is null)
        {
            _container = new PostgreSqlBuilder("postgres:16").Build();
            await _container.StartAsync();
            _admin = _container.GetConnectionString();
        }
        else _admin = env;

        await using (var c = new NpgsqlConnection(_admin))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {_dbName}", c);
            await cmd.ExecuteNonQueryAsync();
        }
        var conn = new NpgsqlConnectionStringBuilder(_admin) { Database = _dbName }.ToString();
        ConnectionString = conn;

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("DB_CONNECTION", conn);
        });
        using var scope = Factory.Services.CreateScope();
        await Seeder.MigrateAndSeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task DisposeAsync()
    {
        Factory?.Dispose();
        if (_container is not null) await _container.DisposeAsync();
    }
}

[CollectionDefinition("db")]
public class DbCollection : ICollectionFixture<DbFixture>;
