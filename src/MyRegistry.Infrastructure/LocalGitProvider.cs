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
