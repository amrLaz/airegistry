using System.Security.Cryptography;
using System.Text;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;

namespace MyRegistry.Application;

public sealed record FilePayload(string Path, byte[] Content, bool IsBinary);

public sealed record ValidateRequest(
    ArtifactType? Type, string FileName, string Content,
    Dictionary<string, string>? AdditionalFiles = null, Dictionary<string, string>? BinaryFiles = null,
    string? Namespace = null, string? Name = null);

public sealed record ImportRequest(
    ArtifactType Type, string Namespace, string Name, string? Version, string? Description,
    string FileName, string RawContent, string[]? Tags = null,
    Dictionary<string, string>? AdditionalFiles = null, Dictionary<string, string>? BinaryFiles = null);

public sealed record FindingDto(string Detector, string File, int Line, bool Blocking);

public sealed record ValidationReport(
    bool IsValid, ArtifactType? DetectedType, IReadOnlyList<Issue> Errors, IReadOnlyList<Issue> Warnings,
    IReadOnlyList<FindingDto> Findings, string Grade);

public sealed record ImportResult(
    bool Success, Guid? VersionId, string? Version, bool AutoBumped, string? Grade, string? ContentHash,
    string? CanonicalName, IReadOnlyList<Issue> Errors, IReadOnlyList<Issue> Warnings, IReadOnlyList<string> Notes, string? PullRequestUrl = null);

public static class ContentHasher
{
    /// <summary>SHA-256 de la liste triée « chemin + SHA-256(contenu) » : stable quel que soit l'ordre d'envoi.</summary>
    public static string Hash(IEnumerable<FilePayload> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            sb.Append(f.Path).Append('\n').Append(Convert.ToHexString(SHA256.HashData(f.Content)).ToLowerInvariant()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

public static class PayloadBuilder
{
    public static List<FilePayload> Build(string fileName, string content,
        Dictionary<string, string>? additional, Dictionary<string, string>? binary)
    {
        var list = new List<FilePayload> { new(Normalize(fileName), Encoding.UTF8.GetBytes(content), false) };
        foreach (var (p, c) in additional ?? [])
            list.Add(new(Normalize(p), Encoding.UTF8.GetBytes(c), false));
        foreach (var (p, b64) in binary ?? [])
            list.Add(new(Normalize(p), Convert.FromBase64String(b64), true));
        return list;
    }

    // Empêche les chemins hors racine (path traversal).
    public static string Normalize(string path)
    {
        var p = path.Replace('\\', '/').TrimStart('/');
        if (p.Split('/').Any(s => s == ".." )) throw new ArgumentException($"Chemin invalide : {path}");
        return p;
    }
}

public static class Validation
{
    public static ValidationReport Run(ArtifactType? declared, IReadOnlyList<FilePayload> files)
    {
        var text = files.Where(f => !f.IsBinary).ToDictionary(f => f.Path, f => Encoding.UTF8.GetString(f.Content));
        var type = declared ?? ArtifactTypeDetector.Detect(files.Select(f => f.Path));
        if (type is null)
            return new(false, null, [new("unknown-type", "Type d'artefact non détecté.")], [], [], "C");

        var r = ArtifactValidator.Validate(type.Value, text);
        var findings = SecretScanner.Scan(text.Select(kv => (kv.Key, kv.Value)));
        var errors = r.Errors.ToList();
        foreach (var f in findings.Where(f => f.Blocking))
            errors.Add(new("secret-detected", $"Secret ou valeur interdite détecté : {f.Detector}", f.File, f.Line));
        var warnings = r.Warnings.ToList();
        foreach (var f in findings.Where(f => !f.Blocking))
            warnings.Add(new("observation-" + f.Detector, $"Observation : {f.Detector}", f.File, f.Line));
        return new(errors.Count == 0, type, errors, warnings,
            findings.Select(f => new FindingDto(f.Detector, f.File, f.Line, f.Blocking)).ToList(),
            QualityScore.Grade(warnings.Count).ToString());
    }
}
