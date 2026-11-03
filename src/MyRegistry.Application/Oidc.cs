using System.Text.RegularExpressions;
using MyRegistry.Domain;

namespace MyRegistry.Application;

/// <summary>Règles pures de l'authentification OIDC (Entra ID) : lecture des claims, rôle initial, domaines autorisés, URL de retour.</summary>
public static class OidcRules
{
    private static readonly string[] EmailClaims =
        ["email", "preferred_username", "upn", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn"];
    private static readonly string[] NameClaims = ["name", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"];
    private static readonly Regex Guid = new("^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", RegexOptions.Compiled);
    private static readonly Regex Domain = new(@"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$", RegexOptions.Compiled);

    /// <summary>Premier claim exploitable parmi email, preferred_username, upn ; en minuscules. Null si aucun n'ressemble à une adresse.</summary>
    public static string? EmailFrom(IEnumerable<(string Type, string Value)> claims)
    {
        var list = claims.ToList();
        foreach (var type in EmailClaims)
            foreach (var c in list.Where(c => c.Type == type))
            {
                var v = c.Value.Trim().ToLowerInvariant();
                if (v.Length is > 0 and <= 200 && v.Count(ch => ch == '@') == 1 && !v.Any(char.IsWhiteSpace) && !v.StartsWith('@') && !v.EndsWith('@')) return v;
            }
        return null;
    }

    public static string DisplayName(IEnumerable<(string Type, string Value)> claims, string email) =>
        claims.FirstOrDefault(c => NameClaims.Contains(c.Type) && !string.IsNullOrWhiteSpace(c.Value)).Value?.Trim() is { Length: > 0 } n ? n : email.Split('@')[0];

    /// <summary>Rôle d'un compte vu pour la première fois : Admin s'il figure dans la liste d'amorçage, sinon Reader. Ne change jamais un rôle existant.</summary>
    public static Role InitialRole(string email, IEnumerable<string> bootstrapAdmins) =>
        bootstrapAdmins.Any(a => string.Equals(a.Trim(), email, StringComparison.OrdinalIgnoreCase)) ? Role.Admin : Role.Reader;

    /// <summary>Liste vide = tous les domaines ; sinon le domaine exact de l'adresse doit y figurer.</summary>
    public static bool IsDomainAllowed(string email, IReadOnlyCollection<string> domains)
    {
        if (domains.Count == 0) return true;
        var d = email[(email.LastIndexOf('@') + 1)..];
        return domains.Any(x => string.Equals(x.Trim().TrimStart('@'), d, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>URL d'autorité : une URL https est conservée, un identifiant de tenant (GUID ou domaine) devient l'autorité v2.0 d'Entra ID. Null si inexploitable.</summary>
    public static string? Authority(string? tenantOrAuthority)
    {
        var v = tenantOrAuthority?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.Contains("://")) return Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.IsLoopback) ? v.TrimEnd('/') : null;
        return Guid.IsMatch(v) || Domain.IsMatch(v) ? $"https://login.microsoftonline.com/{v}/v2.0" : null;
    }

    /// <summary>Redirection après connexion : chemin relatif du site uniquement (anti open-redirect).</summary>
    public static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && !url.StartsWith("//") && !url.StartsWith("/\\") && !url.Contains('\n') && !url.Contains('\r') ? url : "/";

    public static IReadOnlyList<string> ParseList(string? value) =>
        (value ?? "").Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
