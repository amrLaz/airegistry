using System.Text.RegularExpressions;
using MyRegistry.Domain;

namespace MyRegistry.Application.Rules;

public sealed record NameCheck(bool IsValid, IReadOnlyList<Issue> Errors, string? Canonical);

public static class NameRules
{
    private static readonly Regex Format = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly string[] BadSuffixes = ["-gh", "-azd", "-feed"];

    private static readonly Dictionary<ArtifactType, string[]> Reserved = new()
    {
        [ArtifactType.Skill] = ["synced"],
        [ArtifactType.Agent] = ["default", "worker", "explorer"],
    };

    private static readonly Dictionary<ArtifactType, string> TypeWord = new()
    {
        [ArtifactType.McpServer] = "mcp", [ArtifactType.Skill] = "skill", [ArtifactType.Agent] = "agent",
        [ArtifactType.Prompt] = "prompt", [ArtifactType.Plugin] = "plugin", [ArtifactType.Hook] = "hook",
    };

    public static string Canonicalize(string s) =>
        Regex.Replace(s.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    public static NameCheck Check(string ns, string name, ArtifactType type, bool creation)
    {
        var errors = new List<Issue>();
        if (ns.Length is < 2 or > 32) errors.Add(new("ns-length", "Le namespace doit faire 2 à 32 caractères."));
        if (!Format.IsMatch(ns)) errors.Add(new("ns-format", "Namespace : minuscules ASCII, chiffres, tirets simples."));
        if (name.Length is < 2 or > 48) errors.Add(new("name-length", "Le nom doit faire 2 à 48 caractères."));
        var formatOk = Format.IsMatch(name);
        if (!formatOk) errors.Add(new("name-format", "Nom : minuscules ASCII, chiffres, tirets simples."));
        if (ns.Length + 1 + name.Length > 64) errors.Add(new("combined-length", "La forme namespace-nom dépasse 64 caractères."));
        if (Reserved.TryGetValue(type, out var res) && res.Contains(name)) errors.Add(new("reserved-name", $"Nom réservé : {name}."));
        if (BadSuffixes.Any(name.EndsWith)) errors.Add(new("forbidden-suffix", "Suffixe interdit (-gh, -azd, -feed)."));
        if (creation && name.Split('-').Contains(TypeWord[type]))
            errors.Add(new("name-contains-type", "Le nom ne doit pas contenir son type."));

        var canonical = formatOk ? null : Canonicalize(name);
        return new(errors.Count == 0, errors, canonical);
    }
}

public static class NameSimilarity
{
    private static string Normalize(string s) =>
        s.ToLowerInvariant().Replace('0', 'o').Replace('1', 'l').Replace('i', 'l').Replace('5', 's').Replace("-", "");

    /// <summary>Deux noms distincts sont « proches » s'ils sont à une édition (insertion, suppression, substitution, transposition) après normalisation.</summary>
    public static bool AreClose(string a, string b)
    {
        if (a == b) return false;
        var x = Normalize(a);
        var y = Normalize(b);
        return x == y || Osa(x, y) <= 1;
    }

    // Distance Damerau-Levenshtein (OSA).
    private static int Osa(string s, string t)
    {
        var d = new int[s.Length + 1, t.Length + 1];
        for (var i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= t.Length; j++) d[0, j] = j;
        for (var i = 1; i <= s.Length; i++)
            for (var j = 1; j <= t.Length; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[s.Length, t.Length];
    }
}
