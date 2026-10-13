using MyRegistry.Domain;

namespace MyRegistry.Application;

public sealed record CurrentUser(string Email, string DisplayName, Role Role, bool IsAuthenticated);

public interface IAuthProvider
{
    CurrentUser GetCurrentUser();
}

public sealed class DevAuthOptions
{
    public string Email { get; set; } = "dev@example.com";
    public Role Role { get; set; } = Role.Admin;
}

/// <summary>Mode dev : utilisateur factice dont le rôle vient de MYREGISTRY_DEV_USER_ROLE.</summary>
public sealed class DevAuthProvider(DevAuthOptions options) : IAuthProvider
{
    public CurrentUser GetCurrentUser() =>
        new(options.Email, options.Email.Split('@')[0], options.Role, true);
}
