using System.Net;
using System.Text;
using System.Text.Json;
using MyRegistry.Application;
using MyRegistry.Infrastructure;

namespace MyRegistry.Integration.Tests;

/// <summary>Faux serveur HTTP : enregistre les requêtes et répond selon des routes ; aucune dépendance réseau.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, string Url, string? Body, string? Auth);
    public List<Call> Calls { get; } = [];
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public FakeHttp On(HttpMethod method, string urlContains, Func<HttpRequestMessage, HttpResponseMessage> respond)
    { _routes.Add((r => r.Method == method && r.RequestUri!.ToString().Contains(urlContains), respond)); return this; }

    public static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Bytes(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
    public static HttpResponseMessage Status(HttpStatusCode c) => new(c);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Calls.Add(new(request.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
        foreach (var (match, respond) in _routes) if (match(request)) return respond(request);
        return Status(HttpStatusCode.NotFound);
    }
}

public class GitHubProviderTests
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static (GitHubProvider P, FakeHttp H) Make(bool truncated = false, long bigSize = 10)
    {
        var h = new FakeHttp()
            .On(HttpMethod.Get, "/branches/main", _ => FakeHttp.Json(new { commit = new { sha = "abc123" } }))
            .On(HttpMethod.Get, "/git/trees/abc123", _ => FakeHttp.Json(new
            {
                truncated,
                tree = new object[]
                {
                    new { path = "skills/pdf/SKILL.md", type = "blob", sha = "s1", size = 10 },
                    new { path = "skills/pdf", type = "tree", sha = "t1" },
                    new { path = "big.bin", type = "blob", sha = "s2", size = bigSize },
                },
            }))
            .On(HttpMethod.Get, "/git/blobs/s1", _ => FakeHttp.Json(new { encoding = "base64", content = B64("# hello") }))
            .On(HttpMethod.Get, "/git/blobs/s2", _ => FakeHttp.Json(new { encoding = "base64", content = B64("x") }));
        return (new GitHubProvider(new HttpClient(h)), h);
    }

    [Fact] public async Task Reads_tree_and_blobs_with_a_bearer_token()
    {
        var (p, h) = Make();
        var snap = await p.ReadRepoAsync("https://github.com/org/repo", "main", "tok");
        Assert.Equal("abc123", snap.HeadCommit);
        Assert.Equal("# hello", Encoding.UTF8.GetString(snap.Files["skills/pdf/SKILL.md"]));
        Assert.All(h.Calls, c => Assert.Equal("Bearer tok", c.Auth));
        Assert.Contains(h.Calls, c => c.Url.Contains("/repos/org/repo/branches/main"));
    }

    [Fact] public async Task Large_files_are_skipped_and_reported()
    {
        var (p, _) = Make(bigSize: 5_000_000);
        var snap = await p.ReadRepoAsync("https://github.com/org/repo", "main", null);
        Assert.DoesNotContain("big.bin", snap.Files.Keys);
        Assert.Contains("big.bin", snap.Skipped);
    }

    [Fact] public async Task Truncated_tree_is_reported()
    {
        var (p, _) = Make(truncated: true);
        Assert.Contains(await p.ReadRepoAsync("https://github.com/org/repo", "main", null) is var s ? s.Skipped : [], x => x.Contains("tronqué"));
    }

    [Fact] public async Task Without_a_token_no_authorization_header_is_sent()
    {
        var (p, h) = Make();
        await p.ReadRepoAsync("https://github.com/org/repo", "main", null);
        Assert.All(h.Calls, c => Assert.Null(c.Auth));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "introuvable")]
    [InlineData(HttpStatusCode.Unauthorized, "refusé")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Http_errors_become_clear_messages(HttpStatusCode code, string expected)
    {
        var p = new GitHubProvider(new HttpClient(new FakeHttp().On(HttpMethod.Get, "/branches/", _ => FakeHttp.Status(code))));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => p.ReadRepoAsync("https://github.com/o/r", "main", null));
        Assert.Contains(expected, ex.Message);
    }

    [Fact] public async Task Exhausted_rate_limit_is_explained()
    {
        var p = new GitHubProvider(new HttpClient(new FakeHttp().On(HttpMethod.Get, "/branches/", _ =>
        { var r = FakeHttp.Status(HttpStatusCode.Forbidden); r.Headers.Add("X-RateLimit-Remaining", "0"); return r; })));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => p.ReadRepoAsync("https://github.com/o/r", "main", null));
        Assert.Contains("Limite de requêtes", ex.Message);
    }

    [Fact] public async Task Invalid_url_and_write_attempts_are_refused()
    {
        var (p, _) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() => p.ReadRepoAsync("https://gitlab.com/o/r", "main", null));
        await Assert.ThrowsAsync<NotSupportedException>(() => p.CommitFilesAsync("https://github.com/o/r", new Dictionary<string, string>(), "m"));
    }
}

public class AzureDevOpsProviderTests
{
    private const string Repo = "https://dev.azure.com/acme/proj/_git/gov";
    private const string BaseHead = "1111111111111111111111111111111111111111";

    private static FakeHttp Server(Dictionary<string, string>? existing = null, string prStatus = "active")
    {
        existing ??= new();
        return new FakeHttp()
            .On(HttpMethod.Get, "/refs?filter=heads/main", _ => FakeHttp.Json(new { value = new[] { new { name = "refs/heads/main", objectId = BaseHead } } }))
            .On(HttpMethod.Get, $"/commits/{BaseHead}", _ => FakeHttp.Json(new { treeId = "tree1" }))
            .On(HttpMethod.Get, "/trees/tree1", _ => FakeHttp.Json(new { treeEntries = new object[]
            {
                new { relativePath = "skills/pdf/SKILL.md", gitObjectType = "blob", objectId = "b1", size = 7 },
                new { relativePath = "skills", gitObjectType = "tree", objectId = "t" },
            } }))
            .On(HttpMethod.Get, "/blobs/b1", _ => FakeHttp.Bytes(Encoding.UTF8.GetBytes("# hello")))
            .On(HttpMethod.Get, "/items?path=", r =>
            {
                var path = Uri.UnescapeDataString(System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["path"]!).TrimStart('/');
                return existing.TryGetValue(path, out var c) ? FakeHttp.Bytes(Encoding.UTF8.GetBytes(c)) : FakeHttp.Status(HttpStatusCode.NotFound);
            })
            .On(HttpMethod.Post, "/refs?", _ => FakeHttp.Json(new { value = new[] { new { success = true } } }))
            .On(HttpMethod.Post, "/pushes?", _ => FakeHttp.Json(new { pushId = 1 }))
            .On(HttpMethod.Post, "/pullrequests?", _ => FakeHttp.Json(new { pullRequestId = 42 }))
            .On(HttpMethod.Get, "/pullrequests/42", _ => FakeHttp.Json(new { status = prStatus, lastMergeSourceCommit = new { commitId = "feedface" } }))
            .On(new HttpMethod("PATCH"), "/pullrequests/42", _ => FakeHttp.Json(new { pullRequestId = 42 }));
    }

    private static NewPullRequest Pr(params (string, string)[] files) =>
        new("main", "artifact/myregistry/pdf/1.0.0-abcd1234", files.ToDictionary(f => f.Item1, f => Encoding.UTF8.GetBytes(f.Item2)), "feat: publish", "desc");

    [Fact] public async Task Reads_the_tree_with_basic_auth_built_from_the_pat()
    {
        var h = Server();
        var snap = await new AzureDevOpsProvider(new HttpClient(h)).ReadRepoAsync(Repo, "main", "my-pat");
        Assert.Equal(BaseHead, snap.HeadCommit);
        Assert.Equal("# hello", Encoding.UTF8.GetString(snap.Files["skills/pdf/SKILL.md"]));
        var expected = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":my-pat"));
        Assert.All(h.Calls, c => Assert.Equal(expected, c.Auth));
    }

    [Fact] public async Task Create_pull_request_branches_from_base_pushes_then_opens_the_pr_in_order()
    {
        var h = Server(new() { ["artifacts/myregistry/pdf/SKILL.md"] = "ancien" });
        var info = await new AzureDevOpsProvider(new HttpClient(h)).CreatePullRequestAsync(Repo,
            Pr(("artifacts/myregistry/pdf/SKILL.md", "nouveau"), ("artifacts/myregistry/pdf/README.md", "r")), "pat");

        Assert.Equal(42, info.Id);
        Assert.Equal("https://dev.azure.com/acme/proj/_git/gov/pullrequest/42", info.Url);
        var writes = h.Calls.Where(c => c.Method == HttpMethod.Post).ToList();
        Assert.Collection(writes, c => Assert.Contains("/refs?", c.Url), c => Assert.Contains("/pushes?", c.Url), c => Assert.Contains("/pullrequests?", c.Url));

        var refBody = JsonDocument.Parse(writes[0].Body!).RootElement[0];
        Assert.Equal("refs/heads/artifact/myregistry/pdf/1.0.0-abcd1234", refBody.GetProperty("name").GetString());
        Assert.Equal(new string('0', 40), refBody.GetProperty("oldObjectId").GetString());
        Assert.Equal(BaseHead, refBody.GetProperty("newObjectId").GetString());

        var push = JsonDocument.Parse(writes[1].Body!).RootElement;
        Assert.Equal(BaseHead, push.GetProperty("refUpdates")[0].GetProperty("oldObjectId").GetString());
        var changes = push.GetProperty("commits")[0].GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal("add", changes.First(c => c.GetProperty("item").GetProperty("path").GetString()!.EndsWith("README.md")).GetProperty("changeType").GetString());
        var edit = changes.First(c => c.GetProperty("item").GetProperty("path").GetString()!.EndsWith("SKILL.md"));
        Assert.Equal("edit", edit.GetProperty("changeType").GetString());
        Assert.Equal("nouveau", Encoding.UTF8.GetString(Convert.FromBase64String(edit.GetProperty("newContent").GetProperty("content").GetString()!)));

        var pr = JsonDocument.Parse(writes[2].Body!).RootElement;
        Assert.Equal("refs/heads/main", pr.GetProperty("targetRefName").GetString());
        Assert.Equal("refs/heads/artifact/myregistry/pdf/1.0.0-abcd1234", pr.GetProperty("sourceRefName").GetString());
    }

    [Fact] public async Task Empty_pull_request_is_refused_before_anything_is_written()
    {
        var h = Server(new() { ["artifacts/myregistry/pdf/SKILL.md"] = "identique" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AzureDevOpsProvider(new HttpClient(h)).CreatePullRequestAsync(Repo, Pr(("artifacts/myregistry/pdf/SKILL.md", "identique")), "pat"));
        Assert.Contains("vide", ex.Message);
        Assert.DoesNotContain(h.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact] public async Task Complete_merges_with_squash_and_deletes_the_source_branch()
    {
        var h = Server();
        await new AzureDevOpsProvider(new HttpClient(h)).CompletePullRequestAsync(Repo, 42, "pat");
        var patch = JsonDocument.Parse(h.Calls.Single(c => c.Method.Method == "PATCH").Body!).RootElement;
        Assert.Equal("completed", patch.GetProperty("status").GetString());
        Assert.Equal("feedface", patch.GetProperty("lastMergeSourceCommit").GetProperty("commitId").GetString());
        Assert.Equal("squash", patch.GetProperty("completionOptions").GetProperty("mergeStrategy").GetString());
        Assert.True(patch.GetProperty("completionOptions").GetProperty("deleteSourceBranch").GetBoolean());
    }

    [Fact] public async Task Complete_is_idempotent_and_refuses_an_abandoned_pr()
    {
        var done = Server(prStatus: "completed");
        await new AzureDevOpsProvider(new HttpClient(done)).CompletePullRequestAsync(Repo, 42, "pat");
        Assert.DoesNotContain(done.Calls, c => c.Method.Method == "PATCH");

        var abandoned = Server(prStatus: "abandoned");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AzureDevOpsProvider(new HttpClient(abandoned)).CompletePullRequestAsync(Repo, 42, "pat"));
    }

    [Fact] public async Task Abandon_patches_the_status()
    {
        var h = Server();
        await new AzureDevOpsProvider(new HttpClient(h)).AbandonPullRequestAsync(Repo, 42, "pat");
        Assert.Equal("abandoned", JsonDocument.Parse(h.Calls.Single(c => c.Method.Method == "PATCH").Body!).RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refusé")]
    [InlineData(HttpStatusCode.Forbidden, "refusé")]
    [InlineData(HttpStatusCode.NotFound, "Introuvable")]
    public async Task Errors_are_explained_without_leaking_the_token(HttpStatusCode code, string expected)
    {
        var p = new AzureDevOpsProvider(new HttpClient(new FakeHttp().On(HttpMethod.Get, "/refs", _ => FakeHttp.Status(code))));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => p.ReadRepoAsync(Repo, "main", "SECRET-PAT"));
        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("SECRET-PAT", ex.Message);
    }

    [Fact] public async Task Direct_commits_and_invalid_urls_are_refused()
    {
        var p = new AzureDevOpsProvider(new HttpClient(Server()));
        await Assert.ThrowsAsync<NotSupportedException>(() => p.CommitFilesAsync(Repo, new Dictionary<string, string>(), "m"));
        await Assert.ThrowsAsync<ArgumentException>(() => p.ReadRepoAsync("https://github.com/o/r", "main", null));
    }
}
