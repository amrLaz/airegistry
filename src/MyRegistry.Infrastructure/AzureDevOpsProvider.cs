using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MyRegistry.Application;

namespace MyRegistry.Infrastructure;

/// <summary>Azure DevOps Repos en lecture/écriture : lecture d'un arbre, branche + push + pull request, fusion squash, abandon.</summary>
public sealed class AzureDevOpsProvider(HttpClient http) : IGitHostProvider
{
    private const string Api = "api-version=7.1";
    private const string ZeroId = "0000000000000000000000000000000000000000";

    private static string Base(GitUrls.AzdoRepo r) =>
        $"https://dev.azure.com/{Uri.EscapeDataString(r.Org)}/{Uri.EscapeDataString(r.Project)}/_apis/git/repositories/{Uri.EscapeDataString(r.Repo)}";

    private static GitUrls.AzdoRepo Parse(string url) => GitUrls.ParseAzdo(url) ?? throw new ArgumentException("URL Azure DevOps invalide.", nameof(url));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token, object? body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token)));
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await http.SendAsync(req, ct);
    }

    private static void Check(HttpResponseMessage res, string what)
    {
        if (res.IsSuccessStatusCode) return;
        throw new InvalidOperationException(res.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation => $"Accès Azure DevOps refusé pendant « {what} » ({(int)res.StatusCode}) : vérifiez le jeton et ses droits.",
            HttpStatusCode.NotFound => $"Introuvable sur Azure DevOps pendant « {what} » (dépôt, branche ou objet).",
            _ => $"Erreur Azure DevOps {(int)res.StatusCode} pendant « {what} ».",
        });
    }

    private async Task<JsonElement> JsonAsync(HttpMethod m, string url, string? token, object? body, string what, CancellationToken ct)
    {
        using var res = await SendAsync(m, url, token, body, ct);
        Check(res, what);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    private async Task<string> BranchHeadAsync(GitUrls.AzdoRepo r, string branch, string? token, CancellationToken ct)
    {
        var refs = await JsonAsync(HttpMethod.Get, $"{Base(r)}/refs?filter=heads/{Uri.EscapeDataString(branch)}&{Api}", token, null, "lecture de la branche", ct);
        foreach (var e in refs.GetProperty("value").EnumerateArray())
            if (e.GetProperty("name").GetString() == $"refs/heads/{branch}") return e.GetProperty("objectId").GetString()!;
        throw new InvalidOperationException($"Branche introuvable : {branch}.");
    }

    public async Task<RepoSnapshot> ReadRepoAsync(string repoLocation, string? branch, string? token, CancellationToken ct = default)
    {
        var r = Parse(repoLocation);
        var b = string.IsNullOrWhiteSpace(branch) ? "main" : branch!;
        var head = await BranchHeadAsync(r, b, token, ct);
        var commit = await JsonAsync(HttpMethod.Get, $"{Base(r)}/commits/{head}?{Api}", token, null, "lecture du commit", ct);
        var treeId = commit.GetProperty("treeId").GetString()!;
        var tree = await JsonAsync(HttpMethod.Get, $"{Base(r)}/trees/{treeId}?recursive=true&{Api}", token, null, "lecture de l'arbre", ct);

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var skipped = new List<string>();
        if (tree.TryGetProperty("treeEntries", out var entries))
            foreach (var e in entries.EnumerateArray())
            {
                if (e.GetProperty("gitObjectType").GetString() != "blob") continue;
                var path = e.GetProperty("relativePath").GetString()!;
                var size = e.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                if (size > LocalGitProvider.MaxFileBytes || files.Count >= LocalGitProvider.MaxFiles) { skipped.Add(path); continue; }
                using var res = await SendAsync(HttpMethod.Get, $"{Base(r)}/blobs/{e.GetProperty("objectId").GetString()}?$format=octetstream&{Api}", token, null, ct);
                Check(res, $"lecture de {path}");
                files[path] = await res.Content.ReadAsByteArrayAsync(ct);
            }
        return new(head, files, skipped);
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> ReadFilesAsync(string repoUrl, string branch, IEnumerable<string> paths, string? token, CancellationToken ct = default)
    {
        var r = Parse(repoUrl);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            using var res = await SendAsync(HttpMethod.Get,
                $"{Base(r)}/items?path={Uri.EscapeDataString("/" + path.TrimStart('/'))}&versionDescriptor.version={Uri.EscapeDataString(branch)}&versionDescriptor.versionType=branch&$format=octetstream&download=true&{Api}", token, null, ct);
            if (res.StatusCode == HttpStatusCode.NotFound) continue;
            Check(res, $"lecture de {path}");
            result[path] = await res.Content.ReadAsByteArrayAsync(ct);
        }
        return result;
    }

    public async Task<PullRequestInfo> CreatePullRequestAsync(string repoUrl, NewPullRequest pr, string? token, CancellationToken ct = default)
    {
        var r = Parse(repoUrl);
        var baseHead = await BranchHeadAsync(r, pr.BaseBranch, token, ct);
        var existing = await ReadFilesAsync(repoUrl, pr.BaseBranch, pr.Files.Keys, token, ct);

        // PR vide refusée : rien ne change par rapport à la branche de base.
        if (pr.Files.All(f => existing.TryGetValue(f.Key, out var cur) && cur.AsSpan().SequenceEqual(f.Value)))
            throw new InvalidOperationException("Pull request vide : le contenu est identique à celui du dépôt.");

        // 1. créer la branche à partir de la branche de base ; 2. y pousser le commit ; 3. ouvrir la PR.
        var refRes = await JsonAsync(HttpMethod.Post, $"{Base(r)}/refs?{Api}", token,
            new[] { new { name = $"refs/heads/{pr.SourceBranch}", oldObjectId = ZeroId, newObjectId = baseHead } }, "création de la branche", ct);
        if (refRes.TryGetProperty("value", out var vals) && vals.GetArrayLength() > 0 && vals[0].TryGetProperty("success", out var ok) && !ok.GetBoolean())
            throw new InvalidOperationException("Création de la branche refusée (existe-t-elle déjà ?).");

        var changes = pr.Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new
        {
            changeType = existing.ContainsKey(f.Key) ? "edit" : "add",
            item = new { path = "/" + f.Key.TrimStart('/') },
            newContent = new { content = Convert.ToBase64String(f.Value), contentType = "base64encoded" },
        }).ToArray();
        await JsonAsync(HttpMethod.Post, $"{Base(r)}/pushes?{Api}", token, new
        {
            refUpdates = new[] { new { name = $"refs/heads/{pr.SourceBranch}", oldObjectId = baseHead } },
            commits = new[] { new { comment = pr.Title, changes } },
        }, "envoi du commit", ct);

        var created = await JsonAsync(HttpMethod.Post, $"{Base(r)}/pullrequests?{Api}", token, new
        {
            sourceRefName = $"refs/heads/{pr.SourceBranch}", targetRefName = $"refs/heads/{pr.BaseBranch}", title = pr.Title, description = pr.Description,
        }, "création de la pull request", ct);
        var id = created.GetProperty("pullRequestId").GetInt32();
        return new(id, $"https://dev.azure.com/{Uri.EscapeDataString(r.Org)}/{Uri.EscapeDataString(r.Project)}/_git/{Uri.EscapeDataString(r.Repo)}/pullrequest/{id}", pr.SourceBranch);
    }

    public async Task CompletePullRequestAsync(string repoUrl, int pullRequestId, string? token, CancellationToken ct = default)
    {
        var r = Parse(repoUrl);
        var pr = await JsonAsync(HttpMethod.Get, $"{Base(r)}/pullrequests/{pullRequestId}?{Api}", token, null, "lecture de la pull request", ct);
        var status = pr.GetProperty("status").GetString();
        if (status == "completed") return;
        if (status == "abandoned") throw new InvalidOperationException("La pull request a été abandonnée : elle ne peut plus être fusionnée.");
        var sourceHead = pr.GetProperty("lastMergeSourceCommit").GetProperty("commitId").GetString();
        await JsonAsync(new HttpMethod("PATCH"), $"{Base(r)}/pullrequests/{pullRequestId}?{Api}", token, new
        {
            status = "completed",
            lastMergeSourceCommit = new { commitId = sourceHead },
            completionOptions = new { mergeStrategy = "squash", deleteSourceBranch = true },
        }, "fusion de la pull request", ct);
    }

    public async Task AbandonPullRequestAsync(string repoUrl, int pullRequestId, string? token, CancellationToken ct = default)
    {
        var r = Parse(repoUrl);
        await JsonAsync(new HttpMethod("PATCH"), $"{Base(r)}/pullrequests/{pullRequestId}?{Api}", token, new { status = "abandoned" }, "abandon de la pull request", ct);
    }

    public Task<GitCommitResult> CommitFilesAsync(string repoLocation, IReadOnlyDictionary<string, string> files, string message, CancellationToken ct = default) =>
        throw new NotSupportedException("Azure DevOps écrit par pull request (CreatePullRequestAsync), pas par commit direct.");
}
