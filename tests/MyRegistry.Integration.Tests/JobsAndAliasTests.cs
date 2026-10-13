using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyRegistry.Application;
using MyRegistry.Infrastructure;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class JobsAndAliasTests(DbFixture db) : IDisposable
{
    private readonly List<string> _dirs = [];
    private HttpClient Admin() => db.Factory.CreateClient();
    private static string U(string p) => p + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');

    public void Dispose() { foreach (var d in _dirs) if (Directory.Exists(d)) Directory.Delete(d, true); }

    private sealed class TestJob(string name, TimeSpan interval, Func<Task> body) : IBackgroundJob
    {
        public string Name => name; public TimeSpan Interval => interval;
        public Task RunAsync(IServiceProvider services, CancellationToken ct) => body();
    }

    private JobExecutor Executor(params IBackgroundJob[] jobs) =>
        new(jobs, new PostgresAdvisoryLock(db.ConnectionString), db.Factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<JobExecutor>.Instance);

    // ---------- Verrou consultatif ----------
    [Fact]
    public async Task Advisory_lock_is_exclusive_per_name_and_released_on_dispose()
    {
        var a = new PostgresAdvisoryLock(db.ConnectionString); var b = new PostgresAdvisoryLock(db.ConnectionString);
        var name = U("lock");
        var first = await a.TryAcquireAsync(name);
        Assert.NotNull(first);
        Assert.Null(await b.TryAcquireAsync(name));                     // second processus : refusé
        var other = await b.TryAcquireAsync(U("autre"));                // autre nom : indépendant
        Assert.NotNull(other);
        await first!.DisposeAsync();
        var again = await b.TryAcquireAsync(name);                      // libéré
        Assert.NotNull(again);
        await again!.DisposeAsync(); await other!.DisposeAsync();
    }

    [Fact]
    public async Task In_process_lock_behaves_the_same_for_local_mode()
    {
        var l = new InProcessLock(); var name = U("l");
        var h = await l.TryAcquireAsync(name);
        Assert.NotNull(h);
        Assert.Null(await l.TryAcquireAsync(name));
        await h!.DisposeAsync();
        Assert.NotNull(await l.TryAcquireAsync(name));
    }

    // ---------- Exécuteur ----------
    [Fact]
    public async Task Job_runs_when_due_then_waits_for_its_interval()
    {
        var runs = 0;
        var job = new TestJob(U("job"), TimeSpan.FromHours(1), () => { runs++; return Task.CompletedTask; });
        var ex = Executor(job);
        Assert.Equal("ok", (await ex.RunAsync(job)).Reason);
        Assert.Equal("not-due", (await ex.RunAsync(job)).Reason);
        Assert.Equal(1, runs);
        Assert.Equal("ok", (await ex.RunAsync(job, force: true)).Reason);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Two_replicas_racing_on_the_same_job_run_it_exactly_once()
    {
        var runs = 0;
        var gate = new TaskCompletionSource();
        var job = new TestJob(U("race"), TimeSpan.FromHours(1), async () => { Interlocked.Increment(ref runs); await gate.Task; });
        var replicaA = Executor(job); var replicaB = Executor(job);

        var a = replicaA.RunAsync(job);
        await Task.Delay(300);                                   // A tient le verrou, le job est en cours
        var b = await replicaB.RunAsync(job);
        Assert.Equal("locked", b.Reason);
        gate.SetResult();
        Assert.Equal("ok", (await a).Reason);
        Assert.Equal("not-due", (await replicaB.RunAsync(job)).Reason); // la dernière exécution est partagée en base
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Failing_job_is_reported_without_crashing_and_is_not_retried_in_a_tight_loop()
    {
        var runs = 0;
        var job = new TestJob(U("boom"), TimeSpan.FromHours(1), () => { runs++; throw new InvalidOperationException("kaboom"); });
        var ex = Executor(job);
        var r = await ex.RunAsync(job);
        Assert.False(r.Ran);
        Assert.Contains("kaboom", r.Reason);
        Assert.Equal("not-due", (await ex.RunAsync(job)).Reason);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Lock_is_released_after_the_job_even_when_it_fails()
    {
        var job = new TestJob(U("rel"), TimeSpan.FromHours(1), () => throw new InvalidOperationException("x"));
        await Executor(job).RunAsync(job);
        var again = await new PostgresAdvisoryLock(db.ConnectionString).TryAcquireAsync("job:" + job.Name);
        Assert.NotNull(again);
        await again!.DisposeAsync();
    }

    // ---------- Jobs réels ----------
    private string NewRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mr-job-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir); _dirs.Add(dir);
        Git(dir, "init", "-q", "-b", "main");
        return dir;
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@t" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return o;
    }

    private static void Commit(string repo, string rel, string content)
    {
        var full = Path.Combine(repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        Git(repo, "add", "-A"); Git(repo, "commit", "-q", "-m", "c");
    }

    private static string Skill(string n) => $"---\nname: {n}\ndescription: d\n---\n# {n}";

    private async Task<Guid> CreateFeed(string repo, string? ns = null, int interval = 5)
    {
        var res = await Admin().PostAsJsonAsync("/api/feeds", new { name = U("feed"), kind = "Local", url = repo, branch = "main", intervalMinutes = interval, namespaceOverride = ns });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Feed_sync_job_syncs_due_feeds_and_skips_inactive_ones()
    {
        var repo = NewRepo(); var a = U("auto");
        Commit(repo, $"skills/{a}/SKILL.md", Skill(a));
        var feed = await CreateFeed(repo);

        var paused = NewRepo(); var p = U("pausd");
        Commit(paused, $"skills/{p}/SKILL.md", Skill(p));
        var pausedFeed = await CreateFeed(paused);
        var pf = await Admin().GetFromJsonAsync<JsonElement>($"/api/feeds/{pausedFeed}");
        await Admin().PutAsJsonAsync($"/api/feeds/{pausedFeed}", new { name = pf.GetProperty("name").GetString(), kind = "Local", url = paused, branch = "main", intervalMinutes = 5, active = false });

        var job = new FeedSyncJob();
        var r = await Executor(job).RunAsync(job, force: true);
        Assert.Equal("ok", r.Reason);

        Assert.Equal(1, (await Admin().GetFromJsonAsync<JsonElement>($"/api/admin/versions?q={a}")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Admin().GetFromJsonAsync<JsonElement>($"/api/admin/versions?q={p}")).GetProperty("total").GetInt32());
        Assert.Equal("Ok", (await Admin().GetFromJsonAsync<JsonElement>($"/api/feeds/{feed}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Snapshot_job_is_a_silent_noop_when_no_repo_is_configured()
    {
        var before = (await Admin().GetFromJsonAsync<Dictionary<string, string>>("/api/admin/settings"))!;
        try
        {
            await Admin().PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string> { ["marketplace_repo"] = "" });
            var job = new SnapshotJob();
            Assert.Equal("ok", (await Executor(job).RunAsync(job, force: true)).Reason);
        }
        finally { await Admin().PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string> { ["marketplace_repo"] = before.GetValueOrDefault("marketplace_repo", "") }); }
    }

    // ---------- Changement de namespace ----------
    [Fact]
    public async Task Namespace_preview_does_not_write_and_apply_renames_with_aliases()
    {
        var repo = NewRepo(); var a = U("rn");
        Commit(repo, $"skills/{a}/SKILL.md", Skill(a));
        var feed = await CreateFeed(repo);
        await Admin().PostAsync($"/api/feeds/{feed}/sync", null);
        var newNs = U("acme");

        var preview = await (await Admin().PostAsJsonAsync($"/api/feeds/{feed}/namespace/preview", new { namespaceOverride = newNs })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(preview.GetProperty("canApply").GetBoolean());
        Assert.Equal($"myregistry-local/{a}", preview.GetProperty("items")[0].GetProperty("oldSlug").GetString());
        Assert.Equal($"{newNs}/{a}", preview.GetProperty("items")[0].GetProperty("newSlug").GetString());
        Assert.Equal("myregistry-local", (await Admin().GetFromJsonAsync<JsonElement>("/api/admin/versions?q=" + a)).GetProperty("items")[0].GetProperty("namespace").GetString()); // pas encore appliqué

        var apply = await Admin().PutAsJsonAsync($"/api/feeds/{feed}/namespace", new { namespaceOverride = newNs });
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        Assert.Equal(newNs, (await Admin().GetFromJsonAsync<JsonElement>("/api/admin/versions?q=" + a)).GetProperty("items")[0].GetProperty("namespace").GetString());
        Assert.Equal(newNs, (await Admin().GetFromJsonAsync<JsonElement>($"/api/feeds/{feed}")).GetProperty("effectiveNamespace").GetString());
    }

    [Fact]
    public async Task Old_slug_keeps_resolving_through_the_alias_until_it_expires()
    {
        var repo = NewRepo(); var a = U("al");
        Commit(repo, $"skills/{a}/SKILL.md", Skill(a));
        var feed = await CreateFeed(repo);
        await Admin().PostAsync($"/api/feeds/{feed}/sync", null);
        var id = (await Admin().GetFromJsonAsync<JsonElement>("/api/admin/versions?q=" + a)).GetProperty("items")[0].GetProperty("id").GetGuid();
        await Admin().PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" });
        var newNs = U("neo");
        await Admin().PutAsJsonAsync($"/api/feeds/{feed}/namespace", new { namespaceOverride = newNs });

        Assert.Equal(HttpStatusCode.OK, (await Admin().GetAsync($"/api/catalog/by-name/{newNs}/{a}")).StatusCode);
        var viaOld = await Admin().GetAsync($"/api/catalog/by-name/myregistry-local/{a}");
        Assert.Equal(HttpStatusCode.OK, viaOld.StatusCode);                                         // ancien slug : alias actif
        Assert.Equal(newNs, (await viaOld.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("namespace").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Admin().GetAsync($"/api/artifacts/by-name/myregistry-local/{a}/raw")).StatusCode);

        using (var scope = db.Factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var al in await ctx.Aliases.Where(x => x.OldSlug == $"myregistry-local/{a}").ToListAsync()) al.ExpiresAt = DateTime.UtcNow.AddDays(-1);
            await ctx.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/catalog/by-name/myregistry-local/{a}")).StatusCode);        // alias expiré
    }

    [Fact]
    public async Task Namespace_conflict_blocks_the_change_and_changes_nothing()
    {
        var repo = NewRepo(); var a = U("cf");
        Commit(repo, $"skills/{a}/SKILL.md", Skill(a));
        var feed = await CreateFeed(repo);
        await Admin().PostAsync($"/api/feeds/{feed}/sync", null);
        var target = U("taken");
        (await Admin().PostAsJsonAsync("/api/import", new { type = "Skill", @namespace = target, name = a, fileName = "SKILL.md", rawContent = Skill(a) })).EnsureSuccessStatusCode();

        var preview = await (await Admin().PostAsJsonAsync($"/api/feeds/{feed}/namespace/preview", new { namespaceOverride = target })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(preview.GetProperty("canApply").GetBoolean());
        var apply = await Admin().PutAsJsonAsync($"/api/feeds/{feed}/namespace", new { namespaceOverride = target });
        Assert.Equal(HttpStatusCode.Conflict, apply.StatusCode);
        Assert.Contains($"{target}/{a}", await apply.Content.ReadAsStringAsync());
        Assert.Equal("myregistry-local", (await Admin().GetFromJsonAsync<JsonElement>($"/api/feeds/{feed}")).GetProperty("effectiveNamespace").GetString());
    }

    [Fact]
    public async Task Sync_after_a_namespace_change_adds_a_version_to_the_renamed_artifact_not_a_duplicate()
    {
        var repo = NewRepo(); var a = U("keep");
        Commit(repo, $"skills/{a}/SKILL.md", Skill(a));
        var feed = await CreateFeed(repo);
        await Admin().PostAsync($"/api/feeds/{feed}/sync", null);
        var newNs = U("moved");
        await Admin().PutAsJsonAsync($"/api/feeds/{feed}/namespace", new { namespaceOverride = newNs });

        Commit(repo, $"skills/{a}/SKILL.md", $"---\nname: {a}\ndescription: d\n---\n# changé");
        var r = await (await Admin().PostAsync($"/api/feeds/{feed}/sync", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, r.GetProperty("updated").GetInt32());
        var items = (await Admin().GetFromJsonAsync<JsonElement>("/api/admin/versions?q=" + a)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(newNs, i.GetProperty("namespace").GetString()));
    }

    [Fact]
    public async Task Namespace_change_is_admin_only_audited_and_validated()
    {
        var repo = NewRepo(); var a = U("au");
        Commit(repo, $"skills/{a}/SKILL.md", Skill(a));
        var feed = await CreateFeed(repo);
        await Admin().PostAsync($"/api/feeds/{feed}/sync", null);
        var reader = db.Factory.CreateClient(); reader.DefaultRequestHeaders.Add("X-Dev-User", $"{U("r")}@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync($"/api/feeds/{feed}/namespace", new { namespaceOverride = "valid-ns" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Admin().PutAsJsonAsync($"/api/feeds/{feed}/namespace", new { namespaceOverride = "Bad_NS" })).StatusCode);

        await Admin().PutAsJsonAsync($"/api/feeds/{feed}/namespace", new { namespaceOverride = U("okns") });
        var audit = await Admin().GetFromJsonAsync<JsonElement>("/api/audit?action=feed-namespace&pageSize=5");
        Assert.True(audit.GetProperty("total").GetInt32() >= 1);
    }
}
