using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyRegistry.Domain;

namespace MyRegistry.Application;

public sealed record InstallFile(string Path, byte[] Data);
public sealed record InstallPlan(IReadOnlyList<InstallFile> Files, string? Error);

/// <summary>Où l'archive installable place les fichiers selon l'agent cible (docs/04, « Emplacements d'installation »). Pur : ne lit ni n'écrit rien.</summary>
public static class InstallLayout
{
    public static readonly string[] Targets = ["copilot", "claude", "codex"];

    public static InstallPlan Plan(ArtifactType type, string name, string target, bool global, IReadOnlyDictionary<string, byte[]> files)
    {
        target = target.Trim().ToLowerInvariant();
        if (!Targets.Contains(target)) return Fail($"Cible inconnue « {target} » (copilot, claude ou codex).");
        var writes = new List<InstallFile>();
        InstallPlan Done() => new(writes.OrderBy(w => w.Path, StringComparer.Ordinal).ToList(), null);
        InstallPlan Unsupported(string what) => Fail($"{what} : non supporté pour {target}{(global ? " en global" : "")}.");
        void Put(string path, byte[] data) => writes.Add(new(path, data));

        switch (type)
        {
            case ArtifactType.Plugin:
                return Fail("Les plugins ne s'installent pas par archive : utilisez /api/plugins/{ns}/{nom}/download et les commandes natives de l'agent.");
            case ArtifactType.Skill:
            {
                var baseDir = target switch { "copilot" => global ? ".agents/skills" : ".github/skills", "claude" => ".claude/skills", _ => ".codex/skills" };
                foreach (var (p, d) in files) Put($"{baseDir}/{name}/{p}", d);
                return Done();
            }
            case ArtifactType.Agent:
            {
                var md = files.Keys.FirstOrDefault(p => p.EndsWith(".agent.md", StringComparison.Ordinal)) ?? (files.ContainsKey("AGENTS.md") ? "AGENTS.md" : null);
                if (md is null) return Fail("Fichier d'agent introuvable.");
                if (target == "codex") return Fail("Les agents Codex sont convertis en .toml : utilisez la CLI (myregistry add).");
                Put($"{(target == "copilot" ? (global ? ".agents/agents" : ".github/agents") : ".claude/agents")}/{name}.agent.md", files[md]);
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
                if (target == "codex" || global) return Unsupported("Les hooks");
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
                var entry = McpEntry(files);
                if (entry is null) return Fail("Configuration MCP introuvable ou illisible.");
                var (file, key) = target switch { "copilot" => (".vscode/mcp.json", "servers"), "claude" => (".mcp.json", "mcpServers"), _ => (".codex/mcp.json", "mcpServers") };
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
