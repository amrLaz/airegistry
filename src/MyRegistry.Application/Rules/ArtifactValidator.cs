using System.Text.Json;
using MyRegistry.Domain;

namespace MyRegistry.Application.Rules;

public enum McpTransport { Stdio, Http, Docker, Npx, Uvx }

public static class McpTransportDetector
{
    public static McpTransport Detect(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object)
            foreach (var s in servers.EnumerateObject()) { root = s.Value; break; }

        string? Str(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var transport = Str("transport")?.ToLowerInvariant();
        if (transport is "http" or "sse" or "streamable-http" || Str("url") is not null) return McpTransport.Http;
        if (root.TryGetProperty("docker", out _) || root.TryGetProperty("image", out _)) return McpTransport.Docker;
        var cmd = Str("command")?.ToLowerInvariant() ?? "";
        if (cmd.Contains("npx")) return McpTransport.Npx;
        if (cmd is "uvx" or "uv" || cmd.EndsWith("/uvx") || cmd.EndsWith("/uv")) return McpTransport.Uvx;
        return McpTransport.Stdio;
    }
}

public static class ArtifactTypeDetector
{
    private static readonly string[] PluginManifests = [".claude-plugin/plugin.json", ".github/plugin/plugin.json", ".codex-plugin/plugin.json"];

    public static ArtifactType? Detect(IEnumerable<string> paths)
    {
        var p = paths.Select(x => x.Replace('\\', '/')).ToList();
        if (p.Any(x => PluginManifests.Contains(x))) return ArtifactType.Plugin;
        if (p.Contains("hooks.json")) return ArtifactType.Hook;
        if (p.Contains("SKILL.md")) return ArtifactType.Skill;
        if (p.Any(x => x.EndsWith(".agent.md") || x == "AGENTS.md")) return ArtifactType.Agent;
        if (p.Any(x => x.EndsWith(".prompt.md"))) return ArtifactType.Prompt;
        if (p.Contains("mcp.json") || p.Contains("package.json")) return ArtifactType.McpServer;
        return null;
    }
}

public static class ArtifactValidator
{
    public static ValidationResult Validate(ArtifactType type, IReadOnlyDictionary<string, string> files)
    {
        var e = new List<Issue>();
        var w = new List<Issue>();
        switch (type)
        {
            case ArtifactType.Skill: Skill(files, e, w); break;
            case ArtifactType.McpServer: Mcp(files, e, w); break;
            case ArtifactType.Agent: Agent(files, e, w); break;
            case ArtifactType.Prompt: Prompt(files, e); break;
            case ArtifactType.Hook: Hook(files, e); break;
            case ArtifactType.Plugin: Plugin(files, e, w); break;
        }
        return new(e, w);
    }

    private static void RequireFields(Dictionary<string, string> fm, string file, List<Issue> e, params string[] keys)
    {
        foreach (var k in keys)
            if (!fm.TryGetValue(k, out var v) || (k is "name" or "description" or "agent" && string.IsNullOrWhiteSpace(v)))
                e.Add(new($"missing-{k}", $"Champ « {k} » manquant dans le frontmatter.", file));
    }

    private static void Skill(IReadOnlyDictionary<string, string> f, List<Issue> e, List<Issue> w)
    {
        if (!f.TryGetValue("SKILL.md", out var text)) { e.Add(new("missing-skill-md", "SKILL.md est requis à la racine.")); return; }
        var fm = Frontmatter.Parse(text);
        if (fm is null) e.Add(new("missing-frontmatter", "Frontmatter YAML absent.", "SKILL.md"));
        else
        {
            RequireFields(fm, "SKILL.md", e, "name", "description");
            if (fm.GetValueOrDefault("name", "").Length > 64) w.Add(new("name-too-long", "name dépasse 64 caractères.", "SKILL.md"));
            if (fm.GetValueOrDefault("description", "").Length > 1024) w.Add(new("description-too-long", "description dépasse 1024 caractères.", "SKILL.md"));
        }
        if (!f.ContainsKey("README.md")) w.Add(new("missing-readme", "README.md absent."));
    }

    private static void Mcp(IReadOnlyDictionary<string, string> f, List<Issue> e, List<Issue> w)
    {
        var file = f.ContainsKey("mcp.json") ? "mcp.json" : f.ContainsKey("package.json") ? "package.json" : null;
        if (file is null) { e.Add(new("missing-mcp-file", "mcp.json ou package.json requis.")); return; }
        JsonElement root;
        try { root = JsonDocument.Parse(f[file]).RootElement.Clone(); }
        catch (JsonException) { e.Add(new("invalid-json", "JSON invalide.", file)); return; }
        if (root.ValueKind != JsonValueKind.Object) { e.Add(new("invalid-json", "Objet JSON attendu.", file)); return; }
        if (root.TryGetProperty("mcpServers", out var servers) && servers.ValueKind == JsonValueKind.Object)
            foreach (var s in servers.EnumerateObject()) { root = s.Value; break; }

        bool Has(string k) => root.TryGetProperty(k, out _);
        // package.json : un paquet npm publié se lance via npx, donc `name` suffit.
        var launchable = file == "package.json" ? Has("name")
            : Has("command") || Has("url") || Has("docker") || Has("image");
        if (!launchable) e.Add(new("mcp-no-launch", "Aucun moyen de lancement : command+args, url ou docker.", file));
        if (!Has("name")) w.Add(new("missing-name", "name recommandé.", file));
        if (!Has("description")) w.Add(new("missing-description", "description recommandée.", file));
        if (Has("command") && !Has("args")) w.Add(new("missing-args", "args recommandé.", file));
    }

    private static void Agent(IReadOnlyDictionary<string, string> f, List<Issue> e, List<Issue> w)
    {
        var path = f.Keys.FirstOrDefault(k => k.EndsWith(".agent.md")) ?? (f.ContainsKey("AGENTS.md") ? "AGENTS.md" : null);
        if (path is null) { e.Add(new("missing-agent-file", "Un fichier *.agent.md ou AGENTS.md est requis.")); return; }
        var fm = Frontmatter.Parse(f[path]);
        if (fm is null)
        {
            var lines = f[path].Replace("\r\n", "\n").Split('\n');
            var hasH1 = lines.Any(l => l.StartsWith("# "));
            var hasPara = lines.Any(l => l.Trim().Length > 0 && !l.StartsWith("#"));
            if (!hasH1 || !hasPara) e.Add(new("missing-frontmatter", "Sans frontmatter : un titre H1 et un paragraphe sont requis.", path));
            else w.Add(new("no-frontmatter", "Frontmatter absent : titre et premier paragraphe utilisés.", path));
            return;
        }
        RequireFields(fm, path, e, "name", "description");
        if (!fm.ContainsKey("model")) w.Add(new("missing-model", "model recommandé.", path));
        if (!fm.ContainsKey("tools")) w.Add(new("missing-tools", "tools recommandé.", path));
    }

    private static void Prompt(IReadOnlyDictionary<string, string> f, List<Issue> e)
    {
        var path = f.Keys.FirstOrDefault(k => k.EndsWith(".prompt.md"));
        if (path is null) { e.Add(new("missing-prompt-file", "Un fichier *.prompt.md est requis.")); return; }
        var fm = Frontmatter.Parse(f[path]);
        if (fm is null) { e.Add(new("missing-frontmatter", "Frontmatter absent.", path)); return; }
        RequireFields(fm, path, e, "agent", "description");
    }

    private static void Hook(IReadOnlyDictionary<string, string> f, List<Issue> e)
    {
        if (!f.TryGetValue("hooks.json", out var json)) { e.Add(new("missing-hooks-json", "hooks.json requis.")); }
        else
        {
            try
            {
                var root = JsonDocument.Parse(json).RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Object)
                    e.Add(new("missing-hooks-property", "Propriété racine « hooks » (objet) requise.", "hooks.json"));
                else
                {
                    var events = hooks.EnumerateObject().ToList();
                    if (events.Count == 0) e.Add(new("hooks-empty", "Au moins un événement requis.", "hooks.json"));
                    foreach (var ev in events.Where(x => x.Value.ValueKind != JsonValueKind.Array))
                        e.Add(new("hooks-not-array", $"L'événement {ev.Name} doit être un tableau.", "hooks.json"));
                }
            }
            catch (JsonException) { e.Add(new("invalid-json", "JSON invalide.", "hooks.json")); }
        }
        if (!f.TryGetValue("README.md", out var readme)) { e.Add(new("missing-readme", "README.md requis.")); return; }
        var fm = Frontmatter.Parse(readme);
        if (fm is null) { e.Add(new("missing-frontmatter", "Frontmatter absent.", "README.md")); return; }
        RequireFields(fm, "README.md", e, "name", "description", "hooks", "tags");
    }

    private static void Plugin(IReadOnlyDictionary<string, string> f, List<Issue> e, List<Issue> w)
    {
        var path = new[] { ".claude-plugin/plugin.json", ".github/plugin/plugin.json", ".codex-plugin/plugin.json" }.FirstOrDefault(f.ContainsKey);
        if (path is null) { e.Add(new("missing-plugin-manifest", "Manifeste plugin.json requis dans un dossier runtime.")); return; }
        JsonElement root;
        try { root = JsonDocument.Parse(f[path]).RootElement.Clone(); }
        catch (JsonException) { e.Add(new("invalid-json", "JSON invalide.", path)); return; }

        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
        if (name.Length == 0 || NameRules.Canonicalize(name) != name) e.Add(new("name-format", "Nom de plugin invalide.", path));
        if (!root.TryGetProperty("description", out _)) w.Add(new("missing-description", "description manquante.", path));
        if (!root.TryGetProperty("version", out _)) w.Add(new("missing-version", "version manquante.", path));

        if (root.TryGetProperty("x-myregistry-components", out var comps) && comps.ValueKind == JsonValueKind.Array)
            foreach (var c in comps.EnumerateArray())
            {
                string? S(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (S("type") is null) e.Add(new("component-type", "type de composant manquant.", path));
                if (S("slug") is null) e.Add(new("component-slug", "slug de composant manquant.", path));
                var ver = S("version");
                if (ver is null || !VersionRules.IsSemVer(ver)) e.Add(new("component-version", "Version SemVer exacte requise pour chaque composant.", path));
            }
    }
}
