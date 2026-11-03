using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Web.Services;

/// <summary>
/// Auth de développement. L'utilisateur par défaut vient de MYREGISTRY_DEV_USER_EMAIL / _ROLE ; son rôle initial est créé en base.
/// Avec l'en-tête X-Dev-User (si autorisé), on simule un autre utilisateur : rôle lu en base, Reader à la première visite.
/// Les rôles sont ensuite gérés en base par un Admin (page Utilisateurs).
/// </summary>
public sealed class HttpDevAuthProvider(IHttpContextAccessor http, DevAuthOptions options, IUserDirectory directory, ICliTokenResolver tokens) : IAuthProvider
{
    private CurrentUser? _current;

    public CurrentUser GetCurrentUser()
    {
        if (_current is not null) return _current;
        // Un jeton CLI prime sur tout le reste, y compris l'usurpation de développement. Jeton invalide → non authentifié, jamais un repli sur l'utilisateur par défaut.
        if (CliAuthRules.ParseBearer(http.HttpContext?.Request.Headers.Authorization.FirstOrDefault()) is { } bearer)
        {
            var owner = tokens.ResolveEmail(bearer);
            if (owner is null) return _current = new CurrentUser("", "", Role.Reader, false);
            var (r, act, n) = directory.Resolve(owner, Role.Reader);
            return _current = act ? new CurrentUser(owner, n, r, true) : new CurrentUser(owner, n, Role.Reader, false);
        }
        var header = options.AllowImpersonation ? http.HttpContext?.Request.Headers["X-Dev-User"].FirstOrDefault() : null;
        var impersonating = !string.IsNullOrWhiteSpace(header);
        var email = (impersonating ? header! : options.Email).Trim().ToLowerInvariant();
        var (role, active, name) = directory.Resolve(email, impersonating ? Role.Reader : options.Role);
        return _current = active ? new CurrentUser(email, name, role, true) : new CurrentUser(email, name, Role.Reader, false);
    }
}
