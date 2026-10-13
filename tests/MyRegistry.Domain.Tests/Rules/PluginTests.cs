using System.Text;
using System.Text.Json.Nodes;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class PluginRulesTests
{
    private static ComponentRef C(ArtifactType t, string slug, string v = "1.0.0") => new(t, slug, v);

    [Fact] public void Valid_composition_has_no_errors() =>
        Assert.Empty(PluginRules.ValidateRefs("myregistry/pack", [C(ArtifactType.Skill, "myregistry/a"), C(ArtifactType.Agent, "myregistry/b", "2.1.0")]));

    [Fact] public void Empty_composition_is_refused() =>
        Assert.Contains(PluginRules.ValidateRefs("myregistry/pack", []), e => e.Code == "no-components");

    [Theory]
    [InlineData("^1.0.0")]
    [InlineData("~1.2.0")]
    [InlineData("latest")]
    [InlineData("1.x")]
    [InlineData("")]
    public void Ranges_are_refused_only_exact_semver(string v) =>
        Assert.Contains(PluginRules.ValidateRefs("myregistry/pack", [C(ArtifactType.Skill, "myregistry/a", v)]), e => e.Code == "component-version");

    [Fact] public void Prerelease_semver_is_exact_enough() =>
        Assert.Empty(PluginRules.ValidateRefs("myregistry/pack", [C(ArtifactType.Skill, "myregistry/a", "1.0.0-rc.1")]));

    [Fact] public void Nested_plugin_is_refused() =>
        Assert.Contains(PluginRules.ValidateRefs("myregistry/pack", [C(ArtifactType.Plugin, "myregistry/other")]), e => e.Code == "component-type");

    [Fact] public void Self_reference_is_refused() =>
        Assert.Contains(PluginRules.ValidateRefs("myregistry/pack", [C(ArtifactType.Skill, "MyRegistry/Pack")]), e => e.Code == "component-self");

    [Fact] public void Duplicates_are_refused() =>
        Assert.Contains(PluginRules.ValidateRefs("myregistry/pack", [C(ArtifactType.Skill, "myregistry/a"), C(ArtifactType.Skill, "myregistry/a", "2.0.0")]), e => e.Code == "component-duplicate");

    [Theory]
    [InlineData("noslash")]
    [InlineData("a/b/c")]
    [InlineData("a/")]
    public void Bad_slugs_are_refused(string slug) =>
        Assert.Contains(PluginRules.ValidateRefs("myregistry/pack", [C(ArtifactType.Skill, slug)]), e => e.Code == "component-slug");

    [Fact] public void Manifest_round_trips_and_is_sorted()
    {
        var refs = new[] { C(ArtifactType.Skill, "myregistry/z"), C(ArtifactType.Agent, "myregistry/a") };
        var json = PluginRules.BuildManifest("pack", "Un pack", "1.2.3", refs);
        var back = PluginRules.ReadManifest(json);
        Assert.Equal(2, back.Count);
        Assert.Equal("Agent", (string?)JsonNode.Parse(json)!["x-myregistry-components"]![0]!["type"]); // tri stable par type puis slug
        Assert.Contains(back, r => r.Slug == "myregistry/z" && r.Version == "1.0.0");
        Assert.Equal("1.2.3", (string?)JsonNode.Parse(json)!["version"]);
        Assert.Equal(json, PluginRules.BuildManifest("pack", "Un pack", "1.2.3", refs.Reverse().ToArray())); // indépendant de l'ordre
    }

    [Fact] public void Manifest_produced_validates_as_a_plugin()
    {
        var json = PluginRules.BuildManifest("pack", "d", "1.0.0", [C(ArtifactType.Skill, "myregistry/a")]);
        var r = MyRegistry.Application.Rules.ArtifactValidator.Validate(ArtifactType.Plugin, new Dictionary<string, string> { [".claude-plugin/plugin.json"] = json });
        Assert.Empty(r.Errors);
    }

    private static IReadOnlyDictionary<string, byte[]> F(params (string, string)[] f) => f.ToDictionary(x => x.Item1, x => Encoding.UTF8.GetBytes(x.Item2));

    [Fact] public void Bundle_layout_per_component_type()
    {
        var manifest = PluginRules.BuildManifest("pack", null, "1.0.0", [C(ArtifactType.Skill, "myregistry/s")]);
        var bundle = PluginRules.BuildBundle(manifest,
        [
            (C(ArtifactType.Skill, "myregistry/s"), F(("SKILL.md", "# s"), ("ref/x.md", "x"))),
            (C(ArtifactType.Agent, "myregistry/rev"), F(("rev.agent.md", "# a"))),
            (C(ArtifactType.Prompt, "myregistry/p"), F(("p.prompt.md", "# p"))),
            (C(ArtifactType.McpServer, "myregistry/srv"), F(("mcp.json", "{\"command\":\"npx\",\"args\":[\"-y\",\"p\"]}"))),
        ]);
        var keys = bundle.Keys.ToList();
        Assert.Contains("skills/s/SKILL.md", keys);
        Assert.Contains("skills/s/ref/x.md", keys);
        Assert.Contains("agents/rev.md", keys);
        Assert.Contains("prompts/p.prompt.md", keys);
        Assert.Contains(".mcp.json", keys);
        foreach (var m in PluginRules.ManifestPaths) Assert.Contains(m, keys);
        Assert.Equal("npx", (string?)JsonNode.Parse(Encoding.UTF8.GetString(bundle[".mcp.json"]))!["mcpServers"]!["srv"]!["command"]);
    }

    [Fact] public void Hooks_from_several_components_are_merged_per_event()
    {
        var bundle = PluginRules.BuildBundle("{}",
        [
            (C(ArtifactType.Hook, "myregistry/h1"), F(("hooks.json", "{\"hooks\":{\"PreToolUse\":[{\"id\":1}],\"Stop\":[{\"id\":2}]}}"))),
            (C(ArtifactType.Hook, "myregistry/h2"), F(("hooks.json", "{\"hooks\":{\"PreToolUse\":[{\"id\":3}]}}"))),
        ]);
        var hooks = JsonNode.Parse(Encoding.UTF8.GetString(bundle["hooks/hooks.json"]))!["hooks"]!;
        Assert.Equal(2, hooks["PreToolUse"]!.AsArray().Count);
        Assert.Single(hooks["Stop"]!.AsArray());
    }

    [Fact] public void Bundle_is_deterministic_regardless_of_input_order()
    {
        var a = (C(ArtifactType.Skill, "myregistry/a"), F(("SKILL.md", "a")));
        var b = (C(ArtifactType.Skill, "myregistry/b"), F(("SKILL.md", "b")));
        Assert.Equal(PluginRules.BuildBundle("{}", [a, b]).Keys, PluginRules.BuildBundle("{}", [b, a]).Keys);
    }
}

public class PluginUpdatesTests
{
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Approved(params (string, string[])[] x) =>
        x.ToDictionary(i => i.Item1, i => (IReadOnlyList<string>)i.Item2);

    [Fact] public void Newer_approved_version_is_reported()
    {
        var u = PluginUpdates.Find([new(ArtifactType.Skill, "myregistry/a", "1.0.0")], Approved(("myregistry/a", ["1.0.0", "1.2.0", "1.10.0"])));
        Assert.Equal("1.10.0", Assert.Single(u).Latest);
    }

    [Fact] public void Up_to_date_or_unknown_components_are_not_reported()
    {
        Assert.Empty(PluginUpdates.Find([new(ArtifactType.Skill, "myregistry/a", "2.0.0")], Approved(("myregistry/a", ["1.0.0", "2.0.0"]))));
        Assert.Empty(PluginUpdates.Find([new(ArtifactType.Skill, "myregistry/gone", "1.0.0")], Approved()));
    }
}

public class MarketplaceManifestTests
{
    [Fact] public void Builds_archive_sources_with_https_download_url()
    {
        var json = MarketplaceManifest.Build("Acme Registry", "https://reg.acme.io/", [new("myregistry", "pack", "1.2.0", "Un pack", ["onboarding"])]);
        var root = JsonNode.Parse(json)!;
        Assert.Equal("acme-registry", (string?)root["name"]);
        var p = root["plugins"]![0]!;
        Assert.Equal("myregistry-pack", (string?)p["name"]);
        Assert.Equal("archive", (string?)p["source"]!["source"]);
        Assert.Equal("https://reg.acme.io/api/plugins/myregistry/pack/download?version=1.2.0", (string?)p["source"]!["url"]);
        Assert.Equal("onboarding", (string?)p["tags"]![0]);
    }

    [Fact] public void Empty_marketplace_is_valid_json_with_no_plugins() =>
        Assert.Empty(JsonNode.Parse(MarketplaceManifest.Build("X", "https://x.io", []))!["plugins"]!.AsArray());

    [Fact] public void Plugins_are_sorted_for_stable_output()
    {
        var a = MarketplaceManifest.Build("X", "https://x.io", [new("n", "b", "1.0.0", null, []), new("n", "a", "1.0.0", null, [])]);
        var b = MarketplaceManifest.Build("X", "https://x.io", [new("n", "a", "1.0.0", null, []), new("n", "b", "1.0.0", null, [])]);
        Assert.Equal(a, b);
    }
}
