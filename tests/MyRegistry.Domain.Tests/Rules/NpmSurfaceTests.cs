using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using MyRegistry.Application;

namespace MyRegistry.Domain.Tests.Rules;

public class NpmSurfaceTests
{
    private static NpmVersion V(string v, int day) => new(v, "desc " + v, new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc));

    [Fact] public void Names_and_files()
    {
        Assert.Equal("@acme/pdf", NpmSurface.PackageName("acme", "pdf"));
        Assert.Equal("pdf-1.2.3.tgz", NpmSurface.TarballFile("pdf", "1.2.3"));
        Assert.Equal("1.2.3-beta.1", NpmSurface.VersionFromFile("pdf", "pdf-1.2.3-beta.1.tgz"));
        Assert.Null(NpmSurface.VersionFromFile("pdf", "other-1.0.0.tgz"));
        Assert.Null(NpmSurface.VersionFromFile("pdf", "pdf-.tgz"));
    }

    [Fact] public void Latest_is_the_highest_semver_not_the_newest_and_stable_beats_prerelease()
    {
        Assert.Equal("1.10.0", NpmSurface.Latest([V("1.9.0", 3), V("1.10.0", 1), V("1.2.0", 5)]));
        Assert.Equal("2.0.0", NpmSurface.Latest([V("2.0.0-rc.1", 9), V("2.0.0", 1)]));
    }

    [Fact] public void Packument_lists_versions_with_dist_and_latest_tag()
    {
        var doc = NpmSurface.Packument("acme", "pdf", [V("1.0.0", 1), V("1.1.0", 2)], v => ($"http://h/skills/@acme/pdf/-/pdf-{v.Version}.tgz", "sha", "sha512-x"));
        Assert.Equal("@acme/pdf", (string?)doc["name"]);
        Assert.Equal("1.1.0", (string?)doc["dist-tags"]!["latest"]);
        Assert.Equal("http://h/skills/@acme/pdf/-/pdf-1.0.0.tgz", (string?)doc["versions"]!["1.0.0"]!["dist"]!["tarball"]);
        Assert.Equal("sha512-x", (string?)doc["versions"]!["1.1.0"]!["dist"]!["integrity"]);
    }

    private static Dictionary<string, string> Untar(byte[] tgz)
    {
        var result = new Dictionary<string, string>();
        using var gz = new GZipStream(new MemoryStream(tgz), CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } e)
            if (e.DataStream is { } s) { using var r = new StreamReader(s, Encoding.UTF8); result[e.Name] = r.ReadToEnd(); }
        return result;
    }

    [Fact] public void Tarball_is_rooted_in_package_with_a_generated_package_json()
    {
        var tgz = NpmSurface.Tarball("acme", "pdf", "1.0.0", "d", [("SKILL.md", Encoding.UTF8.GetBytes("# s")), ("scripts/a.sh", Encoding.UTF8.GetBytes("echo"))]);
        var files = Untar(tgz);
        Assert.Equal(["package/SKILL.md", "package/package.json", "package/scripts/a.sh"], files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var pkg = JsonNode.Parse(files["package/package.json"])!;
        Assert.Equal("@acme/pdf", (string?)pkg["name"]);
        Assert.Equal("1.0.0", (string?)pkg["version"]);
    }

    [Fact] public void Tarball_is_deterministic_and_replaces_a_shipped_package_json()
    {
        (string, byte[])[] input = [("SKILL.md", [1, 2, 3]), ("package.json", Encoding.UTF8.GetBytes("{\"name\":\"evil\"}"))];
        var a = NpmSurface.Tarball("acme", "pdf", "1.0.0", null, input);
        var b = NpmSurface.Tarball("acme", "pdf", "1.0.0", null, input);
        Assert.Equal(NpmSurface.Digests(a), NpmSurface.Digests(b));
        Assert.Equal("@acme/pdf", (string?)JsonNode.Parse(Untar(a)["package/package.json"])!["name"]);
    }
}
