using System.IO.Compression;
using MyRegistry.Domain;

namespace MyRegistry.Application;

public sealed record PackageLimits(int MaxEntries = 200, long MaxTotalBytes = 5_000_000, long MaxFileBytes = 1_000_000);

public sealed record PackageReadResult(IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<string> Errors, IReadOnlyList<string> Ignored);

public static class ArtifactLayout
{
    /// <summary>Fichier principal d'un artefact selon son type (même règle que la CLI).</summary>
    public static string? MainFile(ArtifactType type, IEnumerable<string> paths)
    {
        var list = paths.ToList();
        return type switch
        {
            ArtifactType.Skill => list.Contains("SKILL.md") ? "SKILL.md" : null,
            ArtifactType.Hook => list.Contains("hooks.json") ? "hooks.json" : null,
            ArtifactType.McpServer => list.Contains("mcp.json") ? "mcp.json" : list.Contains("package.json") ? "package.json" : null,
            ArtifactType.Agent => list.FirstOrDefault(p => p.EndsWith(".agent.md")) ?? (list.Contains("AGENTS.md") ? "AGENTS.md" : null),
            ArtifactType.Prompt => list.FirstOrDefault(p => p.EndsWith(".prompt.md")),
            ArtifactType.Plugin => list.FirstOrDefault(p => PluginRulesPaths.Contains(p)),
            _ => null,
        };
    }

    private static readonly string[] PluginRulesPaths = [".claude-plugin/plugin.json", ".github/plugin/plugin.json", ".codex-plugin/plugin.json"];
}

public static class ZipPackage
{
    private static readonly string[] Junk = ["__MACOSX/", ".DS_Store", "Thumbs.db", ".git/", "node_modules/"];

    /// <summary>
    /// Lit une archive d'artefact en mémoire avec des limites strictes : nombre d'entrées, taille totale décompressée (anti « zip bomb »),
    /// taille par fichier, chemins absolus ou « .. » refusés (anti « zip slip »). Si tout est dans un unique dossier racine, il est retiré.
    /// </summary>
    public static PackageReadResult Read(byte[] zipBytes, PackageLimits? limits = null)
    {
        limits ??= new();
        var errors = new List<string>();
        var ignored = new List<string>();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > limits.MaxEntries) return new(files, [$"Archive trop volumineuse : plus de {limits.MaxEntries} entrées."], ignored);
            if (zip.Entries.Sum(e => e.Length) > limits.MaxTotalBytes) return new(files, [$"Archive trop volumineuse : plus de {limits.MaxTotalBytes / 1_000_000} Mo décompressés."], ignored);

            long total = 0;
            foreach (var e in zip.Entries)
            {
                var path = e.FullName.Replace('\\', '/');
                if (path.EndsWith('/')) continue; // dossier
                if (Junk.Any(j => path.StartsWith(j, StringComparison.Ordinal) || path.Contains("/" + j) || path.EndsWith(j.TrimEnd('/'), StringComparison.Ordinal) && j == ".DS_Store")) { ignored.Add(path); continue; }
                if (path.StartsWith('/') || path.Split('/').Any(s => s == ".." || s.Length == 0) || path.Contains(':'))
                { errors.Add($"Chemin refusé dans l'archive : {e.FullName}"); continue; }
                if (e.Length > limits.MaxFileBytes) { errors.Add($"Fichier trop gros (> {limits.MaxFileBytes / 1000} Ko) : {path}"); continue; }

                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                total += ms.Length;
                if (total > limits.MaxTotalBytes) return new(new Dictionary<string, byte[]>(), [$"Archive trop volumineuse : plus de {limits.MaxTotalBytes / 1_000_000} Mo décompressés."], ignored);
                files[path] = ms.ToArray();
            }
        }
        catch (InvalidDataException) { return new(files, ["Archive ZIP illisible."], ignored); }

        if (errors.Count > 0) return new(new Dictionary<string, byte[]>(), errors, ignored);
        return new(StripSingleRoot(files), [], ignored);
    }

    /// <summary>Si tous les fichiers sont sous un même dossier racine (cas d'un dossier compressé), on retire ce préfixe.</summary>
    public static IReadOnlyDictionary<string, byte[]> StripSingleRoot(IReadOnlyDictionary<string, byte[]> files)
    {
        if (files.Count == 0 || files.Keys.Any(k => !k.Contains('/'))) return files;
        var roots = files.Keys.Select(k => k[..k.IndexOf('/')]).Distinct().ToList();
        return roots.Count == 1 ? files.ToDictionary(f => f.Key[(roots[0].Length + 1)..], f => f.Value, StringComparer.Ordinal) : files;
    }

    /// <summary>Nom du dossier racine d'une archive (avant retrait), utile pour proposer un nom d'artefact.</summary>
    public static string? RootFolder(byte[] zipBytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            var roots = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).Where(p => p.Contains('/') && !p.StartsWith("__MACOSX/"))
                .Select(p => p[..p.IndexOf('/')]).Distinct().ToList();
            return roots.Count == 1 ? roots[0] : null;
        }
        catch (InvalidDataException) { return null; }
    }
}
