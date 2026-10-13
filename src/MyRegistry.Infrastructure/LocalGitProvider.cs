using System.Diagnostics;
using MyRegistry.Application;

namespace MyRegistry.Infrastructure;

/// <summary>
/// Écrit dans un dépôt Git local via le binaire `git` (arguments passés en liste : aucune interprétation par un shell).
/// Utilisé pour le snapshot de la marketplace ; les fournisseurs distants (Azure DevOps, GitHub) arrivent en phase 5.
/// </summary>
public sealed class LocalGitProvider : IGitProvider
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const string AuthorName = "MyRegistry";
    private const string AuthorEmail = "myregistry@localhost";

    public async Task<GitCommitResult> CommitFilesAsync(string repoLocation, IReadOnlyDictionary<string, string> files, string message, CancellationToken ct = default)
    {
        var path = RepoLocation.ToLocalPath(repoLocation) ?? throw new ArgumentException("Dépôt local invalide.", nameof(repoLocation));
        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(path);
            if (!Directory.Exists(Path.Combine(path, ".git"))) await Git(path, ct, "init", "-q", "-b", "main");

            foreach (var (rel, content) in files)
            {
                if (Path.IsPathRooted(rel) || rel.Split('/', '\\').Contains("..")) throw new ArgumentException($"Chemin invalide : {rel}");
                var full = Path.Combine(path, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content, new System.Text.UTF8Encoding(false), ct);
            }

            await Git(path, ct, "add", "-A");
            if ((await Git(path, ct, "status", "--porcelain")).Trim().Length == 0) return new(false, await HeadAsync(path, ct));
            await Git(path, ct, "-c", $"user.name={AuthorName}", "-c", $"user.email={AuthorEmail}", "commit", "-q", "-m", message);
            return new(true, await HeadAsync(path, ct));
        }
        finally { Gate.Release(); }
    }

    public const long MaxFileBytes = 1_000_000;
    public const int MaxFiles = 3000;

    public async Task<RepoSnapshot> ReadRepoAsync(string repoLocation, string? branch, string? token, CancellationToken ct = default)
    {
        var path = RepoLocation.ToLocalPath(repoLocation) ?? throw new ArgumentException("Dépôt local invalide.", nameof(repoLocation));
        if (!Directory.Exists(Path.Combine(path, ".git"))) throw new InvalidOperationException($"Aucun dépôt Git dans {path}.");
        var rev = string.IsNullOrWhiteSpace(branch) ? "HEAD" : branch!;
        if (rev.StartsWith('-')) throw new ArgumentException("Branche invalide.", nameof(branch));

        var head = (await Git(path, ct, "rev-parse", "--verify", rev + "^{commit}")).Trim();
        // « -l » donne la taille ; « -z » protège les noms de fichiers exotiques.
        var listing = await Git(path, ct, "ls-tree", "-r", "-l", "-z", head);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var entry in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = entry.IndexOf('\t');
            if (tab < 0) continue;
            var meta = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var name = entry[(tab + 1)..];
            if (meta.Length < 4 || meta[1] != "blob") continue; // pas les sous-modules ni les liens
            if (!long.TryParse(meta[3], out var size) || size > MaxFileBytes || files.Count >= MaxFiles) { skipped.Add(name); continue; }
            files[name] = await GitBytes(path, ct, "show", $"{head}:{name}");
        }
        return new(head, files, skipped);
    }

    private static async Task<byte[]> GitBytes(string workDir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git introuvable.");
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var err = p.StandardError.ReadToEndAsync(ct);
        await copy;
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {args[0]} a échoué : {(await err).Trim()}");
        return ms.ToArray();
    }

    private static async Task<string?> HeadAsync(string path, CancellationToken ct)
    {
        try { return (await Git(path, ct, "rev-parse", "HEAD")).Trim(); } catch (InvalidOperationException) { return null; }
    }

    private static async Task<string> Git(string workDir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git introuvable.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {args[0]} a échoué : {(await stderr).Trim()}");
        return await stdout;
    }
}
