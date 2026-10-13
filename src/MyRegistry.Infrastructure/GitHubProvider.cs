using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MyRegistry.Application;

namespace MyRegistry.Infrastructure;

/// <summary>GitHub en lecture seule (API REST). Lecture d'un arbre complet puis des blobs ; l'écriture est refusée.</summary>
public sealed class GitHubProvider(HttpClient http) : IGitProvider
{
    public const long MaxFileBytes = 1_000_000;
    public const int MaxFiles = 3000;

    private HttpRequestMessage Req(string path, string? token)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com" + path);
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        r.Headers.UserAgent.Add(new ProductInfoHeaderValue("MyRegistry", "1.0"));
        r.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token)) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    private async Task<JsonElement> GetJsonAsync(string path, string? token, CancellationToken ct)
    {
        using var res = await http.SendAsync(Req(path, token), ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var remaining = res.Headers.TryGetValues("X-RateLimit-Remaining", out var v) ? v.FirstOrDefault() : null;
            throw new InvalidOperationException(remaining == "0" ? "Limite de requêtes GitHub atteinte : réessayez plus tard ou configurez un jeton." : $"Accès GitHub refusé ({(int)res.StatusCode}) : vérifiez le jeton.");
        }
        if (res.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("Dépôt ou branche introuvable sur GitHub (ou jeton sans accès).");
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Erreur GitHub {(int)res.StatusCode}.");
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    public async Task<RepoSnapshot> ReadRepoAsync(string repoLocation, string? branch, string? token, CancellationToken ct = default)
    {
        var repo = GitUrls.ParseGitHub(repoLocation) ?? throw new ArgumentException("URL GitHub invalide.", nameof(repoLocation));
        var b = string.IsNullOrWhiteSpace(branch) ? "main" : branch!;
        var info = await GetJsonAsync($"/repos/{repo.Owner}/{repo.Repo}/branches/{Uri.EscapeDataString(b)}", token, ct);
        var head = info.GetProperty("commit").GetProperty("sha").GetString()!;

        var tree = await GetJsonAsync($"/repos/{repo.Owner}/{repo.Repo}/git/trees/{head}?recursive=1", token, ct);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var skipped = new List<string>();
        if (tree.TryGetProperty("truncated", out var t) && t.GetBoolean()) skipped.Add("(arbre tronqué par GitHub : dépôt trop volumineux)");

        foreach (var e in tree.GetProperty("tree").EnumerateArray())
        {
            if (e.GetProperty("type").GetString() != "blob") continue;
            var path = e.GetProperty("path").GetString()!;
            var size = e.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            if (size > MaxFileBytes || files.Count >= MaxFiles) { skipped.Add(path); continue; }
            var blob = await GetJsonAsync($"/repos/{repo.Owner}/{repo.Repo}/git/blobs/{e.GetProperty("sha").GetString()}", token, ct);
            files[path] = blob.GetProperty("encoding").GetString() == "base64"
                ? Convert.FromBase64String(blob.GetProperty("content").GetString()!.Replace("\n", ""))
                : System.Text.Encoding.UTF8.GetBytes(blob.GetProperty("content").GetString()!);
        }
        return new(head, files, skipped);
    }

    public Task<GitCommitResult> CommitFilesAsync(string repoLocation, IReadOnlyDictionary<string, string> files, string message, CancellationToken ct = default) =>
        throw new NotSupportedException("GitHub est en lecture seule dans MyRegistry.");
}
