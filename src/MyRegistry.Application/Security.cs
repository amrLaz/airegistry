using System.Text;
using System.Text.RegularExpressions;
using MyRegistry.Application.Rules;
using MyRegistry.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MyRegistry.Application;

public enum FindingSeverity { Low = 1, Medium = 2, High = 3, Critical = 4 }

public enum ScanStatus { Succeeded, Failed, Unavailable }

public sealed record ScanFinding(string RuleId, FindingSeverity Severity, string File, int Line, string Message);

/// <summary>Résultat d'une analyse. <c>Incomplete</c> : des fichiers n'ont pas pu être analysés (binaires, trop gros).</summary>
public sealed record ScanOutcome(ScanStatus Status, string ToolVersion, IReadOnlyList<ScanFinding> Findings, int FilesScanned, int FilesTotal, bool Incomplete, string? Error = null);

public interface IScanner
{
    string ToolVersion { get; }
    Task<ScanOutcome> ScanAsync(IReadOnlyList<FilePayload> files, ArtifactType type, CancellationToken ct = default);
}

public sealed record RuleInfo(string Id, FindingSeverity Severity, string Description);

/// <summary>
/// Scanner statique intégré (règles locales). L'interface <see cref="IScanner"/> permet de brancher SkillSpector ou un équivalent
/// sans toucher au reste : seule la liste des constats compte pour la politique de blocage.
/// </summary>
public sealed class StaticScanner : IScanner
{
    public const string Version = "builtin-1.0";
    public const long MaxFileBytes = 500_000;
    public string ToolVersion => Version;

    private sealed record Rule(string Id, FindingSeverity Severity, string Description, Regex Pattern);
    private static Regex R(string p) => new(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    private static readonly Rule[] Rules =
    [
        new("pipe-to-shell", FindingSeverity.Critical, "Téléchargement exécuté directement par un shell (curl | sh).", R(@"(curl|wget)\b[^\n|]*\|\s*(sudo\s+)?(ba|z|da)?sh\b")),
        new("destructive-command", FindingSeverity.Critical, "Commande destructrice (suppression racine/home, formatage, écriture disque brute).", R(@"(rm\s+-[a-z]*r[a-z]*f?[a-z]*\s+(/(\s|$|\*)|~(/|\s|$)|\$HOME\b)|\bmkfs(\.\w+)?\b|\bdd\s+if=\S+\s+of=/dev/)")),
        new("prompt-injection", FindingSeverity.High, "Formulation typique d'injection de prompt (ignorer les instructions précédentes, cacher à l'utilisateur).",
            R(@"(ignore|disregard|forget)\s+(all\s+|any\s+)?(the\s+)?(previous|prior|above|earlier)\s+(instructions|rules|prompts?)|do\s+not\s+(tell|inform|mention\s+(this\s+)?to)\s+the\s+user|override\s+(the\s+)?system\s+prompt")),
        new("hidden-characters", FindingSeverity.High, "Caractères invisibles ou de contrôle bidirectionnel pouvant masquer des instructions.", new Regex("[​-‏‪-‮⁠⁦-⁩﻿]", RegexOptions.Compiled)),
        new("credential-access", FindingSeverity.High, "Lecture de fichiers d'identifiants (clés SSH, credentials cloud, /etc/shadow).", R(@"(~|\$HOME|/root|/home/\w+)?/?\.(ssh/(id_\w+|authorized_keys)|aws/credentials|netrc)\b|/etc/shadow")),
        new("exfiltration-command", FindingSeverity.High, "Envoi réseau de variables d'environnement ou de secrets.", R(@"(curl|wget|nc|ncat)\b[^\n]*(\$\{?[A-Z_]*(KEY|TOKEN|SECRET|PASSWORD)\w*\}?|\bprintenv\b|\benv\b\s*\|)")),
        new("skip-permissions", FindingSeverity.High, "Contournement des garde-fous de permission de l'agent.", R(@"--dangerously-skip-permissions|bypassPermissions|--no-verify-permissions")),
        new("encoded-execution", FindingSeverity.High, "Exécution de contenu encodé (base64 décodé puis évalué).", R(@"(base64\s+(-d|--decode)[^\n]*\|\s*(ba|z)?sh\b|eval\s*\(\s*(atob|Buffer\.from)\b)")),
        new("dynamic-eval", FindingSeverity.Medium, "Évaluation dynamique de code (eval / exec / shell=True).", R(@"\beval\s*\(|\bexec\s*\(|os\.system\s*\(|shell\s*=\s*True")),
        new("privilege-escalation", FindingSeverity.Medium, "Élévation de privilèges (sudo, chmod 777).", R(@"\bsudo\s+\S|chmod\s+(-R\s+)?0?777\b")),
        new("insecure-transport", FindingSeverity.Medium, "Désactivation de la vérification TLS.", R(@"curl\b[^\n]*\s(-k|--insecure)\b|verify\s*=\s*False|NODE_TLS_REJECT_UNAUTHORIZED\s*=\s*['""]?0|--no-check-certificate")),
        new("suspicious-sink", FindingSeverity.Medium, "Destination typique d'exfiltration ou de test (webhook, pastebin, tunnel).", R(@"https?://[^\s""']*(webhook\.site|requestbin|pastebin\.com|ngrok\.(io|app)|trycloudflare\.com|interact\.sh|burpcollaborator)")),
        new("wildcard-permissions", FindingSeverity.Medium, "Permissions d'outils génériques (« * ») accordées à l'agent.", R(@"allowed[-_ ]?tools""?\s*[:=]\s*\[?\s*[""']\*[""']")),
        new("raw-ip-url", FindingSeverity.Low, "URL vers une adresse IP publique.", R(@"https?://(?!10\.|127\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)\d{1,3}(\.\d{1,3}){3}\b")),
    ];

    /// <summary>Catalogue des règles (utile à la baseline : on ne peut exclure qu'une règle connue).</summary>
    public static IReadOnlyList<RuleInfo> Catalog { get; } =
        Rules.Select(r => new RuleInfo(r.Id, r.Severity, r.Description))
            .Concat([
                new RuleInfo("secret-private-key", FindingSeverity.Critical, "Clé privée en clair."),
                new RuleInfo("secret-token", FindingSeverity.Critical, "Jeton d'accès (GitHub, AWS…) en clair."),
                new RuleInfo("secret-assignment", FindingSeverity.High, "Mot de passe ou clé d'API assigné en clair."),
            ]).ToList();

    private static (string Id, FindingSeverity Sev)? MapSecret(string detector) => detector switch
    {
        "private-key" => ("secret-private-key", FindingSeverity.Critical),
        "github-token" or "aws-access-key" => ("secret-token", FindingSeverity.Critical),
        "password-assignment" or "api-key-assignment" => ("secret-assignment", FindingSeverity.High),
        _ => null,
    };

    public Task<ScanOutcome> ScanAsync(IReadOnlyList<FilePayload> files, ArtifactType type, CancellationToken ct = default)
    {
        var findings = new List<ScanFinding>();
        var scanned = 0;
        var incomplete = false;
        var textFiles = new List<(string Path, string Content)>();

        foreach (var f in files)
        {
            if (f.IsBinary || f.Content.Length > MaxFileBytes) { incomplete = true; continue; }
            var text = Encoding.UTF8.GetString(f.Content);
            textFiles.Add((f.Path, text));
            scanned++;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
                foreach (var r in Rules)
                {
                    try { if (r.Pattern.IsMatch(lines[i])) findings.Add(new(r.Id, r.Severity, f.Path, i + 1, r.Description)); }
                    catch (RegexMatchTimeoutException) { incomplete = true; }
                }
        }

        foreach (var s in SecretScanner.Scan(textFiles).Where(s => s.Blocking))
            if (MapSecret(s.Detector) is { } m)
                findings.Add(new(m.Id, m.Sev, s.File, s.Line, Catalog.First(c => c.Id == m.Id).Description));

        var distinct = findings.GroupBy(f => (f.RuleId, f.File, f.Line)).Select(g => g.First())
            .OrderByDescending(f => f.Severity).ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).ToList();
        return Task.FromResult(new ScanOutcome(ScanStatus.Succeeded, Version, distinct, scanned, files.Count, incomplete));
    }
}

public sealed record WaiverView(FindingSeverity AcceptedSeverity, string ContentHash, DateTime? ExpiresAt, DateTime? RevokedAt);

public static class SecurityPolicy
{
    public static bool Blocks(FindingSeverity s) => s >= FindingSeverity.High;

    public static FindingSeverity? Max(IEnumerable<ScanFinding> f) => f.Any() ? f.Max(x => x.Severity) : null;

    /// <summary>Dérogation : Moderator, SecurityValidator ou Admin ; un constat Critical est réservé à SecurityValidator/Admin.</summary>
    public static bool CanWaive(Role role, FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => role is Role.SecurityValidator or Role.Admin,
        _ => role is Role.Moderator or Role.SecurityValidator or Role.Admin,
    };

    /// <summary>Une dérogation couvre un constat si elle est active, liée à la même empreinte de contenu et d'une gravité acceptée au moins égale.</summary>
    public static bool Covers(WaiverView w, string contentHash, FindingSeverity findingSeverity, DateTime now) =>
        w.RevokedAt is null && w.ContentHash == contentHash && (w.ExpiresAt is null || now < w.ExpiresAt) && findingSeverity <= w.AcceptedSeverity;

    /// <summary>
    /// Constats qui bloquent réellement l'approbation : High/Critical, hors règles exclues par la baseline et hors dérogation valide.
    /// Un constat plus grave que la gravité acceptée par la dérogation re-bloque.
    /// </summary>
    public static IReadOnlyList<ScanFinding> EffectiveBlocking(IEnumerable<ScanFinding> findings, ISet<string> excludedRules, IEnumerable<WaiverView> waivers, string contentHash, DateTime now)
    {
        var ws = waivers.ToList();
        return findings.Where(f => Blocks(f.Severity) && !excludedRules.Contains(f.RuleId) && !ws.Any(w => Covers(w, contentHash, f.Severity, now))).ToList();
    }

    public static string Verdict(IEnumerable<ScanFinding> all, IReadOnlyList<ScanFinding> blocking) =>
        blocking.Count > 0 ? "Blocked" : all.Any() ? "Findings" : "Clean";
}

public sealed record BaselineRule(string Id, string Reason);
public sealed record BaselineParse(IReadOnlyList<BaselineRule> Rules, IReadOnlyList<Issue> Errors, IReadOnlyList<BaselineRule> HighSeverityRules);

public static class BaselineYaml
{
    private sealed class Doc { public int Version { get; set; } public List<Entry>? ExcludedRules { get; set; } }
    private sealed class Entry { public string? Id { get; set; } public string? Reason { get; set; } }

    /// <summary>
    /// Baseline v2 : <c>version: 2</c> puis <c>excluded_rules</c> = liste de { id, reason }. Exclusion par identifiant EXACT d'une règle connue,
    /// motif obligatoire. Les règles High/Critical sont renvoyées à part : l'enregistrement exige une confirmation supplémentaire.
    /// </summary>
    public static BaselineParse Parse(string yaml)
    {
        var errors = new List<Issue>();
        Doc? doc;
        try
        {
            doc = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).IgnoreUnmatchedProperties().Build().Deserialize<Doc>(yaml);
        }
        catch (YamlDotNet.Core.YamlException ex) { return new([], [new("yaml", "YAML invalide : " + ex.Message.Split('\n')[0])], []); }
        if (doc is null) return new([], [new("yaml", "Document vide.")], []);
        if (doc.Version != 2) errors.Add(new("version", "La baseline doit déclarer « version: 2 »."));

        var rules = new List<BaselineRule>();
        foreach (var e in doc.ExcludedRules ?? [])
        {
            var id = e.Id?.Trim() ?? "";
            if (id.Length == 0) { errors.Add(new("id", "Une exclusion n'a pas d'identifiant de règle.")); continue; }
            if (StaticScanner.Catalog.All(c => c.Id != id)) { errors.Add(new("unknown-rule", $"Règle inconnue : {id} (identifiant exact requis).")); continue; }
            if (string.IsNullOrWhiteSpace(e.Reason)) { errors.Add(new("reason", $"Motif obligatoire pour l'exclusion de {id}.")); continue; }
            if (rules.Any(r => r.Id == id)) { errors.Add(new("duplicate", $"Règle exclue deux fois : {id}.")); continue; }
            rules.Add(new(id, e.Reason.Trim()));
        }
        var high = rules.Where(r => StaticScanner.Catalog.First(c => c.Id == r.Id).Severity >= FindingSeverity.High).ToList();
        return new(rules, errors, high);
    }
}

public static class FixPrompt
{
    /// <summary>Prompt de correction : donne à un assistant les constats à corriger, sans contenu sensible.</summary>
    public static string Build(string slug, string version, IEnumerable<ScanFinding> findings)
    {
        var list = findings.OrderByDescending(f => f.Severity).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Corrige l'artefact IA « {slug} » (version {version}) pour supprimer les constats de sécurité ci-dessous, sans changer son comportement utile.");
        sb.AppendLine();
        sb.AppendLine("Pour chaque constat : explique la cause, propose le correctif minimal, et indique le fichier et la ligne modifiés.");
        sb.AppendLine("Ne désactive aucun contrôle de sécurité et n'ajoute aucun secret. Si un constat est un faux positif, dis-le et justifie.");
        sb.AppendLine();
        foreach (var f in list) sb.AppendLine($"- [{f.Severity}] {f.RuleId} — {f.File}:{f.Line} — {f.Message}");
        if (list.Count == 0) sb.AppendLine("(aucun constat)");
        return sb.ToString();
    }
}
