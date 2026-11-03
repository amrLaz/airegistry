using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Web.Services;

/// <summary>
/// Authentification Entra ID : l'identité vient du cookie posé après la connexion OIDC (requête HTTP) ou de l'état d'authentification du circuit Blazor.
/// Le rôle n'est jamais lu dans le jeton : il vient de la base (Admin via la page Utilisateurs) ; un compte inconnu démarre Reader, sauf liste d'amorçage.
/// </summary>
public sealed class OidcAuthProvider(IHttpContextAccessor http, IUserDirectory directory, ICliTokenResolver tokens, OidcOptions options, AuthenticationStateProvider circuit) : IAuthProvider
{
    private CurrentUser? _current;

    public CurrentUser GetCurrentUser()
    {
        if (_current is not null) return _current;
        if (AuthSupport.FromBearer(http.HttpContext, tokens, directory) is { } viaToken) return _current = viaToken;

        var principal = http.HttpContext?.User is { Identity.IsAuthenticated: true } p ? p : FromCircuit();
        if (principal is not { Identity.IsAuthenticated: true }) return _current = AuthSupport.Anonymous;

        var claims = principal.Claims.Select(c => (c.Type, c.Value)).ToList();
        var email = OidcRules.EmailFrom(claims);
        if (email is null || !OidcRules.IsDomainAllowed(email, options.AllowedDomains.ToList())) return _current = AuthSupport.Anonymous;

        var (role, active, name) = directory.Resolve(email, OidcRules.InitialRole(email, options.BootstrapAdmins));
        return _current = active ? new CurrentUser(email, OidcRules.DisplayName(claims, email) is { Length: > 0 } dn ? dn : name, role, true) : new CurrentUser(email, name, Role.Reader, false);
    }

    // Dans un circuit Blazor, HttpContext n'est plus fiable : l'état d'authentification du circuit fait foi.
    private ClaimsPrincipal? FromCircuit()
    {
        try { return circuit.GetAuthenticationStateAsync().GetAwaiter().GetResult().User; }
        catch (InvalidOperationException) { return null; }
    }
}
