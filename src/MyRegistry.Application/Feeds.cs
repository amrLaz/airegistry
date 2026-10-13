using System.Text;
using System.Text.Json;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Application;

public sealed record DiscoveredArtifact(ArtifactType Type, string RawName, string GitPath, IReadOnlyDictionary<string, byte[]> Files, string MainFile);

public static class RepoDiscovery
{
    private static readonly string[] Ignored = [".git/", "node_modules/", ".github/workflows/"];

    private static string Dir(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static bool Under(string path, string dir) => dir.Length == 0 || path.StartsWith(dir + "/", StringComparison.Ordinal);
    private static string Rel(string path, string dir) => dir.Length == 0 ? path : path[(dir.Length + 1)..];

    private static string? Fm(IReadOnlyDictionary<string, byte[]> files, string path, string key) =>
        files.TryGetValue(path, out var b) && Frontmatter.Parse(Encoding.UTF8.GetString(b)) is { } fm && fm.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static string? JsonName(IReadOnlyDictionary<string, byte[]> files, string path)
    {
        try { return JsonDocument.Parse(files[path]).RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null; }
        catch (JsonException) { return null; }
    }

    private static string DirName(string dir, string fallback) => dir.Length == 0 ? fallback : dir[(dir.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Détecte les artefacts d'un dépôt : plugin (manifeste runtime), skill (SKILL.md), hook (hooks.json), serveur MCP (mcp.json),
    /// agent (*.agent.md, AGENTS.md) et prompt (*.prompt.md). Un fichier n'est attribué qu'à un seul artefact (priorité dans cet ordre).
    /// </summary>
    public static IReadOnlyList<DiscoveredArtifact> Discover(IReadOnlyDictionary<string, byte[]> repoFiles, string repoName = "repo")
    {
        var files = repoFiles.Where(f => !Ignored.Any(i => f.Key.StartsWith(i, StringComparison.Ordinal) || f.Key.Contains("/" + i)))
            .ToDictionary(f => f.Key.Replace('\\', '/'), f => f.Value);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DiscoveredArtifact>();

        void Add(ArtifactType type, string name, string root, string main, IEnumerable<string> paths)
        {
            var list = paths.Where(p => claimed.Add(p)).ToList();
            if (list.Count == 0) return;
            result.Add(new(type, name, root.Length == 0 ? "." : root, list.ToDictionary(p => Rel(p, root), p => files[p]), Rel(main, root)));
        }

        // 1. Plugins : la racine est le parent du dossier runtime (.claude-plugin, .github/plugin, .codex-plugin).
        foreach (var manifest in files.Keys.Where(k => PluginRules.ManifestPaths.Any(m => k == m || k.EndsWith("/" + m))).OrderBy(k => k, StringComparer.Ordinal))
        {
            var suffix = PluginRules.ManifestPaths.First(m => manifest == m || manifest.EndsWith("/" + m));
            var root = manifest.Length == suffix.Length ? "" : manifest[..^(suffix.Length + 1)];
            if (result.Any(r => r.Type == ArtifactType.Plugin && r.GitPath == (root.Length == 0 ? "." : root))) continue;
            Add(ArtifactType.Plugin, JsonName(files, manifest) ?? DirName(root, repoName), root, manifest, files.Keys.Where(p => Under(p, root)));
        }

        // 2. Skills, 3. hooks, 4. serveurs MCP : un dossier, tous ses fichiers non encore attribués.
        foreach (var p in files.Keys.Where(k => FileName(k) == "SKILL.md").OrderBy(k => k, StringComparer.Ordinal))
        {
            var root = Dir(p);
            Add(ArtifactType.Skill, Fm(files, p, "name") ?? DirName(root, repoName), root, p, files.Keys.Where(k => Under(k, root)));
        }
        foreach (var p in files.Keys.Where(k => FileName(k) == "hooks.json").OrderBy(k => k, StringComparer.Ordinal))
        {
            var root = Dir(p);
            var readme = root.Length == 0 ? "README.md" : root + "/README.md";
            Add(ArtifactType.Hook, Fm(files, readme, "name") ?? DirName(root, repoName), root, p, files.Keys.Where(k => Under(k, root)));
        }
        foreach (var p in files.Keys.Where(k => FileName(k) == "mcp.json").OrderBy(k => k, StringComparer.Ordinal))
        {
            var root = Dir(p);
            Add(ArtifactType.McpServer, JsonName(files, p) ?? DirName(root, repoName), root, p, files.Keys.Where(k => Under(k, root)));
        }

        // 5. Agents et prompts : fichiers simples.
        foreach (var p in files.Keys.Where(k => !claimed.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            var name = FileName(p);
            if (name.EndsWith(".agent.md")) Add(ArtifactType.Agent, Fm(files, p, "name") ?? name[..^".agent.md".Length], Dir(p), p, [p]);
            else if (name == "AGENTS.md") Add(ArtifactType.Agent, Fm(files, p, "name") ?? DirName(Dir(p), repoName), Dir(p), p, [p]);
            else if (name.EndsWith(".prompt.md")) Add(ArtifactType.Prompt, name[..^".prompt.md".Length], Dir(p), p, [p]);
        }

        return result.OrderBy(r => r.Type.ToString(), StringComparer.Ordinal).ThenBy(r => r.GitPath, StringComparer.Ordinal).ToList();
    }
}

public enum FeedKind { AzureDevOps, GitHub, Local }

public static class FeedRules
{
    public const int MaxConsecutiveFailures = 3;

    /// <summary>Namespace par défaut d'un feed : namespace du registre + suffixe fournisseur (-gh, -azd) ; ≤ 32 caractères.</summary>
    public static string DefaultNamespace(string registryNamespace, FeedKind kind)
    {
        var suffix = kind switch { FeedKind.GitHub => "-gh", FeedKind.AzureDevOps => "-azd", _ => "-local" };
        var baseNs = NameRules.Canonicalize(registryNamespace);
        var max = 32 - suffix.Length;
        return (baseNs.Length > max ? baseNs[..max].TrimEnd('-') : baseNs) + suffix;
    }

    public static bool ShouldPause(int consecutiveFailures) => consecutiveFailures >= MaxConsecutiveFailures;

    /// <summary>Un feed actif est dû si sa dernière synchronisation date de plus de <paramref name="intervalMinutes"/> (ou n'a jamais eu lieu).</summary>
    public static bool IsDue(bool active, DateTime? lastSync, int intervalMinutes, DateTime now) =>
        active && (lastSync is null || now - lastSync.Value >= TimeSpan.FromMinutes(intervalMinutes));

    public sealed record FeedInput(string Name, FeedKind Kind, string Url, string? Branch, string? PatRef, int? IntervalMinutes, string? NamespaceOverride);

    private static readonly System.Text.RegularExpressions.Regex GitHubUrl = new(@"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?(\.git)?/?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex AzdoUrl = new(@"^https://dev\.azure\.com/[^/\s]+/[^/\s]+/_git/[^/\s]+/?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex PatName = new("^MYREGISTRY_[A-Z0-9_]{1,60}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex BranchName = new(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,98}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Intervalle par défaut : 60 min pour Azure DevOps (feed système), 1440 min pour GitHub (docs/01).</summary>
    public static int DefaultInterval(FeedKind kind) => kind == FeedKind.GitHub ? 1440 : 60;

    public static IReadOnlyList<Issue> Validate(FeedInput i)
    {
        var e = new List<Issue>();
        if (i.Name is null || i.Name.Trim().Length is < 2 or > 60) e.Add(new("name", "Le nom doit faire 2 à 60 caractères."));
        switch (i.Kind)
        {
            case FeedKind.GitHub when !GitHubUrl.IsMatch(i.Url ?? ""): e.Add(new("url", "URL GitHub attendue : https://github.com/propriétaire/dépôt")); break;
            case FeedKind.AzureDevOps when !AzdoUrl.IsMatch(i.Url ?? ""): e.Add(new("url", "URL Azure DevOps attendue : https://dev.azure.com/org/projet/_git/dépôt")); break;
            case FeedKind.Local when RepoLocation.ToLocalPath(i.Url) is null: e.Add(new("url", "Dépôt local : chemin absolu ou file:///chemin")); break;
        }
        if (!string.IsNullOrWhiteSpace(i.Branch) && !BranchName.IsMatch(i.Branch) || i.Branch?.Contains("..") == true) e.Add(new("branch", "Nom de branche invalide."));
        // Le « pat_ref » est le NOM d'une variable d'environnement, jamais le jeton ; limité à MYREGISTRY_* pour ne pas lire n'importe quelle variable.
        if (!string.IsNullOrWhiteSpace(i.PatRef) && !PatName.IsMatch(i.PatRef)) e.Add(new("pat-ref", "Référence de jeton : nom de variable MYREGISTRY_… (le jeton lui-même n'est jamais saisi ici)."));
        if (i.IntervalMinutes is < 5 or > 10080) e.Add(new("interval", "Intervalle : 5 à 10080 minutes."));
        if (!string.IsNullOrWhiteSpace(i.NamespaceOverride) && (i.NamespaceOverride.Length is < 2 or > 32 || NameRules.Canonicalize(i.NamespaceOverride) != i.NamespaceOverride))
            e.Add(new("namespace", "Namespace : 2 à 32 caractères, minuscules, chiffres, tirets simples."));
        return e;
    }
}
