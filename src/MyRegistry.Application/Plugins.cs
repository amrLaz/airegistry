using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Application;

public sealed record ComponentRef(ArtifactType Type, string Slug, string Version);

public static class PluginRules
{
    public const int MaxComponents = 50;

    public static readonly string[] ManifestPaths = [".claude-plugin/plugin.json", ".github/plugin/plugin.json", ".codex-plugin/plugin.json"];

    /// <summary>Contrôles de forme de la composition : SemVer exact, pas de plugin imbriqué, pas de doublon, pas d'auto-référence.</summary>
    public static IReadOnlyList<Issue> ValidateRefs(string pluginSlug, IReadOnlyList<ComponentRef> refs)
    {
        var errors = new List<Issue>();
        if (refs.Count == 0) errors.Add(new("no-components", "Un plugin doit contenir au moins un composant."));
        if (refs.Count > MaxComponents) errors.Add(new("too-many-components", $"Au plus {MaxComponents} composants."));
        foreach (var r in refs)
        {
            var parts = r.Slug.Split('/');
            if (parts.Length != 2 || parts.Any(p => p.Length == 0)) errors.Add(new("component-slug", $"Slug invalide : « {r.Slug} » (attendu : namespace/nom)."));
            if (r.Type == ArtifactType.Plugin) errors.Add(new("component-type", $"Un plugin ne peut pas contenir un autre plugin ({r.Slug})."));
            if (!VersionRules.IsSemVer(r.Version)) errors.Add(new("component-version", $"Version SemVer exacte requise pour {r.Slug} (reçu « {r.Version} »)."));
            if (string.Equals(r.Slug, pluginSlug, StringComparison.OrdinalIgnoreCase)) errors.Add(new("component-self", "Un plugin ne peut pas se contenir lui-même."));
        }
        foreach (var dup in refs.GroupBy(r => (r.Type, r.Slug.ToLowerInvariant())).Where(g => g.Count() > 1))
            errors.Add(new("component-duplicate", $"Composant en double : {dup.Key.Item2}."));
        return errors;
    }

    public static string BuildManifest(string name, string? description, string version, IReadOnlyList<ComponentRef> refs)
    {
        var comps = new JsonArray();
        foreach (var r in refs.OrderBy(r => r.Type.ToString(), StringComparer.Ordinal).ThenBy(r => r.Slug, StringComparer.Ordinal))
            comps.Add(new JsonObject { ["type"] = r.Type.ToString(), ["slug"] = r.Slug, ["version"] = r.Version });
        var m = new JsonObject { ["name"] = name, ["version"] = version, ["x-myregistry-components"] = comps };
        if (!string.IsNullOrWhiteSpace(description)) m["description"] = description;
        return m.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static IReadOnlyList<ComponentRef> ReadManifest(string manifestJson)
    {
        var list = new List<ComponentRef>();
        var root = JsonNode.Parse(manifestJson) as JsonObject;
        if (root?["x-myregistry-components"] is not JsonArray arr) return list;
        foreach (var n in arr.OfType<JsonObject>())
            if (Enum.TryParse<ArtifactType>((string?)n["type"], true, out var t) && (string?)n["slug"] is { } s && (string?)n["version"] is { } v)
                list.Add(new(t, s, v));
        return list;
    }

    /// <summary>
    /// Assemble l'archive installable d'un plugin. Disposition : skills/{nom}/…, agents/{nom}.md, prompts/{nom}.prompt.md,
    /// hooks/hooks.json (fusion des événements), .mcp.json (fusion des serveurs), plus le manifeste dans les trois dossiers runtime.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> BuildBundle(string manifestJson, IEnumerable<(ComponentRef Ref, IReadOnlyDictionary<string, byte[]> Files)> components)
    {
        var bundle = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var p in ManifestPaths) bundle[p] = Encoding.UTF8.GetBytes(manifestJson);

        var hookEvents = new JsonObject();
        var mcpServers = new JsonObject();
        foreach (var (r, files) in components)
        {
            var name = r.Slug.Split('/').Last();
            switch (r.Type)
            {
                case ArtifactType.Skill:
                    foreach (var (path, data) in files) bundle[$"skills/{name}/{path}"] = data;
                    break;
                case ArtifactType.Agent:
                    if (files.FirstOrDefault(f => f.Key.EndsWith(".agent.md") || f.Key == "AGENTS.md") is { Value: not null } a)
                        bundle[$"agents/{name}.md"] = a.Value;
                    break;
                case ArtifactType.Prompt:
                    if (files.FirstOrDefault(f => f.Key.EndsWith(".prompt.md")) is { Value: not null } pr)
                        bundle[$"prompts/{name}.prompt.md"] = pr.Value;
                    break;
                case ArtifactType.Hook:
                    if (files.TryGetValue("hooks.json", out var hj) && JsonNode.Parse(hj) is JsonObject ho && ho["hooks"] is JsonObject events)
                        foreach (var (ev, handlers) in events)
                        {
                            if (hookEvents[ev] is not JsonArray target) hookEvents[ev] = target = new JsonArray();
                            if (handlers is JsonArray src) foreach (var h in src) target.Add(h?.DeepClone());
                        }
                    break;
                case ArtifactType.McpServer:
                    if (Exports.ClaudeDesktop(name, Encoding.UTF8.GetString(files.TryGetValue("mcp.json", out var mj) ? mj : [])) is { } cfg
                        && JsonNode.Parse(cfg)?["mcpServers"]?[name] is { } entry)
                        mcpServers[name] = entry.DeepClone();
                    break;
            }
        }
        var indent = new JsonSerializerOptions { WriteIndented = true };
        if (hookEvents.Count > 0) bundle["hooks/hooks.json"] = Encoding.UTF8.GetBytes(new JsonObject { ["hooks"] = hookEvents }.ToJsonString(indent));
        if (mcpServers.Count > 0) bundle[".mcp.json"] = Encoding.UTF8.GetBytes(new JsonObject { ["mcpServers"] = mcpServers }.ToJsonString(indent));
        return bundle;
    }
}

public sealed record ComponentUpdate(string Slug, ArtifactType Type, string Pinned, string Latest);

public static class PluginUpdates
{
    /// <summary>Composants pour lesquels une version plus récente (SemVer) existe : badge « mise à jour disponible ».</summary>
    public static IReadOnlyList<ComponentUpdate> Find(IEnumerable<ComponentRef> pinned, IReadOnlyDictionary<string, IReadOnlyList<string>> approvedBySlug) =>
        pinned.Select(p => (p, latest: approvedBySlug.TryGetValue(p.Slug, out var vs)
                ? vs.Where(VersionRules.IsSemVer).OrderBy(v => v, Comparer<string>.Create(VersionRules.Compare)).LastOrDefault() : null))
            .Where(x => x.latest is not null && VersionRules.IsSemVer(x.p.Version) && VersionRules.Compare(x.latest, x.p.Version) > 0)
            .Select(x => new ComponentUpdate(x.p.Slug, x.p.Type, x.p.Version, x.latest!)).ToList();
}

public static class MarketplaceManifest
{
    public sealed record Entry(string Namespace, string Name, string Version, string? Description, string[] Tags);

    /// <summary>Manifeste /marketplace.json : plugins approuvés ; source de type « archive » (URL HTTPS de téléchargement).</summary>
    public static string Build(string title, string baseUrl, IEnumerable<Entry> plugins)
    {
        var arr = new JsonArray();
        foreach (var p in plugins.OrderBy(p => p.Namespace, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            var o = new JsonObject
            {
                ["name"] = $"{p.Namespace}-{p.Name}",
                ["version"] = p.Version,
                ["source"] = new JsonObject
                {
                    ["source"] = "archive",
                    ["url"] = $"{baseUrl.TrimEnd('/')}/api/plugins/{p.Namespace}/{p.Name}/download?version={Uri.EscapeDataString(p.Version)}",
                },
            };
            if (!string.IsNullOrWhiteSpace(p.Description)) o["description"] = p.Description;
            if (p.Tags.Length > 0) o["tags"] = new JsonArray(p.Tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
            arr.Add(o);
        }
        var root = new JsonObject
        {
            ["name"] = NameRules.Canonicalize(title) is { Length: > 0 } n ? n : "myregistry",
            ["owner"] = new JsonObject { ["name"] = title },
            ["metadata"] = new JsonObject { ["description"] = $"Plugins approuvés de {title}", ["version"] = "1" },
            ["plugins"] = arr,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
