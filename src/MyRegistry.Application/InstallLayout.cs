using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Application;

public sealed record InstallFile(string Path, byte[] Data);
public sealed record InstallPlan(IReadOnlyList<InstallFile> Files, string? Error, IReadOnlyList<string>? Warnings = null);

/// <summary>Où l'archive installable place les fichiers selon l'agent cible (docs/04, « Emplacements d'installation »). Pur : ne lit ni n'écrit rien.</summary>
public static class InstallLayout
{
    // Chaîne YAML entre guillemets : JSON sans échappement HTML (les guillemets restent \", les accents restent lisibles).
    private static readonly JsonSerializerOptions YamlString = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static readonly string[] Targets = ["copilot", "claude", "codex", "cursor"];

    public static InstallPlan Plan(ArtifactType type, string name, string target, bool global, IReadOnlyDictionary<string, byte[]> files)
    {
        target = target.Trim().ToLowerInvariant();
        if (!Targets.Contains(target)) return Fail($"Cible inconnue « {target} » (copilot, claude ou codex).");
        var writes = new List<InstallFile>();
        var warnings = new List<string>();
        InstallPlan Done() => new(writes.OrderBy(w => w.Path, StringComparer.Ordinal).ToList(), null, warnings);
        InstallPlan Unsupported(string what) => Fail($"{what} : non supporté pour {target}{(global ? " en global" : "")}.");
        void Put(string path, byte[] data) => writes.Add(new(path, data));

        switch (type)
        {
            case ArtifactType.Plugin:
                return Fail("Les plugins ne s'installent pas par archive : utilisez /api/plugins/{ns}/{nom}/download et les commandes natives de l'agent.");
            case ArtifactType.Skill when target == "cursor":
            {
                // Cursor n'a pas de format « skill » : le skill devient une règle « demandée par l'agent » (description + corps).
                if (global) return Unsupported("Les skills");
                if (!files.TryGetValue("SKILL.md", out var skill)) return Fail("SKILL.md introuvable.");
                var text = Encoding.UTF8.GetString(skill);
                var description = Frontmatter.Parse(text)?.GetValueOrDefault("description") is { Length: > 0 } d ? d : name;
                var mdc = $"---\ndescription: {JsonSerializer.Serialize(description, YamlString)}\nalwaysApply: false\n---\n\n{Exports.StripFrontmatter(text).TrimEnd()}\n";
                Put($".cursor/rules/{name}.mdc", Encoding.UTF8.GetBytes(mdc));
                var extra = files.Count(f => f.Key != "SKILL.md");
                if (extra > 0) warnings.Add($"{extra} fichier(s) complémentaire(s) du skill non installés : Cursor ne charge que la règle (SKILL.md).");
                return Done();
            }
            case ArtifactType.Skill:
            {
                var baseDir = target switch { "copilot" => global ? ".agents/skills" : ".github/skills", "claude" => ".claude/skills", _ => ".codex/skills" };
                foreach (var (p, d) in files) Put($"{baseDir}/{name}/{p}", d);
                return Done();
            }
            case ArtifactType.Agent when target == "cursor":
                return Unsupported("Les agents");
            case ArtifactType.Agent:
            {
                var md = files.Keys.FirstOrDefault(p => p.EndsWith(".agent.md", StringComparison.Ordinal)) ?? (files.ContainsKey("AGENTS.md") ? "AGENTS.md" : null);
                if (md is null) return Fail("Fichier d'agent introuvable.");
                if (target == "codex") return Fail("Les agents Codex sont convertis en .toml : utilisez la CLI (myregistry add).");
                Put($"{(target == "copilot" ? (global ? ".agents/agents" : ".github/agents") : ".claude/agents")}/{name}.agent.md", files[md]);
                return Done();
            }
            case ArtifactType.Prompt when target == "cursor":
            {
                if (global) return Unsupported("Les prompts");
                var pm = files.Keys.FirstOrDefault(p => p.EndsWith(".prompt.md", StringComparison.Ordinal));
                if (pm is null) return Fail("Fichier de prompt introuvable.");
                Put($".cursor/commands/{name}.md", Encoding.UTF8.GetBytes(Exports.StripFrontmatter(Encoding.UTF8.GetString(files[pm])).TrimEnd() + "\n"));
                return Done();
            }
            case ArtifactType.Prompt:
            {
                if (target != "copilot" || global) return Unsupported("Les prompts");
                var pm = files.Keys.FirstOrDefault(p => p.EndsWith(".prompt.md", StringComparison.Ordinal));
                if (pm is null) return Fail("Fichier de prompt introuvable.");
                Put($".github/prompts/{name}.prompt.md", files[pm]);
                return Done();
            }
            case ArtifactType.Hook:
            {
                if (target is "codex" or "cursor" || global) return Unsupported("Les hooks");
                if (target == "copilot")
                {
                    if (!files.TryGetValue("hooks.json", out var hj)) return Fail("hooks.json introuvable.");
                    Put(".github/hooks/hooks.json", hj);
                }
                else foreach (var (p, d) in files) Put($".claude/hooks/{name}/{p}", d);
                return Done();
            }
            case ArtifactType.McpServer:
            {
                if (target == "copilot" && global) return Unsupported("Les serveurs MCP");
                if (target == "claude" && global) return Fail("Un MCP global se fusionne dans ~/.claude.json : utilisez la CLI (myregistry add -g).");
                if (target == "cursor" && global) return Fail("Un MCP global se fusionne dans ~/.cursor/mcp.json : utilisez la CLI (myregistry add -g).");
                var entry = McpEntry(files);
                if (entry is null) return Fail("Configuration MCP introuvable ou illisible.");
                var (file, key) = target switch { "copilot" => (".vscode/mcp.json", "servers"), "claude" => (".mcp.json", "mcpServers"), "cursor" => (".cursor/mcp.json", "mcpServers"), _ => (".codex/mcp.json", "mcpServers") };
                var doc = new JsonObject { [key] = new JsonObject { [name] = entry } };
                Put(file, Encoding.UTF8.GetBytes(doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n"));
                return Done();
            }
            default:
                return Unsupported(type.ToString());
        }
    }

    private static InstallPlan Fail(string error) => new([], error);

    private static JsonNode? McpEntry(IReadOnlyDictionary<string, byte[]> files)
    {
        var src = files.TryGetValue("mcp.json", out var m) ? m : null;
        if (src is null) return null;
        try
        {
            if (JsonNode.Parse(Encoding.UTF8.GetString(src)) is not JsonObject root) return null;
            if (root["mcpServers"] is JsonObject servers && servers.FirstOrDefault().Value is JsonNode first) return first.DeepClone();
            if (root["servers"] is JsonObject s2 && s2.FirstOrDefault().Value is JsonNode first2) return first2.DeepClone();
            return root.DeepClone();
        }
        catch (JsonException) { return null; }
    }
}
