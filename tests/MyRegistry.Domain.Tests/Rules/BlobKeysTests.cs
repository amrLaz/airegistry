using MyRegistry.Application;

namespace MyRegistry.Domain.Tests.Rules;

public class BlobKeysTests
{
    [Theory]
    [InlineData("a")] [InlineData("security-reports/3f2a.csv")] [InlineData("a/b/c.d_e-f")] [InlineData("v1.2.3/file.zip")]
    public void Valid_keys(string key) => Assert.True(BlobKeys.IsValid(key));

    [Theory]
    [InlineData("")] [InlineData(null)] [InlineData("/abs")] [InlineData("a//b")] [InlineData("a/")] [InlineData("../x")] [InlineData("a/../b")] [InlineData("./a")]
    [InlineData("a\\b")] [InlineData("a b")] [InlineData("a\nb")] [InlineData("é")] [InlineData("a%2e%2e/b")] [InlineData("C:/x")]
    public void Invalid_keys(string? key) => Assert.False(BlobKeys.IsValid(key));

    [Fact] public void Length_limits()
    {
        Assert.False(BlobKeys.IsValid(new string('a', 129)));   // un segment dépasse 128
        Assert.False(BlobKeys.IsValid(string.Join('/', Enumerable.Repeat(new string('a', 100), 6))));  // la clé dépasse 512
        Assert.True(BlobKeys.IsValid(new string('a', 128)));
    }

    [Fact] public void Require_throws_on_invalid() => Assert.Throws<ArgumentException>(() => BlobKeys.Require("../x"));

    [Theory]
    [InlineData("myregistry", true)] [InlineData("my-registry-1", true)] [InlineData("abc", true)]
    [InlineData("ab", false)] [InlineData("My-Registry", false)] [InlineData("a--b", false)] [InlineData("-ab", false)] [InlineData("ab-", false)] [InlineData("a_b", false)] [InlineData(null, false)]
    public void Container_names(string? name, bool ok) => Assert.Equal(ok, BlobKeys.IsValidContainer(name));

    [Theory] [InlineData("", true)] [InlineData(null, true)] [InlineData("a/b/", true)] [InlineData("a", true)] [InlineData("../", false)]
    public void Prefixes(string? p, bool ok) => Assert.Equal(ok, BlobKeys.IsValidPrefix(p));

    [Theory]
    [InlineData("oidc-client-secret", "OIDC_CLIENT_SECRET")]
    [InlineData("db-connection", "DB_CONNECTION")]
    [InlineData("Bootstrap-Admins", "BOOTSTRAP_ADMINS")]
    [InlineData("-bad", null)] [InlineData("has_underscore", null)] [InlineData("", null)] [InlineData("a b", null)]
    public void Key_vault_secret_names_map_to_config_keys(string name, string? expected) => Assert.Equal(expected, KeyVaultNames.ToConfigKey(name));
}
