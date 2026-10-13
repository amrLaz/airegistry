using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests;

public class DevAuthProviderTests
{
    [Theory]
    [InlineData(Role.Reader)]
    [InlineData(Role.Admin)]
    public void Returns_configured_role(Role role)
    {
        var user = new DevAuthProvider(new DevAuthOptions { Email = "a@b.c", Role = role }).GetCurrentUser();
        Assert.Equal(role, user.Role);
        Assert.Equal("a@b.c", user.Email);
        Assert.True(user.IsAuthenticated);
    }

    [Fact]
    public void Default_namespace_is_myregistry() =>
        Assert.Equal("myregistry", SettingDefaults.Initial[SettingKeys.DefaultNamespace]);
}
