using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyRegistry.Application.Rules;

namespace MyRegistry.Application;

public sealed record NpmVersion(string Version, string? Description, DateTime PublishedAt);

/// <summary>Surface compatible « registre npm » pour les skills : `/skills/@namespace/nom` (métadonnées) et `/skills/@namespace/nom/-/nom-1.0.0.tgz` (archive).</summary>
public static class NpmSurface
{
    public static string PackageName(string ns, string name) => $"@{ns}/{name}";
    public static string TarballFile(string name, string version) => $"{name}-{version}.tgz";

    /// <summary>Extrait la version d'un nom de fichier `nom-1.2.3.tgz` ; null si le nom ne correspond pas.</summary>
    public static string? VersionFromFile(string name, string file)
    {
        var prefix = name + "-";
        return file.StartsWith(prefix, StringComparison.Ordinal) && file.EndsWith(".tgz", StringComparison.Ordinal) && file.Length > prefix.Length + 4
            ? file[prefix.Length..^4] : null;
    }

    /// <summary>`tag latest` = plus haute version SemVer ; à défaut, la plus récente.</summary>
    public static string Latest(IReadOnlyList<NpmVersion> versions)
    {
        var semver = versions.Where(v => VersionRules.IsSemVer(v.Version)).ToList();
        return (semver.Count > 0 ? semver.OrderByDescending(v => SemVerKey(v.Version), StringComparer.Ordinal) : versions.OrderByDescending(v => v.PublishedAt)).First().Version;
    }

    // Clé de tri SemVer simple : chaque composant numérique sur 10 chiffres ; une version stable passe devant ses pré-versions.
    private static string SemVerKey(string v)
    {
        var core = v.Split('+')[0]; var dash = core.IndexOf('-');
        var nums = (dash < 0 ? core : core[..dash]).Split('.').Select(p => long.TryParse(p, out var n) ? n : 0).Take(3);
        return string.Join('.', nums.Select(n => n.ToString("D10"))) + (dash < 0 ? "~" : "-" + core[(dash + 1)..]);
    }

    public static JsonObject Packument(string ns, string name, IReadOnlyList<NpmVersion> versions, Func<NpmVersion, (string Url, string Shasum, string Integrity)> dist)
    {
        var full = PackageName(ns, name);
        var vs = new JsonObject(); var time = new JsonObject();
        foreach (var v in versions.OrderBy(v => v.PublishedAt))
        {
            var d = dist(v);
            vs[v.Version] = new JsonObject
            {
                ["name"] = full, ["version"] = v.Version, ["description"] = v.Description ?? "",
                ["dist"] = new JsonObject { ["tarball"] = d.Url, ["shasum"] = d.Shasum, ["integrity"] = d.Integrity },
            };
            time[v.Version] = v.PublishedAt.ToString("o");
        }
        var latest = Latest(versions);
        return new JsonObject
        {
            ["name"] = full, ["description"] = versions.First(v => v.Version == latest).Description ?? "",
            ["dist-tags"] = new JsonObject { ["latest"] = latest }, ["versions"] = vs, ["time"] = time,
        };
    }

    /// <summary>Archive .tgz déterministe : `package/` + fichiers du skill + `package.json` généré (dates et droits fixés).</summary>
    public static byte[] Tarball(string ns, string name, string version, string? description, IEnumerable<(string Path, byte[] Data)> files)
    {
        var pkg = new JsonObject { ["name"] = PackageName(ns, name), ["version"] = version, ["description"] = description ?? "", ["files"] = new JsonArray("*") }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var entries = files.Where(f => f.Path != "package.json").Append(("package.json", Encoding.UTF8.GetBytes(pkg))).OrderBy(f => f.Item1, StringComparer.Ordinal);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, true))
            foreach (var (path, data) in entries)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "package/" + path)
                {
                    DataStream = new MemoryStream(data), ModificationTime = DateTimeOffset.UnixEpoch, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, Uid = 0, Gid = 0,
                });
        return ms.ToArray();
    }

    public static (string Shasum, string Integrity) Digests(byte[] tgz) =>
        (Convert.ToHexString(SHA1.HashData(tgz)).ToLowerInvariant(), "sha512-" + Convert.ToBase64String(SHA512.HashData(tgz)));
}
