using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyRegistry.Application;
using MyRegistry.Domain;
using MyRegistry.Infrastructure;

namespace MyRegistry.Integration.Tests;

public sealed class FakeHost : IGitHostProvider
{
    public List<NewPullRequest> Created { get; } = [];
    public List<int> Completed { get; } = [];
    public List<int> Abandoned { get; } = [];
    public string? FailCreate { get; set; }
    public string? FailComplete { get; set; }
    public RepoSnapshot Snapshot { get; set; } = new("a".PadRight(40, 'a'), new Dictionary<string, byte[]>(), []);
    private int _next = 100;

    public Task<RepoSnapshot> ReadRepoAsync(string repoLocation, string? branch, string? token, CancellationToken ct = default) => Task.FromResult(Snapshot);
    public Task<IReadOnlyDictionary<string, byte[]>> ReadFilesAsync(string repoUrl, string branch, IEnumerable<string> paths, string? token, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, byte[]>>(new Dictionary<string, byte[]>());
    public Task<PullRequestInfo> CreatePullRequestAsync(string repoUrl, NewPullRequest request, string? token, CancellationToken ct = default)
    {
        if (FailCreate is not null) throw new InvalidOperationException(FailCreate);
        Created.Add(request);
        var id = _next++;
        return Task.FromResult(new PullRequestInfo(id, $"https://dev.azure.com/o/p/_git/gov/pullrequest/{id}", request.SourceBranch));
    }
    public Task CompletePullRequestAsync(string repoUrl, int pullRequestId, string? token, CancellationToken ct = default)
    {
        if (FailComplete is not null) throw new InvalidOperationException(FailComplete);
        Completed.Add(pullRequestId); return Task.CompletedTask;
    }
    public Task AbandonPullRequestAsync(string repoUrl, int pullRequestId, string? token, CancellationToken ct = default) { Abandoned.Add(pullRequestId); return Task.CompletedTask; }
    public Task<GitCommitResult> CommitFilesAsync(string repoLocation, IReadOnlyDictionary<string, string> files, string message, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

public sealed class FakeResolver(FakeHost host) : IGitProviderResolver
{
    public IGitProvider For(FeedKind kind) => kind == FeedKind.AzureDevOps ? host : throw new NotSupportedException();
}

[Collection("db")]
public class PullRequestFlowTests : IAsyncLifetime
{
    private readonly DbFixture _db;
    private readonly FakeHost _host = new();
    private WebApplicationFactory<Program> _factory = null!;

    public PullRequestFlowTests(DbFixture db) { _db = db; }

    private static string U(string p) => p + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');
    private HttpClient Admin() => _factory.CreateClient();

    public async Task InitializeAsync()
    {
        _factory = _db.Factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IGitProviderResolver>();
            s.AddScoped<IGitProviderResolver>(_ => new FakeResolver(_host));
        }));
        await ResetSystemFeedsAsync();
    }

    public async Task DisposeAsync() { await ResetSystemFeedsAsync(); _factory.Dispose(); }

    private async Task ResetSystemFeedsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ctx.Feeds.RemoveRange(await ctx.Feeds.Where(f => f.IsSystem).ToListAsync());
        await ctx.SaveChangesAsync();
    }

    private async Task<Guid> CreateSystemFeed()
    {
        var res = await Admin().PostAsJsonAsync("/api/feeds", new { name = U("gov"), kind = "AzureDevOps", url = "https://dev.azure.com/acme/proj/_git/gov", branch = "main", isSystem = true });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static object Skill(string name, string? version = null) => new
    {
        type = "Skill", @namespace = "myregistry", name, version, fileName = "SKILL.md", description = "d",
        rawContent = $"---\nname: {name}\ndescription: d\n---\n# {name}",
        additionalFiles = new Dictionary<string, string> { ["README.md"] = "# r" },
    };

    [Fact]
    public async Task Without_a_system_feed_publication_stays_direct_and_opens_no_pull_request()
    {
        var res = await Admin().PostAsJsonAsync("/api/import", Skill(U("direct")));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Empty(_host.Created);
        Assert.Equal(JsonValueKind.Null, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pullRequestUrl").ValueKind);
    }

    [Fact]
    public async Task Publication_opens_a_pull_request_on_the_governance_repo_with_the_documented_branch_and_layout()
    {
        await CreateSystemFeed();
        var name = U("pr");
        var res = await Admin().PostAsJsonAsync("/api/import", Skill(name, "1.2.0"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        var pr = Assert.Single(_host.Created);
        var versionId = body.GetProperty("versionId").GetGuid();
        Assert.Equal($"artifact/myregistry/{name}/1.2.0-{versionId.ToString("N")[..8]}", pr.SourceBranch);
        Assert.Equal("main", pr.BaseBranch);
        Assert.Equal([$"artifacts/myregistry/{name}/README.md", $"artifacts/myregistry/{name}/SKILL.md"], pr.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Contains(name, pr.Title);

        Assert.StartsWith("https://dev.azure.com/", body.GetProperty("pullRequestUrl").GetString());
        var detail = await Admin().GetFromJsonAsync<JsonElement>($"/api/catalog/{versionId}");
        Assert.Equal("Open", detail.GetProperty("prState").GetString());
        Assert.Equal(body.GetProperty("pullRequestUrl").GetString(), detail.GetProperty("prUrl").GetString());
    }

    [Fact]
    public async Task Approval_merges_the_pull_request_once_and_rejection_abandons_it()
    {
        await CreateSystemFeed();
        var approved = (await (await Admin().PostAsJsonAsync("/api/import", Skill(U("ok")))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
        var rejected = (await (await Admin().PostAsJsonAsync("/api/import", Skill(U("ko")))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await Admin().PutAsJsonAsync($"/api/catalog/{approved}/status", new { status = "Approved" })).StatusCode);
        Assert.Single(_host.Completed);
        Assert.Equal("Merged", (await Admin().GetFromJsonAsync<JsonElement>($"/api/catalog/{approved}")).GetProperty("prState").GetString());
        await Admin().PutAsJsonAsync($"/api/catalog/{approved}/status", new { status = "Approved" }); // idempotent : pas de seconde fusion
        Assert.Single(_host.Completed);

        Assert.Equal(HttpStatusCode.NoContent, (await Admin().PostAsJsonAsync($"/api/catalog/{rejected}/reject", new { reason = "hors périmètre" })).StatusCode);
        Assert.Single(_host.Abandoned);
        var d = await Admin().GetFromJsonAsync<JsonElement>($"/api/catalog/{rejected}");
        Assert.Equal("Abandoned", d.GetProperty("prState").GetString());
        Assert.Equal("Deprecated", d.GetProperty("status").GetString());
    }

    [Fact]
    public async Task If_the_pull_request_cannot_be_created_the_publication_fails_and_nothing_is_stored()
    {
        await CreateSystemFeed();
        _host.FailCreate = "Pull request vide : le contenu est identique à celui du dépôt.";
        var name = U("fail");
        var res = await Admin().PostAsJsonAsync("/api/import", Skill(name));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Contains("pull-request", text);
        Assert.Contains("vide", text);
        Assert.Equal(0, (await Admin().GetFromJsonAsync<JsonElement>($"/api/admin/versions?q={name}")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task If_the_merge_fails_the_status_does_not_change()
    {
        await CreateSystemFeed();
        var id = (await (await Admin().PostAsJsonAsync("/api/import", Skill(U("nomerge")))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
        _host.FailComplete = "conflit de fusion";
        var res = await Admin().PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" });
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Contains("conflit de fusion", await res.Content.ReadAsStringAsync());
        var d = await Admin().GetFromJsonAsync<JsonElement>($"/api/catalog/{id}");
        Assert.Equal("Pending", d.GetProperty("status").GetString());
        Assert.Equal("Open", d.GetProperty("prState").GetString());

        _host.FailComplete = null;
        Assert.Equal(HttpStatusCode.NoContent, (await Admin().PutAsJsonAsync($"/api/catalog/{id}/status", new { status = "Approved" })).StatusCode); // réessai possible
    }

    [Fact]
    public async Task Validation_errors_never_reach_git()
    {
        await CreateSystemFeed();
        var res = await Admin().PostAsJsonAsync("/api/import", new { type = "Skill", @namespace = "myregistry", name = "Bad_Name", fileName = "SKILL.md", rawContent = "x" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Empty(_host.Created);
    }

    [Fact]
    public async Task Syncing_the_system_feed_ignores_internal_publications_under_artifacts()
    {
        var feed = await CreateSystemFeed();
        var real = U("real"); var internalName = U("internal");
        string Md(string n) => $"---\nname: {n}\ndescription: d\n---\n# {n}";
        _host.Snapshot = new("b".PadRight(40, 'b'), new Dictionary<string, byte[]>
        {
            [$"skills/{real}/SKILL.md"] = Encoding.UTF8.GetBytes(Md(real)),
            [$"artifacts/myregistry/{internalName}/SKILL.md"] = Encoding.UTF8.GetBytes(Md(internalName)),
        }, []);
        var r = await (await Admin().PostAsync($"/api/feeds/{feed}/sync", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(r.GetProperty("success").GetBoolean());
        Assert.Equal(1, r.GetProperty("imported").GetInt32());
        Assert.Equal(1, (await Admin().GetFromJsonAsync<JsonElement>($"/api/admin/versions?q={real}")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Admin().GetFromJsonAsync<JsonElement>($"/api/admin/versions?q={internalName}")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Only_one_system_feed_is_allowed_and_it_cannot_be_deleted()
    {
        var first = await CreateSystemFeed();
        var second = await Admin().PostAsJsonAsync("/api/feeds", new { name = U("gov2"), kind = "AzureDevOps", url = "https://dev.azure.com/acme/proj/_git/gov2", isSystem = true });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Admin().DeleteAsync($"/api/feeds/{first}")).StatusCode);
    }

    [Fact]
    public async Task Plugins_published_from_the_editor_also_go_through_a_pull_request()
    {
        await CreateSystemFeed();
        var skill = U("comp");
        var sid = (await (await Admin().PostAsJsonAsync("/api/import", Skill(skill))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
        await Admin().PutAsJsonAsync($"/api/catalog/{sid}/status", new { status = "Approved" });
        var d = (await (await Admin().PostAsJsonAsync("/api/plugins/drafts", new { @namespace = "myregistry", name = U("pack"), description = "p",
            components = new[] { new { type = "Skill", slug = $"myregistry/{skill}", version = "1.0.0" } } })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var before = _host.Created.Count;
        var res = await Admin().PostAsync($"/api/plugins/drafts/{d}/publish", null);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(before + 1, _host.Created.Count);
        Assert.Contains(_host.Created.Last().Files.Keys, k => k.EndsWith(".claude-plugin/plugin.json"));
    }
}
