using MyRegistry.Application;

namespace MyRegistry.Domain.Tests.Rules;

public class CliAuthRulesTests
{
    [Theory]
    [InlineData("http://localhost:54321/callback", true)]
    [InlineData("http://127.0.0.1:8123/cb", true)]
    [InlineData("http://[::1]:9000/cb", true)]
    [InlineData("https://localhost:54321/cb", false)]
    [InlineData("http://evil.example.com:54321/cb", false)]
    [InlineData("http://localhost.evil.com:54321/cb", false)]
    [InlineData("http://localhost/cb", false)]
    [InlineData("http://localhost:80/cb", false)]
    [InlineData("http://localhost:1023/cb", false)]
    [InlineData("http://user:pw@localhost:54321/cb", false)]
    [InlineData("http://localhost:54321/cb#frag", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Callback_must_be_a_local_http_listener(string? url, bool ok) => Assert.Equal(ok, CliAuthRules.IsValidCallback(url));

    [Theory]
    [InlineData("abcdEFGH12345678", true)]
    [InlineData("short", false)]
    [InlineData("has space in it!", false)]
    [InlineData("ok_ok-ok_ok-1234", true)]
    [InlineData(null, false)]
    public void State_format(string? s, bool ok) => Assert.Equal(ok, CliAuthRules.IsValidState(s));

    [Fact] public void State_longer_than_128_is_refused() => Assert.False(CliAuthRules.IsValidState(new string('a', 129)));

    [Fact] public void Tokens_are_prefixed_unique_and_url_safe()
    {
        var a = CliAuthRules.NewToken(); var b = CliAuthRules.NewToken();
        Assert.StartsWith("mrt_", a);
        Assert.NotEqual(a, b);
        Assert.Matches("^mrt_[A-Za-z0-9_-]{43}$", a);
    }

    [Fact] public void Hash_is_stable_hex_and_differs_from_the_token()
    {
        var t = CliAuthRules.NewToken();
        Assert.Equal(CliAuthRules.Hash(t), CliAuthRules.Hash(t));
        Assert.Matches("^[0-9a-f]{64}$", CliAuthRules.Hash(t));
        Assert.NotEqual(CliAuthRules.Hash(t), CliAuthRules.Hash(t + "x"));
    }

    [Theory]
    [InlineData("Bearer mrt_abc123", "mrt_abc123")]
    [InlineData("bearer  mrt_abc123 ", "mrt_abc123")]
    [InlineData("Bearer other_token", null)]
    [InlineData("Bearer mrt_", null)]
    [InlineData("Basic mrt_abc", null)]
    [InlineData(null, null)]
    public void Bearer_parsing(string? header, string? expected) => Assert.Equal(expected, CliAuthRules.ParseBearer(header));

    [Fact] public void Redirect_keeps_existing_query_and_encodes_values() =>
        Assert.Equal("http://localhost:5000/cb?x=1&token=mrt_a%2Bb&state=s-1", CliAuthRules.RedirectUrl("http://localhost:5000/cb?x=1", "mrt_a+b", "s-1"));

    [Theory]
    [InlineData(null, 30)] [InlineData(0, 30)] [InlineData(-5, 30)] [InlineData(7, 7)] [InlineData(90, 90)] [InlineData(500, 90)]
    public void Ci_lifetime_is_clamped(int? days, int expected) => Assert.Equal(expected, CliAuthRules.ClampCiDays(days));

    [Fact] public void Usable_only_when_unexpired_and_not_revoked()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(CliAuthRules.IsUsable(now.AddSeconds(1), null, now));
        Assert.False(CliAuthRules.IsUsable(now, null, now));
        Assert.False(CliAuthRules.IsUsable(now.AddHours(1), now.AddMinutes(-1), now));
    }
}
