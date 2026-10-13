using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyRegistry.Application;
using Npgsql;

namespace MyRegistry.Infrastructure;

/// <summary>Verrou consultatif PostgreSQL (pg_try_advisory_lock) : tenu par une connexion dédiée jusqu'à la libération.</summary>
public sealed class PostgresAdvisoryLock(string connectionString) : IJobLock
{
    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@n, 0))", conn);
            cmd.Parameters.AddWithValue("n", "myregistry:" + name);
            if (await cmd.ExecuteScalarAsync(ct) is true) return new Handle(conn, name);
        }
        catch { await conn.DisposeAsync(); throw; }
        await conn.DisposeAsync();
        return null;
    }

    private sealed class Handle(NpgsqlConnection conn, string name) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@n, 0))", conn);
                cmd.Parameters.AddWithValue("n", "myregistry:" + name);
                await cmd.ExecuteScalarAsync();
            }
            finally { await conn.DisposeAsync(); } // fermer la connexion libère de toute façon le verrou
        }
    }
}

/// <summary>Verrou en mémoire pour le mode local SQLite (un seul processus).</summary>
public sealed class InProcessLock : IJobLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        return await gate.WaitAsync(0, ct) ? new Handle(gate) : null;
    }

    private sealed class Handle(SemaphoreSlim gate) : IAsyncDisposable { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
}

public sealed record JobOutcome(string Job, bool Ran, string Reason);

/// <summary>
/// Exécute les tâches échues : verrou → relecture de la dernière exécution (partagée en base entre réplicas) → exécution → enregistrement.
/// </summary>
public sealed class JobExecutor(IEnumerable<IBackgroundJob> jobs, IJobLock jobLock, IServiceScopeFactory scopes, ILogger<JobExecutor> log)
{
    public async Task<IReadOnlyList<JobOutcome>> RunDueAsync(CancellationToken ct = default, bool force = false)
    {
        var outcomes = new List<JobOutcome>();
        foreach (var job in jobs) outcomes.Add(await RunAsync(job, ct, force));
        return outcomes;
    }

    public async Task<JobOutcome> RunAsync(IBackgroundJob job, CancellationToken ct = default, bool force = false)
    {
        using var scope0 = scopes.CreateScope();
        if (!force && !await IsDueAsync(scope0.ServiceProvider, job, ct)) return new(job.Name, false, "not-due");

        await using var held = await jobLock.TryAcquireAsync("job:" + job.Name, ct);
        if (held is null) return new(job.Name, false, "locked");

        using var scope = scopes.CreateScope();
        if (!force && !await IsDueAsync(scope.ServiceProvider, job, ct)) return new(job.Name, false, "not-due"); // une autre réplique vient de l'exécuter
        try
        {
            await job.RunAsync(scope.ServiceProvider, ct);
            await scope.ServiceProvider.GetRequiredService<SettingsService>().SetInternalAsync(
                new Dictionary<string, string> { [JobSchedule.LastRunKey(job.Name)] = DateTime.UtcNow.ToString("O") }, ct);
            return new(job.Name, true, "ok");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Job {Job} en échec", job.Name);
            // On enregistre quand même l'exécution pour ne pas relancer en boucle serrée.
            await scope.ServiceProvider.GetRequiredService<SettingsService>().SetInternalAsync(
                new Dictionary<string, string> { [JobSchedule.LastRunKey(job.Name)] = DateTime.UtcNow.ToString("O") }, ct);
            return new(job.Name, false, "error: " + ex.Message);
        }
    }

    private static async Task<bool> IsDueAsync(IServiceProvider sp, IBackgroundJob job, CancellationToken ct)
    {
        var raw = await sp.GetRequiredService<SettingsService>().GetAsync(JobSchedule.LastRunKey(job.Name), "", ct);
        DateTime? last = DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
        return JobSchedule.IsDue(last, job.Interval, DateTime.UtcNow);
    }
}

public sealed class BackgroundJobRunner(JobExecutor executor, ILogger<BackgroundJobRunner> log) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); // laisse l'application finir de démarrer (migrations)
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await executor.RunDueAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Cycle de jobs en échec"); }
            await Task.Delay(Tick, stoppingToken);
        }
    }
}

/// <summary>Synchronise les feeds échus (chaque feed a son propre intervalle). Passe toutes les minutes.</summary>
public sealed class FeedSyncJob : IBackgroundJob
{
    public string Name => "feed-sync";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var feeds = sp.GetRequiredService<FeedService>();
        var now = DateTime.UtcNow;
        var due = (await db.Feeds.AsNoTracking().ToListAsync(ct)).Where(f => FeedRules.IsDue(f.Active, f.LastSyncAt, f.IntervalMinutes, now)).Select(f => f.Id).ToList();
        foreach (var id in due) await feeds.SyncAsync(id, "system", ct: ct);
    }
}

/// <summary>Publie le snapshot de la marketplace toutes les 6 h ; ne pousse rien si le digest est inchangé (déjà géré par le service).</summary>
public sealed class SnapshotJob : IBackgroundJob
{
    public string Name => "marketplace-snapshot";
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(IServiceProvider sp, CancellationToken ct)
    {
        var svc = sp.GetRequiredService<SnapshotService>();
        if (!(await svc.StatusAsync(ct)).Configured) return; // pas de dépôt configuré : rien à faire, sans erreur
        await svc.PublishAsync("system", ct);
    }
}
