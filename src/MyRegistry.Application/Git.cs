using System.Security.Cryptography;
using System.Text;

namespace MyRegistry.Application;

public sealed record GitCommitResult(bool Changed, string? CommitSha);

/// <summary>Contenu d'un dépôt à un commit : fichiers texte et binaires de taille raisonnable (chemin relatif → octets).</summary>
public sealed record RepoSnapshot(string HeadCommit, IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<string> Skipped);

/// <summary>
/// Fournisseur Git (docs/02 : IGitProvider). Pour l'instant : écriture de fichiers dans un dépôt local ;
/// Azure DevOps (lecture/écriture) et GitHub (lecture seule) viendront en phase 5.
/// </summary>
public interface IGitProvider
{
    /// <summary>Écrit les fichiers (chemin relatif → contenu) dans le dépôt et commit s'il y a un changement.</summary>
    /// <summary>Lit l'état d'une branche (ou HEAD). Les fichiers trop gros sont ignorés et listés dans <c>Skipped</c>.</summary>
    Task<RepoSnapshot> ReadRepoAsync(string repoLocation, string? branch, string? token, CancellationToken ct = default);

    Task<GitCommitResult> CommitFilesAsync(string repoLocation, IReadOnlyDictionary<string, string> files, string message, CancellationToken ct = default);
}

public static class RepoLocation
{
    /// <summary>
    /// Accepte « file:///chemin/absolu » ou un chemin absolu local. Retourne le chemin ou null si la valeur n'est pas un dépôt local sûr
    /// (relatif, racine, segments « .. », schéma distant).
    /// </summary>
    public static string? ToLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        string path;
        if (v.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(v, UriKind.Absolute, out var u) || !u.IsFile) return null;
            if (u.Host.Length > 0 && !u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return null; // pas de partage distant
            path = Uri.UnescapeDataString(u.AbsolutePath);
            if (OperatingSystem.IsWindows() && path.StartsWith('/')) path = path.TrimStart('/');
        }
        else if (v.Contains("://")) return null;
        else path = v;

        if (!Path.IsPathRooted(path)) return null;
        if (path.Split('/', '\\').Any(s => s == "..")) return null;
        var full = Path.GetFullPath(path);
        return full == Path.GetPathRoot(full) ? null : full.TrimEnd(Path.DirectorySeparatorChar);
    }

    public static string ToFileUrl(string localPath) => new Uri(localPath).AbsoluteUri;
}

public static class MarketplaceSnapshot
{
    /// <summary>Un manifeste lu par chaque agent depuis son dossier : VS Code/Copilot, Claude Code, Codex (.agents), Cursor.</summary>
    public static readonly string[] ManifestPaths =
    [
        ".github/plugin/marketplace.json",
        ".claude-plugin/marketplace.json",
        ".agents/plugins/marketplace.json",
        ".cursor-plugin/marketplace.json",
    ];

    public static IReadOnlyDictionary<string, string> Files(string manifestJson) =>
        ManifestPaths.ToDictionary(p => p, _ => manifestJson);

    public static string Digest(string manifestJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson))).ToLowerInvariant();
}
