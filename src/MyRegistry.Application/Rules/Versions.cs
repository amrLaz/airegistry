using System.Text.RegularExpressions;

namespace MyRegistry.Application.Rules;

public sealed record VersionDecision(string Version, bool AutoBumped, string? Error = null);

public static class VersionRules
{
    private static readonly Regex SemVer = new(@"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.Compiled);

    public static bool IsSemVer(string v) => SemVer.IsMatch(v);

    public static int Compare(string a, string b)
    {
        var x = SemVer.Match(a);
        var y = SemVer.Match(b);
        for (var g = 1; g <= 3; g++)
        {
            var c = long.Parse(x.Groups[g].Value).CompareTo(long.Parse(y.Groups[g].Value));
            if (c != 0) return c;
        }
        var px = x.Groups[4].Value;
        var py = y.Groups[4].Value;
        if (px == py) return 0;
        if (px == "") return 1;
        if (py == "") return -1;
        return string.CompareOrdinal(px, py);
    }

    public static string BumpPatch(string v)
    {
        var m = SemVer.Match(v);
        return $"{m.Groups[1].Value}.{m.Groups[2].Value}.{long.Parse(m.Groups[3].Value) + 1}";
    }

    public static VersionDecision Resolve(string? proposed, IEnumerable<string> known)
    {
        var all = known.ToList();
        var semvers = all.Where(IsSemVer).OrderBy(v => v, Comparer<string>.Create(Compare)).ToList();
        var latest = semvers.LastOrDefault();

        if (string.IsNullOrWhiteSpace(proposed))
            return new(latest is null ? "1.0.0" : BumpPatch(latest), latest is not null);

        if (!IsSemVer(proposed))
            return all.Contains(proposed)
                ? new(proposed, false, "Cette version existe déjà.")
                : new(proposed, false);

        if (latest is null || Compare(proposed, latest) > 0) return new(proposed, false);
        return new(BumpPatch(latest), true);
    }
}

public static class FeedVersion
{
    /// <summary>Cascade version | semver | release du frontmatter, sinon 0.0.N (N augmente seulement si le contenu change).</summary>
    public static string Resolve(IReadOnlyDictionary<string, string> frontmatter, string? lastVersion, bool contentChanged)
    {
        foreach (var key in new[] { "version", "semver", "release" })
            if (frontmatter.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;

        if (lastVersion is null) return "0.0.1";
        var parts = lastVersion.Split('.');
        if (!contentChanged || parts.Length != 3 || !long.TryParse(parts[2], out var n)) return lastVersion;
        return $"{parts[0]}.{parts[1]}.{n + 1}";
    }
}
