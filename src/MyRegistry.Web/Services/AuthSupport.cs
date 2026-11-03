using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Web.Services;

public sealed record AuthModeOptions(bool Oidc);

public sealed class OidcOptions
{
    public IReadOnlyList<string> BootstrapAdmins { get; init; } = [];
    public IReadOnlyList<string> AllowedDomains { get; init; } = [];
}

public static class AuthSupport
{
    /// <summary>Jeton CLI (Authorization: Bearer mrt_…). Null s'il n'y en a pas ; un jeton inconnu, expiré ou d'un compte désactivé donne un utilisateur non authentifié, jamais un repli sur un autre compte.</summary>
    public static CurrentUser? FromBearer(HttpContext? http, ICliTokenResolver tokens, IUserDirectory directory)
    {
        if (CliAuthRules.ParseBearer(http?.Request.Headers.Authorization.FirstOrDefault()) is not { } bearer) return null;
        var owner = tokens.ResolveEmail(bearer);
        if (owner is null) return Anonymous;
        var (role, active, name) = directory.Resolve(owner, Role.Reader);
        return active ? new CurrentUser(owner, name, role, true) : new CurrentUser(owner, name, Role.Reader, false);
    }

    public static readonly CurrentUser Anonymous = new("", "", Role.Reader, false);
}
