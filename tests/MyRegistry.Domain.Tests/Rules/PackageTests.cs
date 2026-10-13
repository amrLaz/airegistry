using System.IO.Compression;
using System.Text;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class ArtifactLayoutTests
{
    [Theory]
    [InlineData(ArtifactType.Skill, new[] { "README.md", "SKILL.md" }, "SKILL.md")]
    [InlineData(ArtifactType.Skill, new[] { "README.md" }, null)]
    [InlineData(ArtifactType.Hook, new[] { "hooks.json", "README.md" }, "hooks.json")]
    [InlineData(ArtifactType.McpServer, new[] { "package.json", "mcp.json" }, "mcp.json")]
    [InlineData(ArtifactType.McpServer, new[] { "package.json" }, "package.json")]
    [InlineData(ArtifactType.Agent, new[] { "rev.agent.md" }, "rev.agent.md")]
    [InlineData(ArtifactType.Agent, new[] { "AGENTS.md" }, "AGENTS.md")]
    [InlineData(ArtifactType.Prompt, new[] { "x.prompt.md" }, "x.prompt.md")]
    [InlineData(ArtifactType.Plugin, new[] { ".claude-plugin/plugin.json", "SKILL.md" }, ".claude-plugin/plugin.json")]
    public void Main_file_per_type(ArtifactType type, string[] paths, string? expected) => Assert.Equal(expected, ArtifactLayout.MainFile(type, paths));
}

public class ZipPackageTests
{
    private static byte[] Zip(params (string path, string content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (p, c) in entries)
            {
                using var w = new StreamWriter(z.CreateEntry(p).Open(), new UTF8Encoding(false));
                w.Write(c);
            }
        return ms.ToArray();
    }

    [Fact] public void Reads_files_and_keeps_their_content()
    {
        var r = ZipPackage.Read(Zip(("SKILL.md", "# a"), ("ref/notes.md", "n")));
        Assert.Empty(r.Errors);
        Assert.Equal(["SKILL.md", "ref/notes.md"], r.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("n", Encoding.UTF8.GetString(r.Files["ref/notes.md"]));
    }

    [Fact] public void A_single_root_folder_is_stripped_like_a_zipped_directory()
    {
        var r = ZipPackage.Read(Zip(("resume-reunion/SKILL.md", "# a"), ("resume-reunion/README.md", "r")));
        Assert.Equal(["README.md", "SKILL.md"], r.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact] public void Several_roots_or_root_files_are_left_as_they_are()
    {
        Assert.Contains("a/SKILL.md", ZipPackage.Read(Zip(("a/SKILL.md", "x"), ("b/README.md", "y"))).Files.Keys);
        Assert.Contains("SKILL.md", ZipPackage.Read(Zip(("SKILL.md", "x"), ("sub/a.md", "y"))).Files.Keys);
    }

    [Fact] public void Root_folder_name_is_reported_for_naming() =>
        Assert.Equal("resume-reunion", ZipPackage.RootFolder(Zip(("resume-reunion/SKILL.md", "x"))));

    [Fact] public void Junk_files_are_ignored_and_listed()
    {
        var r = ZipPackage.Read(Zip(("SKILL.md", "x"), ("__MACOSX/._SKILL.md", "j"), (".DS_Store", "j"), ("node_modules/a/b.js", "j")));
        Assert.Equal(["SKILL.md"], r.Files.Keys.ToArray());
        Assert.Equal(3, r.Ignored.Count);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x")]
    public void Path_traversal_and_absolute_paths_are_refused_and_nothing_is_returned(string path)
    {
        var r = ZipPackage.Read(Zip(("SKILL.md", "ok"), (path, "x")));
        Assert.NotEmpty(r.Errors);
        Assert.Empty(r.Files);
    }

    [Fact] public void Too_many_entries_are_refused() =>
        Assert.Contains("entrées", Assert.Single(ZipPackage.Read(Zip(Enumerable.Range(0, 5).Select(i => ($"f{i}.md", "x")).ToArray()), new PackageLimits(MaxEntries: 3)).Errors));

    [Fact] public void Decompressed_size_limit_stops_zip_bombs() =>
        Assert.NotEmpty(ZipPackage.Read(Zip(("big.txt", new string('a', 100_000))), new PackageLimits(MaxTotalBytes: 10_000)).Errors);

    [Fact] public void Oversized_single_file_is_refused() =>
        Assert.Contains("trop gros", Assert.Single(ZipPackage.Read(Zip(("big.txt", new string('a', 5_000))), new PackageLimits(MaxFileBytes: 1_000)).Errors));

    [Fact] public void Corrupt_archive_is_reported_cleanly() =>
        Assert.Contains("illisible", Assert.Single(ZipPackage.Read([1, 2, 3, 4, 5]).Errors));

    [Fact] public void Empty_archive_yields_no_files() => Assert.Empty(ZipPackage.Read(Zip()).Files);
}
