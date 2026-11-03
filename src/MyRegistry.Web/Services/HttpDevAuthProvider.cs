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
        // Un jeton CLI prime sur tout le reste, y compris l'usurpation de développement.
        if (AuthSupport.FromBearer(http.HttpContext, tokens, directory) is { } viaToken) return _current = viaToken;
        var header = options.AllowImpersonation ? http.HttpContext?.Request.Headers["X-Dev-User"].FirstOrDefault() : null;
        var impersonating = !string.IsNullOrWhiteSpace(header);
        var email = (impersonating ? header! : options.Email).Trim().ToLowerInvariant();
        var (role, active, name) = directory.Resolve(email, impersonating ? Role.Reader : options.Role);
        return _current = active ? new CurrentUser(email, name, role, true) : new CurrentUser(email, name, Role.Reader, false);
    }
}
