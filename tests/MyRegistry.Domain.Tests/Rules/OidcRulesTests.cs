using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Domain.Tests.Rules;

public class OidcRulesTests
{
    private static (string, string)[] C(params (string, string)[] c) => c;

    [Fact] public void Email_prefers_the_email_claim_then_preferred_username_then_upn()
    {
        Assert.Equal("a@x.com", OidcRules.EmailFrom(C(("preferred_username", "b@x.com"), ("email", "A@X.com"))));
        Assert.Equal("b@x.com", OidcRules.EmailFrom(C(("name", "B"), ("preferred_username", "B@x.com"))));
        Assert.Equal("c@x.com", OidcRules.EmailFrom(C(("upn", "c@x.com"))));
        Assert.Equal("d@x.com", OidcRules.EmailFrom(C(("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress", "d@x.com"))));
    }

    [Theory]
    [InlineData("pas-un-mail")] [InlineData("a@@x.com")] [InlineData("a b@x.com")] [InlineData("@x.com")] [InlineData("a@")] [InlineData("")]
    public void Email_rejects_values_that_are_not_an_address(string v) => Assert.Null(OidcRules.EmailFrom(C(("email", v))));

    [Fact] public void Email_skips_an_unusable_claim_and_falls_back_to_the_next()
    {
        Assert.Equal("ok@x.com", OidcRules.EmailFrom(C(("email", "oid-1234"), ("preferred_username", "ok@x.com"))));
        Assert.Null(OidcRules.EmailFrom(C()));
    }

    [Fact] public void Display_name_falls_back_to_the_local_part()
    {
        Assert.Equal("Amr L", OidcRules.DisplayName(C(("name", " Amr L ")), "amr@x.com"));
        Assert.Equal("amr", OidcRules.DisplayName(C(), "amr@x.com"));
    }

    [Fact] public void Only_listed_emails_start_as_admin_everyone_else_is_reader()
    {
        Assert.Equal(Role.Admin, OidcRules.InitialRole("Boss@X.com", ["boss@x.com", "other@x.com"]));
        Assert.Equal(Role.Reader, OidcRules.InitialRole("user@x.com", ["boss@x.com"]));
        Assert.Equal(Role.Reader, OidcRules.InitialRole("user@x.com", []));
    }

    [Theory]
    [InlineData("a@up.coop", "up.coop", true)]
    [InlineData("a@UP.coop", "@up.coop", true)]
    [InlineData("a@evil-up.coop", "up.coop", false)]
    [InlineData("a@sub.up.coop", "up.coop", false)]
    [InlineData("a@anything.com", "", true)]
    public void Domain_allow_list(string email, string list, bool expected) =>
        Assert.Equal(expected, OidcRules.IsDomainAllowed(email, OidcRules.ParseList(list)));

    [Theory]
    [InlineData("475fd1d1-212f-472f-9c02-635526f47568", "https://login.microsoftonline.com/475fd1d1-212f-472f-9c02-635526f47568/v2.0")]
    [InlineData("contoso.onmicrosoft.com", "https://login.microsoftonline.com/contoso.onmicrosoft.com/v2.0")]
    [InlineData("https://login.microsoftonline.com/t/v2.0/", "https://login.microsoftonline.com/t/v2.0")]
    [InlineData("http://localhost:9000/realm", "http://localhost:9000/realm")]
    public void Authority_is_derived_from_a_tenant_or_kept_as_a_url(string input, string expected) => Assert.Equal(expected, OidcRules.Authority(input));

    [Theory]
    [InlineData("http://evil.example.com")] [InlineData("ftp://x")] [InlineData("not a tenant")] [InlineData("")] [InlineData(null)]
    public void Authority_rejects_insecure_or_garbage_values(string? input) => Assert.Null(OidcRules.Authority(input));

    [Theory]
    [InlineData("/artifact/1?x=2", "/artifact/1?x=2")]
    [InlineData("//evil.com", "/")]
    [InlineData("/\\evil.com", "/")]
    [InlineData("https://evil.com", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/ok\r\nSet-Cookie: x=1", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void Return_url_is_a_local_path_only(string? url, string expected) => Assert.Equal(expected, OidcRules.SafeReturnUrl(url));

    [Fact] public void List_parsing_splits_on_separators_and_removes_duplicates() =>
        Assert.Equal(["a@x.com", "b@x.com"], OidcRules.ParseList("a@x.com, b@x.com;A@x.com\n"));
}
