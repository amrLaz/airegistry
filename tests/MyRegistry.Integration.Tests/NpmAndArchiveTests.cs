using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class NpmAndArchiveTests(DbFixture db)
{
    private HttpClient Client() => db.Factory.CreateClient();
    private static string U(string p) => p + "-" + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');

    private async Task<Guid> Publish(string name, string version, string status = "Approved")
    {
        var c = Client();
        var res = await c.PostAsJsonAsync("/api/import", new
        {
            type = "Skill", @namespace = "myregistry", name, version, fileName = "SKILL.md", description = "desc " + version,
            rawContent = $"---\nname: {name}\ndescription: d\n---\n# {name} {version}",
            additionalFiles = new Dictionary<string, string> { ["scripts/run.sh"] = "echo hi" },
        });
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
        if (status != "Pending") (await c.PutAsJsonAsync($"/api/catalog/{id}/status", new { status })).EnsureSuccessStatusCode();
        return id;
    }

    private static List<string> ZipNames(byte[] zip)
    {
        using var z = new ZipArchive(new MemoryStream(zip));
        return z.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Archive_places_files_for_the_chosen_agent()
    {
        var name = U("arch"); var id = await Publish(name, "1.0.0");
        var c = Client();
        Assert.Equal([$".claude/skills/{name}/SKILL.md", $".claude/skills/{name}/scripts/run.sh"], ZipNames(await c.GetByteArrayAsync($"/api/catalog/{id}/archive?target=claude")));
        Assert.Equal([$".agents/skills/{name}/SKILL.md", $".agents/skills/{name}/scripts/run.sh"], ZipNames(await c.GetByteArrayAsync($"/api/catalog/{id}/archive?target=copilot&scope=global")));
        Assert.Equal([$".codex/skills/{name}/SKILL.md", $".codex/skills/{name}/scripts/run.sh"], ZipNames(await c.GetByteArrayAsync($"/api/catalog/{id}/archive?target=codex")));
    }

    [Fact]
    public async Task Archive_for_cursor_is_a_rule_and_extra_files_are_reported_in_a_header()
    {
        var name = U("cur"); var id = await Publish(name, "1.0.0");
        var res = await Client().GetAsync($"/api/catalog/{id}/archive?target=cursor");
        res.EnsureSuccessStatusCode();
        Assert.Equal([$".cursor/rules/{name}.mdc"], ZipNames(await res.Content.ReadAsByteArrayAsync()));
        Assert.Contains("1 fichier(s) compl", Uri.UnescapeDataString(res.Headers.GetValues("X-Install-Warnings").Single()));
        Assert.False((await Client().GetAsync($"/api/catalog/{id}/archive?target=claude")).Headers.Contains("X-Install-Warnings"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Client().GetAsync($"/api/catalog/{id}/archive?target=cursor&scope=global")).StatusCode);
    }

    [Fact]
    public async Task Archive_refuses_unknown_targets_and_unapproved_versions()
    {
        var c = Client();
        var ok = await Publish(U("arch"), "1.0.0");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.GetAsync($"/api/catalog/{ok}/archive?target=vim")).StatusCode);
        var pending = await Publish(U("arch"), "1.0.0", "Pending");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/catalog/{pending}/archive?target=claude")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/catalog/{Guid.NewGuid()}/archive")).StatusCode);
    }

    [Fact]
    public async Task Packument_lists_only_approved_versions_with_a_matching_tarball()
    {
        var name = U("npm");
        await Publish(name, "1.0.0"); await Publish(name, "1.1.0"); await Publish(name, "2.0.0", "Pending");
        var c = Client();

        var doc = await c.GetFromJsonAsync<JsonElement>($"/skills/@myregistry/{name}");
        Assert.Equal($"@myregistry/{name}", doc.GetProperty("name").GetString());
        Assert.Equal("1.1.0", doc.GetProperty("dist-tags").GetProperty("latest").GetString());
        var versions = doc.GetProperty("versions");
        Assert.True(versions.TryGetProperty("1.0.0", out _) && versions.TryGetProperty("1.1.0", out _));
        Assert.False(versions.TryGetProperty("2.0.0", out _));

        var dist = versions.GetProperty("1.1.0").GetProperty("dist");
        var url = new Uri(dist.GetProperty("tarball").GetString()!);
        var tgz = await c.GetByteArrayAsync(url.PathAndQuery);
        Assert.Equal(Convert.ToHexString(SHA1.HashData(tgz)).ToLowerInvariant(), dist.GetProperty("shasum").GetString());
        Assert.Equal("sha512-" + Convert.ToBase64String(SHA512.HashData(tgz)), dist.GetProperty("integrity").GetString());

        using var gz = new GZipStream(new MemoryStream(tgz), CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        var names = new List<string>();
        while (tar.GetNextEntry() is { } e) names.Add(e.Name);
        Assert.Equal(["package/SKILL.md", "package/package.json", "package/scripts/run.sh"], names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Npm_style_encoded_scope_url_and_unknown_packages()
    {
        var name = U("npm"); await Publish(name, "1.0.0");
        var c = Client();
        var doc = await c.GetFromJsonAsync<JsonElement>($"/skills/@myregistry%2f{name}");
        Assert.Equal($"@myregistry/{name}", doc.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/skills/@myregistry/inexistant-pkg")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/skills/@myregistry/{name}/-/{name}-9.9.9.tgz")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/skills/nonscoped")).StatusCode);
    }
}
