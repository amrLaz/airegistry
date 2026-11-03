using MyRegistry.Application;

namespace MyRegistry.Domain.Tests.Rules;

public class UsageRulesTests
{
    [Theory]
    [InlineData("myregistry-cli/0.1.0", "cli")] [InlineData("MyRegistry-CLI/2", "cli")]
    [InlineData("Mozilla/5.0 (X11; Linux) Chrome/120", "web")]
    [InlineData("node", "api")] [InlineData("curl/8.5", "api")] [InlineData("", "api")] [InlineData(null, "api")]
    [InlineData("not-myregistry-cli/1", "api")]
    public void Source_from_user_agent(string? ua, string expected) => Assert.Equal(expected, UsageSources.FromUserAgent(ua));

    [Theory] [InlineData(null, 30)] [InlineData(0, 30)] [InlineData(-3, 30)] [InlineData(7, 7)] [InlineData(365, 365)] [InlineData(9999, 365)]
    public void Window_is_clamped(int? days, int expected) => Assert.Equal(expected, UsageRules.ClampDays(days));

    [Fact] public void Day_is_the_utc_midnight()
    {
        var d = UsageRules.DayOf(new DateTime(2026, 3, 4, 23, 59, 59, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc), d);
        Assert.Equal(DateTimeKind.Utc, d.Kind);
    }

    [Fact] public void Window_includes_today_and_spans_exactly_n_days()
    {
        var now = new DateTime(2026, 3, 10, 15, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc), UsageRules.WindowStart(now, 7));
        var series = UsageRules.FillDays(new Dictionary<DateTime, int> { [new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc)] = 5, [new DateTime(2026, 3, 6, 0, 0, 0, DateTimeKind.Utc)] = 2 }, now, 7);
        Assert.Equal(7, series.Count);
        Assert.Equal(new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc), series[0].Day);
        Assert.Equal(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), series[^1].Day);
        Assert.Equal([0, 0, 2, 0, 0, 0, 5], series.Select(s => s.Count));
    }
}
