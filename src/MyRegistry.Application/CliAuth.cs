using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MyRegistry.Application;

public interface ICliTokenResolver
{
    /// <summary>Courriel du propriétaire d'un jeton valide (non expiré, non révoqué), sinon null.</summary>
    string? ResolveEmail(string token);
}

public static class CliAuthRules
{
    public const string Prefix = "mrt_";
    public static readonly TimeSpan InteractiveTtl = TimeSpan.FromMinutes(1);
    public const int DefaultCiDays = 30, MaxCiDays = 90;

    private static readonly Regex StateFormat = new("^[A-Za-z0-9_-]{8,128}$", RegexOptions.Compiled);

    /// <summary>Le jeton n'est remis qu'à un serveur local : http, localhost/127.0.0.1/[::1], port ≥ 1024, ni identifiants ni fragment.</summary>
    public static bool IsValidCallback(string? callback)
    {
        if (!Uri.TryCreate(callback, UriKind.Absolute, out var u)) return false;
        return u.Scheme == Uri.UriSchemeHttp && u.Host is "localhost" or "127.0.0.1" or "[::1]"
            && !u.IsDefaultPort && u.Port >= 1024 && string.IsNullOrEmpty(u.UserInfo) && string.IsNullOrEmpty(u.Fragment);
    }

    public static bool IsValidState(string? state) => state is not null && StateFormat.IsMatch(state);

    public static string RedirectUrl(string callback, string token, string state)
    {
        var u = new UriBuilder(callback);
        var q = string.IsNullOrEmpty(u.Query) ? "" : u.Query.TrimStart('?') + "&";
        u.Query = $"{q}token={Uri.EscapeDataString(token)}&state={Uri.EscapeDataString(state)}";
        return u.Uri.ToString();
    }

    public static string NewToken() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Jeton dans « Authorization: Bearer … » ; null si absent ou s'il n'a pas notre préfixe.</summary>
    public static string? ParseBearer(string? header)
    {
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var t = header[7..].Trim();
        return t.StartsWith(Prefix, StringComparison.Ordinal) && t.Length > Prefix.Length ? t : null;
    }

    public static int ClampCiDays(int? days) => days is null or < 1 ? DefaultCiDays : Math.Min(days.Value, MaxCiDays);

    public static bool IsUsable(DateTime expiresAt, DateTime? revokedAt, DateTime now) => revokedAt is null && now < expiresAt;
}
