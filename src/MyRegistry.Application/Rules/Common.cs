using System.Text.RegularExpressions;

namespace MyRegistry.Application.Rules;

public sealed record Issue(string Code, string Message, string? File = null, int? Line = null);

public sealed record ValidationResult(IReadOnlyList<Issue> Errors, IReadOnlyList<Issue> Warnings)
{
    public bool IsValid => Errors.Count == 0;
    public QualityGrade Grade => QualityScore.Grade(Warnings.Count);
}

public enum QualityGrade { A, B, C }

public static class QualityScore
{
    public static QualityGrade Grade(int warnings) => warnings switch { 0 => QualityGrade.A, < 3 => QualityGrade.B, _ => QualityGrade.C };
}

/// <summary>Frontmatter YAML minimal (clé: valeur sur une ligne). Suffisant pour les règles de docs/05.</summary>
public static class Frontmatter
{
    public static Dictionary<string, string>? Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2 || lines[0].Trim() != "---") return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") return map;
            var m = Regex.Match(lines[i], @"^([A-Za-z0-9_-]+)\s*:\s*(.*)$");
            if (m.Success) map[m.Groups[1].Value] = m.Groups[2].Value.Trim().Trim('"', '\'');
        }
        return null; // frontmatter non fermé
    }
}
